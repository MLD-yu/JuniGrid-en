using System.IO;
using System.IO.Compression;
using System.Text.Json;

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
    public IReadOnlyList<ModEntry> Scan(string gamePath)
    {
        if (string.IsNullOrWhiteSpace(gamePath)) return Array.Empty<ModEntry>();
        var modsDir = Path.Combine(gamePath, "Mods");
        if (!Directory.Exists(modsDir)) return Array.Empty<ModEntry>();

        // v0.52.0: clean up the recycle bin left over from a previous locked-file run (delete if possible, skip if not — it may contain locked files)
        var trashDir = Path.Combine(modsDir, ".junigrid_trash");
        if (Directory.Exists(trashDir))
            try { Directory.Delete(trashDir, recursive: true); } catch { }

        // v0.72.6: materialize the directory list first — during bulk enable/disable, folders get
        // renamed mid-enumeration (X ↔ .X), and the enumerator throws DirectoryNotFoundException
        // that blows up the whole Rescan (one root cause of the 2026-08-29 error wall)
        List<string> dirs;
        try { dirs = Directory.EnumerateDirectories(modsDir).ToList(); }
        catch (DirectoryNotFoundException) { return Array.Empty<ModEntry>(); }

        var results = new List<ModEntry>();
        foreach (var dir in dirs)
        {
            // v0.72.6: a single directory being renamed/deleted at the instant of scanning is a legitimate
            // race — tolerate it locally and skip the item; never let it abort the whole scan. IO/permission
            // errors are logged separately without changing the scan-result semantics (not swallowed wholesale)
            try
            {
                // v0.52.0: the recycle bin directory is not scanned
                if (string.Equals(Path.GetFileName(dir), ".junigrid_trash", StringComparison.OrdinalIgnoreCase))
                    continue;
                // Folders starting with "." are "disable-marker directories" (created by renaming to .X on disable).
                // They must NOT be skipped: include them and mark them Disabled so the UI can show "Disabled"
                // and re-enable them. (v0.42.0 used to skip them with continue, which made disabled mods vanish
                // from the list and become impossible to re-enable — reverted.)
                var manifest = Path.Combine(dir, "manifest.json");
                if (!File.Exists(manifest))
                {
                    var nested = Directory
                        .EnumerateFiles(dir, "manifest.json", SearchOption.AllDirectories)
                        .OrderBy(f => f.Length)
                        .ToList();
                    // A folder may contain multiple sub-mods (Content Pack subfolders are common);
                    // each nested manifest is treated as its own mod
                    if (nested.Count == 0)
                    {
                        // No manifest at any level → fall back to the folder name so the mod doesn't vanish
                        results.Add(OrphanEntry(modsDir, dir));
                        continue;
                    }
                    foreach (var nm in nested)
                    {
                        var e = BuildModEntry(modsDir, dir, nm);
                        if (e is not null) { results.Add(e); continue; }
                        // v1.06.4: manifest exists but is empty/unparseable → must still be included as a fallback.
                        // Dropping it used to make the whole package invisible in the list: "Disable all" couldn't
                        // touch it (the folder gets no dot prefix) while SMAPI still scanned it, flooding the log
                        // with Skipped mods (root cause for East Scarp REMASTERED and three other large packs
                        // shipping a 0-byte top-level manifest).
                        results.Add(OrphanEntry(modsDir, Path.GetDirectoryName(nm)!,
                            "⚠ manifest.json is empty or unparseable (reinstalling this mod is recommended)"));
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
                    // Empty/unparseable manifest → fall back to the folder name, marked unrecognized, so the mod doesn't vanish
                    results.Add(OrphanEntry(modsDir, dir));
                }
                    }
            catch (DirectoryNotFoundException) { continue; }
            catch (IOException ioe) { AppLog.Warn("Mods", "Scan skipped (IO): " + Path.GetFileName(dir) + " - " + ioe.Message); continue; }
            catch (UnauthorizedAccessException) { AppLog.Warn("Mods", "Scan skipped (no permission): " + Path.GetFileName(dir)); continue; }
        }
        return results;
    }

    // ------------------------------------------------------------------
    // Enable / disable / uninstall
    // ------------------------------------------------------------------
    /// <summary>Disabling = prefixing the folder with a dot (SMAPI skips those).
    /// Only the "top-level folder" is renamed: if a multi-level sub-path is passed (e.g. a Content Pack
    /// subfolder like Weather-Beta/[CC]), only the first segment (the entire top-level mod) is renamed.
    /// Disabling a multi-pack mod therefore renames it as a whole, without splitting directories or leaving husks.</summary>
    public string? SetDisabled(string gamePath, string folderName, bool disabled)
    {
        try
        {
            var modsDir = Path.Combine(gamePath, "Mods");
            // Take only the top level: multi-level sub-paths ("top/sub-pack") resolve to the top-level folder for a whole-folder rename
            var topLevel = folderName.Split('/')[0];
            var src = Path.Combine(modsDir, topLevel);
            if (!Directory.Exists(src) && !disabled && !topLevel.StartsWith('.'))
            {
                // Enable tolerance: the caller passes the old name without a dot, but the disk actually has .X
                var alt = Path.Combine(modsDir, "." + topLevel);
                if (Directory.Exists(alt)) { topLevel = "." + topLevel; src = alt; }
            }
            if (!Directory.Exists(src)) return "Mod folder not found";

            var targetName = disabled
                ? (topLevel.StartsWith('.') ? topLevel : "." + topLevel)
                : topLevel.TrimStart('.');

            if (targetName == topLevel) return null;

            var dest = Path.Combine(modsDir, targetName);
            if (Directory.Exists(dest)) return "A folder with the same name already exists; cannot rename";
            // v0.44.0: src and dest are both in the same Mods directory, so Directory.Move is a pure
            // metadata rename (instant, no content copy). The old MoveDirectorySafe fell back to
            // "copy + delete source" on locks, which for large mods meant moving hundreds of MB —
            // the root cause of very slow bulk enable/disable. Use the instant rename instead and let
            // Windows fail directly on a lock; the caller surfaces the message.
            Directory.Move(src, dest);
            return null;
        }
        catch (Exception ex)
        {
            // v0.51.0: show a friendly message when files are locked instead of the raw English exception
            if (ex is IOException or UnauthorizedAccessException)
                return $"\"{folderName}\" is currently in use. Close the related program before {(disabled ? "disabling" : "enabling")} it";
            return ex.Message;
        }
    }

    public string? Uninstall(string gamePath, string folderName)
    {
        try
        {
            var dir = Path.Combine(gamePath, "Mods", folderName);
            if (!Directory.Exists(dir)) return "Mod folder not found";
            // v0.51.0: atomic delete — first move the whole folder into the recycle bin to verify it
            // "can be deleted"; only if the move succeeds is it fully deleted from the bin. If it can't
            // move (locked), it is restored — never leave half a folder behind
            var trash = Path.Combine(gamePath, "Mods", ".junigrid_trash");
            Directory.CreateDirectory(trash);
            var staging = Path.Combine(trash, folderName.Replace('/', '_') + "_" + Guid.NewGuid().ToString("N")[..8]);
            try
            {
                Directory.Move(dir, staging);   // same-volume instant rename; a lock throws right here
            }
            catch (Exception ex)
            {
                // Locked → restore (if staging was partially moved, move it back)
                if (Directory.Exists(staging) && !Directory.Exists(dir))
                    try { Directory.Move(staging, dir); } catch { }
                if (ex is IOException or UnauthorizedAccessException)
                    return $"\"{folderName}\" is currently in use. Close the related program before deleting it";
                return ex.Message;
            }
            // Move succeeded → delete from the recycle bin for good
            try { Directory.Delete(staging, recursive: true); }
            catch (Exception ex) { AppLog.Warn("ModService", "Recycle bin cleanup failed: " + ex.Message); }
            // v0.52.0: right after deleting, remove the recycle bin itself if empty so no .junigrid_trash husk shows up in the list
            try { if (Directory.Exists(trash) && !Directory.EnumerateFileSystemEntries(trash).Any()) Directory.Delete(trash); }
            catch { }
            return null;
        }
        catch (Exception ex)
        {
            // v0.47.0: understandable message when a file is locked (e.g. Stardrop.exe is running)
            if (ex is UnauthorizedAccessException or IOException)
                return $"\"{folderName}\" is currently in use. Close the related program before deleting it";
            return ex.Message;
        }
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
            var modRoot = Path.GetDirectoryName(manifest)!;

            // Safety lock: when multiple mods share one GitHub repo, the latest release may belong to
            // a different mod. If the UniqueID doesn't match, abort — never install the wrong package.
            if (expectedUniqueId is not null)
            {
                try
                {
                    using var check = JsonDocument.Parse(File.ReadAllText(manifest));
                    var uid = check.RootElement.TryGetProperty("UniqueID", out var u)
                        ? u.GetString() : null;
                    if (!string.Equals(uid, expectedUniqueId, StringComparison.OrdinalIgnoreCase))
                        return "The downloaded package is not this mod (the release repo contains multiple mods); installation aborted to prevent a wrong install";
                }
                catch { return "Unable to verify the update package; installation aborted"; }
            }

            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(manifest));
                if (doc.RootElement.TryGetProperty("Version", out var v))
                    newVersion = v.GetString();
            }
            catch (Exception __ex) { AppLog.Warn("ModService", __ex.Message); }

            var dest = Path.Combine(gamePath, "Mods", targetFolderName);
            if (Directory.Exists(dest)) Directory.Delete(dest, recursive: true);
            MoveDirectorySafe(modRoot, dest);   // cross-volume safety

            TryDelete(temp);
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
    /// </summary>
    public string? InstallNew(string gamePath, string zipPath, out string? modName)
    {
        modName = null;
        string? temp = null;
        try
        {
            var manifest = ExtractToTemp(zipPath, "mod-install-", out temp);
            if (manifest is null)
            {
                // Archives without manifest.json are not standalone mods (usually translation patches or
                // overlay-only file packs). Auto-installing them would mix "orphan folders" into the list,
                // with no way to identify version/dependencies. Instead, prompt for a manual download and
                // let the user decide how to handle it.
                TryDelete(temp);
                modName = null;
                return "This archive has no manifest.json, so it is not a complete standalone mod (likely a translation patch or overlay pack). Please use Manual download instead and place the files yourself.";
            }
            var modRoot = Path.GetDirectoryName(manifest)!;

            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(manifest));
                if (doc.RootElement.TryGetProperty("Name", out var n))
                    modName = n.GetString();
            }
            catch (Exception __ex) { AppLog.Warn("ModService", __ex.Message); }

            var folderName = Path.GetFileName(modRoot);
            if (string.IsNullOrEmpty(folderName) || modRoot == temp)
                folderName = SanitizeFolderName(modName ?? "NewMod");

            var dest = Path.Combine(gamePath, "Mods", folderName);
            if (Directory.Exists(dest)) Directory.Delete(dest, recursive: true);
            // v0.71.9: a same-named disabled directory (.folderName) must also be removed — the old logic
            // only checked the dot-less dest, so a disabled mod (Mods/.X) plus a fresh download (Mods/X)
            // could coexist and the scan would list the same mod twice.
            var destDisabled = Path.Combine(gamePath, "Mods", "." + folderName);
            if (Directory.Exists(destDisabled)) Directory.Delete(destDisabled, recursive: true);
            // v0.71.9: also deduplicate by manifest UniqueID — folders with different names but the same
            // UniqueID (e.g. ABC / ABC-1.2 / .ABC) are the same mod and are cleaned up too, preventing
            // duplicates of any kind.
            try
            {
                string? newUid = null;
                using (var doc = JsonDocument.Parse(File.ReadAllText(manifest)))
                    if (doc.RootElement.TryGetProperty("UniqueID", out var u)) newUid = u.GetString();
                if (!string.IsNullOrWhiteSpace(newUid))
                {
                    var modsDirScan = Path.Combine(gamePath, "Mods");
                    foreach (var dir2 in Directory.EnumerateDirectories(modsDirScan))
                    {
                        var name2 = Path.GetFileName(dir2);
                        if (string.Equals(name2, folderName, StringComparison.OrdinalIgnoreCase)
                            || string.Equals(name2, "." + folderName, StringComparison.OrdinalIgnoreCase))
                            continue;   // already handled above
                        foreach (var mf in Directory.EnumerateFiles(dir2, "manifest.json", SearchOption.AllDirectories))
                        {
                            try
                            {
                                using var d2 = JsonDocument.Parse(File.ReadAllText(mf));
                                if (d2.RootElement.TryGetProperty("UniqueID", out var u2)
                                    && string.Equals(u2.GetString(), newUid, StringComparison.OrdinalIgnoreCase))
                                { Directory.Delete(dir2, recursive: true); break; }
                            }
                            catch { }
                        }
                    }
                }
            }
            catch (Exception __ex) { AppLog.Warn("ModService", "UniqueID dedup cleanup failed: " + __ex.Message); }

            if (modRoot == temp)
                CopyDirectoryContents(temp, dest);   // files at zip root — copy into named folder
            else
            MoveDirectorySafe(modRoot, dest);   // cross-volume safety

            TryDelete(temp);
            return null;
        }
        catch (Exception ex)
        {
            if (temp is not null) TryDelete(temp);
            return ex.Message;
        }
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
    /// Cross-volume-safe directory move: Directory.Move only supports the same volume and throws
    /// "Source and destination path must have identical roots" across volumes.
    /// When different drives are detected, fall back to "copy + delete source".
    /// </summary>
    private static void MoveDirectorySafe(string src, string dest)
    {
        try
        {
            Directory.Move(src, dest);
        }
        catch (IOException)
        {
            // Cross-volume / dest already exists / handle locked → copy + delete source
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
    /// Reads manifest text: strips the non-strict comments common in Stardew Valley mods (/* */ and //).
    /// Returns null for empty/unreadable files; callers fall back to the folder name so the mod isn't swallowed.
    /// </summary>
    private static string? TryReadManifestCleaned(string manifestPath)
    {
        try
        {
            var text = File.ReadAllText(manifestPath);
            if (string.IsNullOrWhiteSpace(text)) return null;
            // Return the raw text — Newtonsoft's JObject.Parse is lenient and
            // accepts SMAPI manifests with trailing commas, inline // comments, and /* */ comments.
            return text;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Sub-directories with no manifest at any level (or empty folders, etc.): fall back to the folder name.</summary>
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
    /// Missing/empty/unparseable manifest → returns null (caller falls back to OrphanEntry).
    /// Parses the Dependencies array and ContentPackFor.UniqueID as "dependencies" (for missing-mod detection).
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
            // Lenient Newtonsoft parsing: SMAPI manifests allow trailing commas/inline comments;
            // strict JsonDocument would wrongly report "no manifest". JObject.Parse handles all of it.
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
                        // e.g. "Nexus:23135@main": strip the GitHub-style @suffix, then take the numeric ID
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

            // Dependencies: only "required" ones. SMAPI's IsRequired (formerly Required) defaults to true;
            // explicit IsRequired=false marks an optional dependency (may be absent and shouldn't be reported
            // as missing) → excluded, to avoid false positives.
            var deps = new List<string>();
            if (root["Dependencies"] is Newtonsoft.Json.Linq.JArray depsArr)
            {
                foreach (var item in depsArr)
                {
                    if (item is not Newtonsoft.Json.Linq.JObject io
                        || io["UniqueID"]?.Type != Newtonsoft.Json.Linq.JTokenType.String) continue;
                    var s = (string)io["UniqueID"]!;
                    if (string.IsNullOrWhiteSpace(s)) continue;

                    // Explicit IsRequired/Required=false → optional dependency, not a required one
                    var required = true;
                    var reqToken = io["IsRequired"] ?? io["Required"];
                    if (reqToken?.Type == Newtonsoft.Json.Linq.JTokenType.Boolean)
                        required = (bool)reqToken!;
                    if (required) deps.Add(s);
                }
            }

            // ContentPackFor: "this pack requires a host framework". A missing host is not something the
            // user should be told to hard-install — many packs merely lose dynamic features without it,
            // so don't misreport it as a "missing dependency" → stored in a separate field, not in deps.
            string? contentPackHost = null;
            if (root["ContentPackFor"] is Newtonsoft.Json.Linq.JObject cpfObj)
            {
                contentPackHost = cpfObj["UniqueID"]?.Type == Newtonsoft.Json.Linq.JTokenType.String
                    ? (string)cpfObj["UniqueID"]! : null;
            }

            // v0.45.0: category tag — a manifest with EntryDll is a code mod (contains C# logic);
            // one declaring ContentPackFor is a content pack (attached to a host framework like Content Patcher).
            var hasEntryDll = root["EntryDll"]?.Type == Newtonsoft.Json.Linq.JTokenType.String;
            var category = hasEntryDll && contentPackHost is not null ? "Code · Content pack"
                : hasEntryDll ? "Code mod"
                : contentPackHost is not null ? "Content pack"
                : "";

            // Path relative to Mods/ as the unique folder identity (multi-level structures are labeled clearly, e.g. "SVE/[CP] xx")
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
                UniqueID = root["UniqueID"]?.Type == Newtonsoft.Json.Linq.JTokenType.String ? (string)root["UniqueID"]! : "",
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
    /// <summary>Last write time of the mod folder, used for "sort by time".</summary>
    public DateTime LastWrite { get; set; }
    public bool Disabled { get; set; }
    public string Name { get; set; } = "";
    public string Author { get; set; } = "";
    public string Version { get; set; } = "";
    public string Description { get; set; } = "";
    public string UniqueID { get; set; } = "";           // unique ID from the manifest, used to verify update packages
    public int? NexusModId { get; set; }   // from manifest UpdateKeys "Nexus:<id>"
    public string? GitHubRepo { get; set; }  // from manifest UpdateKeys "GitHub:<owner>/<repo>" (free direct download)
    public List<string> Dependencies { get; set; } = new();  // UniqueIDs this mod depends on (including ContentPackFor host)
    public List<string> ContentPackIds { get; set; } = new(); // host mod UniqueIDs required when this is a content pack (merged into Dependencies for missing-mod detection)
    public bool HasManifest { get; set; } = true;   // false = this folder has no valid manifest (folder name used as fallback)
    /// <summary>v0.45.0: category tag (PCL2-style), shown before the summary line in the list. Code mod / Content pack / Code · Content pack; empty if none.</summary>
    public string Category { get; set; } = "";
}
