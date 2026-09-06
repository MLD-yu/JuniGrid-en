using System.IO;

namespace JuniGrid.Services;

/// <summary>One category row on the Cache & Storage page: shows path + usage + whether it can be cleaned/moved.
/// Tip is the plain-language explanation inside the hover "?" (for users unfamiliar with the terminology).</summary>
public sealed record StorageCategory(
    string Id,
    string Name,
    string Note,
    string DisplayPath,
    string[] SizeRoots,     // roots used to measure usage (mix of files and directories)
    string[] CleanRoots,    // roots whose contents are deleted when cleaning
    bool Cleanable,
    bool Movable,
    string Tip = "");

/// <summary>
/// v0.2.1: cache & storage management — usage stats per cache category, per-item/one-click cleanup,
/// and changing/migrating the unified cache directory.
/// Stats are computed in the background (may take a few seconds) and pushed to the UI via OnStats; cleanup goes
/// through the task center (kind=cleanup), deleting file by file and skipping in-use files without aborting.
/// </summary>
public sealed class StorageService
{
    private readonly ConfigService _cfg;
    private readonly TaskCenterService _center;

    public StorageService(ConfigService cfg, TaskCenterService center)
    {
        _cfg = cfg;
        _center = center;
    }

    /// <summary>Raised after a category's usage is computed/refreshed (may be a background thread; UI subscribers must dispatch themselves).</summary>
    public event Action? OnStats;

    private readonly object _gate = new();
    private readonly Dictionary<string, long> _sizes = new();     // id → bytes; missing = not computed yet
    private readonly HashSet<string> _computing = new(StringComparer.Ordinal);
    private DateTime _lastRefreshUtc = DateTime.MinValue;

    public bool IsComputing(string id) { lock (_gate) return _computing.Contains(id); }
    public long GetSize(string id) { lock (_gate) return _sizes.TryGetValue(id, out var v) ? v : -1; }

    /// <summary>Sum of known usage across cleanable items (items not yet computed are excluded).</summary>
    public long TotalKnownBytes
    {
        get { lock (_gate) return _sizes.Where(kv => kv.Key != "data" && kv.Value > 0).Sum(kv => kv.Value); }
    }

    /// <summary>Category list (built fresh each time: backup cleanup scope and the game trash depend on current settings).</summary>
    public List<StorageCategory> GetCategories()
    {
        var list = new List<StorageCategory>();

        list.Add(new("downloads", "Downloads & install temp", "Downloaded zips and extraction temp files",
            StoragePaths.DownloadsDir,
            new[] { StoragePaths.DownloadsDir }, new[] { StoragePaths.DownloadsDir },
            Cleanable: true, Movable: true,
            Tip: "Archives downloaded from Nexus or GitHub when getting mods, plus extraction leftovers. Useless once installation finishes — safe to clean up"));

        list.Add(new("smapi", "SMAPI installer cache", "Installer downloads and extracted files",
            StoragePaths.SmapiInstallerDir,
            new[] { StoragePaths.SmapiInstallerDir }, new[] { StoragePaths.SmapiInstallerDir },
            Cleanable: true, Movable: true,
            Tip: "The official SMAPI installer and extracted files downloaded when installing or updating SMAPI. Useless once installed — safe to clean up"));

        // Only count/clean the HTTP and shader cache subdirectories (names verified against a real local EBWebView layout) —
        // Cookies/LocalStorage live in other subdirectories, so your signed-in state is unaffected
        var wv2Root = StoragePaths.WebView2Dir;
        var wv2Caches = new[]
        {
            Path.Combine(wv2Root, "EBWebView", "Default", "Cache"),
            Path.Combine(wv2Root, "EBWebView", "Default", "Code Cache"),
            Path.Combine(wv2Root, "EBWebView", "Default", "GPUCache"),
            Path.Combine(wv2Root, "EBWebView", "Default", "DawnGraphiteCache"),
            Path.Combine(wv2Root, "EBWebView", "Default", "DawnWebGPUCache"),
            Path.Combine(wv2Root, "EBWebView", "GrShaderCache"),
            Path.Combine(wv2Root, "EBWebView", "ShaderCache"),
        };
        list.Add(new("wv2", "WebView2 network cache", "Web page & image cache (keeps you signed in)", wv2Root,
            wv2Caches, wv2Caches, Cleanable: true, Movable: true,
            Tip: "The whole UI is a set of web components; this is the network cache left behind by loading images such as mod covers. Clearing it does not affect your Nexus sign-in. Changing the cache location takes effect after restarting the app"));

        // Pre-update Mods safety snapshot: cleaning keeps the latest one and deletes the rest (foolproofing: directory names are timestamps)
        var backupRoot = StoragePaths.ModsBackupDir;
        var oldSnapshots = SafeDirs(backupRoot).OrderByDescending(p => p, StringComparer.OrdinalIgnoreCase).Skip(1).ToArray();
        list.Add(new("backup", "Pre-update Mods backup", "Safety snapshot before SMAPI updates (cleanup keeps the latest one)", backupRoot,
            new[] { backupRoot }, oldSnapshots, Cleanable: oldSnapshots.Length > 0, Movable: true,
            Tip: "Before updating SMAPI, all mods are automatically backed up so you can restore them if the update fails. Cleanup only removes older backups — the most recent one is always kept"));

        var logFiles = new[]
        {
            Path.Combine(StoragePaths.AppDataDir, "juni-grid.log"),
            Path.Combine(StoragePaths.AppDataDir, "juni-grid.log.old"),
            Path.Combine(StoragePaths.AppDataDir, "startup.log"),
            Path.Combine(StoragePaths.AppDataDir, "crash.log"),
        };
        list.Add(new("logs", "Log files", "Run & crash logs", StoragePaths.AppDataDir,
            logFiles, logFiles, Cleanable: true, Movable: false,
            Tip: "Text logs written while the app runs or crashes. Only for troubleshooting — cleaning them up does not affect any feature"));

        var gp = _cfg.Current.GamePath;
        if (!string.IsNullOrWhiteSpace(gp))
        {
            var trash = StoragePaths.GameTrashDir(gp);
            list.Add(new("trash", "Game uninstall recycle bin", "Recycle bin for uninstalled mods", trash,
                new[] { trash }, new[] { trash }, Cleanable: true, Movable: false,
                Tip: "When you uninstall a mod, its files are moved here instead of being deleted outright. Only emptying this bin actually removes those mods, and that cannot be undone — make sure you no longer need them"));
        }

        var dataFiles = new[]
        {
            Path.Combine(StoragePaths.AppDataDir, "junigrid.config.json"),
            Path.Combine(StoragePaths.AppDataDir, "tasks.json"),
        };
        list.Add(new("data", "Settings & task data", "Config & task records (fixed, not cleanable)", StoragePaths.AppDataDir,
            dataFiles, Array.Empty<string>(), Cleanable: false, Movable: false,
            Tip: "Your settings, Nexus API key, saved mod list, and download task records. This is personal data the app will never clean automatically"));

        return list;
    }

