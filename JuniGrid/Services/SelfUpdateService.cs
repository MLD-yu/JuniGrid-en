using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text.Json;

namespace JuniGrid.Services;

/// <summary>Result of one self-update check.</summary>
public sealed record SelfUpdateInfo(string LatestVersion, string DownloadUrl, string SetupUrl, bool HasUpdate);

/// <summary>
/// The single channel for app self-update (check + download cache + launch installer):
///  1) api.github.com /releases/latest — full info, but the anonymous quota is 60 requests/hour/IP and easily rate-limited;
///  2) github.com/.../releases/latest HTML 302 fallback — the final redirect URL carries the tag, not subject to the API quota.
/// The installer downloads to StoragePaths.SelfUpdateDir (resumable):
///  · fully cached → clicking the button launches the installer wizard directly, no re-download;
///  · wizard closed by the user → the cache is kept; the next click relaunches it directly;
///  · after a successful install (app version >= installer version), the cache is cleared automatically on next startup.
/// </summary>
public sealed class SelfUpdateService
{
    private const string TagMarker = "/releases/tag/";
    private static readonly HttpClient Http = CreateClient();
    private static readonly HttpClient DownloadHttp = CreateClient(minutes: 10);
    private volatile SelfUpdateInfo? _latest;

    /// <summary>Most recent check result; null = nothing found yet (not checked or failed).</summary>
    public SelfUpdateInfo? Latest => _latest;

    /// <summary>Notifies after a check completes (UI subscribes to refresh the badge/button).</summary>
    public event Action? Changed;

    private static HttpClient CreateClient(double minutes = 0.2)
    {
        var h = new HttpClient();
        h.DefaultRequestHeaders.UserAgent.ParseAdd("JuniGrid-Launcher");
        h.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        h.Timeout = TimeSpan.FromMinutes(minutes);
        return h;
    }

    /// <summary>Starts a background pre-check (non-blocking, silent on failure) and also clears the installer cache for already-installed versions.</summary>
    public void StartBackgroundCheck()
        => _ = Task.Run(async () =>
        {
            CleanupOldInstallers();
            try { await CheckAsync(); } catch { }
        });

    /// <summary>Requests the latest version from GitHub and compares it with the current version (falls back to HTML automatically if the API fails).</summary>
    public async Task<SelfUpdateInfo?> CheckAsync()
    {
        SelfUpdateInfo? result = await TryCheckViaApiAsync() ?? await TryCheckViaHtmlAsync();
        if (result is not null)
        {
            _latest = result;
            try { Changed?.Invoke(); } catch { }
        }
        return _latest;
    }

    /// <summary>Returns the installer path if fully cached (a .done marker is written after a successful download).</summary>
    public string? CachedInstallerPath(SelfUpdateInfo info)
    {
        var dest = InstallerPath(info.LatestVersion);
        return File.Exists(dest) && File.Exists(dest + ".done") ? dest : null;
    }

    public static string InstallerPath(string version)
        => Path.Combine(StoragePaths.SelfUpdateDir, $"JuniGrid-en-v{version}-setup.exe");

    /// <summary>
    /// Ensures the installer is cached: returns the path directly if fully cached; otherwise downloads (resumable) to the cache directory.
    /// </summary>
    public async Task<string> EnsureInstallerAsync(SelfUpdateInfo info,
        Action<string, double?>? progress, CancellationToken ct = default)
    {
        var cached = CachedInstallerPath(info);
        if (cached is not null)
        {
            progress?.Invoke("Installer ready", 100);
            return cached;
        }

        Directory.CreateDirectory(StoragePaths.SelfUpdateDir);
        var dest = InstallerPath(info.LatestVersion);
        await ResumableDownload.RunAsync(DownloadHttp, info.SetupUrl, dest,
            (msg, pct, _) => progress?.Invoke(msg, pct), ct: ct);

        // write the .done marker only after the download finishes fully; half-written files left by cancellation/interruption are continued via resume
        File.WriteAllText(dest + ".done", info.LatestVersion);
        progress?.Invoke("Download complete", 100);
        return dest;
    }

