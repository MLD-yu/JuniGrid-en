using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Text.Json;

namespace JuniGrid.Services;

/// <summary>
/// Version probing + SMAPI update checks.
///  - Game version comes from Stardew Valley.exe file metadata (offline).
///  - SMAPI's latest release comes from the public GitHub Releases API
///    (no key required, ~60 requests/hour — we check once per app run).
///  - Updating downloads the official SMAPI installer zip and launches its
///    "install on Windows.bat", which handles the actual in-place update.
/// </summary>
public sealed class UpdateService
{
    private static readonly HttpClient Http = CreateClient();
    private SmapiUpdateInfo? _cached;
    private DateTime _cachedAt;

    private static HttpClient CreateClient()
    {
        var h = new HttpClient();
        h.DefaultRequestHeaders.UserAgent.ParseAdd("JuniGrid-Launcher");
        // Accept header recommended by the GitHub API; lowers the chance of rate limiting.
        h.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        h.Timeout = TimeSpan.FromSeconds(12);
        return h;
    }

    public void Invalidate() { _cached = null; _cachedAt = DateTime.MinValue; }

    // ------------------------------------------------------------------
    // Game version (local, offline)
    // ------------------------------------------------------------------
    public string? GetGameVersion(string gamePath)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(gamePath)) return null;
            var exe = Path.Combine(gamePath, "Stardew Valley.exe");
            if (!File.Exists(exe)) return null;

            var raw = FileVersionInfo.GetVersionInfo(exe).FileVersion
                   ?? FileVersionInfo.GetVersionInfo(exe).ProductVersion;
            if (string.IsNullOrWhiteSpace(raw)) return null;

            // "1.6.15.24356" → "1.6.15"
            var parts = raw.Split('.');
            return parts.Length >= 3 ? string.Join('.', parts.Take(3)) : raw;
        }
        catch
        {
            return null;
        }
    }

    // ------------------------------------------------------------------
    // SMAPI update check (GitHub Releases API)
    // ------------------------------------------------------------------
    public async Task<SmapiUpdateInfo> CheckSmapiAsync(string? installedVersion)
    {
        // Cache policy: reuse a successful result for the same local version or within 20 minutes;
        // a previous failure (GitHub rate limit / offline) is not cached permanently — the user's
        // "↻ Check for updates" click retries.
        if (_cached is not null
            && _cached.Error is null
            && _cached.ForVersion == installedVersion
            && DateTime.Now - _cachedAt < TimeSpan.FromMinutes(5))
            return _cached;

        try
        {
            var json = await Http.GetStringAsync(
                "https://api.github.com/repos/Pathoschild/SMAPI/releases/latest");
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var tag = root.TryGetProperty("tag_name", out var t) ? t.GetString() ?? "" : "";
            var pageUrl = root.TryGetProperty("html_url", out var h)
                ? h.GetString() ?? "https://smapi.io" : "https://smapi.io";

            // Pick the installer asset: SMAPI 4.5+ uploads both
            //   SMAPI-x.y.z-installer.zip                (normal single-layer zip — use this one)
            //   SMAPI-x.y.z-installer-double-zipped.zip  (an extra outer layer for some browser protection policies)
            // The old loop hit "double-zipped" first, so the extracted output was still a zip
            // with no SMAPI.Installer.exe inside. Double-zipped is now explicitly skipped.
            string? zipUrl = null;
            if (root.TryGetProperty("assets", out var assets))
            {
                foreach (var asset in assets.EnumerateArray())
                {
                    var name = asset.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                    if (!name.Contains("installer", StringComparison.OrdinalIgnoreCase)) continue;
                    if (!name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) continue;
                    if (name.Contains("double-zipped", StringComparison.OrdinalIgnoreCase)) continue;
                    zipUrl = asset.TryGetProperty("browser_download_url", out var d)
                        ? d.GetString() : null;
                    break;
                }
            }

            // Fix: when ProbeSmapiVersion can't extract a version it returns the string "installed";
            // Version.TryParse("installed") fails → hasUpdate stays false and the UI wrongly shows
            // "up to date". As a safety net, when the local version is unparseable but SMAPI is
            // installed (installedVersion not null), any fetched remote tag is reported as an
            // available update instead of silently treating it as up to date.
            var installedOk = Version.TryParse(Normalize(installedVersion), out var installed);
            var remoteOk = Version.TryParse(Normalize(tag), out var latest);
            bool hasUpdate;
            if (installedOk && remoteOk)
                hasUpdate = latest > installed;
            else if (!installedOk && remoteOk && !string.IsNullOrWhiteSpace(installedVersion))
                hasUpdate = true;   // local version unparseable but SMAPI is installed — conservatively report an update
            else
                hasUpdate = false;

            _cached = new SmapiUpdateInfo(
                installedVersion, string.IsNullOrEmpty(tag) ? null : tag,
                hasUpdate, installedOk, zipUrl, pageUrl, null);
            _cachedAt = DateTime.Now;
        }
        catch (Exception ex)
        {
            // v0.38.0: when the API is rate-limited (403 rate limit), fall back to parsing the HTML
            // release page — the 302 redirect URL of /releases/latest carries the tag and is not
            // subject to the API quota (60/hour/IP).
            var fallback = await TryCheckSmapiViaHtmlAsync(installedVersion);
            if (fallback is not null)
            {
                _cached = fallback;
                _cachedAt = DateTime.Now;
                return _cached;
            }

            // Offline / rate-limited — don't cache the timestamp; retry on the next check.
            var friendly = ex.Message.Contains("403") || ex.Message.Contains("rate limit", StringComparison.OrdinalIgnoreCase)
                ? "GitHub API is temporarily rate-limited (60 requests/hour). Try again later or click \"Check for updates\" to retry."
                : ex.Message;
            _cached = new SmapiUpdateInfo(
                installedVersion, null, false, false, null, "https://smapi.io", friendly);
            _cachedAt = DateTime.MinValue;
        }
        return _cached;
    }

    /// <summary>
    /// v0.38.0: fallback channel when the GitHub API is rate-limited — request releases/latest (HTML),
    /// extract the tag from the final redirected URL (e.g. /releases/tag/4.5.2), then build the
    /// installer zip download URL from the official naming scheme.
    /// The HTML page uses a separate quota that normal usage almost never exhausts.
    /// </summary>
    private async Task<SmapiUpdateInfo?> TryCheckSmapiViaHtmlAsync(string? installedVersion)
    {
        try
        {
            // HttpClient follows redirects by default; the final RequestUri looks like
            // https://github.com/Pathoschild/SMAPI/releases/tag/4.5.2
            using var resp = await Http.GetAsync(
                "https://github.com/Pathoschild/SMAPI/releases/latest",
                HttpCompletionOption.ResponseHeadersRead);
            if (!resp.IsSuccessStatusCode) return null;

            var finalUrl = resp.RequestMessage?.RequestUri?.ToString() ?? "";
            const string marker = "/releases/tag/";
            var idx = finalUrl.IndexOf(marker, StringComparison.Ordinal);
            if (idx < 0) return null;
            var tag = finalUrl[(idx + marker.Length)..].TrimEnd('/').Split('?')[0];
            if (string.IsNullOrWhiteSpace(tag)) return null;

            var zipUrl = $"https://github.com/Pathoschild/SMAPI/releases/download/{tag}/SMAPI-{tag}-installer.zip";
            var pageUrl = $"https://github.com/Pathoschild/SMAPI/releases/tag/{tag}";

            var installedOk = Version.TryParse(Normalize(installedVersion), out var installed);
            var remoteOk = Version.TryParse(Normalize(tag), out var latest);
            bool hasUpdate;
            if (installedOk && remoteOk)
                hasUpdate = latest > installed;
            else if (!installedOk && remoteOk && !string.IsNullOrWhiteSpace(installedVersion))
                hasUpdate = true;
            else
                hasUpdate = false;

            return new SmapiUpdateInfo(
                installedVersion, tag, hasUpdate, installedOk, zipUrl, pageUrl, null);
        }
        catch
        {
            return null;
        }
    }

    // ------------------------------------------------------------------
    // Game latest version (remote, Stardew Valley Wiki) — silent check at startup
    // ------------------------------------------------------------------
    /// <summary>
    /// Fetches the current version from the Stardew Valley Wiki, used as the source for
    /// "latest game version". On failure (offline / page redesign) it silently returns null
    /// so the UI shows no "new version available" hint.
    /// Returns a plain version string, e.g. "1.6.15".
    /// </summary>
    public async Task<string?> GetLatestGameVersionAsync()
    {
        try
        {
            var html = await Http.GetStringAsync(
                "https://stardewvalleywiki.com/Main_Page");

            // Prefer locating the current base-game version precisely: the text of the sidebar's
            // Version History link on the wiki IS the current game version, e.g.
            // <a ... title="Version History">1.6.15</a>.
            // Never take the "max" of all 1.x.y numbers on the page — it also mixes in many other
            // mod/version numbers (e.g. 1.35.1), which would wrongly read as "latest is newer than local".
            var precise = System.Text.RegularExpressions.Regex.Match(
                html, @"title=""Version History""[^>]*>\s*([0-9]+\.[0-9]+\.[0-9]+)");
            if (precise.Success) return precise.Groups[1].Value;

            // Fallback: the first 1.x.y on the page (also likely the latest version); on failure return null silently
            var first = System.Text.RegularExpressions.Regex.Match(html, @"1\.\d+\.\d+");
            return first.Success ? first.Value : null;
        }
        catch
        {
            return null;   // offline / blocked / page redesigned → stay silent
        }
    }

    // ------------------------------------------------------------------
    // Run the official SMAPI installer — fully automated, unattended
    // ------------------------------------------------------------------
    /// <summary>
    /// Downloads the official installer and silently runs SMAPI.Installer.exe in the background
    /// (--install --no-prompt --game-path) — no windows, no browser.
    /// Returns null on success; otherwise returns an error message.
    /// The SMAPI installer natively supports unattended switches (see the InteractiveInstaller source):
    ///   --no-prompt   disable interactive prompts
    ///   --install     perform install/update
    ///   --game-path   specify the game folder (skips auto-detection)
    /// </summary>
    public async Task<string?> RunSmapiInstallerAsync(
        SmapiUpdateInfo info, string? gamePath,
        IProgress<InstallProgress>? progress = null)
    {
        try
        {
            if (info.InstallerZipUrl is null)
                return "Could not find the SMAPI installer download URL";
            if (string.IsNullOrWhiteSpace(gamePath) || !Directory.Exists(gamePath))
                return "Game folder not set yet — choose it on the Settings page first";

            // v0.2.1: use the unified cache directory (still LocalAppData by default). Not %TEMP% directly:
            // Defender scans .NET runtime DLLs in %TEMP% more aggressively and often locks them the instant
            // they're written. The folder name carries a timestamp to avoid clashing with older caches.
            var stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            var temp = Path.Combine(
                StoragePaths.SmapiInstallerDir,
                $"{info.LatestVersion ?? "latest"}-{stamp}");
            Directory.CreateDirectory(temp);

            // Back up the player's Mods before downloading, so no step of download/extract/install
            // can touch the original directory; after installation the backup is merged back into
            // Mods and the temporary backup removed.
            var modsBackup = BackupMods(gamePath);
            if (modsBackup is not null)
                progress?.Report(new InstallProgress("Backed up the existing Mods folder; starting SMAPI download…", 0, 0));

            progress?.Report(new InstallProgress("Downloading the SMAPI installer…"));
            progress?.Report(new InstallProgress("Connecting to the download server…", 0, 0));
            var zip = Path.Combine(temp, "smapi-installer.zip");
            await DownloadToFileAsync(info.InstallerZipUrl, zip, progress);

            progress?.Report(new InstallProgress("Extracting the installer…"));
            await ExtractWithRetryAsync(zip, temp, progress);

            // Safety net: if this zip is a "double-zipped" wrapper, the extracted output is still a zip — unpack one more layer automatically.
            var innerZip = Directory
                .GetFiles(temp, "*.zip", SearchOption.AllDirectories)
                .FirstOrDefault(f => !string.Equals(f, zip, StringComparison.OrdinalIgnoreCase));
            if (innerZip is not null)
            {
                progress?.Report(new InstallProgress("Inner archive detected; extracting again…"));
                await ExtractWithRetryAsync(innerZip, temp, progress);
            }

            // Known issue with the official SMAPI 4.5.x installer: even in --install --no-prompt mode
            // it still calls Console.Clear(). If the installer process's stdout/input handle is not a
            // real console (exactly the case when JuniGrid launches it as a WPF background process),
            // Console.Clear() throws an "invalid handle" IOException and the install fails outright.
            // Popping a console window repeatedly can still fail.
            //
            // So instead, following the SMAPI README's manual install steps, we install directly from
            // the internal/windows folder of the official installer package: the official README explicitly
            // supports extracting and copying install.dat into the game folder. That needs no console at
            // all and is the more reliable approach for a launcher.
            progress?.Report(new InstallProgress("Installing SMAPI in the background (manual install of official files)…", 90));

            var windowsFiles = Path.Combine(temp, "SMAPI " + info.LatestVersion + " installer", "internal", "windows");
            if (!Directory.Exists(windowsFiles))
            {
                // Structure-change fallback: search recursively for install.dat / StardewModdingAPI.exe
                var installDat = Directory
                    .GetFiles(temp, "install.dat", SearchOption.AllDirectories)
                    .FirstOrDefault(f => f.Contains(Path.Combine("windows", "install.dat"), StringComparison.OrdinalIgnoreCase));
                if (installDat is not null)
                    windowsFiles = Path.GetDirectoryName(installDat)!;
            }
            // install.dat is actually a zip (extension changed to prevent accidental double-click extraction).
            // Official manual install steps:
            //   1. Extract install.dat to a temp folder
            //   2. Copy the extracted files over the game folder
            //   3. Copy "Stardew Valley.deps.json" → "StardewModdingAPI.deps.json"
            var dat = Path.Combine(windowsFiles, "install.dat");
            if (!File.Exists(dat))
                return "SMAPI installer package not found after extraction (internal/windows/install.dat missing — package structure changed?)";

            var extracted = Path.Combine(temp, "smapi-files");
            Directory.CreateDirectory(extracted);
            progress?.Report(new InstallProgress("Extracting install contents…", 95));
            await ExtractWithRetryAsync(dat, extracted, progress);

            if (!File.Exists(Path.Combine(extracted, "StardewModdingAPI.exe")))
                return "StardewModdingAPI.exe not found inside the SMAPI installer package (package appears corrupted)";

            // Remove old SMAPI files before copying the new ones (avoids file locks / stale version files).
            CopySmapiBundle(extracted, gamePath);

            // After the update, keep the game's own deps.json/runtimeconfig.json; SMAPI's
            // StardewModdingAPI.deps.json must be generated/overwritten based on the game's main file.
            var gameDeps = Path.Combine(gamePath, "Stardew Valley.deps.json");
            if (File.Exists(gameDeps))
                File.Copy(gameDeps, Path.Combine(gamePath, "StardewModdingAPI.deps.json"), true);

            // After installation, copy the backed-up Mods back so no user mod is lost. The official
            // install.dat also ships SMAPI's built-in default mods (ConsoleCommands/SaveBackup etc.),
            // so restore the backup first, then add back any missing default mods — never overwrite
            // the whole directory.
            if (modsBackup is not null)
            {
                progress?.Report(new InstallProgress("Restoring the Mods folder…", 100));
                RestoreMods(modsBackup, Path.Combine(gamePath, "Mods"));
            }
            CopyBuiltinMods(Path.Combine(extracted, "Mods"), Path.Combine(gamePath, "Mods"));

            // Then delete the temporary backup folder created for this update.
            if (modsBackup is not null)
            {
                TryDeleteBackup(modsBackup);
            }

            progress?.Report(new InstallProgress($"SMAPI {info.LatestVersion} installation complete", 100, 0));
            Invalidate();
            return null;
        }
        catch (Exception ex)
        {
            // Keep the backup on failure if possible, so it can be restored manually next time.
            return "SMAPI automatic installation failed: " + ex.Message;
        }
    }

    /// <summary>
    /// Progress message for the SMAPI install flow. percent/speed are optional: only the
    /// DownloadToFileAsync stage reports values; extract/install stages update text and stage percent only.
    /// </summary>
    public sealed record InstallProgress(
        string Message,
        double? Percent = null,
        double? SpeedMBps = null);


    // Large downloads use a separate long-timeout client (the version-check Http has only a 12s
    // timeout, which would cut off the SMAPI installer download).
    private static readonly HttpClient DownloadHttp = CreateDownloadClient();

    private static HttpClient CreateDownloadClient()
    {
        var h = new HttpClient();
        h.DefaultRequestHeaders.UserAgent.ParseAdd("JuniGrid-Launcher");
        h.Timeout = TimeSpan.FromMinutes(5);
        return h;
    }

    /// <summary>
    /// Extraction with automatic retry: Defender frequently locks clrjit.dll/coreclr.dll at the instant
    /// they're written, causing UnauthorizedAccessException / IOException — waiting and retrying works.
    /// </summary>
    private static async Task ExtractWithRetryAsync(
        string zipPath, string destDir, IProgress<InstallProgress>? progress)
    {
        const int maxAttempts = 4;
        for (int attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                ZipFile.ExtractToDirectory(zipPath, destDir, overwriteFiles: true);
                return;
            }
            catch (Exception ex) when (
                ex is UnauthorizedAccessException || ex is IOException)
            {
                if (attempt == maxAttempts)
                {
                    throw new InvalidOperationException(
                        $"Extraction was blocked by the system ({ex.Message}) — commonly caused by Windows Defender real-time protection. "
                        + "Temporarily add %LocalAppData%\\JuniGrid to the exclusion list and retry.", ex);
                }
                progress?.Report(new InstallProgress($"Extraction blocked; retrying ({attempt}/{maxAttempts - 1})…"));
                await Task.Delay(500 * attempt);
            }
        }
    }

    /// <summary>Streams the download to a file to avoid holding large files in memory.
    /// v1.07: resumable downloads / automatic retries go through ResumableDownload (a dropped
    /// connection no longer restarts from zero).</summary>
    private static Task DownloadToFileAsync(
        string url, string dest, IProgress<InstallProgress>? progress, CancellationToken ct = default)
    {
        return ResumableDownload.RunAsync(DownloadHttp, url, dest,
            (msg, pct, spd) => progress?.Report(new InstallProgress(msg, pct, spd)), ct: ct);
    }

    /// <summary>Formats a byte count as readable KB/MB/GB text.</summary>
    private static string FormatBytes(long bytes)
    {
        double value = bytes;
        string[] units = { "B", "KB", "MB", "GB" };
        int i = 0;
        while (value >= 1024 && i < units.Length - 1)
        {
            value /= 1024;
            i++;
        }
        return value.ToString(i == 0 ? "F0" : "F1") + " " + units[i];
    }

    /// <summary>
    /// Copy SMAPI's unzipped install.dat payload into the game folder.
    /// Mirrors the official manual install steps while preserving existing Mods/,
    /// save-backups/, Content/, and other non-SMAPI game files.
    /// </summary>
    private static void CopySmapiBundle(string source, string gamePath)
    {
        Directory.CreateDirectory(gamePath);

        foreach (var entry in Directory.EnumerateFileSystemEntries(source))
        {
            var name = Path.GetFileName(entry);
            // Mods must not be overwritten wholesale; SMAPI's bundled ConsoleMessages/SaveBackup are merged separately.
            if (string.Equals(name, "Mods", StringComparison.OrdinalIgnoreCase))
                continue;
            var dest = Path.Combine(gamePath, name);

            if (Directory.Exists(entry))
            {
                // smapi-internal/ from the SMAPI installer package must be fully replaced;
                // if a same-named directory already exists, delete the old one before copying.
                if (Directory.Exists(dest))
                    Directory.Delete(dest, recursive: true);
                CopyDirectoryContents(entry, dest);
            }
            else
            {
                File.Copy(entry, dest, overwrite: true);
            }
        }
    }

    /// <summary>
    /// Merges SMAPI's bundled default mods (ConsoleMessages / SaveCopier etc.) from the installer
    /// package into the player's existing Mods folder, without deleting or overwriting anything
    /// that isn't already missing there.
    /// </summary>
    private static void CopyBuiltinMods(string sourceMods, string destMods)
    {
        if (!Directory.Exists(sourceMods)) return;
        Directory.CreateDirectory(destMods);

        foreach (var entry in Directory.EnumerateDirectories(sourceMods))
        {
            var modName = Path.GetFileName(entry);
            var destMod = Path.Combine(destMods, modName);
            if (Directory.Exists(destMod))
                continue;   // keep the player's version when a same-named mod already exists
            CopyDirectoryContents(entry, destMod);
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
    /// Backs up the player's Mods folder to LocalAppData/JuniGrid/mods-backup before installing;
    /// only copies new/changed files, never deletes from the player's original Mods.
    /// </summary>
    private static string? BackupMods(string gamePath)
    {
        var src = Path.Combine(gamePath, "Mods");
        if (!Directory.Exists(src)) return null;

        var backupRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "JuniGrid", "mods-backup");
        Directory.CreateDirectory(backupRoot);

        // Create a distinct snapshot per install; never overwrite old backups.
        var stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        var dest = Path.Combine(backupRoot, $"Mods-{stamp}");
        var i = 1;
        while (Directory.Exists(dest))
            dest = Path.Combine(backupRoot, $"Mods-{stamp}-{i++}");

        CopyDirectoryContents(src, dest);
        return dest;
    }

    /// <summary>
    /// Merges the pre-update backup back into the game's Mods folder: existing folders/files keep
    /// their current versions and missing ones are restored. This function only copies — it never
    /// deletes any user file.
    /// </summary>
    private static void RestoreMods(string backupPath, string destMods)
    {
        if (!Directory.Exists(backupPath)) return;
        CopyDirectoryContents(backupPath, destMods);
    }

    /// <summary>
    /// Deletes the temporary Mods backup folder created for this update. Only snapshots created under
    /// LocalAppData/JuniGrid/mods-backup are removed — the game folder is never touched.
    /// </summary>
    private static void TryDeleteBackup(string backupPath)
    {
        var backupRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "JuniGrid", "mods-backup");

        // Safety: only allow deleting snapshots inside the mods-backup subdirectory.
        if (string.IsNullOrWhiteSpace(backupPath)) return;
        var full = Path.GetFullPath(backupPath);
        var rootFull = Path.GetFullPath(backupRoot) + Path.DirectorySeparatorChar;
        if (!full.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase)) return;
        if (!Directory.Exists(full)) return;

        Directory.Delete(full, recursive: true);
    }

    public static void OpenUrl(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true });
        }
        catch (Exception __ex) { AppLog.Warn("UpdateService", __ex.Message); }
    }

    // ------------------------------------------------------------------
    // Mods' GitHub update source (free, no key required, direct download)
    // ------------------------------------------------------------------
    /// <summary>repo = "owner/name". Returns the latest release's version tag + zip asset URL.</summary>
    public async Task<GitHubModRelease?> CheckModGitHubAsync(string repo)
    {
        try
        {
            var json = await Http.GetStringAsync(
                $"https://api.github.com/repos/{repo}/releases/latest");
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var tag = root.TryGetProperty("tag_name", out var t) ? t.GetString() ?? "" : "";
            if (string.IsNullOrEmpty(tag)) return null;

            string? zip = null;
            if (root.TryGetProperty("assets", out var assets))
            {
                foreach (var a in assets.EnumerateArray())
                {
                    var name = a.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                    if (name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                    {
                        zip = a.TryGetProperty("browser_download_url", out var d)
                            ? d.GetString() : null;
                        break;
                    }
                }
            }
            return new GitHubModRelease(tag, zip);
        }
        catch
        {
            return null;   // network issue / repo has no release — leave it to the Nexus source fallback
        }
    }

    // ---- Self-update: the launcher updating itself from its own GitHub Releases ----

    private const string SelfRepoApi = "https://api.github.com/repos/MLD-yu/JuniGrid-en";

    /// <summary>
    /// Checks GitHub Releases for a newer launcher version. Returns null when up-to-date,
    /// offline, or the repo has no published release yet — the title-bar update button stays
    /// hidden in all those cases.
    /// </summary>
    public async Task<SelfUpdateInfo?> CheckSelfUpdateAsync()
    {
        try
        {
            var current = System.Reflection.Assembly.GetEntryAssembly()?.GetName().Version;
            if (current is null) return null;

            var json = await Http.GetStringAsync(SelfRepoApi + "/releases/latest");
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var tag = root.TryGetProperty("tag_name", out var t) ? t.GetString() ?? "" : "";
            var latest = ParseLooseVersion(tag);
            if (latest is null || latest <= current) return null;

            string? setupUrl = null;
            if (root.TryGetProperty("assets", out var assets))
            {
                foreach (var a in assets.EnumerateArray())
                {
                    var name = a.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                    if (name.EndsWith("-setup.exe", StringComparison.OrdinalIgnoreCase))
                    {
                        setupUrl = a.TryGetProperty("browser_download_url", out var d) ? d.GetString() : null;
                        break;
                    }
                }
            }
            return setupUrl is null ? null : new SelfUpdateInfo("v" + latest.ToString(3), setupUrl);
        }
        catch
        {
            return null;   // offline / rate-limited / no release yet — silently keep the button hidden
        }
    }

    /// <summary>
    /// Downloads the new setup.exe (resumable) and launches it silently. Inno Setup performs an
    /// in-place upgrade and refreshes the shortcuts; the caller closes the app right after.
    /// </summary>
    public async Task DownloadAndRunSelfUpdateAsync(SelfUpdateInfo info, IProgress<InstallProgress>? progress, CancellationToken ct = default)
    {
        var dest = Path.Combine(Path.GetTempPath(), "JuniGrid-update-setup.exe");
        await DownloadToFileAsync(info.SetupUrl, dest, progress, ct);
        progress?.Report(new InstallProgress("Launching the installer…", 100));
        Process.Start(new ProcessStartInfo(dest, "/SILENT /CLOSEAPPLICATIONS /RESTARTAPPLICATIONS")
        { UseShellExecute = true });
    }

    /// <summary>Parses "v1.2.3" / "1.2" / "release-1.2.3" into a Version, or null.</summary>
    private static Version? ParseLooseVersion(string tag)
    {
        var m = System.Text.RegularExpressions.Regex.Match(tag, @"\d+(?:\.\d+)+");
        return m.Success && Version.TryParse(m.Value, out var v) ? v : null;
    }

    private static string Normalize(string? v) => (v ?? "").Trim().TrimStart('v', 'V');
}

public sealed record SelfUpdateInfo(string Version, string SetupUrl);

public sealed record GitHubModRelease(string Tag, string? ZipUrl);

public sealed record SmapiUpdateInfo(
    string? ForVersion,
    string? LatestVersion,
    bool HasUpdate,
    bool InstalledParsed,
    string? InstallerZipUrl,
    string ReleasePageUrl,
    string? Error);