    /// <summary>Refreshes all usage figures (skipped if refreshed within the last 30 seconds, unless force). For callers that don't care about the result.</summary>
    public void RefreshAll(bool force = false) => _ = RefreshAllAsync(force);

    /// <summary>Refreshes all usage figures and waits for completion — returns true if every item was computed, false if any failed
    /// (used by the "Refresh usage" button to report success/failure via a toast).</summary>
    public async Task<bool> RefreshAllAsync(bool force = false)
    {
        if (!force && DateTime.UtcNow - _lastRefreshUtc < TimeSpan.FromSeconds(30)) return true;
        _lastRefreshUtc = DateTime.UtcNow;
        var results = await Task.WhenAll(GetCategories().Select(ComputeAsync)).ConfigureAwait(false);
        return results.All(ok => ok);
    }

    private async Task<bool> ComputeAsync(StorageCategory c)
    {
        lock (_gate) { if (!_computing.Add(c.Id)) return true; }   // already computing; treat as in progress and report success
        try
        {
            var roots = c.SizeRoots;
            var bytes = await Task.Run(() =>
            {
                long sum = 0;
                foreach (var root in roots) sum += DirSize(root);
                return sum;
            }).ConfigureAwait(false);
            lock (_gate) _sizes[c.Id] = bytes;
            OnStats?.Invoke();
            return true;
        }
        catch { return false; }
        finally
        {
            lock (_gate) _computing.Remove(c.Id);
        }
    }

    private static long DirSize(string root)
    {
        try
        {
            if (File.Exists(root)) return new FileInfo(root).Length;
            if (!Directory.Exists(root)) return 0;
            var opts = new EnumerationOptions
            {
                RecurseSubdirectories = true,
                IgnoreInaccessible = true,
                AttributesToSkip = FileAttributes.ReparsePoint
            };
            long sum = 0;
            foreach (var f in Directory.EnumerateFiles(root, "*", opts))
            {
                try { sum += new FileInfo(f).Length; } catch { }
            }
            return sum;
        }
        catch { return 0; }
    }

