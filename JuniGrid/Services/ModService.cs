using System.IO;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;

namespace JuniGrid.Services;

/// <summary>
/// Scans the Mods/ folder, parses each mod's manifest.json (including
/// Nexus UpdateKeys so mods can be update-checked), and performs
/// install / update / enable / disable / uninstall operations.
/// </summary>
public sealed class ModService
{
    // ------------------------------------------------------------------
    // Scan
    // ------------------------------------------------------------------
    /// <summary>v1.1.4: raw scan — only enumerates the entries on disk, no dedup.
    /// Physical duplicate cleanup must be based on this raw result (Scan's UID dedup
    /// "hides" loose duplicates, after which cleanup could never see them).</summary>
    private List<ModEntry> ScanRaw(string gamePath)
    {
        if (string.IsNullOrWhiteSpace(gamePath)) return new List<ModEntry>();
        var modsDir = Path.Combine(gamePath, "Mods");
        if (!Directory.Exists(modsDir)) return new List<ModEntry>();

        // v1.09: .junigrid_trash became a persistent recycle bin — mods deleted with "move to mod
        // recycle bin" stay here awaiting manual restore/cleanup; scanning no longer empties it automatically (it only skips it).
        // Cleanup is consolidated under Settings → Storage → game recycle bin.

        // v0.72.6: materialize the directory list first — during batch enable/disable a directory can be renamed (X ↔ .X)
        // mid lazy-enumeration; the enumerator throws DirectoryNotFoundException that blows up the whole Rescan (one root cause of the 2026-08-29 error wall)
        List<string> dirs;
        try { dirs = Directory.EnumerateDirectories(modsDir).ToList(); }
        catch (DirectoryNotFoundException) { return new List<ModEntry>(); }

        var results = new List<ModEntry>();
        foreach (var dir in dirs)
        {
            // v0.72.6: a single directory being renamed/deleted at scan time is a legitimate race — tolerate it locally and skip the item;
            // never let it abort the whole scan; IO/permission exceptions are logged separately without changing the scan-result semantics (no blanket swallowing)
            try
            {
                // v0.52.0: the recycle bin directory does not take part in scanning
                if (string.Equals(Path.GetFileName(dir), ".junigrid_trash", StringComparison.OrdinalIgnoreCase))
                    continue;
                // v1.1.4: skip fully empty directories outright — the empty top-level shell left after a
                // bundle's subpackages are deleted produces no orphan entries and stays out of the list (Uninstall already removes the shell; this covers manual deletion and other sources)
                bool isEmpty;
                try { isEmpty = !Directory.EnumerateFileSystemEntries(dir).Any(); }
                catch { isEmpty = false; }
                if (isEmpty) continue;
                // Folders starting with "." are "disabled marker directories" (created by renaming to .X on disable).
                // They must not be skipped: include them and mark them Disabled so the UI can show "disabled" and re-enable them.
                // (v0.42.0 used continue to skip them, which made disabled mods vanish from the list and become impossible to re-enable — reverted)
                var manifest = Path.Combine(dir, "manifest.json");
                if (!File.Exists(manifest))
                {
                    var nested = Directory
                        .EnumerateFiles(dir, "manifest.json", SearchOption.AllDirectories)
                        .OrderBy(f => f.Length)
                        .ToList();
                    // One folder may contain several sub-mods (very common with Content Pack splits),
                    // so each nested manifest is taken in as its own mod
                    if (nested.Count == 0)
                    {
                        // not even one manifest → fall back to the folder name for display so the mod doesn't vanish entirely
                        results.Add(OrphanEntry(modsDir, dir));
                        continue;
                    }
                    foreach (var nm in nested)
                    {
                        var e = BuildModEntry(modsDir, dir, nm);
                        if (e is not null) { results.Add(e); continue; }
                        // v1.06.4: a manifest that exists but is an empty file / fails to parse must also be taken in via the fallback.
                        // Dropping it used to make the whole package invisible in the list: "Disable all" couldn't reach it (no dot
                        // is added to the folder), yet SMAPI still scanned it and the log filled with Skipped mods (the root cause for East Scarp
                        // REMASTERED and three other large packages whose manifests were 0 bytes).
                        results.Add(OrphanEntry(modsDir, Path.GetDirectoryName(nm)!,
                            "⚠ manifest.json is empty or unparseable (reinstall this mod)"));
                    }
                    continue;
                }

                var folderName = Path.GetFileName(dir);
                var entry = BuildModEntry(modsDir, dir, manifest, folderName);
                if (entry is not null)
                {
                    results.Add(entry);
                }
                else
                {
                    // manifest empty / unparseable → fall back to the folder and mark it unrecognized; don't let the mod disappear
                    results.Add(OrphanEntry(modsDir, dir));
                }
                    }
            catch (DirectoryNotFoundException) { continue; }
            catch (IOException ioe) { AppLog.Warn("Mods", "Scan skipped (IO): " + Path.GetFileName(dir) + " - " + ioe.Message); continue; }
            catch (UnauthorizedAccessException) { AppLog.Warn("Mods", "Scan skipped (no permission): " + Path.GetFileName(dir)); continue; }
        }
        return results;
    }

    public IReadOnlyList<ModEntry> Scan(string gamePath)
    {
        var results = ScanRaw(gamePath);
        // v1.1.5: if the recycle bin already exists, apply the hidden attribute and runtime protection lock during the scan (when absent, don't
        // create it proactively — no bin means nothing to protect; EnsureTrashReady creates it on the first uninstall/stage).
        try
        {
            var trash = StoragePaths.GameTrashDir(gamePath);
            if (Directory.Exists(trash)) ProtectTrash(trash);
        }
        catch { }
        // v1.08: UniqueID dedup — when a mod's disabled copy (.X) and enabled copy (X) coexist,
        // show only one (typical case: the old copy was disabled, then a new copy was re-downloaded/reinstalled). Rule: prefer
        // the enabled copy; if both are enabled or both disabled, keep the higher version. The hidden copy stays untouched on disk; no files are deleted.
        var byUid = new Dictionary<string, ModEntry>(StringComparer.OrdinalIgnoreCase);
        var ordered = new List<ModEntry>();
        foreach (var e in results)
        {
            var uid = e.UniqueID?.Trim();
            if (string.IsNullOrWhiteSpace(uid)) { ordered.Add(e); continue; }
            if (byUid.TryGetValue(uid, out var prev))
            {
                ModEntry keep = prev, drop = e;
                var prevBetter = !prev.Disabled && e.Disabled;
                var dropBetter = prev.Disabled && !e.Disabled;
                if (dropBetter) { keep = e; drop = prev; }
                else if (!prevBetter && !dropBetter)
                {
                    var vp = Version.TryParse((prev.Version ?? "").TrimStart('v', 'V'), out var a) ? a : null;
                    var ve = Version.TryParse((e.Version ?? "").TrimStart('v', 'V'), out var b) ? b : null;
                    if (ve is not null && (vp is null || ve > vp)) { keep = e; drop = prev; }
                }
                // v1.1.4: rewritten dedup — the old ordered.Remove(drop)+Add(keep) had two bugs:
                // when keep==prev it re-Added a prev already in the list (source of the @key crash wall); in the positional-replace
                // branch, Remove running first made IndexOf(prev) return -1 (out-of-range crash in the three-copies swap scenario).
                // Semantics now: the loser never enters the list; if the winner is the new entry, it replaces the old one in place.
                if (ReferenceEquals(keep, e))
                {
                    var idx = ordered.IndexOf(prev);
                    if (idx >= 0) ordered[idx] = e;
                    else ordered.Add(e);
                }
                // keep==prev: e simply doesn't enter the list; the list stays unchanged
                byUid[uid] = keep;   // the anchor advances to the current winner; later copies compare against it
                AppLog.Warn("Mods", $"[dedup] UniqueID {uid} has multiple copies: showing {keep.Folder}, hiding {drop.Folder}");
            }
            else
            {
                byUid[uid] = e;
                ordered.Add(e);
            }
        }
        // v1.1.2: Folder-based dedup fallback — entries with an empty UniqueID (Content Pack subpackages,
        // OrphanEntry from manifest parse failure) don't take part in the UID dedup above and can produce
        // two rows for the same relative path in race/reinstall scenarios. The list UI keys on Folder; duplicates would crash the whole page render.
        var seenFolders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var deduped = new List<ModEntry>(ordered.Count);
        foreach (var e in ordered)
        {
            if (seenFolders.Add(e.Folder)) deduped.Add(e);
            else AppLog.Warn("Mods", $"[dedup] Folder {e.Folder} duplicated; hid the extra entry");
        }
        return deduped;
    }

