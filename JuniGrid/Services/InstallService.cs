using System.IO;
using Microsoft.Win32;

namespace JuniGrid.Services;

/// <summary>
/// Handles nxm:// links handed over by Windows (via the single-instance
/// named pipe, or startup args), downloads the file and installs it into
/// Mods/. Works for FREE Nexus accounts: the key+expires inside the nxm
/// link come from the user clicking "Mod Manager Download" on the website.
/// </summary>
public sealed class InstallService
{
    private readonly ConfigService _cfg;
    private readonly NexusService _nexus;
    private readonly ModService _mods;
    private readonly UpdateQueueService _queue;
    private readonly TaskCenterService _center;

    public InstallService(ConfigService cfg, NexusService nexus, ModService mods,
        UpdateQueueService queue, TaskCenterService center)
    {
        _cfg = cfg;
        _nexus = nexus;
        _mods = mods;
        _queue = queue;
        _center = center;
    }

    public event Action? OnChanged;

    /// <summary>Newest-first status feed, shown on the Mods page.</summary>
    public List<string> RecentStatus { get; } = new();

    public bool Busy { get; private set; }

    // ------------------------------------------------------------------
    // Direct install (no built-in browser popup)
    // ------------------------------------------------------------------
    // When "Install" is clicked on the ModDetail / trending pages, the app requests a one-time
    // download link from Nexus in the background and streams the download + install,
    // never leaving the launcher. Free accounts are throttled to about 1MB/s;
    // on 403 (mod is Premium-only) the caller falls back to opening the built-in browser.
    // ------------------------------------------------------------------

    /// <summary>
    /// One-click direct install: downloads and installs the latest MAIN file of the given mod in the background.
    /// Returns null on success; otherwise returns an error message (containing the "premium" keyword means Premium is required).
    /// Progress feeds the task center: a task appears in the bottom-right corner and the /tasks page shows download percent/speed/per-step details.
    /// </summary>
    public async Task<string?> InstallModDirectAsync(int modId)
    {
        if (Busy) return "The previous install is still running — wait for it to finish and try again";

        var cfg = _cfg.Current;
        if (string.IsNullOrWhiteSpace(cfg.NexusApiKey))
            return "No Nexus API Key configured — paste one on the Nexus page first";
        if (string.IsNullOrWhiteSpace(cfg.GamePath))
            return "Game directory not set — choose one on the Settings page first";

        var taskTitle = await ResolveModTitleAsync(cfg.NexusApiKey, modId, null);
        var task = _center.Start($"Download and install {taskTitle}", "install");

        void Step(string msg, double? pct = null, double? speed = null) =>
            _center.Report(task, msg, pct, speed);

        Busy = true;
        try
        {
            Step("Fetching file info…", 2);
            var file = await _nexus.GetLatestMainFileAsync(cfg.NexusApiKey, modId);
            if (file is null) { _center.Finish(task, false, "No downloadable file found"); return "No downloadable file found"; }

            Step("Fetching download URL…", 5);
            var dl = await _nexus.GetDownloadUrlAsync(cfg.NexusApiKey, modId, file.FileId);
            if (dl.NeedsPremium)
            { _center.Finish(task, false, "Nexus Premium required; falling back to the web flow"); return "premium: direct download of this mod requires a Nexus Premium membership"; }
            if (dl.Url is null)
            { _center.Finish(task, false, "Failed to get download URL: " + (dl.Error ?? "unknown error")); return "Failed to get download URL: " + (dl.Error ?? "unknown error"); }

            var zip = Path.Combine(StoragePaths.DownloadsDir,
                $"direct-{modId}-{file.FileId}.zip");
            Directory.CreateDirectory(Path.GetDirectoryName(zip)!);

            var progress = new Progress<NexusDownloadProgress>(p =>
                Step(p.Message, p.Percent, p.SpeedMBps));
            Step($"Downloading {file.Name}…", 8, 0);
            await _nexus.DownloadFileAsync(dl.Url, zip, progress);

            Step("Installing into Mods…", 95);
            var err = _mods.InstallNew(cfg.GamePath, zip, out var modName);
            if (err is not null)
            { _center.Finish(task, false, "Install failed: " + err); return "Install failed: " + err; }

            _queue.NotifyInstalled(modId);
            // v0.69.0: record the "last download date" (shown under the detail-page title + as the green check on the Files tab)
            cfg.ModLastDownload[modId.ToString()] = DateTime.Now.ToString("yyyy-MM-dd");
            cfg.ModFileLastDownload[file.FileId.ToString()] = DateTime.Now.ToString("yyyy-MM-dd");
            _cfg.Save(cfg);
            var done = $"Install finished: {modName ?? "New Mod"}";
            _center.Finish(task, true, done);
            Notify("✅ " + done);
            return null;
        }
        catch (Exception ex)
        {
            _center.Finish(task, false, ex.Message);
            Notify("❌ " + ex.Message);
            return ex.Message;
        }
        finally
        {
            Busy = false;
        }
    }