    /// <summary>
    /// Cleans one category. Reports progress through the task center; deletes file by file, skipping in-use files.
    /// Returns a one-line result for the toast. Refuses to clean download/install directories while a task is running (it would delete the zip in use).
    /// </summary>
    public Task<string> CleanCategoryAsync(string id)
    {
        var c = GetCategories().FirstOrDefault(x => x.Id == id);
        if (c is null || !c.Cleanable) return Task.FromResult("This item cannot be cleaned");
        if ((c.Id == "downloads" || c.Id == "smapi") && _center.RunningCount > 0)
            return Task.FromResult("Download/install tasks are running — clean up after they finish");

        var task = _center.Start("Cleaning: " + c.Name, "cleanup");
        _center.Report(task, "Starting cleanup...", 3);

        var roots = c.CleanRoots;
        var pruneDirs = roots.Any(Directory.Exists);   // only directory roots need empty-shell pruning afterwards
        return Task.Run(() =>
        {
            long freed = 0, skipped = 0;
            var files = CollectFiles(roots);
            for (var i = 0; i < files.Count; i++)
            {
                try
                {
                    var len = new FileInfo(files[i]).Length;
                    File.Delete(files[i]);
                    freed += len;
                }
                catch { skipped++; }   // in use / access denied: skip without aborting
                if (i % 50 == 0 || i == files.Count - 1)
                    _center.Report(task,
                        $"Cleaned {ResumableDownload.FormatBytes(freed)} (skipped {skipped} in use)",
                        3 + 92.0 * (i + 1) / Math.Max(1, files.Count));
            }
            if (pruneDirs)
                foreach (var root in roots) PruneEmptyDirs(root);

            var msg = files.Count == 0
                ? "Nothing to clean here"
                : $"Cleanup complete: freed {ResumableDownload.FormatBytes(freed)}" +
                  (skipped > 0 ? $", skipped {skipped} file(s) in use" : "");
            _center.Finish(task, true, msg);
            AppLog.Warn("Storage", $"Cleanup[{c.Id}] freed {freed} bytes, skipped {skipped}");
            lock (_gate) _sizes[c.Id] = DirSizeSum(roots);
            OnStats?.Invoke();
            return msg;
        });
    }

    private static long DirSizeSum(string[] roots) => roots.Sum(DirSize);

    private static List<string> CollectFiles(IEnumerable<string> roots)
    {
        var files = new List<string>();
        foreach (var root in roots)
        {
            try
            {
                if (File.Exists(root)) { files.Add(root); continue; }
                if (!Directory.Exists(root)) continue;
                files.AddRange(Directory.EnumerateFiles(root, "*", new EnumerationOptions
                {
                    RecurseSubdirectories = true,
                    IgnoreInaccessible = true
                }));
            }
            catch { }
        }
        return files;
    }

    /// <summary>Removes empty directory shells after cleaning (keeps the root itself; the service still writes into it).</summary>
    private static void PruneEmptyDirs(string root)
    {
        try
        {
            if (!Directory.Exists(root)) return;
            var dirs = Directory.EnumerateDirectories(root, "*", new EnumerationOptions
            {
                RecurseSubdirectories = true,
                AttributesToSkip = 0
            }).OrderByDescending(d => d.Length);   // deepest first so parent directories empty out
            foreach (var dir in dirs)
            {
                try
                {
                    if (!Directory.EnumerateFileSystemEntries(dir).Any()) Directory.Delete(dir);
                }
                catch { }
            }
        }
        catch { }
    }

    /// <summary>Lists full paths of first-level subdirectories (empty if missing/unreadable).</summary>
    private static List<string> SafeDirs(string dir)
    {
        try
        {
            return Directory.Exists(dir)
                ? Directory.GetDirectories(dir).ToList()
                : new List<string>();
        }
        catch { return new List<string>(); }
    }

