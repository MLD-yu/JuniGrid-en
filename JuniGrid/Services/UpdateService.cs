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
        // Accept header recommended by the GitHub API; lowers the chance of being rate-limited.
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
    public async Task<SmapiUpdateInfo> CheckSmapiAsync(string? installedVersion, bool force = false)
    {
        // Caching policy: reuse a successful result for the same local version or within 20 minutes;
        // the last failure (GitHub rate limit / offline) is not cached permanently — retry when the user clicks "Check for updates".
        // v1.08: force = manual refresh, bypassing the 5-minute cache to force a re-check.
        if (!force
            && _cached is not null
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
            //   SMAPI-x.y.z-installer.zip                (a normal single-layer zip — take this one)
            //   SMAPI-x.y.z-installer-double-zipped.zip  (an extra outer layer, for some browser protection policies)
            // The old loop hit "double-zipped" first, so the extraction still yielded a zip
            // with no SMAPI.Installer.exe inside. Explicitly skip double-zipped here.
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

            // Fix: when ProbeSmapiVersion cannot extract a version it returns the string "installed";
            // Version.TryParse("installed") fails → hasUpdate stays false forever,
            // and the UI wrongly shows "already up to date". As a safety net, when the local version cannot
            // be parsed but SMAPI is installed (installedVersion is not null), report an update as available
            // as long as a remote tag was obtained, instead of silently treating it as up to date.
            var installedOk = Version.TryParse(Normalize(installedVersion), out var installed);
            var remoteOk = Version.TryParse(Normalize(tag), out var latest);
            bool hasUpdate;
            if (installedOk && remoteOk)
                hasUpdate = latest > installed;
            else if (!installedOk && remoteOk && !string.IsNullOrWhiteSpace(installedVersion))
                hasUpdate = true;   // local version unparseable but SMAPI is installed; conservatively report an update
            else
                hasUpdate = false;

            _cached = new SmapiUpdateInfo(
                installedVersion, string.IsNullOrEmpty(tag) ? null : tag,
                hasUpdate, installedOk, zipUrl, pageUrl, null);
            _cachedAt = DateTime.Now;
        }
        catch (Exception ex)
        {
            // v0.38.0: when the API is rate-limited (403 rate limit), fall back to parsing the HTML release page —
            // the 302 redirect URL of /releases/latest carries the tag and is not subject to the API quota (60/hour/IP).
            var fallback = await TryCheckSmapiViaHtmlAsync(installedVersion);
            if (fallback is not null)
            {
                _cached = fallback;
                _cachedAt = DateTime.Now;
                return _cached;
            }

            // Offline / rate-limited — do not cache the timestamp; retry on the next check.
            var friendly = ex.Message.Contains("403") || ex.Message.Contains("rate limit", StringComparison.OrdinalIgnoreCase)
                ? "The GitHub API is rate-limited right now (60 requests/hour); try again later or click Check for updates to retry"
                : ex.Message;
            _cached = new SmapiUpdateInfo(
                installedVersion, null, false, false, null, "https://smapi.io", friendly);
            _cachedAt = DateTime.MinValue;
        }
        return _cached;
    }

    /// <summary>
    /// v0.38.0: fallback channel when the GitHub API is rate-limited — request releases/latest (HTML),
    /// extract the tag from the final URL after the redirect (e.g. /releases/tag/4.5.2),
    /// then build the installer zip download URL from the official naming scheme.
    /// HTML pages use a separate quota that normal use almost never exhausts.
    /// </summary>
    private async Task<SmapiUpdateInfo?> TryCheckSmapiViaHtmlAsync(string? installedVersion)
    {
        try
        {
            // HttpClient follows redirects by default; the final RequestUri looks like
            // https://github.com/Pathoschild/SMAPI/releases/tag/4.5.2
            // v1.08: when the direct connection fails (github.com:443 unreachable), try the mirrors in order;
            // the final URL returned by a mirror also contains /releases/tag/, so tag parsing is unchanged.
            HttpResponseMessage? resp = null;
            foreach (var url in GithubUrls("https://github.com/Pathoschild/SMAPI/releases/latest"))
            {
                try
                {
                    resp = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
                    if (resp.IsSuccessStatusCode) break;
                    resp.Dispose(); resp = null;
                }
                catch { /* try the next channel */ }
            }
            if (resp is null) return null;

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
    // Game latest version (remote, Stardew Valley Wiki) — checked silently at startup
    // ------------------------------------------------------------------
    /// <summary>
    /// Fetches the current version from the Stardew Valley Wiki as the source of truth for "latest game version".
    /// On failure (offline / page redesign) it silently returns null, so the UI shows no "new version available" hint.
    /// Returns a plain version string, e.g. "1.6.15".
    /// </summary>
    public async Task<string?> GetLatestGameVersionAsync()
    {
        try
        {
            var html = await Http.GetStringAsync(
                "https://stardewvalleywiki.com/Main_Page");

            // First try to pinpoint the current game version exactly: the Version History link text in the
            // wiki sidebar is the current game version, e.g. <a ... title="Version History">1.6.15</a>.
            // Never take the "max" of every 1.x.y on the page — it also mixes in many other mod/number values
            // (e.g. 1.35.1), which would be misread as "the latest version is newer than local".
            var precise = System.Text.RegularExpressions.Regex.Match(
                html, @"title=""Version History""[^>]*>\s*([0-9]+\.[0-9]+\.[0-9]+)");
            if (precise.Success) return precise.Groups[1].Value;

            // Fallback: the first 1.x.y on the page (usually the latest too); return null silently on failure
            var first = System.Text.RegularExpressions.Regex.Match(html, @"1\.\d+\.\d+");
            return first.Success ? first.Value : null;
        }
        catch
        {
            return null;   // offline / blocked / page redesign → stay silent
        }
    }

    // ------------------------------------------------------------------
    // Run the official SMAPI installer — fully automatic and unattended
    // ------------------------------------------------------------------
    /// <summary>
    /// Downloads the official installer and silently runs SMAPI.Installer.exe in the background
    /// (--install --no-prompt --game-path); no window ever pops up and no browser opens.
    /// Returns null on success; otherwise returns an error message.
    /// The SMAPI installer natively supports unattended switches (see the InteractiveInstaller source):
    ///   --no-prompt   disable interactive prompts
    ///   --install     perform the install/update
    ///   --game-path   specify the game folder (skips auto-detection)
    /// </summary>
    public async Task<string?> RunSmapiInstallerAsync(
        SmapiUpdateInfo info, string? gamePath,
        IProgress<InstallProgress>? progress = null)
    {
        string? temp = null;
        try
        {
            if (info.InstallerZipUrl is null)
                throw new InvalidOperationException("Could not find a download URL for the SMAPI installer");
            if (string.IsNullOrWhiteSpace(gamePath) || !Directory.Exists(gamePath))
                throw new InvalidOperationException("No game folder is set yet — choose one on the Settings page first");

            // v0.2.1: use the shared cache directory (still LocalAppData by default). Do not extract straight into %TEMP%:
            // Defender scans .NET runtime DLLs in %TEMP% more aggressively and often locks them mid-write.
            // The timestamped folder name avoids colliding with an old cache.
            var stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            temp = Path.Combine(
                StoragePaths.SmapiInstallerDir,
                $"{info.LatestVersion ?? "latest"}-{stamp}");
            Directory.CreateDirectory(temp);

            // Back up the player's Mods before downloading, so a failure at any step of download/extract/install
            // never touches the original folder; after installation the backup is merged back into Mods and the
            // temporary backup deleted.
            // v1.08: the backup is a whole-folder copy of hundreds of MB and must run off the UI thread (the old
            // version copied synchronously, freezing the entire UI from the click until the backup finished); the
            // progress bar also announces the backup phase.
            progress?.Report(new InstallProgress("Backing up the existing Mods folder...", 0, 0));
            var modsBackup = await Task.Run(() => BackupMods(gamePath));
            if (modsBackup is not null)
                progress?.Report(new InstallProgress("Existing Mods folder backed up; starting the SMAPI download...", 0, 0));

            progress?.Report(new InstallProgress("Downloading the SMAPI installer..."));
            progress?.Report(new InstallProgress("Connecting to the download server...", 0, 0));
            var zip = Path.Combine(temp, "smapi-installer.zip");
            await DownloadToFileAsync(info.InstallerZipUrl, zip, progress);

            progress?.Report(new InstallProgress("Extracting the installer..."));
            await Task.Run(() => ExtractWithRetryAsync(zip, temp, progress));   // v1.08: off the UI thread

            // Safety net: if this zip is a "double-zipped" shell, the extraction is still a zip — unpack one more layer automatically.
            var innerZip = Directory
                .GetFiles(temp, "*.zip", SearchOption.AllDirectories)
                .FirstOrDefault(f => !string.Equals(f, zip, StringComparison.OrdinalIgnoreCase));
            if (innerZip is not null)
            {
                progress?.Report(new InstallProgress("Inner archive detected; extracting again..."));
                await Task.Run(() => ExtractWithRetryAsync(innerZip, temp, progress));   // v1.08
            }

            // The official SMAPI 4.5.x installer has a known issue: even in --install --no-prompt mode it still
            // calls Console.Clear(). If the installer process's stdout/input handles are not a real console
            // (exactly the case when JuniGrid runs it as a hidden WPF background process), Console.Clear() throws
            // an "invalid handle" IOException and the install fails outright. Flashing a console window repeatedly can still fail.
            //
            // So instead, following the SMAPI README's manual install steps, install directly from the
            // internal/windows folder inside the official installer package: the official README explicitly supports
            // extracting install.dat and copying it into the game folder — no console needed at all, and a more reliable approach for a launcher.
            progress?.Report(new InstallProgress("Installing SMAPI in the background (manual install of the official files)...", 90));

            var windowsFiles = Path.Combine(temp, "SMAPI " + info.LatestVersion + " installer", "internal", "windows");
            if (!Directory.Exists(windowsFiles))
            {
                // Structure-change safety net: recursively look for install.dat / StardewModdingAPI.exe
                var installDat = Directory
                    .GetFiles(temp, "install.dat", SearchOption.AllDirectories)
                    .FirstOrDefault(f => f.Contains(Path.Combine("windows", "install.dat"), StringComparison.OrdinalIgnoreCase));
                if (installDat is not null)
                    windowsFiles = Path.GetDirectoryName(installDat)!;
            }
            // install.dat is actually a zip (only the extension was changed to stop casual extraction).
            // Official manual install steps:
            //   1. extract install.dat to a temporary folder
            //   2. copy the extracted files over the game folder
            //   3. copy "Stardew Valley.deps.json" → "StardewModdingAPI.deps.json"
            var dat = Path.Combine(windowsFiles, "install.dat");
            if (!File.Exists(dat))
                throw new InvalidOperationException(
                    "SMAPI installer payload not found after extraction (internal/windows/install.dat does not exist — did the package layout change?)");

            var extracted = Path.Combine(temp, "smapi-files");
            Directory.CreateDirectory(extracted);
            progress?.Report(new InstallProgress("Extracting the install contents...", 95));
            await Task.Run(() => ExtractWithRetryAsync(dat, extracted, progress));   // v1.08

            if (!File.Exists(Path.Combine(extracted, "StardewModdingAPI.exe")))
                throw new InvalidOperationException("StardewModdingAPI.exe is missing from the SMAPI installer package (package looks broken)");

            // Remove the old SMAPI files before copying the new ones (avoids file locks / leftover version files).
            // v1.08: copying/cleanup are hundreds-of-MB disk jobs and must not freeze the UI.
            await Task.Run(() => CopySmapiBundle(extracted, gamePath));

            // Keep the game's own deps.json and runtimeconfig.json after the update; SMAPI's
            // StardewModdingAPI.deps.json needs to be generated/overwritten on top of the game's main file.
            var gameDeps = Path.Combine(gamePath, "Stardew Valley.deps.json");
            if (File.Exists(gameDeps))
                File.Copy(gameDeps, Path.Combine(gamePath, "StardewModdingAPI.deps.json"), true);

            // After installation, copy the backed-up Mods back so not a single user mod is lost;
            // the official install.dat also ships SMAPI's own default mods such as ConsoleCommands/SaveBackup,
            // so restore the backup first, then re-add any missing default mods — never overwrite the whole folder.
            if (modsBackup is not null)
            {
                progress?.Report(new InstallProgress("Restoring the Mods folder...", 100));
                await Task.Run(() => RestoreMods(modsBackup, Path.Combine(gamePath, "Mods")));   // v1.08: off the UI thread
            }
            CopyBuiltinMods(Path.Combine(extracted, "Mods"), Path.Combine(gamePath, "Mods"));

            // Then delete the temporary backup folder this update created.
            if (modsBackup is not null)
            {
                await Task.Run(() => TryDeleteBackup(modsBackup));   // v1.08: deleting the backup is heavy IO too
            }

            progress?.Report(new InstallProgress($"SMAPI {info.LatestVersion} installed", 100, 0));
            Invalidate();
            return null;
        }
        catch (Exception ex)
        {
            // v1.1.6: on failure, delete this run's temporary directory (download + double extraction) — the old
            // version left it all (hundreds of MB) in the cache folder until the user cleared the cache manually.
            // modsBackup is kept so the user can recover manually.
            if (temp is not null)
            { try { Directory.Delete(temp, true); } catch { } }
            return "SMAPI automatic install failed: " + ex.Message;
        }
    }

    /// <summary>
    /// Progress message for the SMAPI install flow. percent/speed are optional: only the stages reported by
    /// DownloadToFileAsync carry values; the extract/install stages only update the text and the stage percentage.
    /// </summary>
    public sealed record InstallProgress(
        string Message,
        double? Percent = null,
        double? SpeedMBps = null);


    // Large downloads use a separate long-timeout client (the version-check Http has only a 12s timeout,
    // which would cut off the SMAPI installer download).
    private static readonly HttpClient DownloadHttp = CreateDownloadClient();

    private static HttpClient CreateDownloadClient()
    {
        var h = new HttpClient();
        h.DefaultRequestHeaders.UserAgent.ParseAdd("JuniGrid-Launcher");
        h.Timeout = TimeSpan.FromMinutes(5);
        return h;
    }

    /// <summary>
    /// Extraction with automatic retries: Defender often locks clrjit.dll/coreclr.dll the instant they are
    /// written, causing UnauthorizedAccessException / IOException; waiting a moment and retrying resolves it.
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
                        + "Temporarily add %LocalAppData%\\JuniGrid to the exclusions and retry.", ex);
                }
                progress?.Report(new InstallProgress($"Extraction blocked, retrying ({attempt}/{maxAttempts - 1})..."));
                await Task.Delay(500 * attempt);
            }
        }
    }

    /// <summary>Streams the download to a file so large files never sit in memory.
    /// v1.07: resumable downloads / automatic retries all go through ResumableDownload (a dropped connection no longer restarts from 0).
    /// v1.08: GitHub assets automatically get mirror candidates — if the direct download fails with 0 bytes, switch to a mirror immediately.</summary>
    private static Task DownloadToFileAsync(
        string url, string dest, IProgress<InstallProgress>? progress, CancellationToken ct = default)
    {
        var fallback = url.StartsWith("https://github.com/", StringComparison.OrdinalIgnoreCase)
            ? GithubUrls(url).Skip(1)
            : null;
        return ResumableDownload.RunAsync(DownloadHttp, url, dest,
            (msg, pct, spd) => progress?.Report(new InstallProgress(msg, pct, spd)),
            fallbackUrls: fallback, ct: ct);
    }

    // ------------------------------------------------------------------
    // v1.08: mirror acceleration — when the GitHub direct connection fails (443 refused), switch mirror prefixes automatically.
    // Verified 2026-09: ghfast.top / gh-proxy.com / ghproxy.net can all proxy
    // releases/download; for api.github.com only gh-proxy.com works.
    // ------------------------------------------------------------------
    internal static readonly string[] GithubMirrorPrefixes =
        { "https://ghfast.top/", "https://gh-proxy.com/", "https://ghproxy.net/" };

    /// <summary>Yields in order: the original URL → each mirror-prefixed URL. Try them one by one until one succeeds.</summary>
    public static IEnumerable<string> GithubUrls(string url)
    {
        yield return url;
        foreach (var p in GithubMirrorPrefixes)
            yield return p + url;
    }

    // v1.1.6: the private FormatBytes copy here was removed (no callers) — use ResumableDownload.FormatBytes everywhere.

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
                // smapi-internal/ from the SMAPI installer needs a complete overwrite;
                // if a folder of the same name already exists, delete the old one before copying.
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
    /// Merges the default mods bundled with the SMAPI installer (ConsoleMessages / SaveCopier, etc.) into
    /// the player's existing Mods folder without deleting or overwriting anything already there.
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
                continue;   // keep the player's version when a mod with the same name already exists
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
    /// Before installing, fully back up the player's Mods folder to LocalAppData/JuniGrid/mods-backup;
    /// only copies new/changed files and never deletes the player's original Mods.
    /// </summary>
    private static string? BackupMods(string gamePath)
    {
        var src = Path.Combine(gamePath, "Mods");
        if (!Directory.Exists(src)) return null;

        var backupRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "JuniGrid", "mods-backup");
        Directory.CreateDirectory(backupRoot);

        // Create an independent snapshot before each install; old backups are never overwritten.
        var stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        var dest = Path.Combine(backupRoot, $"Mods-{stamp}");
        var i = 1;
        while (Directory.Exists(dest))
            dest = Path.Combine(backupRoot, $"Mods-{stamp}-{i++}");

        CopyDirectoryContents(src, dest);
        return dest;
    }

    /// <summary>
    /// Merges the pre-update backup back into the game's Mods: existing folders/files keep their current
    /// version and missing ones are restored. This function only copies; it never deletes any user files.
    /// </summary>
    private static void RestoreMods(string backupPath, string destMods)
    {
        if (!Directory.Exists(backupPath)) return;
        CopyDirectoryContents(backupPath, destMods);
    }

    /// <summary>
    /// Deletes the temporary Mods backup folder this update created. Only removes the snapshot just created
    /// under LocalAppData/JuniGrid/mods-backup and never touches the game folder.
    /// </summary>
    private static void TryDeleteBackup(string backupPath)
    {
        var backupRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "JuniGrid", "mods-backup");

        // Safety: only snapshots inside the mods-backup subfolder may ever be deleted.
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
    // GitHub update source for mods (free, no key required, direct download)
    // ------------------------------------------------------------------
    /// <summary>repo = "owner/name". Returns the latest release's version tag + zip asset URL.
    /// v1.08: when api.github.com is unreachable directly, go through the gh-proxy.com mirror (in practice the only mirror that proxies the API).</summary>
    public async Task<GitHubModRelease?> CheckModGitHubAsync(string repo)
    {
        var api = $"https://api.github.com/repos/{repo}/releases/latest";
        foreach (var url in GithubUrls(api))
        {
            try
            {
                var json = await Http.GetStringAsync(url);
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
                continue;   // try the next channel (mirror/direct)
            }
        }
        return null;   // every channel failed — let the Nexus source handle the fallback
    }

    private static string Normalize(string? v) => (v ?? "").Trim().TrimStart('v', 'V');
}

public sealed record GitHubModRelease(string Tag, string? ZipUrl);

public sealed record SmapiUpdateInfo(
    string? ForVersion,
    string? LatestVersion,
    bool HasUpdate,
    bool InstalledParsed,
    string? InstallerZipUrl,
    string ReleasePageUrl,
    string? Error);