    /// <summary>Launches the installer wizard (visible, not silent). The old app exits on its own via the caller,
    /// so /CLOSEAPPLICATIONS is not passed — no "close applications" prompt appears;
    /// clicking "Finish" in the wizard has Inno start the new version automatically per [Run]; closing the wizard outright doesn't affect the cache.</summary>
    public void LaunchInstaller(string path)
    {
        Process.Start(new ProcessStartInfo(path)
        { UseShellExecute = true });
    }

    /// <summary>Startup cleanup: cached installer version <= current app version means already installed — delete the cache.</summary>
    private static void CleanupOldInstallers()
    {
        try
        {
            var dir = StoragePaths.SelfUpdateDir;
            if (!Directory.Exists(dir)) return;
            if (!Version.TryParse(AppInfo.Version.Trim(), out var current)) return;

            foreach (var exe in Directory.GetFiles(dir, "JuniGrid-en-v*-setup.exe"))
            {
                var m = System.Text.RegularExpressions.Regex.Match(
                    Path.GetFileName(exe), @"\d+(?:\.\d+)+");
                if (!m.Success || !Version.TryParse(m.Value, out var v)) continue;
                if (v <= current)
                {
                    File.Delete(exe);
                    if (File.Exists(exe + ".done")) File.Delete(exe + ".done");
                    AppLog.Warn("SelfUpdate", $"Cleaned up old installer cache: {Path.GetFileName(exe)}");
                }
            }
        }
        catch (Exception ex)
        {
            AppLog.Warn("SelfUpdate", $"Failed to clean up installer cache: {ex.Message}");
        }
    }

    // Channel 1: GitHub API (full info, but rate-limited)
    private async Task<SelfUpdateInfo?> TryCheckViaApiAsync()
    {
        try
        {
            using var resp = await Http.GetAsync(AppInfo.LatestApiUrl);
            if (!resp.IsSuccessStatusCode)
            {
                AppLog.Warn("SelfUpdate", $"API channel failed: HTTP {(int)resp.StatusCode}, falling back to HTML");
                return null;
            }
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStreamAsync());
            var root = doc.RootElement;

            var tag = root.TryGetProperty("tag_name", out var t) ? t.GetString() ?? "" : "";
            return Build(tag);
        }
        catch (Exception ex)
        {
            AppLog.Warn("SelfUpdate", $"API channel error: {ex.Message}, falling back to HTML");
            return null;
        }
    }

    // Channel 2: pull the tag from the releases/latest page's 302 redirect (same scheme as the SMAPI check, no quota limit)
    private async Task<SelfUpdateInfo?> TryCheckViaHtmlAsync()
    {
        try
        {
            using var resp = await Http.GetAsync(AppInfo.ReleasesUrl + "/latest");
            var finalUrl = resp.RequestMessage?.RequestUri?.ToString() ?? "";
            var marker = finalUrl.IndexOf(TagMarker, StringComparison.OrdinalIgnoreCase);
            if (!resp.IsSuccessStatusCode || marker < 0)
            {
                AppLog.Warn("SelfUpdate", $"HTML fallback failed: HTTP {(int)resp.StatusCode}");
                return null;
            }
            var tag = finalUrl[(marker + TagMarker.Length)..];
            return Build(tag);
        }
        catch (Exception ex)
        {
            AppLog.Warn("SelfUpdate", $"HTML fallback error: {ex.Message}");
            return null;
        }
    }

    private static SelfUpdateInfo? Build(string tag)
    {
        // the tag may carry a v prefix (both v1.0.2 and 1.0.2 are accepted)
        var ver = tag.TrimStart('v', 'V');
        if (!Version.TryParse(ver, out var latest)) return null;
        if (!Version.TryParse(AppInfo.Version.Trim(), out var current)) return null;

        // Release asset fixed naming: JuniGrid-en-vX.Y.Z-setup.exe
        var setupUrl = $"{AppInfo.ReleasesUrl}/latest/download/JuniGrid-en-v{ver}-setup.exe";
        var hasUpdate = latest > current;
        return new SelfUpdateInfo(ver, $"{AppInfo.ReleasesUrl}/tag/v{ver}", setupUrl, hasUpdate);
    }
}