    /// <summary>
    /// Changes the unified cache directory (newDir = null restores the default location): downloads/install temp,
    /// SMAPI installer, and Mods backup switch immediately and move existing content on the spot; WebView2 data is
    /// in use, so it is recorded in PendingWebView2MoveFrom and moved automatically by MainWindow on next startup
    /// (before WebView2 initialization).
    /// </summary>
    public async Task<string> MigrateCacheRootAsync(string? newRoot)
    {
        newRoot = string.IsNullOrWhiteSpace(newRoot) ? null : Path.GetFullPath(newRoot.Trim());
        if (newRoot is not null) Directory.CreateDirectory(newRoot);

        // capture the old locations before saving (SyncStoragePaths switches the resolved paths to the new root)
        var oldDownloads = StoragePaths.DownloadsDir;
        var oldSmapi = StoragePaths.SmapiInstallerDir;
        var oldBackup = StoragePaths.ModsBackupDir;
        var oldWv2 = StoragePaths.WebView2Dir;

        var cfg = _cfg.Current;
        cfg.CacheRoot = newRoot;
        _cfg.Save(cfg);

        var task = _center.Start(newRoot is null ? "Restore default cache location" : "Migrate cache directory", "cleanup");
        _center.Report(task, newRoot is null ? "Restoring default location..." : $"Target: {newRoot}", 5);
        return await Task.Run(() =>
        {
            long moved = 0, skipped = 0;
            var pairs = new (string oldDir, string target)[]
            {
                (oldDownloads, StoragePaths.DownloadsDir),
                (oldSmapi, StoragePaths.SmapiInstallerDir),
                (oldBackup, StoragePaths.ModsBackupDir),
            };
            var movedNotes = new List<string>();
            foreach (var (oldDir, target) in pairs)
            {
                if (string.Equals(Path.GetFullPath(oldDir), Path.GetFullPath(target), StringComparison.OrdinalIgnoreCase)
                    || !Directory.Exists(oldDir))
                    continue;
                Directory.CreateDirectory(target);
                var m0 = moved; var s0 = skipped;
                MoveInto(oldDir, target, ref moved, ref skipped);
                if (moved + skipped > m0 + s0)
                {
                    movedNotes.Add($"{Path.GetFileName(oldDir)} → {Path.GetFileName(target)}");
                    // if the old directory is fully emptied, delete the empty shell too (kept if in-use files remain inside)
                    try
                    {
                        if (Directory.Exists(oldDir) && !Directory.EnumerateFileSystemEntries(oldDir).Any())
                            Directory.Delete(oldDir);
                    }
                    catch { }
                }
            }

            // WebView2 is in use by this process → record a pending migration; MainWindow moves it automatically on next startup (before WebView2 initialization)
            var wv2Note = "";
            if (!string.Equals(Path.GetFullPath(oldWv2), Path.GetFullPath(StoragePaths.WebView2Dir), StringComparison.OrdinalIgnoreCase)
                && Directory.Exists(oldWv2))
            {
                cfg.PendingWebView2MoveFrom = oldWv2;
                _cfg.Save(cfg);
                wv2Note = "; WebView2 data will migrate automatically after the app restarts";
                _center.Report(task, "WebView2 data will migrate automatically after restart", 90);
            }

            var msg = newRoot is null
                ? "Restored the default location" + wv2Note
                : $"Migration complete: moved {ResumableDownload.FormatBytes(moved)}" +
                  (skipped > 0 ? $", {ResumableDownload.FormatBytes(skipped)} in use and left in place" : "") + wv2Note;
            _center.Finish(task, true, msg);
            AppLog.Warn("Storage", $"Cache directory migrated to {newRoot ?? "<default>"}: moved {moved}, skipped {skipped}");
            lock (_gate) _sizes.Clear();
            RefreshAll(force: true);
            return msg;
        }).ConfigureAwait(false);
    }

    /// <summary>Moves everything in src into dstDir (tries Move per item; on failure copies + deletes the source; if that also fails, counts as skipped).</summary>
    private static void MoveInto(string src, string dstDir, ref long moved, ref long skipped)
    {
        try
        {
            if (!Directory.Exists(src)) return;
            Directory.CreateDirectory(dstDir);
            foreach (var srcDir in Directory.GetDirectories(src))
            {
                var dst = Path.Combine(dstDir, Path.GetFileName(srcDir));
                var len = DirSize(srcDir);
                if (TryMoveTree(srcDir, dst)) moved += len; else skipped += len;
            }
            foreach (var srcFile in Directory.GetFiles(src))
            {
                var dst = Path.Combine(dstDir, Path.GetFileName(srcFile));
                var len = new FileInfo(srcFile).Length;
                try
                {
                    File.Move(srcFile, dst, overwrite: true);
                    moved += len;
                }
                catch
                {
                    try
                    {
                        File.Copy(srcFile, dst, overwrite: true);
                        File.Delete(srcFile);
                        moved += len;
                    }
                    catch { skipped += len; }
                }
            }
        }
        catch (Exception ex) { AppLog.Warn("Storage", "Failed to migrate directory: " + ex.Message); }
    }

    /// <summary>Moves a whole tree: direct Move on the same volume; across volumes / when in use, copies file by file and deletes the source — an in-use file counts as failure (whole tree stays put).
    /// Used by MainWindow at startup to perform the pending WebView2 data migration.</summary>
    public static bool TryMoveTree(string srcDir, string dstDir)
    {
        try
        {
            Directory.Move(srcDir, dstDir);
            return true;
        }
        catch
        {
            try
            {
                CopyTree(srcDir, dstDir);
                Directory.Delete(srcDir, recursive: true);
                return true;
            }
            catch
            {
                try { if (Directory.Exists(dstDir)) Directory.Delete(dstDir, recursive: true); } catch { }
                return false;
            }
        }
    }

    private static void CopyTree(string src, string dst)
    {
        Directory.CreateDirectory(dst);
        foreach (var f in Directory.GetFiles(src))
            File.Copy(f, Path.Combine(dst, Path.GetFileName(f)), overwrite: true);
        foreach (var d in Directory.GetDirectories(src))
            CopyTree(d, Path.Combine(dst, Path.GetFileName(d)));
    }
}