    /// <summary>
    /// v1.1.5: physical cleanup of multiple copies sharing a UniqueID — subpackage-level semantics (aligned with SMAPI: SMAPI only skips
    /// duplicate subpackages; it never carries off a whole bundle). Must be based on the raw scan (ScanRaw): Scan's UID dedup
    /// hides loose duplicates from the list one step earlier.
    /// Rules (settled jointly with fable-5.1):
    ///   1.1 only enabled entries are processed (SMAPI already skips .X disabled copies; no conflict);
    ///   1.2 when one UID spans multiple top-level folders, the top level with the most subpackages wins; in a losing top level,
    ///       **only the subpackage directories matching the UID** move to the recycle bin (hierarchy preserved); unique subpackages are never touched;
    ///   1.3 same-UID duplicate subpackages inside the winner stay put (SMAPI reports the conflict itself; the user decides);
    ///   1.4 tie-break: subpackage count → main manifest version (top-level root manifest, else the first subpackage in
    ///       lexicographic order; unparseable counts as oldest) → top-level name alphabetical;
    ///   1.5 disabled entries don't join the grouping, so they are never touched.
    /// The old "move the losing top level wholesale" rule carried off entire bundles that shared any common subpackage UID
    /// with the winner (including unique subpackages) — verified real-world data loss when bundles share common libraries/content packs.
    /// Returns the number of directories moved to the recycle bin. The caller must rescan afterwards.
    /// </summary>
    public int CleanupDuplicateCopies(string gamePath)
    {
        if (string.IsNullOrWhiteSpace(gamePath)) return 0;
        // v1.1.5: also sweep orphan .tmp files — temp files from atomic manifest writes linger
        // when the process is hard-killed (finally never gets to run). Legit mods never carry .tmp files, and the 24-hour
        // threshold guarantees we never delete a temp file that is mid-write.
        try
        {
            var tmpCutoff = DateTime.UtcNow.AddHours(-24);
            foreach (var tmp in Directory.EnumerateFiles(Path.Combine(gamePath, "Mods"), "*.tmp", SearchOption.AllDirectories))
                if (File.GetLastWriteTimeUtc(tmp) < tmpCutoff)
                { try { File.Delete(tmp); } catch { } }
        }
        catch { }
        var raw = ScanRaw(gamePath);
        var removed = 0;
        var groups = raw
            .Where(m => !m.Disabled && !string.IsNullOrWhiteSpace(m.UniqueID))
            .GroupBy(m => m.UniqueID.Trim(), StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Select(x => x.Folder.Split('/')[0])
                         .Distinct(StringComparer.OrdinalIgnoreCase).Count() > 1);
        foreach (var g in groups)
        {
            var tops = g.GroupBy(x => x.Folder.Split('/')[0], StringComparer.OrdinalIgnoreCase)
                        .OrderByDescending(t => t.Count())
                        .ThenByDescending(t => MainVersionOf(gamePath, t))
                        .ThenBy(t => t.Key, StringComparer.OrdinalIgnoreCase)
                        .ToList();
            if (tops.Count < 2) continue;
            var keep = tops[0].Key;
            foreach (var top in tops.Skip(1))
            {
                foreach (var loser in top)
                {
                    var dir = Path.Combine(gamePath, "Mods", loser.Folder.Replace('/', Path.DirectorySeparatorChar));
                    if (!Directory.Exists(dir)) continue;
                    try
                    {
                        var grave = loser.Folder.Contains('/')
                            ? StageSubpackageToTrash(gamePath, loser.Folder)
                            : StageExistingToTrash(gamePath, dir);
                        removed++;
                        AppLog.Warn("Mods", $"[dedup-cleanup] Subpackage {loser.Folder} duplicates a subpackage UID of top level {keep}; moved to the recycle bin: {Path.GetFileName(grave)}");
                    }
                    catch (Exception ex)
                    {
                        AppLog.Warn("Mods", $"[dedup-cleanup] Failed to move {loser.Folder} to the recycle bin (possibly in use); left as is: {ex.Message}");
                    }
                }
                // 2.2 only delete the shell once the losing top level is empty; partial leftovers (README/config/shared assets) are conservatively kept and logged
                TryRemoveEmptyTopShell(gamePath, top.Key);
                var topDir = Path.Combine(gamePath, "Mods", top.Key);
                if (Directory.Exists(topDir) && Directory.EnumerateFileSystemEntries(topDir).Any())
                    AppLog.Warn("Mods", $"[dedup-cleanup] Duplicate subpackages of top level {top.Key} cleaned; shell directory kept (contains "
                        + Directory.GetFileSystemEntries(topDir, "*", SearchOption.AllDirectories).Length
                        + " files/subdirectories) — keep or remove it yourself");
            }
        }
        return removed;
    }

    /// <summary>The "main manifest version" for the 1.4 tie-break: use the top-level root manifest.json when present; otherwise take
    /// the first subpackage entry's version in lexicographic order; return null when neither parses (treated as oldest when sorting).</summary>
    private static Version? MainVersionOf(string gamePath, IGrouping<string, ModEntry> top)
    {
        try
        {
            var rootMf = Path.Combine(gamePath, "Mods", top.Key, "manifest.json");
            if (File.Exists(rootMf))
            {
                var text = ReadManifestText(rootMf);
                using var doc = System.Text.Json.JsonDocument.Parse(CleanManifestJson(text));
                if (doc.RootElement.TryGetProperty("Version", out var v)
                    && v.ValueKind == System.Text.Json.JsonValueKind.String
                    && Version.TryParse((v.GetString() ?? "").TrimStart('v', 'V'), out var ver))
                    return ver;
                return null;
            }
            var firstFolder = top.Select(x => x.Folder)
                                 .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                                 .First();
            var entry = top.First(x => x.Folder == firstFolder);
            return Version.TryParse((entry.Version ?? "").TrimStart('v', 'V'), out var v2) ? v2 : null;
        }
        catch { return null; }
    }