    public async Task HandleNxmLinkAsync(string link)
    {
        if (Busy)
        {
            Notify("⏳ The previous install is still running — wait for it to finish before clicking again");
            return;
        }
        var task = _center.Start("Web one-click install (Nexus)", "install");

        void Step(string msg, double? pct = null, double? speed = null) =>
            _center.Report(task, msg, pct, speed);

        Busy = true;
        try
        {
            Step("Parsing the received Nexus download link…", 2);
            if (!TryParseNxm(link, out var modId, out var fileId, out var key, out var exp))
            {
                _center.Finish(task, false, "Could not parse the link");
                Notify("❌ Could not parse the link: " + link);
                return;
            }

            var cfg = _cfg.Current;
            if (string.IsNullOrWhiteSpace(cfg.NexusApiKey))
            {
                _center.Finish(task, false, "No Nexus API Key configured");
                Notify("❌ No Nexus API Key configured — paste one on the Nexus page first");
                return;
            }
            if (string.IsNullOrWhiteSpace(cfg.GamePath))
            {
                _center.Finish(task, false, "Game directory not set");
                Notify("❌ Game directory not set — choose one on the Settings page first");
                return;
            }

            // Once modId is parsed, enrich the task title with the mod name so it's recognizable on /tasks
            task.Title = "Download and install " + await ResolveModTitleAsync(cfg.NexusApiKey, modId, null);

            Step($"Fetching download URL (Mod #{modId})…", 8);
            // v0.62.0: restored the API key header — Nexus's download_link.json endpoint requires the apikey header;
            // even with key/expires in the URL it returns 401 without the header (removing the key in v0.61 introduced this error).
            var dl = await _nexus.GetNxmDownloadUrlAsync(cfg.NexusApiKey, modId, fileId, key, exp);
            if (dl.Url is null)
            {
                _center.Finish(task, false, dl.Error ?? "Failed to get download URL");
                Notify("❌ " + (dl.Error ?? "Failed to get download URL"));
                _queue.NotifyFailed(modId);   // expired/rejected link → skip, don't let the queue stall
                return;
            }

            var zip = Path.Combine(StoragePaths.DownloadsDir, $"nxm-{modId}-{fileId}.zip");
            Directory.CreateDirectory(Path.GetDirectoryName(zip)!);

            var progress = new Progress<NexusDownloadProgress>(p =>
                Step(p.Message, p.Percent, p.SpeedMBps));
            Step("Downloading…", 12, 0);
            await _nexus.DownloadFileAsync(dl.Url, zip, progress);

            Step("Installing into Mods…", 95);
            var err = _mods.InstallNew(cfg.GamePath, zip, out var modName);
            if (err is null)
            {
                _queue.NotifyInstalled(modId);   // update queue: one installed, advance automatically
                _center.Finish(task, true, $"Install finished: {modName ?? "New Mod"}");
                Notify($"✅ Install finished: {modName ?? "New Mod"} (now visible on the Mod Management page)");
            }
            else
            {
                _center.Finish(task, false, "Install failed: " + err);
                Notify("❌ Install failed: " + err);
                _queue.NotifyFailed(modId);   // advance the queue even when the install fails
            }
        }
        catch (Exception ex)
        {
            _center.Finish(task, false, ex.Message);
            Notify("❌ " + ex.Message);
        }
        finally
        {
            Busy = false;
        }
    }

    private void Notify(string msg)
    {
        RecentStatus.Insert(0, $"[{DateTime.Now:HH:mm:ss}] {msg}");
        if (RecentStatus.Count > 20)
            RecentStatus.RemoveAt(RecentStatus.Count - 1);
        OnChanged?.Invoke();
    }

    /// <summary>
    /// Resolves a readable mod name for the task title (the /tasks page needs to show "which mod is downloading").
    /// Falls back to the given fallbackName or "Mod #{id}" when the network request can't get the name.
    /// </summary>
    private async Task<string> ResolveModTitleAsync(string apiKey, int modId, string? fallbackName)
    {
        try
        {
            var info = await _nexus.GetModAsync(apiKey, modId);
            if (!string.IsNullOrWhiteSpace(info?.Name)) return info.Name;
        }
        catch (Exception __ex) { AppLog.Warn("InstallService", __ex.Message); }
        return string.IsNullOrWhiteSpace(fallbackName) ? $"Mod #{modId}" : fallbackName;
    }

    /// <summary>nxm://stardewvalley/mods/1234/files/5678?key=…&amp;expires=…</summary>
    private static bool TryParseNxm(
        string link, out int modId, out long fileId, out string key, out string expires)
    {
        modId = 0; fileId = 0; key = ""; expires = "";
        try
        {
            var uri = new Uri(link);
            var seg = uri.AbsolutePath.Trim('/').Split('/');
            var mi = Array.IndexOf(seg, "mods");
            var fi = Array.IndexOf(seg, "files");
            if (mi < 0 || fi < 0 || mi + 1 >= seg.Length || fi + 1 >= seg.Length)
                return false;

            modId = int.Parse(seg[mi + 1]);
            fileId = long.Parse(seg[fi + 1]);

            foreach (var pair in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                var kv = pair.Split('=', 2);
                if (kv.Length != 2) continue;
                if (kv[0] == "key") key = Uri.UnescapeDataString(kv[1]);
                if (kv[0] == "expires") expires = kv[1];
            }
            return key.Length > 0 && expires.Length > 0;
        }
        catch
        {
            return false;
        }
    }
}
