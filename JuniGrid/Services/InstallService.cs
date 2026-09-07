using System.Collections.Concurrent;
using System.IO;
using System.Text;
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

    // v1.1.6: the RecentStatus list was removed — dead state that was only written, never read (no reader anywhere in the repo).
    // The OnChanged event itself is still used by Mods.razor and stays.

    public bool Busy { get; private set; }

    // ------------------------------------------------------------------
    // Direct install (no built-in browser popup)
    // ------------------------------------------------------------------
    // When "Install" is clicked on the ModDetail / leaderboards pages, request a one-time download
    // link from Nexus in the background and stream-download and install it, never leaving the launcher. Free accounts are throttled to about 1MB/s;
    // on 403 (the mod requires Premium) the caller falls back to opening the built-in browser.
    // ------------------------------------------------------------------

    /// <summary>
    /// One-click direct install: downloads and installs the latest MAIN file of the given mod in the background.
    /// Returns null on success; otherwise an error message (containing the "premium" keyword when Premium is required).
    /// Progress feeds the task center: a task appears in the bottom-right corner, and the /tasks page shows download percentage/speed/per-step details.
    /// </summary>
    public async Task<string?> InstallModDirectAsync(int modId)
    {
        if (Busy) return "The previous install is still running; wait for it to finish and try again";

        var cfg = _cfg.Current;
        if (!NexusService.IsAuthenticated)
            return "Not signed in to Nexus Mods yet — sign in on the Nexus page first";
        if (string.IsNullOrWhiteSpace(cfg.GamePath))
            return "Game folder not set yet — choose it on the Settings page first";

        var taskTitle = await ResolveModTitleAsync(modId, null);
        var task = _center.Start($"Download and install {taskTitle}", "install");

        void Step(string msg, double? pct = null, double? speed = null) =>
            _center.Report(task, msg, pct, speed);

        Busy = true;
        string? zipPath = null;   // v1.1.3: for cleaning up the partial zip on cancellation (catch cannot see locals declared inside try)
        try
        {
            Step("Fetching file info…", 2);
            var file = await _nexus.GetLatestMainFileAsync(modId);
            if (file is null) { _center.Finish(task, false, "No downloadable file found"); return "No downloadable file found"; }

            Step("Getting download URL…", 5);
            var dl = await _nexus.GetDownloadUrlAsync(modId, file.FileId);
            if (dl.NeedsPremium)
            { _center.Finish(task, false, "Nexus Premium required; switched to the web flow"); return "premium: this mod's direct download requires a Nexus Premium account"; }
            if (dl.Url is null)
            { _center.Finish(task, false, "Failed to get download URL: " + (dl.Error ?? "unknown error")); return "Failed to get download URL: " + (dl.Error ?? "unknown error"); }

            var zip = Path.Combine(StoragePaths.DownloadsDir,
                $"direct-{modId}-{file.FileId}.zip");
            zipPath = zip;
            Directory.CreateDirectory(Path.GetDirectoryName(zip)!);

            var progress = new Progress<NexusDownloadProgress>(p =>
                Step(p.Message, p.Percent, p.SpeedMBps));
            Step($"Downloading {file.Name}…", 8, 0);
            await _nexus.DownloadFileAsync(dl.Url, zip, progress, task.Cts.Token);
            if (task.Cts.IsCancellationRequested)   // download finished but the task was removed → don't install; delete the partial zip
            { try { File.Delete(zip); } catch { } return "Cancelled"; }

            Step("Installing into Mods…", 95);
            var err = _mods.InstallNew(cfg.GamePath, zip, out var modName, modId);
            if (err is not null)
            { _center.Finish(task, false, "Install failed: " + err); return "Install failed: " + err; }

            _queue.NotifyInstalled(modId);
            // v0.69.0: record the "last downloaded" date (shown under the detail-page title and as the green check on the Files tab)
            cfg.ModLastDownload[modId.ToString()] = DateTime.Now.ToString("yyyy-MM-dd");
            cfg.ModFileLastDownload[file.FileId.ToString()] = DateTime.Now.ToString("yyyy-MM-dd");
            _cfg.Save(cfg);
            var done = $"Install complete: {modName ?? "New mod"}";
            _center.Finish(task, true, done);
            Notify("✅ " + done);
            return null;
        }
        catch (OperationCanceledException)
        {
            // v1.1.3: the user removed the task → delete the partial zip and exit silently (the task entry is already gone from the list)
            try { if (zipPath is not null && File.Exists(zipPath)) File.Delete(zipPath); } catch { }
            return "Cancelled";
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
            Notify("⏳ The previous install is still running; wait for it to finish and try again");
            return;
        }
        var task = _center.Start("Web one-click install (Nexus)", "install");
        string? zipPath = null;   // v1.1.3: for cleanup on cancellation

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
            if (!NexusService.IsAuthenticated)
            {
                _center.Finish(task, false, "Not signed in to Nexus Mods yet");
                Notify("❌ Not signed in to Nexus Mods yet — sign in on the Nexus page first");
                return;
            }
            if (string.IsNullOrWhiteSpace(cfg.GamePath))
            {
                _center.Finish(task, false, "Game folder not set yet");
                Notify("❌ Game folder not set yet — choose it on the Settings page first");
                return;
            }

            // once modId is parsed, add the mod name to the task title so it is recognizable on the /tasks page
            task.Title = "Download and install " + await ResolveModTitleAsync(modId, null);

            Step($"Getting download URL (mod #{modId})…", 8);
            // The nxm:// link's download_link endpoint requires authenticated access matching the Nexus
            // account that generated the link — the OAuth2 Bearer token of the signed-in user is attached automatically.
            var dl = await _nexus.GetNxmDownloadUrlAsync(modId, fileId, key, exp);
            if (dl.Url is null)
            {
                _center.Finish(task, false, dl.Error ?? "Failed to get download URL");
                Notify("❌ " + (dl.Error ?? "Failed to get download URL"));
                _queue.NotifyFailed(modId);   // link expired/rejected → skip it; don't let the queue stall
                return;
            }

            var zip = Path.Combine(StoragePaths.DownloadsDir, $"nxm-{modId}-{fileId}.zip");
            zipPath = zip;
            Directory.CreateDirectory(Path.GetDirectoryName(zip)!);

            var progress = new Progress<NexusDownloadProgress>(p =>
                Step(p.Message, p.Percent, p.SpeedMBps));
            Step("Downloading…", 12, 0);
            await _nexus.DownloadFileAsync(dl.Url, zip, progress, task.Cts.Token);
            if (task.Cts.IsCancellationRequested)   // download finished but the task was removed → don't install; delete the partial zip
            { try { File.Delete(zip); } catch { } return; }

            Step("Installing into Mods…", 95);
            var err = _mods.InstallNew(cfg.GamePath, zip, out var modName, modId);
            if (err is null)
            {
                _queue.NotifyInstalled(modId);   // update queue: one installed, advance automatically
                _center.Finish(task, true, $"Install complete: {modName ?? "New mod"}");
                Notify($"✅ Install complete: {modName ?? "New mod"} (now visible on the Mod Manager page)");
            }
            else
            {
                _center.Finish(task, false, "Install failed: " + err);
                Notify("❌ Install failed: " + err);
                _queue.NotifyFailed(modId);   // advance the queue even when the install fails
            }
        }
        catch (OperationCanceledException)
        {
            try { if (zipPath is not null && File.Exists(zipPath)) File.Delete(zipPath); } catch { }
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

    private void Notify(string msg) => OnChanged?.Invoke();

    // ------------------------------------------------------------------
    // v1.2.0: one-click install of missing dependencies
    // ------------------------------------------------------------------
    // Dependencies are declared as SMAPI UniqueIDs (manifest Dependencies / ContentPackFor host),
    // while Nexus only knows modIds — the gap is bridged by an error-proof loop: "site search (keyless GraphQL, sorted by downloads) →
    // verify the zip's manifest UniqueID after download": a wrong candidate costs at most one
    // download and the wrong mod is never installed. The transitive closure is followed automatically: whatever the installed dependencies still miss gets installed next.
    // SMAPI itself is a loader, not a regular mod — it is reported separately and never auto-installed.
    // Nexus free accounts cannot download via the API (403) → once that happens, all remaining dependencies switch to "manual"
    // with their Nexus page links attached, instead of wasting more API request quota.
    // ------------------------------------------------------------------

    /// <summary>Per-item results of the most recent one-click dependency install (data source for the results dialog on the Mods page).
    /// Status: installed / manual / framework / unresolved / failed.</summary>
    public IReadOnlyList<DependencyRunItem>? LastDependencyRun { get; private set; }

    public sealed record DependencyRunItem(string Uid, string Status, string? Name, int? ModId, string? Detail);

    /// <summary>
    /// One-click install of missing dependencies. onlyUids null = all missing dependencies; passing a specific UID list =
    /// install only those (plus any transitive dependencies they newly expose once installed). preResolved = UID → modId
    /// mappings already resolved by the confirmation dialog (used with zero search overhead; verification still runs). Returns null
    /// when the run completes (individual dependencies may end up manual/failed; see <see cref="LastDependencyRun"/> for per-item results).
    /// </summary>
    public async Task<string?> InstallMissingDependenciesAsync(IReadOnlyList<string>? onlyUids = null,
        IReadOnlyDictionary<string, int>? preResolved = null)
    {
        if (Busy) return "The previous install is still running; wait for it to finish and try again";
        var cfg = _cfg.Current;
        if (!NexusService.IsAuthenticated)
            return "Not signed in to Nexus Mods yet — sign in on the Nexus page first";
        if (string.IsNullOrWhiteSpace(cfg.GamePath))
            return "Game folder not set yet — choose it on the Settings page first";

        var task = _center.Start(
            onlyUids is null ? "Install missing dependencies" : $"Install missing dependencies ({onlyUids.Count} items)", "install");
        void Step(string msg, double? pct = null, double? speed = null) => _center.Report(task, msg, pct, speed);

        Busy = true;
        var outcomes = new List<DependencyRunItem>();
        var toOpen = new List<string>();   // v1.2.2: pages of manual dependencies, batch-opened in the browser at the end
        var processed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var premiumBlocked = false;
        string? zipPath = null;
        try
        {
            // onlyUids only constrains the first round (the user-specified batch); transitive dependencies that installed ones still miss are followed up regardless
            var allowedFirstRound = onlyUids is null ? null : new HashSet<string>(onlyUids, StringComparer.OrdinalIgnoreCase);
            var firstRound = true;

            while (true)
            {
                task.Cts.Token.ThrowIfCancellationRequested();
                // rescan the disk every round — dependencies installed last round may bring new missing ones (transitive closure)
                var mods = _mods.Scan(cfg.GamePath);
                var installedUids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var m in mods)
                    if (!string.IsNullOrWhiteSpace(m.UniqueID))
                        installedUids.Add(m.UniqueID.Trim());

                var batch = new List<string>();
                foreach (var m in mods)
                {
                    foreach (var d in m.Dependencies.Concat(m.ContentPackIds))
                    {
                        var dep = d?.Trim();
                        if (string.IsNullOrWhiteSpace(dep)
                            || installedUids.Contains(dep)
                            || processed.Contains(dep)) continue;
                        if (firstRound && allowedFirstRound is not null && !allowedFirstRound.Contains(dep)) continue;
                        if (!batch.Contains(dep, StringComparer.OrdinalIgnoreCase)) batch.Add(dep);
                    }
                }
                firstRound = false;
                if (batch.Count == 0) break;

                var total = processed.Count + batch.Count;
                foreach (var dep in batch)
                {
                    task.Cts.Token.ThrowIfCancellationRequested();
                    processed.Add(dep);
                    var idx = processed.Count;
                    Step($"({idx}/{total}) Processing {dep}…", Math.Round(idx * 90.0 / (total + 1), 1));

                    // 1) The loader itself is not a mod and cannot be auto-installed
                    if (dep.Equals("SMAPI", StringComparison.OrdinalIgnoreCase))
                    {
                        outcomes.Add(new DependencyRunItem(dep, "framework", "SMAPI", null,
                            "SMAPI is the mod loader itself and cannot be installed as a regular mod — use the SMAPI installer (check/update SMAPI from the launcher home page)"));
                        continue;
                    }

                    // 2) Candidate resolution (v1.2.3: lazy): known candidates (dialog pre-resolution + config cache) are used with zero
                    // searching; only when all verification fails does it fall back to site search — repeat installs / consecutive dependency runs save
                    // whole rounds of GraphQL requests. The premiumBlocked fast path only needs one candidate id to build a manual link.
                    List<NexusModListEntry> cands;
                    if (premiumBlocked)
                    {
                        var face = await FirstCandidateAsync(cfg, dep, preResolved);
                        if (face is null)
                        {
                            Step($"✗ {dep}: no matching mod found on Nexus; opened the search page for manual follow-up");
                            AddUnresolvedOutcome(dep, toOpen, outcomes);
                            continue;
                        }
                        Step($"⏭ {dep}: free accounts cannot download via the API; opened the web page for manual install");
                        outcomes.Add(new DependencyRunItem(dep, "manual", face.Name, face.Id,
                            "This page was opened in your browser — click \"Mod Manager Download\" and the launcher takes over the download and install automatically (Nexus free-account limitation)"));
                        toOpen.Add($"https://www.nexusmods.com/stardewvalley/mods/{face.Id}");
                        continue;
                    }

                    cands = await KnownCandidatesAsync(cfg, dep, preResolved);
                    var searched = false;
                    string? lastErr = null;
                    var ok = false;
                    while (true)
                    {
                        // 3) Per candidate: download → verify UniqueID → install (at most 3 tried per batch to save bandwidth under the free-tier throttle)
                        foreach (var c in cands.Take(3))
                        {
                            task.Cts.Token.ThrowIfCancellationRequested();
                            Step($"({idx}/{total}) Fetching file info for {c.Name}…");
                            var file = await _nexus.GetLatestMainFileAsync(c.Id);
                            if (file is null) { lastErr = "No downloadable file found"; continue; }

                            var dl = await _nexus.GetDownloadUrlAsync(c.Id, file.FileId);
                            if (dl.NeedsPremium)
                            {
                                // switching candidates will not help — account-level limitation: all remaining dependencies this run switch to manual
                                premiumBlocked = true;
                                lastErr = "premium";
                                break;
                            }
                            if (dl.Url is null) { lastErr = dl.Error ?? "Failed to get download URL"; continue; }

                            zipPath = Path.Combine(StoragePaths.DownloadsDir, $"dep-{c.Id}-{file.FileId}.zip");
                            Directory.CreateDirectory(Path.GetDirectoryName(zipPath)!);
                            try
                            {
                                var progress = new Progress<NexusDownloadProgress>(p => Step(p.Message, p.Percent, p.SpeedMBps));
                                Step($"({idx}/{total}) Downloading {c.Name}…", null, 0);
                                await _nexus.DownloadFileAsync(dl.Url, zipPath, progress, task.Cts.Token);
                                if (task.Cts.IsCancellationRequested) return "Cancelled";

                                Step($"({idx}/{total}) Installing {c.Name}…", Math.Round(idx * 95.0 / (total + 1), 1));
                                var err = _mods.InstallNew(cfg.GamePath, zipPath, out var modName, c.Id,
                                    requireUniqueId: dep);
                                if (err == ModService.UidMismatchError)
                                { lastErr = $"Candidate \"{c.Name}\" does not contain {dep}; trying the next one"; continue; }
                                if (err is not null) { lastErr = err; continue; }

                                // success: remember the resolution + the same wrap-up as direct install (update queue / last-download date / refresh list)
                                cfg.DependencyNexusIds[dep] = c.Id;
                                cfg.ModLastDownload[c.Id.ToString()] = DateTime.Now.ToString("yyyy-MM-dd");
                                cfg.ModFileLastDownload[file.FileId.ToString()] = DateTime.Now.ToString("yyyy-MM-dd");
                                _cfg.Save(cfg);
                                _queue.NotifyInstalled(c.Id);
                                outcomes.Add(new DependencyRunItem(dep, "installed", modName ?? c.Name, c.Id, null));
                                Step($"✓ Installed {modName ?? c.Name} ({dep})");
                                Notify("");
                                ok = true;
                                break;
                            }
                            finally
                            {
                                // keep neither partial nor completed zips (a resumable-download cache adds little here; dependency packages are usually small)
                                try { if (zipPath is not null && File.Exists(zipPath)) File.Delete(zipPath); } catch { }
                                zipPath = null;
                            }
                        }
                        // known candidates exhausted → fall back to site search (one search round per dependency; if it still fails, give up)
                        if (ok || premiumBlocked || searched) break;
                        searched = true;
                        Step(cands.Count > 0
                            ? $"({idx}/{total}) Known candidates do not match; searching {dep}…"
                            : $"({idx}/{total}) Searching {dep}…");
                        cands = await SearchDependencyCandidatesAsync(dep);
                        if (cands.Count == 0) break;
                    }

                if (ok) continue;
                if (premiumBlocked)
                {
                    var face = cands.Count > 0 ? cands[0] : null;
                    outcomes.Add(new DependencyRunItem(dep, "manual", face?.Name, face?.Id,
                        "This page was opened in your browser — click \"Mod Manager Download\" and the launcher takes over the download and install automatically (Nexus free-account limitation)"));
                    Step($"⏭ {dep}: Nexus direct download requires Premium; opened the web page for manual install");
                    if (face is not null) toOpen.Add($"https://www.nexusmods.com/stardewvalley/mods/{face.Id}");
                }
                    else if (searched && cands.Count == 0)
                    {
                        Step($"✗ {dep}: no matching mod found on Nexus; opened the search page for manual follow-up");
                        AddUnresolvedOutcome(dep, toOpen, outcomes);
                    }
                    else
                    {
                        var face = cands.Count > 0 ? cands[0] : null;
                        outcomes.Add(new DependencyRunItem(dep, "failed", face?.Name, face?.Id,
                            lastErr ?? "Download/install failed"));
                        Step($"✗ {dep}: {lastErr}");
                    }
                }
                if (processed.Count > 200) break;   // guard: a pathological manifest cannot loop forever
            }

            LastDependencyRun = outcomes;
            var done = outcomes.Count(o => o.Status == "installed");
            var manualC = outcomes.Count(o => o.Status is "manual" or "framework");
            var unresolvedC = outcomes.Count(o => o.Status == "unresolved");
            var failedC = outcomes.Count(o => o.Status == "failed");
            var sb = new StringBuilder();
            if (done > 0) sb.Append($"Installed {done} dependencies");
            if (manualC > 0) sb.Append(sb.Length > 0 ? $"; {manualC} need manual install" : $"{manualC} need manual install");
            if (unresolvedC > 0) sb.Append(sb.Length > 0 ? $"; {unresolvedC} not found" : $"{unresolvedC} not found");
            if (failedC > 0) sb.Append(sb.Length > 0 ? $"; {failedC} failed" : $"{failedC} failed");
            var summary = sb.Length == 0 ? "No missing dependencies to install" : sb.ToString();
            // v1.2.3: only unresolved/failed count as task failure — "needs manual" (opening the web page on a free account)
            // is a planned, normal outcome; the task finishes as a success so the summary line does not show a red cross.
            _center.Finish(task, failedC == 0 && unresolvedC == 0, summary);

            // v1.2.2: dependencies on free accounts / not found — batch-open their pages in the default browser at the end;
            // the user clicks "Mod Manager Download" on each, and the nxm takeover link auto-downloads and installs into Mods
            // (the one-time key for a free download can only come from a web click; the app cannot do it on the user's behalf).
            // Capped at 12 to avoid a tab storm. SMAPI itself gets no page (it has its own installer).
            // v1.2.3: no more "opened in browser…" info lines written to the task log (the browser popup itself is the notice).
            if (toOpen.Count > 0)
            {
                foreach (var url in toOpen.Take(12))
                {
                    try { UpdateService.OpenUrl(url); } catch { }
                }
            }
            Notify("");
            return null;
        }
        catch (OperationCanceledException)
        {
            try { if (zipPath is not null && File.Exists(zipPath)) File.Delete(zipPath); } catch { }
            return "Cancelled";
        }
        catch (Exception ex)
        {
            LastDependencyRun = outcomes;
            _center.Finish(task, false, "Dependency install failed: " + ex.Message);
            return ex.Message;
        }
        finally
        {
            Busy = false;
        }
    }

    /// <summary>Display info for the confirmation dialog: UID → (mod name, Nexus modId, cover).
    /// Keyless GraphQL takes the first hit by download count, for display only — the actual install still hinges on the
    /// post-download UniqueID verification; a wrong search here shows at most a wrong cover, never installs the wrong mod.</summary>
    public sealed record DependencyDisplayInfo(string Uid, string? Name, int? ModId, string? CoverUrl);

    /// <summary>v1.2.3: in-process cache for display info — reopening the dialog in the same session no longer re-searches (mod name/cover rarely change).</summary>
    private static readonly ConcurrentDictionary<string, DependencyDisplayInfo> DisplayCache =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Resolves display info for a single dependency UID. Returns null on search failure/no hits (the caller falls back to showing the UID).</summary>
    public async Task<DependencyDisplayInfo?> ResolveDependencyDisplayAsync(string uid)
    {
        if (DisplayCache.TryGetValue(uid, out var cached)) return cached;
        try
        {
            var term = UidSearchTerms(uid).FirstOrDefault();
            if (string.IsNullOrWhiteSpace(term)) return null;
            var hits = await _nexus.BrowseModsAsync("downloads", 0, 5, "stardewvalley", searchText: term);
            var top = hits?.FirstOrDefault(h => h.Id > 0);
            if (top is null) return null;
            var info = new DependencyDisplayInfo(uid, top.Name, top.Id,
                string.IsNullOrWhiteSpace(top.ThumbnailUrl) ? top.PictureUrl : top.ThumbnailUrl);
            DisplayCache[uid] = info;
            return info;
        }
        catch { return null; }
    }

    /// <summary>Known candidates (v1.2.3: dialog pre-resolution + config cache): used with zero search overhead. Only the first
    /// candidate resolves a display name once (keeps progress logs readable); the rest fall back to "Mod #id". Verification still runs
    /// after download, so a wrong candidate never lands in Mods.</summary>
    private async Task<List<NexusModListEntry>> KnownCandidatesAsync(
        JuniGridConfig cfg, string uid, IReadOnlyDictionary<string, int>? preResolved)
    {
        var ids = new List<int>();
        var seen = new HashSet<int>();
        if (preResolved is not null && preResolved.TryGetValue(uid, out var pre) && pre > 0 && seen.Add(pre))
            ids.Add(pre);
        if (cfg.DependencyNexusIds.TryGetValue(uid, out var known) && known > 0 && seen.Add(known))
            ids.Add(known);
        if (ids.Count == 0) return new List<NexusModListEntry>();
        var firstName = "";
        try
        {
            var info = await _nexus.GetModAsync(ids[0]);
            if (!string.IsNullOrWhiteSpace(info?.Name)) firstName = info!.Name;
        }
        catch { }
        return ids.Select(id => new NexusModListEntry(
            id, id == ids[0] && firstName.Length > 0 ? firstName : $"Mod #{id}", "", "", 0, "")).ToList();
    }

    /// <summary>Site-search candidates (v1.2.3: the fallback when all known candidates fail. Keyless GraphQL,
    /// sorted by downloads). Candidates are only "suspects"; the real gate is post-download verification of the manifest UniqueID.</summary>
    private async Task<List<NexusModListEntry>> SearchDependencyCandidatesAsync(string uid)
    {
        var list = new List<NexusModListEntry>();
        var seen = new HashSet<int>();
        foreach (var term in UidSearchTerms(uid))
        {
            List<NexusModListEntry>? hits = null;
            try { hits = await _nexus.BrowseModsAsync("downloads", 0, 8, "stardewvalley", searchText: term); }
            catch { }
            if (hits is null) continue;
            foreach (var h in hits)
                if (h.Id > 0 && seen.Add(h.Id)) list.Add(h);
            if (list.Count >= 12) break;   // candidate cap, keeps the search bounded
        }
        return list;
    }

    /// <summary>For the premiumBlocked fast path only: find a single candidate id to build the manual link (known → search).</summary>
    private async Task<NexusModListEntry?> FirstCandidateAsync(
        JuniGridConfig cfg, string uid, IReadOnlyDictionary<string, int>? preResolved)
    {
        var known = await KnownCandidatesAsync(cfg, uid, preResolved);
        if (known.Count > 0) return known[0];
        var hits = await SearchDependencyCandidatesAsync(uid);
        return hits.FirstOrDefault();
    }

    /// <summary>unresolved wrap-up: result item + an on-site Nexus search page link (manual confirmation; the browser opens them all at the end).</summary>
    private void AddUnresolvedOutcome(string dep, List<string> toOpen, List<DependencyRunItem> outcomes)
    {
        toOpen.Add("https://www.nexusmods.com/stardewvalley/search/?gsearch="
            + Uri.EscapeDataString(UidSearchTerms(dep).FirstOrDefault() ?? dep)
            + "&gsearchtype=mods");
        outcomes.Add(new DependencyRunItem(dep, "unresolved", null, null,
            "This dependency could not be found automatically — the Nexus search page is open; confirm it manually and click Mod Manager Download (the launcher takes over automatically)"));
    }

    /// <summary>UID → search terms: use the last segment of the UniqueID (the author segment is too noisy to search), camel-case splitting first
    /// ("Pathoschild.ContentPatcher" → "Content Patcher" → "ContentPatcher" → the full UID).</summary>
    private static IEnumerable<string> UidSearchTerms(string uid)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var dot = uid.IndexOf('.');
        if (dot > 0 && dot < uid.Length - 1)
        {
            var namePart = uid[(dot + 1)..].Trim();
            if (namePart.Length > 0)
            {
                var camel = CamelSplit(namePart);
                if (camel.Length > 0 && seen.Add(camel)) yield return camel;
                if (seen.Add(namePart)) yield return namePart;
            }
            if (seen.Add(uid)) yield return uid;
        }
        else if (seen.Add(uid)) yield return uid;
    }

    /// <summary>Camel-case split: "BroadcastAPI" → "Broadcast API" (breaks at lowercase→uppercase and at uppercase followed by lowercase).</summary>
    private static string CamelSplit(string s)
    {
        var sb = new StringBuilder(s.Length + 8);
        for (var i = 0; i < s.Length; i++)
        {
            var c = s[i];
            if (i > 0 && char.IsUpper(c)
                && (char.IsLower(s[i - 1])
                    || (i + 1 < s.Length && char.IsLower(s[i + 1]) && !char.IsWhiteSpace(s[i - 1]))))
                sb.Append(' ');
            sb.Append(c);
        }
        return sb.ToString().Trim();
    }


    /// <summary>
    /// Resolves a readable mod name for the task title (the /tasks page shows which mod is being downloaded).
    /// Falls back to the given fallback or "Mod #{id}" when the network request yields no name.
    /// </summary>
    private async Task<string> ResolveModTitleAsync(int modId, string? fallbackName)
    {
        try
        {
            var info = await _nexus.GetModAsync(modId);
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