    // ------------------------------------------------------------------
    // Enable / disable / uninstall
    // ------------------------------------------------------------------
    /// <summary>Disabling = prefixing the folder with a dot (SMAPI skips those).
    /// v1.1.5: subpackage-level enable/disable — "Top/Sub" only renames the last segment (Top/Sub ↔ Top/.Sub). SMAPI skips
    /// dot-prefixed directories at any level (same convention as a top-level .X), and Scan's Disabled check already includes
    /// Contains("/."). Standalone mods (no subpath) behave as before: the whole level is renamed, never split mid-path, never leaves an empty shell.</summary>
    public string? SetDisabled(string gamePath, string folderName, bool disabled)
    {
        try
        {
            var modsDir = Path.Combine(gamePath, "Mods");
            // v1.1.5: split parent directory and last segment — subpackages ("Top/Sub") rename only the last segment, standalone mods rename the whole level
            var parts = folderName.Replace('\\', '/').Split('/');
            var name = parts[^1];
            var parent = parts.Length == 1
                ? modsDir
                : Path.Combine(modsDir, string.Join(Path.DirectorySeparatorChar, parts, 0, parts.Length - 1));
            var src = Path.Combine(parent, name);
            if (!Directory.Exists(src) && !disabled && !name.StartsWith('.'))
            {
                // enable tolerance: the caller passed the old dot-less name, but on disk it is actually .X
                var alt = Path.Combine(parent, "." + name);
                if (Directory.Exists(alt)) { name = "." + name; src = alt; }
            }
            if (!Directory.Exists(src)) return "Mod folder not found";

            var targetName = disabled
                ? (name.StartsWith('.') ? name : "." + name)
                : name.TrimStart('.');

            if (targetName == name) return null;

            var dest = Path.Combine(parent, targetName);
            if (Directory.Exists(dest))
            {
                // v1.08: target already exists = a duplicate copy of the same mod (e.g. a disabled .X alongside a newly installed X).
                // Move the old duplicate into .junigrid_trash (not truly deleted, recoverable), then finish the rename.
                // v1.1.5: subpackage paths keep the bundle hierarchy (consistent with Uninstall / subpackage-level cleanup).
                string grave;
                if (parts.Length > 1)
                {
                    var rel = string.Join('/',
                        parts.Take(parts.Length - 1).Concat(new[] { targetName.TrimStart('.') }));
                    grave = StageSubpackageToTrash(gamePath, rel);
                }
                else
                {
                    var trash = EnsureTrashReady(gamePath);
                    grave = Path.Combine(trash,
                        targetName.TrimStart('.') + "-" + DateTime.Now.ToString("yyyyMMdd_HHmmss"));
                    Directory.Move(dest, grave);
                }
                AppLog.Warn("Mods", $"[dedup-cleanup] Duplicate copy {targetName} moved to the recycle bin ({Path.GetFileName(grave)})");
            }
            // v0.44.0: src and dest are both inside the same Mods directory, so Directory.Move is a pure metadata
            // rename (instant, no content copy). The old MoveDirectorySafe fell back to "copy + delete source" on locks,
            // moving hundreds of MB for big mods — the root cause of batch enable/disable being extremely slow. Switched to the instant rename; on a lock Windows
            // errors out and the caller reports it.
            // v1.1.4: auto-retry transient locks — the list row's cover image is loaded by WebView2 straight from the mod
            // folder, and its handle may not be released yet at the instant enable/disable is clicked, so the rename can sporadically throw IOException (the root
            // cause of multi-subpackage Downtown-Zuzu-main reporting "in use" on enable/disable). Such locks last milliseconds; a short retry clears them.
            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    Directory.Move(src, dest);
                    return null;
                }
                catch (IOException) when (attempt < 4)
                {
                    Thread.Sleep(150 * attempt);
                }
                catch (UnauthorizedAccessException) when (attempt < 4)
                {
                    Thread.Sleep(150 * attempt);
                }
            }
        }
        catch (Exception ex)
        {
            // v0.51.0: show a friendly message when the file is in use instead of the raw exception text
            if (ex is IOException or UnauthorizedAccessException)
                return $"\"{folderName}\" is in use; close the related program before {(disabled ? "disabling" : "enabling")} it";
            return ex.Message;
        }
    }

    /// <param name="toTrash">true = move into the persistent Mods/.junigrid_trash recycle bin (restorable manually);
    /// false = the old "atomic" flow: stage into the bin to verify deletability, then delete for good.</param>
    public string? Uninstall(string gamePath, string folderName, bool toTrash = false)
    {
        try
        {
            var dir = Path.Combine(gamePath, "Mods", folderName);
            if (!Directory.Exists(dir)) return "Mod folder not found";
            var trash = EnsureTrashReady(gamePath);
            var baseName = folderName.Replace('/', '_');
            string staging;
            if (toTrash)
            {
                // v1.09: recycle bin entries carry a timestamp — delete "1", install "1", delete again, and both copies survive without overwriting each other;
                // for a same-second name collision (theoretically possible when batch-deleting same-named mods), append a sequence number as a fallback
                var stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
                // v1.1.4: bundle subpackages keep their original hierarchy in the recycle bin — what is
                // "Downtown-Zuzu-main/[BL] X" in Mods is also Downtown-Zuzu-main/[BL] X in the bin;
                // subpackages of one bundle cluster under the same top-level folder, so manual restore = drag the top-level folder back into Mods
                if (folderName.Contains('/'))
                {
                    staging = Path.Combine(trash, folderName.Replace('/', Path.DirectorySeparatorChar));
                    if (Directory.Exists(staging))
                        staging = staging + "_" + stamp;
                    for (var n = 2; Directory.Exists(staging); n++)
                        staging = staging + "_" + n;
                    Directory.CreateDirectory(Path.GetDirectoryName(staging)!);
                }
                else
                {
                    staging = Path.Combine(trash, baseName + "_" + stamp);
                    for (var n = 2; Directory.Exists(staging); n++)
                        staging = Path.Combine(trash, $"{baseName}_{stamp}_{n}");
                }
            }
            else
            {
                staging = Path.Combine(trash, baseName + "_" + Guid.NewGuid().ToString("N")[..8]);
            }
            try
            {
                Directory.Move(dir, staging);   // same-volume instant rename; a lock throws right here
            }
            catch (Exception ex)
            {
                // locked → restore (move staging back if it was already partially moved)
                if (Directory.Exists(staging) && !Directory.Exists(dir))
                    try { Directory.Move(staging, dir); } catch { }
                if (ex is IOException or UnauthorizedAccessException)
                    return $"\"{folderName}\" is in use; close the related program before deleting it";
                return ex.Message;
            }
            if (!toTrash)
            {
                // move succeeded → delete from the bin for good
                try { Directory.Delete(staging, recursive: true); }
                catch (Exception ex) { AppLog.Warn("ModService", "Recycle bin cleanup failed: " + ex.Message); }
                // v1.1.5: no longer delete the empty .junigrid_trash directory itself — v0.52.0 removed the empty shell
                // to avoid a visible empty directory in Explorer, but the bin now carries the Hidden|System attributes
                // (EnsureTrashReady) so it is invisible by default, and the runtime protection handle would make deletion fail anyway.
            }
            // v1.1.4: after a multi-subpackage bundle is emptied, remove the top-level shell — the uninstalled path is a subpackage (e.g.
            // "Downtown-Zuzu-main/[CC] X"); if the top-level folder is empty afterwards, clear the shell too,
            // otherwise an orphan manifest-less empty directory stays in Mods (SMAPI would also report it as unloadable)
            if (folderName.Contains('/'))
                TryRemoveEmptyTopShell(gamePath, folderName.Split('/')[0]);
            return null;
        }
        catch (Exception ex)
        {
            // v0.47.0: a human-readable message when the file is in use (e.g. Stardrop.exe running)
            if (ex is UnauthorizedAccessException or IOException)
                return $"\"{folderName}\" is in use; close the related program before deleting it";
            return ex.Message;
        }
    }

    /// <summary>v1.1.4: clean up the top-level shell after a subpackage uninstall. Delete only when the top-level folder has no
    /// files/subdirectories left (truly empty); on failure (locked) give up silently and try again next time.</summary>
    private static void TryRemoveEmptyTopShell(string gamePath, string top)
    {
        try
        {
            var topDir = Path.Combine(gamePath, "Mods", top);
            if (!Directory.Exists(topDir)) return;
            if (Directory.EnumerateFileSystemEntries(topDir).Any()) return;
            Directory.Delete(topDir);
            AppLog.Warn("Mods", $"[shell-cleanup] Bundle subpackages fully deleted; also removed the empty top-level directory {top}");
        }
        catch (Exception ex)
        {
            AppLog.Warn("Mods", $"[shell-cleanup] Failed to clean {top} (harmless; will retry next time): {ex.Message}");
        }
    }

    /// <summary>Whether the game process (SMAPI/the game itself) is alive — a static version sharing the same criteria as LauncherService.AnyProcess.
    /// v1.1.6: each Process returned by GetProcessesByName holds a handle and is released right after use (this property is called repeatedly by background
    /// work such as remark syncing; not releasing them accumulates finalizer pressure).</summary>
    private static bool IsGameProcessAlive
    {
        get
        {
            foreach (var name in new[] { "StardewModdingAPI", "Stardew Valley" })
                foreach (var p in System.Diagnostics.Process.GetProcessesByName(name))
                    using (p) return true;
            return false;
        }
    }

    // ------------------------------------------------------------------
    // v1.1.2: remarks are synced into manifest.json — GMCM in game reads the mod title
    // from the manifest's Name field; setting it to the remark shows the custom name in game.
    // The original name is stored in config ModOriginalNames first and restored when the remark is cleared. Only Name is touched;
    // UniqueID and all other fields are never modified (update checks and dependency resolution rely on UniqueID; unaffected).
    // ------------------------------------------------------------------

    /// <summary>Writes the remark into (or removes it from) the given mod's manifest.json. Empty remark = restore the original name.
    /// Returns null on success, otherwise an error description. skipAliveCheck: for batch sync the caller has already checked aliveness once
    /// outside the loop (per-mod checks = two full system process enumerations per mod).</summary>
    public string? ApplyRemarkToManifest(string gamePath, string folder, string remark,
        Dictionary<string, string> originalNames, bool skipAliveCheck = false)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(gamePath) || string.IsNullOrWhiteSpace(folder))
                return "Invalid path";
            var manifestPath = Path.Combine(gamePath, "Mods", folder.Replace('/', '\\'), "manifest.json");
            if (!File.Exists(manifestPath)) return "manifest.json not found";
            if (!skipAliveCheck && IsGameProcessAlive) return "The game is running; the manifest cannot be modified right now";

            // lenient Newtonsoft parse (SMAPI manifests allow trailing commas/comments), then write back normalized
            var text = ReadManifestText(manifestPath);
            var root = Newtonsoft.Json.Linq.JObject.Parse(CleanManifestJson(text));
            var currentName = root["Name"]?.Type == Newtonsoft.Json.Linq.JTokenType.String
                ? (string?)root["Name"] : null;
            if (string.IsNullOrEmpty(currentName)) return "Manifest is missing the Name field";

            if (string.IsNullOrWhiteSpace(remark))
            {
                // restore the original name
                if (!originalNames.TryGetValue(folder, out var original)) return null;   // never changed; nothing to restore
                root["Name"] = original;
                originalNames.Remove(folder);
            }
            else
            {
                // record the original name (only on first change; after a mod update resets the manifest, the stored value still wins)
                if (!originalNames.TryGetValue(folder, out var original))
                    originalNames[folder] = currentName!;
                // v1.1.4: actually write the remark into Name — previously only the original name was recorded
                // without assigning, so the original content was written back and the manifest never changed (root cause of GMCM always showing the original name)
                root["Name"] = remark;
            }

            WriteManifestAtomic(manifestPath, root.ToString(Newtonsoft.Json.Formatting.Indented));
            return null;
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }

    /// <summary>Batch reconciliation after a scan: for every mod with a remark, if the manifest's Name doesn't match the remark, rewrite it
    /// (covers manifests reset by a mod update). Runs silently; failures are only logged.</summary>
    public void SyncAllRemarks(string gamePath, ConfigService cfg)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(gamePath) || IsGameProcessAlive) return;
            var changed = false;
            foreach (var kv in cfg.Current.ModRemarks)
            {
                var folder = kv.Key;
                if (folder.Contains("/.")) continue;   // skip disabled mods
                var manifestPath = Path.Combine(gamePath, "Mods", folder.Replace('/', '\\'), "manifest.json");
                if (!File.Exists(manifestPath)) continue;
                try
                {
                    var root = Newtonsoft.Json.Linq.JObject.Parse(
                        CleanManifestJson(ReadManifestText(manifestPath)));
                    var name = root["Name"]?.Type == Newtonsoft.Json.Linq.JTokenType.String
                        ? (string?)root["Name"] : null;
                    if (name == kv.Value)
                    {
                        // Self-heal: the manifest already carries the remark name but the original-name record was lost (old versions kept it only in memory
                        // and never wrote config back before exit), so the original name in the list's parentheses disappeared and clearing the remark could not restore it.
                        // The true name is unrecoverable; fall back to recording the folder name (mods like Automate, where folder == original name, are fully restored).
                        if (!cfg.Current.ModOriginalNames.ContainsKey(folder))
                        {
                            cfg.Current.ModOriginalNames[folder] = folder.Split('/').Last();
                            changed = true;
                        }
                        continue;   // already in sync
                    }
                    var err = ApplyRemarkToManifest(gamePath, folder, kv.Value, cfg.Current.ModOriginalNames,
                        skipAliveCheck: true);   // v1.1.6: aliveness was already checked outside the loop; don't enumerate processes per mod
                    if (err is not null) AppLog.Warn("Mods", $"[remark-sync] {folder}: {err}");
                    else changed = true;   // ApplyRemarkToManifest modified ModOriginalNames (in memory); persist that too
                }
                catch (Exception ex) { AppLog.Warn("Mods", $"[remark-sync] {folder}: {ex.Message}"); }
            }
            if (changed) cfg.Save(cfg.Current);
        }
        catch { }
    }

    // ------------------------------------------------------------------
    // v1.1.4 regression fix (BUG-2): concurrency and atomicity of manifest reads/writes.
    // While Scan's background ReadAllText (FileShare.Read) held the file, the remark writer's
    // WriteAllText request for write access was denied outright → IOException (root cause of the E10 reproduction);
    // WriteAllText also truncates and overwrites in place, so a process kill mid-write = corrupted manifest (E11).
    // Fix is triple-guarded: lenient shared lock on reads + temp-file atomic replace on writes + short retries.
    // ------------------------------------------------------------------

    /// <summary>Reads the manifest under a lenient shared lock: concurrent rename/replace is allowed, so Scan no longer collides with remark writes.</summary>
    private static string ReadManifestText(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        using var sr = new StreamReader(fs, System.Text.Encoding.UTF8);
        return sr.ReadToEnd();
    }

    /// <summary>Writes the manifest atomically: temp file first, then a File.Replace-style swap (with 4 short retries);
    /// at any instant the manifest on disk is either the old or the new one, never half-written.</summary>
    private static void WriteManifestAtomic(string path, string content)
    {
        var tempPath = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllText(tempPath, content, new System.Text.UTF8Encoding(false));
            for (var i = 0; i < 4; i++)
            {
                try
                {
                    File.Move(tempPath, path, overwrite: true);
                    return;
                }
                // v1.1.5: retry both IOException and UnauthorizedAccessException — antivirus briefly
                // memory-mapping the manifest makes Move throw UAE ("Access denied"), a transient state;
                // a genuinely read-only target still throws after 4 retries, keeping E9 semantics unchanged (about 300ms extra).
                catch (Exception ex) when (i < 3 && ex is IOException or UnauthorizedAccessException)
                {
                    Thread.Sleep(50 * (i + 1));
                }
            }
        }
        finally
        {
            // clean up the temp file when the replace failed; leave no garbage
            if (File.Exists(tempPath))
                try { File.Delete(tempPath); } catch { }
        }
    }

    /// <summary>Strips the trailing commas and inline // comments SMAPI manifests allow, so JObject.Parse doesn't blow up.</summary>
    private static string CleanManifestJson(string raw)    {
        // same lenient-parse preprocessing as Scan: a minimal implementation removing // comments and trailing commas
        var sb = new System.Text.StringBuilder(raw.Length);
        var inStr = false;
        for (var i = 0; i < raw.Length; i++)
        {
            var ch = raw[i];
            if (inStr)
            {
                sb.Append(ch);
                if (ch == '\\' && i + 1 < raw.Length) { sb.Append(raw[++i]); continue; }
                if (ch == '"') inStr = false;
                continue;
            }
            if (ch == '"') { inStr = true; sb.Append(ch); continue; }
            if (ch == '/' && i + 1 < raw.Length && raw[i + 1] == '/')
            {
                while (i < raw.Length && raw[i] != '\n') i++;
                if (i < raw.Length) sb.Append('\n');
                continue;
            }
            if (ch == ',')
            {
                var j = i + 1;
                while (j < raw.Length && (raw[j] == ' ' || raw[j] == '\t' || raw[j] == '\r' || raw[j] == '\n')) j++;
                if (j < raw.Length && (raw[j] == '}' || raw[j] == ']')) continue;   // skip trailing comma
            }
            sb.Append(ch);
        }
        return sb.ToString();
    }

    // ------------------------------------------------------------------
    // Install / update from zips
    // ------------------------------------------------------------------
    /// <summary>
    /// Installs a downloaded mod UPDATE zip, replacing the existing folder.
    /// The target keeps its old name (so a ".Disabled" prefix survives).
    /// </summary>
    public string? InstallUpdate(string gamePath, string targetFolderName, string zipPath,
        out string? newVersion, string? expectedUniqueId = null)
    {
        newVersion = null;
        string? temp = null;
        try
        {
            var manifest = ExtractToTemp(zipPath, "mod-update-", out temp);
            if (manifest is null) return "manifest.json not found in the archive";
            var modRoot = ResolveModRoot(temp, out var allManifests);

            // Safety lock: when several mods share one GitHub repo, the latest release may belong to a different mod.
            // If the UniqueID doesn't match, abort — never overwrite with a wrong install.
            // v1.1.4: multi-subpackage bundles — a UID match on any subpackage counts as the correct update package
            // (Downtown Zuzu ships 7 subpackages in one release; the expected UID is just one of them).
            if (expectedUniqueId is not null)
            {
                var found = false;
                foreach (var mf in allManifests)
                {
                    try
                    {
                        using var check = JsonDocument.Parse(ReadManifestText(mf));
                        var uid = check.RootElement.TryGetProperty("UniqueID", out var u)
                            ? u.GetString() : null;
                        if (string.Equals(uid, expectedUniqueId, StringComparison.OrdinalIgnoreCase))
                        { found = true; manifest = mf; break; }
                    }
                    catch { }
                }
                if (!found)
                    return "The downloaded package is not this mod (the release repo contains multiple mods); install aborted to prevent a wrong install";
            }

            try
            {
                using var doc = JsonDocument.Parse(ReadManifestText(manifest));
                if (doc.RootElement.TryGetProperty("Version", out var v))
                    newVersion = v.GetString();
            }
            catch (Exception __ex) { AppLog.Warn("ModService", __ex.Message); }

            var dest = Path.Combine(gamePath, "Mods", targetFolderName);
            // v1.1.1: the old version is no longer deleted outright — first rename it wholesale into .junigrid_trash (same-volume atomic rename,
            // never a "half delete"), then install the new version; on install failure it is moved back into Mods. The old version is never lost, whatever interrupts.
            string? stagedOld = null;
            if (Directory.Exists(dest))
            {
                try { stagedOld = StageExistingToTrash(gamePath, dest); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                { return $"\"{targetFolderName}\" is in use; could not back up the old version, update aborted (old version untouched)"; }
            }
            try
            {
                MoveDirectorySafe(modRoot, dest);   // cross-volume protection
            }
            catch
            {
                // remove the possibly incomplete new half-install, then move the old version back; if that fails it stays in the recycle bin (restorable manually)
                try { if (Directory.Exists(dest)) Directory.Delete(dest, recursive: true); } catch { }
                if (stagedOld is not null) RestoreStaged(stagedOld, dest);
                throw;
            }

            TryDelete(temp);
            if (stagedOld is not null)
                AppLog.Warn("Mods", $"[update] Old version kept in the recycle bin: {Path.GetFileName(stagedOld)} (can be restored or cleaned in Settings)");
            return null;
        }
        catch (Exception ex)
        {
            if (temp is not null) TryDelete(temp);
            return ex.Message;
        }
    }

    /// <summary>
    /// Installs a BRAND-NEW mod zip into Mods/. Folder name comes from the
    /// zip's inner folder, or the manifest Name when files sit at zip root.
    /// A name collision replaces the old folder (acts as an update).
    /// <para>v1.2.0: when requireUniqueId is non-null, the zip must contain a manifest whose UniqueID matches it (case-insensitive)
    /// before installing — this prevents "installing the wrong mod" for candidates found by name in one-click dependency installs.
    /// On mismatch the <see cref="UidMismatchError"/> sentinel is returned and the caller tries the next candidate.</para>
    /// </summary>
    public const string UidMismatchError = "uniqueid-mismatch";

    public string? InstallNew(string gamePath, string zipPath, out string? modName, int? nexusModId = null,
        string? requireUniqueId = null)
    {
        modName = null;
        string? temp = null;
        try
        {
            var manifest = ExtractToTemp(zipPath, "mod-install-", out temp);
            if (manifest is null)
            {
                // Archives without manifest.json are not standalone mods (often translation patches / overwrite-style file packs);
                // auto-installing would mix an "orphan folder" into the list with no way to identify version/dependencies.
                // Prompt for manual download instead and let the user decide how to handle it.
                TryDelete(temp);
                modName = null;
                return "This archive has no manifest.json, so it is not a complete standalone mod (possibly a translation patch or overwrite pack). Use Manual download instead and place the files yourself.";
            }
            var modRoot = ResolveModRoot(temp, out var allManifests);
            var isBundle = allManifests.Length > 1;

            // v1.2.0: dependency direct-install guard — the candidate package must actually provide the expected UniqueID (a hit on any
            // subpackage of a bundle suffices, same semantics as InstallUpdate's expectedUniqueId).
            if (requireUniqueId is not null && !ManifestsContainUid(allManifests, requireUniqueId))
            {
                TryDelete(temp);
                modName = null;
                return UidMismatchError;
            }

            // v1.1.5: same policy as "reject when there is no manifest" — bad JSON / an empty file /
            // a non-object root is also rejected (otherwise an orphan entry SMAPI cannot load would be silently installed).
            // CleanManifestJson runs first, so SMAPI-style comments/trailing commas are unaffected.
            foreach (var mf in allManifests)
            {
                try
                {
                    using var checkDoc = JsonDocument.Parse(CleanManifestJson(ReadManifestText(mf)));
                    if (checkDoc.RootElement.ValueKind != JsonValueKind.Object)
                        throw new JsonException("root is not an object");
                }
                catch (Exception __checkEx)
                {
                    AppLog.Warn("ModService", "manifest verification failed: " + __checkEx.Message);
                    return "The manifest.json in the archive is corrupted or empty; not a complete mod — install aborted";
                }
            }

            try
            {
                using var doc = JsonDocument.Parse(ReadManifestText(manifest));
                if (doc.RootElement.TryGetProperty("Name", out var n))
                    modName = n.GetString();
            }
            catch (Exception __ex) { AppLog.Warn("ModService", __ex.Message); }
            if (isBundle)
            {
                // The bundle display name is the common part of all subpackage names. Subpackage names carry tag prefixes like "[BL] "/"[CC] ";
                // strip them before taking the common prefix (a raw per-character prefix forks right after '[' and yields an empty
                // string, degrading to the first subpackage name "[BL] Downtown Zuzu")
                var names = allManifests.Select(m => SafeManifestName(m))
                    .Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s!).ToList();
                var stripped = names.Select(n => StripPackTag(n).Trim()).ToList();
                var common = CommonPrefix(stripped).Trim().TrimEnd('-', '_').Trim();
                if (common.Length > 1)
                    modName = common;
                else
                {
                    // v1.1.5: when subpackage names have no common prefix (e.g. [CC] Buildings/Streets/Props),
                    // fall back to the zip's top-level directory name instead of a single tagged subpackage name.
                    // Don't fall back to the zip file name — on the Nexus direct-install path it is direct-{id}-{fileId}.
                    var topName = Path.GetFileName(modRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                    if (modRoot != temp && !string.IsNullOrWhiteSpace(topName))
                        modName = topName;
                }
            }

            var folderName = Path.GetFileName(modRoot);
            if (string.IsNullOrEmpty(folderName) || modRoot == temp)
                folderName = SanitizeFolderName(
                    (isBundle ? Path.GetFileNameWithoutExtension(zipPath) : null)
                    ?? modName ?? "NewMod");

            // v1.1.1: same-named old directories (both the enabled and disabled copies) are no longer deleted outright — move them into the recycle bin first and roll back on install failure
            var dest = Path.Combine(gamePath, "Mods", folderName);
            string? stagedDest = null, stagedDisabled = null;
            if (Directory.Exists(dest))
            {
                try { stagedDest = StageExistingToTrash(gamePath, dest); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                { TryDelete(temp); return $"\"{folderName}\" is in use; could not back up the old version, install aborted (old version untouched)"; }
            }
            // v0.71.9: the same-named [disabled] directory (.folderName) must be cleared too — the old logic only checked the dot-less dest,
            // so a disabled mod (Mods/.X) plus a fresh download (Mods/X) coexisted and scanned as two rows of the same mod.
            var destDisabled = Path.Combine(gamePath, "Mods", "." + folderName);
            if (Directory.Exists(destDisabled))
            {
                try { stagedDisabled = StageExistingToTrash(gamePath, destDisabled); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    if (stagedDest is not null) RestoreStaged(stagedDest, dest);
                    TryDelete(temp);
                    return $"\".{folderName}\" is in use; could not back up the old version, install aborted (old version untouched)";
                }
            }
            // v0.71.9: dedup fallback by manifest UniqueID — different folder names but the same UniqueID
            // (e.g. ABC / ABC-1.2 / .ABC) are the same mod; clean them up together, preventing duplicates of any form.
            // v1.1.4: bundles collect every subpackage's UID — loose subpackages left behind by the old installer's missed installs
            // (e.g. a standalone "[BL] Downtown Zuzu" under Mods) are swept into the recycle bin as well.
            try
            {
                var newUids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var mf in isBundle ? allManifests : new[] { manifest })
                {
                    try
                    {
                        using var doc = JsonDocument.Parse(ReadManifestText(mf));
                        if (doc.RootElement.TryGetProperty("UniqueID", out var u)
                            && !string.IsNullOrWhiteSpace(u.GetString()))
                            newUids.Add(u.GetString()!);
                    }
                    catch { }
                }
                if (newUids.Count > 0)
                {
                    // v1.1.5: subpackage-level sweep (different winner semantics from CleanupDuplicateCopies —
                    // the freshly installed zip is the winner; rule 2.3 requires the two determinations to be implemented separately, no shared function).
                    // Walks ScanRaw entries: naturally excludes .junigrid_trash (fixes "moving the recycle bin
                    // into itself") and naturally carries disabled state; old subpackage directories matching a UID move to the recycle bin
                    // (hierarchy preserved), while unique subpackages and unrelated mods are never touched; only when the matching directory is the top level itself
                    // (a standalone mod) does the whole level move. The old top-level enumerate + whole-level move carried off entire bundles that shared
                    // any common subpackage UID with the new bundle (verified in testing, T7).
                    foreach (var old in ScanRaw(gamePath))
                    {
                        if (old.Disabled || string.IsNullOrWhiteSpace(old.UniqueID)) continue;
                        if (!newUids.Contains(old.UniqueID.Trim())) continue;
                        // the install target itself (same-name conflicts were handled above); don't sweep it
                        if (string.Equals(old.Folder, folderName, StringComparison.OrdinalIgnoreCase)
                            || string.Equals(old.Folder, "." + folderName, StringComparison.OrdinalIgnoreCase))
                            continue;
                        var oldDir = Path.Combine(gamePath, "Mods", old.Folder.Replace('/', Path.DirectorySeparatorChar));
                        if (!Directory.Exists(oldDir)) continue;
                        try
                        {
                            var stagedDup = old.Folder.Contains('/')
                                ? StageSubpackageToTrash(gamePath, old.Folder)
                                : StageExistingToTrash(gamePath, oldDir);
                            AppLog.Warn("Mods", $"[dedup-cleanup] Old same-UniqueID subpackage copy {old.Folder} moved to the recycle bin: {Path.GetFileName(stagedDup)}");
                        }
                        catch (Exception stageEx)
                        {
                            AppLog.Warn("Mods", $"[dedup-cleanup] Failed to move {old.Folder} to the recycle bin (possibly in use); left as is: {stageEx.Message}");
                        }
                    }
                }
            }
            catch (Exception __ex) { AppLog.Warn("ModService", "UniqueID dedup cleanup failed: " + __ex.Message); }

            try
            {
                if (modRoot == temp)
                    CopyDirectoryContents(temp, dest);   // files at zip root — copy into named folder
                else
                    MoveDirectorySafe(modRoot, dest);   // cross-volume protection
            }
            catch
            {
                // v1.1.1: install failed → remove the incomplete half-install and move the old version back out of the recycle bin
                try { if (Directory.Exists(dest)) Directory.Delete(dest, recursive: true); } catch { }
                if (stagedDest is not null) RestoreStaged(stagedDest, dest);
                if (stagedDisabled is not null) RestoreStaged(stagedDisabled, destDisabled);
                throw;
            }

            TryDelete(temp);
            // v1.1.2: record the install source (Nexus modId) in a sidecar file at the install root. Manifests of packages like SVE leave
            // UpdateKeys as an invalid placeholder "Nexus:???", so the scan cannot get the ID from the manifest and
            // "Installed" recognition plus update checks break — the sidecar is the fallback; reinstalls/updates overwrite it with the folder.
            if (nexusModId is int nid)
                WriteNexusIdSidecar(dest, nid);
            return null;
        }
        catch (Exception ex)
        {
            if (temp is not null) TryDelete(temp);
            return ex.Message;
        }
    }

    // ---------------- Install-source sidecar (.junigrid.json) ----------------

    private const string SidecarFileName = ".junigrid.json";

    /// <summary>v1.1.2: when installing through this system the Nexus modId is known, so drop a sidecar into the install root as a fallback
    /// (the scan side's only link when the manifest's UpdateKeys are missing/invalid). A write failure only affects update
    /// recognition for that package and is not treated as an install failure.</summary>
    private static void WriteNexusIdSidecar(string destDir, int nexusModId)
    {
        try
        {
            var json = JsonSerializer.Serialize(
                new SidecarPayload { NexusModId = nexusModId },
                new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
            File.WriteAllText(Path.Combine(destDir, SidecarFileName), json);
        }
        catch (Exception ex)
        { AppLog.Warn("ModService", "Failed to write the install-source sidecar: " + ex.Message); }
    }

    /// <summary>Walks up from the manifest's directory to the Mods root and takes the nexusModId from the nearest sidecar
    /// (all subpackages of a bundle inherit the sidecar at the install root). Missing/corrupt → null.</summary>
    private static int? ReadNexusIdSidecar(string startDir, string modsDir)
    {
        try
        {
            var seps = new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar };
            var root = Path.GetFullPath(modsDir).TrimEnd(seps);
            var dir = Path.GetFullPath(startDir).TrimEnd(seps);
            while (true)
            {
                var f = Path.Combine(dir, SidecarFileName);
                if (File.Exists(f))
                {
                    using var doc = JsonDocument.Parse(File.ReadAllText(f));
                    if (doc.RootElement.ValueKind == JsonValueKind.Object
                        && doc.RootElement.TryGetProperty("nexusModId", out var v)
                        && v.TryGetInt32(out var id))
                        return id;
                }
                if (string.Equals(dir, root, StringComparison.OrdinalIgnoreCase)) return null;
                var parent = Directory.GetParent(dir)?.FullName;
                if (parent is null) return null;
                dir = parent.TrimEnd(seps);
                // walked past the Mods root without a hit → none (never read files outside Mods)
                if (!dir.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(dir, root, StringComparison.OrdinalIgnoreCase))
                    return null;
            }
        }
        catch
        {
            return null;   // sidecar corrupt/unreadable → treat as absent
        }
    }

    private sealed class SidecarPayload
    {
        public int NexusModId { get; set; }
    }

    // ------------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------------
    /// <summary>Extracts zip to a unique temp dir; returns the shallowest manifest.json path.</summary>
    private static string? ExtractToTemp(string zipPath, string prefix, out string tempDir)
    {
        tempDir = Path.Combine(StoragePaths.DownloadsDir, prefix + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        ZipFile.ExtractToDirectory(zipPath, tempDir);
        return Directory
            .GetFiles(tempDir, "manifest.json", SearchOption.AllDirectories)
            .OrderBy(p => p.Length)
            .FirstOrDefault();
    }

    /// <summary>
    /// v1.1.4: determine the install root inside the extracted directory. Most zips have only one
    /// manifest → install its containing folder; multi-subpackage bundles (one zip containing
    /// several manifest subpackages, such as Downtown Zuzu's
    /// [BL]/[CC]/[CP]/[DLL]/[FTM]/[MFM]/[TS]) would lose the rest if only the first manifest's
    /// subpackage were installed, since the remaining subpackages would stay in the temp directory
    /// and be deleted — the root cause of "only one [BL] got installed".
    /// Rule: when all manifests belong to the same top-level directory → install that top-level
    /// directory (as a whole); when they are scattered across multiple top-level directories →
    /// the whole extraction root is the install content.
    /// </summary>
    private static string ResolveModRoot(string temp, out string[] manifests)
    {
        manifests = Directory.GetFiles(temp, "manifest.json", SearchOption.AllDirectories);
        if (manifests.Length <= 1)
            return Path.GetDirectoryName(manifests.FirstOrDefault() ?? temp)!;

        var tops = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var mf in manifests)
            tops.Add(Path.GetRelativePath(temp, Path.GetDirectoryName(mf)!)
                .Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)[0]);
        return tops.Count == 1 ? Path.Combine(temp, tops.First()) : temp;
    }

    private static string? SafeManifestName(string manifestPath)
    {
        try
        {
            using var doc = JsonDocument.Parse(ReadManifestText(manifestPath));
            return doc.RootElement.TryGetProperty("Name", out var n) ? n.GetString() : null;
        }
        catch { return null; }
    }

    /// <summary>v1.2.0: whether any manifest in the set has a subpackage whose UniqueID matches the expected value (case-insensitive).</summary>
    private static bool ManifestsContainUid(string[] manifestPaths, string expectedUid)
    {
        foreach (var mf in manifestPaths)
        {
            try
            {
                using var doc = JsonDocument.Parse(CleanManifestJson(ReadManifestText(mf)));
                if (doc.RootElement.ValueKind == JsonValueKind.Object
                    && doc.RootElement.TryGetProperty("UniqueID", out var u)
                    && string.Equals(u.GetString(), expectedUid, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            catch { }
        }
        return false;
    }

    /// <summary>Strips a leading tag prefix from a subpackage name, e.g. "[BL] Downtown Zuzu" → "Downtown Zuzu".
    /// The SMAPI community commonly tags content pack types with "[XX] " ([CP]/[CC]/[DLL]…).</summary>
    private static string StripPackTag(string name)
    {
        var n = name.TrimStart();
        while (n.StartsWith("[") )
        {
            var close = n.IndexOf(']');
            if (close < 0) break;
            n = n[(close + 1)..].TrimStart();
        }
        return n;
    }

    /// <summary>Takes the longest common prefix (per character) of a set of names, used to name bundles for display.
    /// Only trims a partial trailing word when the prefix ends mid-name; identical names are returned as-is.</summary>
    private static string CommonPrefix(List<string> names)
    {
        if (names.Count == 0) return "";
        var prefix = names[0];
        foreach (var n in names.Skip(1))
        {
            var len = Math.Min(prefix.Length, n.Length);
            var i = 0;
            while (i < len && prefix[i] == n[i]) i++;
            prefix = prefix[..i];
            if (prefix.Length == 0) break;
        }
        // names fully identical (or the prefix happens to be some name's full length) → no tail trimming needed
        if (prefix.Length == 0 || prefix.Length >= names.Min(x => x.Length)) return prefix;
        // prefix ends mid-word (e.g. "Downtown Zu") → fall back to the nearest space/hyphen
        var cut = prefix.Length;
        while (cut > 0 && prefix[cut - 1] != ' ' && prefix[cut - 1] != '-') cut--;
        return prefix[..cut];
    }


    /// <summary>
    /// Cross-volume-safe directory move: Directory.Move only supports the same volume and throws
    /// "Source and destination path must have identical roots".
    /// When different drives are detected, this switches to "copy + delete source".
    /// </summary>
    private static void MoveDirectorySafe(string src, string dest)
    {
        try
        {
            Directory.Move(src, dest);
        }
        catch (IOException)
        {
            // cross-volume / dest directory exists / handle locked → use copy + delete source
            Directory.CreateDirectory(dest);
            CopyDirectoryContents(src, dest);
            try { Directory.Delete(src, recursive: true); } catch (Exception __ex) { AppLog.Warn("ModService", __ex.Message); }
        }
    }
    private static void CopyDirectoryContents(string src, string dest)
    {
        Directory.CreateDirectory(dest);
        foreach (var f in Directory.GetFiles(src))
            File.Copy(f, Path.Combine(dest, Path.GetFileName(f)), overwrite: true);
        foreach (var d in Directory.GetDirectories(src))
            CopyDirectoryContents(d, Path.Combine(dest, Path.GetFileName(d)));
    }

    /// <summary>
    /// v1.1.1: renames an existing old-version directory under Mods wholesale into .junigrid_trash (same-volume instant atomic rename;
    /// any failure fails the whole operation, never a "half delete"). Naming follows Uninstall's timestamp + sequence fallback rules.
    /// Returns the staging path; rename failures such as locks throw directly, and the caller decides to abort or roll back.
    /// </summary>
    private static string StageExistingToTrash(string gamePath, string existingDir)
    {
        var trash = EnsureTrashReady(gamePath);
        var baseName = Path.GetFileName(existingDir).TrimStart('.');
        var stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        var staging = Path.Combine(trash, baseName + "_" + stamp);
        for (var n = 2; Directory.Exists(staging); n++)
            staging = Path.Combine(trash, $"{baseName}_{stamp}_{n}");
        Directory.Move(existingDir, staging);
        return staging;
    }

    /// <summary>For subpackage-level cleanup: moves a subpackage directory (relative path such as "Top/Sub") into the recycle bin keeping
    /// the bundle hierarchy (trash/Top/Sub[_timestamp]), matching Uninstall's subpackage path convention —
    /// manual restore just drags the top-level folder back into Mods.</summary>
    private static string StageSubpackageToTrash(string gamePath, string folder)
    {
        var trash = EnsureTrashReady(gamePath);
        var stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        var staging = Path.Combine(trash, folder.Replace('/', Path.DirectorySeparatorChar));
        if (Directory.Exists(staging))
            staging = staging + "_" + stamp;
        for (var n = 2; Directory.Exists(staging); n++)
            staging = staging + "_" + n;
        Directory.CreateDirectory(Path.GetDirectoryName(staging)!);
        Directory.Move(Path.Combine(gamePath, "Mods", folder.Replace('/', Path.DirectorySeparatorChar)), staging);
        return staging;
    }

    // ------------------------------------------------------------------
    // v1.1.5: recycle bin protection — prevents users from accidentally renaming/deleting .junigrid_trash in Explorer.
    // M1 hidden attribute: Hidden|System, invisible in Explorer's default view (accidental touches near zero);
    // M2 runtime handle lock: holds a directory handle without FILE_SHARE_DELETE, so while the app is running Windows
    //    outright refuses external rename/delete of the trash itself; storing/restoring subdirectories inside the trash
    //    is unaffected (the handle locks only the directory itself). The OS releases it when the process exits.
    // Self-heals even if the trash is forcibly deleted: EnsureTrashReady rebuilds an empty bin the next time it is needed.
    // ------------------------------------------------------------------

    private static readonly TrashGuard TrashGuardInstance = new();

    /// <summary>Single entry point for the recycle bin: ensures the directory exists + hidden/system attributes + runtime protection lock.
    /// All code that touches the bin must go through this; never Path.Combine+CreateDirectory on your own.</summary>
    private static string EnsureTrashReady(string gamePath)
    {
        var trash = StoragePaths.GameTrashDir(gamePath);
        Directory.CreateDirectory(trash);
        ProtectTrash(trash);
        return trash;
    }

    /// <summary>Applies the hidden attribute + runtime protection lock to an existing recycle bin (idempotent, safe to call repeatedly).</summary>
    private static void ProtectTrash(string trash)
    {
        try
        {
            var di = new DirectoryInfo(trash);
            if ((di.Attributes & (FileAttributes.Hidden | FileAttributes.System))
                != (FileAttributes.Hidden | FileAttributes.System))
                di.Attributes |= FileAttributes.Hidden | FileAttributes.System;
        }
        catch { }
        TrashGuardInstance.Acquire(trash);
    }

    private sealed class TrashGuard
    {
        private SafeFileHandle? _handle;
        private string? _path;

        public void Acquire(string trashDir)
        {
            // v1.1.6: if the directory the old handle points to was deleted and recreated externally, the handle is still open but no longer
            // protects the new directory (the old code short-circuited with return, leaving the recreated bin unprotected) — release and re-acquire when the path is stale
            if (_handle is { IsInvalid: false, IsClosed: false }
                && _path is not null && Directory.Exists(_path))
                return;
            Release();
            _path = trashDir;
            try
            {
                _handle = NativeMethods.CreateFile(trashDir,
                    NativeMethods.GENERIC_READ,
                    NativeMethods.FILE_SHARE_READ | NativeMethods.FILE_SHARE_WRITE,
                    IntPtr.Zero, NativeMethods.OPEN_EXISTING,
                    NativeMethods.FILE_FLAG_BACKUP_SEMANTICS, IntPtr.Zero);
                // if it cannot be opened (e.g. the user just deleted the directory), give up silently — EnsureTrashReady retries next time
            }
            catch { /* the protection lock must never affect the main flow */ }
        }

        private void Release()
        {
            try { _handle?.Dispose(); } catch { }
            _handle = null;
        }
    }

    private static class NativeMethods
    {
        public const uint GENERIC_READ = 0x80000000;
        public const uint FILE_SHARE_READ = 0x1;
        public const uint FILE_SHARE_WRITE = 0x2;
        public const uint OPEN_EXISTING = 3;
        public const uint FILE_FLAG_BACKUP_SEMANTICS = 0x02000000;

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        public static extern SafeFileHandle CreateFile(string lpFileName, uint dwDesiredAccess,
            uint dwShareMode, IntPtr lpSecurityAttributes, uint dwCreationDisposition,
            uint dwFlagsAndAttributes, IntPtr hTemplateFile);
    }

    /// <summary>Install-failure rollback: moves the old version from the recycle bin back into Mods; if that fails it stays in the bin with a log entry,
    /// and the user can restore it manually under Settings → game recycle bin.</summary>
    private static void RestoreStaged(string staging, string dest)
    {
        try { Directory.Move(staging, dest); }
        catch (Exception __ex)
        { AppLog.Warn("Mods", $"[rollback] Old version could not be moved back; kept in the recycle bin: {Path.GetFileName(staging)} - {__ex.Message}"); }
    }

    private static string SanitizeFolderName(string name)
    {
        foreach (var c in Path.GetInvalidFileNameChars())
            name = name.Replace(c, '_');
        var trimmed = name.Trim();
        return string.IsNullOrWhiteSpace(trimmed) ? "NewMod" : trimmed;
    }

    private static void TryDelete(string dir)
    {
        try { if (Directory.Exists(dir)) Directory.Delete(dir, true); } catch (Exception __ex) { AppLog.Warn("ModService", __ex.Message); }
    }

    /// <summary>
    /// Reads manifest text: strips the lenient-but-nonstandard comments Stardew Valley mods commonly include (/* */ and //);
    /// returns null for empty/unreadable files, and the caller falls back to the folder name so the whole mod is never swallowed.
    /// </summary>
    private static string? TryReadManifestCleaned(string manifestPath)
    {
        try
        {
            var text = ReadManifestText(manifestPath);
            if (string.IsNullOrWhiteSpace(text)) return null;
            // return the raw text — Newtonsoft's JObject.Parse is lenient by itself and
            // accepts SMAPI manifest trailing commas, inline // comments, and /* */ comments.
            return text;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Fallback display via the folder name for subdirectories without even one manifest (or empty directories, etc.).</summary>
    private static ModEntry OrphanEntry(string modsDir, string modDir, string? note = null)
    {
        var folderName = Path.GetRelativePath(modsDir, modDir).Replace('\\', '/');
        return new ModEntry
        {
            Folder = folderName,
            LastWrite = Directory.GetLastWriteTime(Path.Combine(modsDir, folderName)),
            Disabled = folderName.StartsWith('.') || folderName.Contains("/."),
            Name = Path.GetFileName(modDir.TrimEnd(Path.DirectorySeparatorChar)),
            Version = "?",
            Description = note ?? "⚠ This folder has no manifest.json",
            HasManifest = false,
        };
    }

    /// <summary>
    /// Builds a ModEntry from a single manifest.json.
    /// Unreadable / empty / unparseable manifest → returns null (the caller falls back to OrphanEntry).
    /// Parses the Dependencies array and ContentPackFor.UniqueID as "dependencies" (for missing detection).
    /// </summary>
    private static ModEntry? BuildModEntry(string modsDir, string ownerDir, string manifestPath,
        string? displayNameOverride = null)
    {
        var cleaned = TryReadManifestCleaned(manifestPath);
        if (cleaned is null)
        {
            return null;
        }

        try
        {
            // Lenient Newtonsoft parse: SMAPI manifests allow trailing commas/inline comments,
            // which strict JsonDocument misjudges as "no manifest". JObject.Parse accepts them all.
            var root = Newtonsoft.Json.Linq.JObject.Parse(cleaned);

            // UpdateKeys: Nexus / GitHub
            int? nexusId = null;
            string? githubRepo = null;
            if (root["UpdateKeys"] is Newtonsoft.Json.Linq.JArray uksArr)
            {
                foreach (var uk in uksArr)
                {
                    var s = uk?.Type == Newtonsoft.Json.Linq.JTokenType.String ? (string)uk! : "";
                    if (nexusId is null
                        && s.StartsWith("Nexus:", StringComparison.OrdinalIgnoreCase))
                    {
                        // e.g. "Nexus:23135@main": drop the GitHub-style @suffix, then take the numeric ID
                        var idPart = s[6..].Split('@')[0].Trim();
                        if (int.TryParse(idPart, out var id))
                            nexusId = id;
                    }
                    else if (githubRepo is null
                        && s.StartsWith("GitHub:", StringComparison.OrdinalIgnoreCase))
                    {
                        var r = s[7..].Trim().TrimEnd('/');
                        if (r.Contains('/')) githubRepo = r;
                    }
                    if (nexusId is not null && githubRepo is not null) break;
                }
            }

            // v1.1.2: UpdateKeys missing/invalid (e.g. SVE 1.15.11 writes "Nexus:???", the numeric part does not parse)
            // → fall back to the install sidecar to restore the Nexus link (both the "Installed" badge and update checks rely on it)
            if (nexusId is null)
                nexusId = ReadNexusIdSidecar(Path.GetDirectoryName(manifestPath) ?? modsDir, modsDir);

            // Dependencies: collect only "required" ones. SMAPI's IsRequired (formerly Required) defaults to true;
            // an explicit IsRequired=false marks an optional dependency (may be absent but should not be reported missing) → exclude it to avoid false positives.
            var deps = new List<string>();
            if (root["Dependencies"] is Newtonsoft.Json.Linq.JArray depsArr)
            {
                foreach (var item in depsArr)
                {
                    if (item is not Newtonsoft.Json.Linq.JObject io
                        || io["UniqueID"]?.Type != Newtonsoft.Json.Linq.JTokenType.String) continue;
                    var s = (string)io["UniqueID"]!;
                    if (string.IsNullOrWhiteSpace(s)) continue;

                    // explicit IsRequired/Required=false → optional dependency, does not count as required
                    var required = true;
                    var reqToken = io["IsRequired"] ?? io["Required"];
                    if (reqToken?.Type == Newtonsoft.Json.Linq.JTokenType.Boolean)
                        required = (bool)reqToken!;
                    if (required) deps.Add(s);
                }
            }

            // ContentPackFor: means "this pack needs a host framework". The host is not a hard missing install for the user;
            // many packs merely lose dynamic features without it, so don't misreport it as a "missing dependency" → store in a separate field, not in deps.
            string? contentPackHost = null;
            if (root["ContentPackFor"] is Newtonsoft.Json.Linq.JObject cpfObj)
            {
                contentPackHost = cpfObj["UniqueID"]?.Type == Newtonsoft.Json.Linq.JTokenType.String
                    ? (string)cpfObj["UniqueID"]! : null;
            }

            // v0.45.0: category tags — a manifest with EntryDll is a code mod (contains C# logic);
            // one declaring ContentPackFor is a content pack (attached to a host framework such as Content Patcher).
            var hasEntryDll = root["EntryDll"]?.Type == Newtonsoft.Json.Linq.JTokenType.String;
            var category = hasEntryDll && contentPackHost is not null ? "Code · Content pack"
                : hasEntryDll ? "Code mod"
                : contentPackHost is not null ? "Content pack"
                : "";

            // the path relative to Mods/ is the unique folder identity (nested structures are labeled clearly too, e.g. "SVE/[CP] xx")
            var manifestDir = Path.GetDirectoryName(manifestPath) ?? ownerDir;
            var relFolder = Path.GetRelativePath(modsDir, manifestDir).Replace('\\', '/');
            var dis = relFolder.StartsWith('.') || relFolder.Contains("/.");

            var folderName = string.IsNullOrEmpty(displayNameOverride)
                ? Path.GetFileName(ownerDir.TrimEnd(Path.DirectorySeparatorChar))
                : displayNameOverride;

            return new ModEntry
            {
                Folder = relFolder,
                LastWrite = Directory.GetLastWriteTime(Path.Combine(modsDir, relFolder)),
                Disabled = dis,
                Name = root["Name"]?.Type == Newtonsoft.Json.Linq.JTokenType.String ? (string)root["Name"]! : folderName,
                Author = root["Author"]?.Type == Newtonsoft.Json.Linq.JTokenType.String ? (string)root["Author"]! : "Unknown",
                Version = root["Version"]?.Type == Newtonsoft.Json.Linq.JTokenType.String ? (string)root["Version"]! : "?",
                Description = root["Description"]?.Type == Newtonsoft.Json.Linq.JTokenType.String ? (string)root["Description"]! : "",
                // v1.08: case-insensitive read — manifests of SMAPI's built-in mods write "UniqueId";
                // strict matching reads empty, so the disabled/enabled copies of the same mod cannot be deduped (two rows in the list).
                UniqueID = (root.GetValue("UniqueID", StringComparison.OrdinalIgnoreCase)?.Type == Newtonsoft.Json.Linq.JTokenType.String
                    ? (string)root.GetValue("UniqueID", StringComparison.OrdinalIgnoreCase)! : ""),
                NexusModId = nexusId,
                GitHubRepo = githubRepo,
                Dependencies = deps,
                ContentPackIds = contentPackHost is not null ? new List<string> { contentPackHost } : new List<string>(),
                HasManifest = true,
                Category = category,
            };
        }
        catch
        {
            return null;
        }
    }
}

public sealed class ModEntry
{
    public string Folder { get; set; } = "";
    /// <summary>Last write time of the mod folder, for "sort by time".</summary>
    public DateTime LastWrite { get; set; }
    public bool Disabled { get; set; }
    public string Name { get; set; } = "";
    public string Author { get; set; } = "";
    public string Version { get; set; } = "";
    public string Description { get; set; } = "";
    public string UniqueID { get; set; } = "";           // unique ID from the manifest, used to verify update packages
    public int? NexusModId { get; set; }   // from manifest UpdateKeys "Nexus:<id>"
    public string? GitHubRepo { get; set; }  // from manifest UpdateKeys "GitHub:<owner>/<repo>" (free direct download)
    public List<string> Dependencies { get; set; } = new();  // UniqueIDs this mod depends on (including the ContentPackFor host)
    public List<string> ContentPackIds { get; set; } = new(); // host mod UniqueIDs it depends on as a content pack (merged into Dependencies for missing detection)
    public bool HasManifest { get; set; } = true;   // false = this folder has no valid manifest (folder name used as fallback)
    /// <summary>v0.45.0: category tag (modeled after PCL2), shown before the summary on a list row. Code mod / Content pack / Code · Content pack; empty when none.</summary>
    public string Category { get; set; } = "";
}
