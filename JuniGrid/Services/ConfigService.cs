using System.IO;
using System.Text.Json;

namespace JuniGrid.Services;

/// <summary>
/// Persistent user config stored as JSON under %APPDATA%/JuniGrid/.
/// </summary>
public sealed class ConfigService
{
    private static readonly string ConfigDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "JuniGrid");
    private static readonly string ConfigPath = Path.Combine(ConfigDir, "junigrid.config.json");

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public JuniGridConfig Current { get; private set; } = new();

    public ConfigService()
    {
        Load();
        // v0.72.6: before process exit, synchronously flush changes still queued in the debounce window to disk — the last changes within the debounce window are not lost
        System.AppDomain.CurrentDomain.ProcessExit += (_, _) => Flush();
    }

        public void Load()
        {
            try
            {
                if (File.Exists(ConfigPath))
                {
                    var loaded = JsonSerializer.Deserialize<JuniGridConfig>(
                        File.ReadAllText(ConfigPath), JsonOpts);
                    if (loaded is not null) Current = loaded;
                }
            }
            catch
            {
                // v1.1.2: archive the corrupted file before resetting — it previously reset silently, wiping the user's game path/sign-in state with no way to diagnose;
                // the backup is timestamped so key fields can be recovered manually and the cause of corruption traced
                try
                {
                    if (File.Exists(ConfigPath))
                    {
                        var backup = ConfigPath + ".corrupt-" + DateTime.Now.ToString("yyyyMMdd_HHmmss");
                        File.Copy(ConfigPath, backup, true);
                        AppLog.Error("Config", "Config file failed to parse; backed up as " + Path.GetFileName(backup) + ", using default config for this launch");
                    }
                }
                catch { }
                Current = new JuniGridConfig();
            }
            SyncAdultFilter();
            SyncStoragePaths();
            // v1.1.5: the first-use date is written back only once (existing users start counting from this upgrade)
            if (Current.FirstRunDate is null)
            {
                Current.FirstRunDate = DateTime.Now.ToString("O");
                Save(Current);
            }
        }

    /// <summary>Syncs the two mutually exclusive switches "Filter adult content / Show adult content only" to NexusService's static query switches
    /// (whether the browsing GraphQL query includes the adult filter condition). When a switch actually changes, NexusService.AdultFilterVersion is incremented;
    /// the Nexus page uses it to decide whether its browsing snapshot was fetched under the old filter and should be discarded and refetched.</summary>
    private void SyncAdultFilter()
    {
        var only = Current.OnlyAdultContent;
        var include = !Current.OnlyAdultContent && !Current.FilterAdultContent;
        if (NexusService.OnlyAdultContent != only || NexusService.IncludeAdultContent != include)
            NexusService.BumpAdultFilterVersion();
        NexusService.OnlyAdultContent = only;
        NexusService.IncludeAdultContent = include;
    }

        /// <summary>v0.2.1: syncs the unified cache directory to the StoragePaths static entry — every service picks up the path change immediately with zero code changes.</summary>
        private void SyncStoragePaths() =>
            StoragePaths.CacheRoot = string.IsNullOrWhiteSpace(Current.CacheRoot) ? null : Current.CacheRoot;

    // v0.72.6: persistence coordinator — Save() no longer writes the full file every time; instead: dirty flag + 250ms debounce coalescing +
    // version snapshot + single-writer background flush + tmp atomic replace + async retry. The 100+ save requests from a 63-mod batch
    // coalesce into a single real disk write (the persistence-side root cause of "batch operations being slow").
    // Keeps the correct v0.72.5 semantics: serial writes, tmp+atomic replace, retry on failure, no UnobservedTaskException crashes.
    private int _dirtyVersion;              // +1 on every Save()
    private int _savedVersion;              // version already flushed to disk
    private int _saveRunning;               // single-writer gate (0/1)
    private readonly object _schedGate = new();   // guards scheduling state only, never wraps I/O
    private System.Threading.CancellationTokenSource? _debounceCts;
    private const int DebounceMs = 250;

    /// <summary>Unified save entry: updates Current + sets the dirty flag + schedules a coalesced disk write. Returns immediately, never blocking the caller.
    /// All existing call sites (including the 4 synchronous ones) need no changes — final persistence is guaranteed by the debounce + Flush on exit.</summary>
    /// <summary>v0.2.2: lightweight notification fired after any save (e.g. TaskDock listens to ShowTaskDock to show/hide instantly).
    /// May fire on a background thread; subscribers must dispatch back to the UI thread themselves.</summary>
    public static event Action? Saved;

    public void Save(JuniGridConfig cfg)
    {
        Current = cfg;
        SyncAdultFilter();
        SyncStoragePaths();
        Saved?.Invoke();
        System.Threading.Interlocked.Increment(ref _dirtyVersion);
        lock (_schedGate)
        {
            // v1.1.6: dispose the old CTS immediately after cancelling — previously only Cancel was called without Dispose,
            // so a single batch of 100+ save requests left 100+ CTS instances for the finalizer
            var old = _debounceCts;
            _debounceCts = new System.Threading.CancellationTokenSource();
            try { old?.Cancel(); old?.Dispose(); } catch { }
            var token = _debounceCts.Token;
            _ = System.Threading.Tasks.Task.Run(async () =>
            {
                try { await System.Threading.Tasks.Task.Delay(DebounceMs, token).ConfigureAwait(false); }
                catch (OperationCanceledException) { return; }   // coalesced away by a newer save request
                try { await SaveLoopAsync().ConfigureAwait(false); }
                catch (Exception ex) { AppLog.Error("Config", "Background save loop exception (caught, prevents UnobservedTaskException): " + ex.Message); }
            });
        }
    }

    /// <summary>Disk write loop: take a version snapshot → serialize (retake the snapshot if a concurrent modification interferes) → tmp+atomic replace →
    /// after writing, if the dirty version has advanced (new changes arrived during the save), immediately run another round — never overwrite newer state with an old snapshot;
    /// on failure it stays dirty and retries later rather than silently reporting success.</summary>
    private async System.Threading.Tasks.Task SaveLoopAsync()
    {
        if (System.Threading.Interlocked.Exchange(ref _saveRunning, 1) == 1) return;  // a write loop is already running
        try
        {
            while (true)
            {
                var v = System.Threading.Volatile.Read(ref _dirtyVersion);
                if (v <= _savedVersion) return;

                var cfg = Current;
                string? json = null;
                for (var k = 0; k < 6; k++)
                {
                    try { json = JsonSerializer.Serialize(cfg, JsonOpts); break; }
                    catch (InvalidOperationException)
                    { await System.Threading.Tasks.Task.Delay(25).ConfigureAwait(false); }  // serialization hit a concurrent dictionary modification → retake the snapshot
                }
                if (json is null)
                { AppLog.Error("Config", "Config serialization failed repeatedly (concurrent modifications too frequent); staying dirty until the next save"); return; }

                if (await WriteAtomicAsync(json).ConfigureAwait(false))
                    System.Threading.Volatile.Write(ref _savedVersion, v);
                else
                { await System.Threading.Tasks.Task.Delay(800).ConfigureAwait(false); continue; }  // on failure stay dirty and retry later
                // back at the top of the loop, dirtyVersion is re-checked — new changes made during the write trigger the next round
            }
        }
        finally { System.Threading.Volatile.Write(ref _saveRunning, 0); }
    }

    /// <summary>tmp + atomic replace + async retry (no locks held, never blocks the UI thread).
    /// v1.1.6: the tmp file gets a random suffix — with a fixed ".tmp", the background write loop and the exit Flush colliding in the same window
    /// would clash with each other (one Moves the tmp away, the other's WriteAllText/Move throws).</summary>
    private static async System.Threading.Tasks.Task<bool> WriteAtomicAsync(string json)
    {
        for (var attempt = 1; ; attempt++)
        {
            var tmp = $"{ConfigPath}.{Guid.NewGuid().ToString("N")[..8]}.tmp";
            try
            {
                Directory.CreateDirectory(ConfigDir);
                await File.WriteAllTextAsync(tmp, json).ConfigureAwait(false);
                File.Move(tmp, ConfigPath, true);   // atomic replace: a crash mid-write never truncates the old config
                return true;
            }
            catch (Exception ex) when (attempt < 4 && ex is IOException or UnauthorizedAccessException)
            { try { File.Delete(tmp); } catch { } await System.Threading.Tasks.Task.Delay(40 * attempt).ConfigureAwait(false); }
            catch (Exception ex)
            { try { File.Delete(tmp); } catch { } AppLog.Error("Config", $"Config save failed ({attempt} attempt(s)): " + ex.Message); return false; }
        }
    }

    /// <summary>Exit safety net (called from ProcessExit): cancels the debounce and synchronously writes any unflushed changes to disk.
    /// Guarantees the last config change is not lost when the app exits.</summary>
    public void Flush()
    {
        try
        {
            lock (_schedGate) { _debounceCts?.Cancel(); }
            var v = System.Threading.Volatile.Read(ref _dirtyVersion);
            if (v <= _savedVersion) return;
            var cfg = Current;
            string? json = null;
            for (var k = 0; k < 6; k++)
            {
                try { json = JsonSerializer.Serialize(cfg, JsonOpts); break; }
                catch (InvalidOperationException) { System.Threading.Thread.Sleep(20); }
            }
            if (json is null) { AppLog.Error("Config", "Exit Flush serialization failed"); return; }
            for (var attempt = 1; ; attempt++)
            {
                var tmp = $"{ConfigPath}.{Guid.NewGuid().ToString("N")[..8]}.tmp";
                try
                {
                    Directory.CreateDirectory(ConfigDir);
                    File.WriteAllText(tmp, json);
                    File.Move(tmp, ConfigPath, true);
                    System.Threading.Volatile.Write(ref _savedVersion, v);
                    return;
                }
                catch (IOException) when (attempt < 4) { try { File.Delete(tmp); } catch { } System.Threading.Thread.Sleep(40 * attempt); }
                catch (Exception ex) { try { File.Delete(tmp); } catch { } AppLog.Error("Config", "Exit Flush disk write failed: " + ex.Message); return; }
            }
        }
        catch (Exception ex) { AppLog.Error("Config", "Flush exception: " + ex.Message); }
    }
}

/// <summary>v0.46.0: mod profile (modeled after Stardrop Profile) — records which mods (by UniqueID) are enabled under this profile.</summary>
public sealed class ModProfile
{
    public string Name { get; set; } = "";
    public List<string> EnabledModUids { get; set; } = new();
}

public sealed class JuniGridConfig
{
    public string GamePath { get; set; } = "";
    public string LaunchMode { get; set; } = "smapi";   // "smapi" | "steam"
    public string SteamAppId { get; set; } = "413150";
    public string ActiveShaderPreset { get; set; } = "balanced";
    public string NexusApiKey { get; set; } = "";

    // Launch history
    public string? LastLaunchTime { get; set; }          // ISO-8601
    public string? LastLaunchMode { get; set; }
    public int TotalLaunchCount { get; set; }

    /// <summary>v1.1.5: date of first use of JuniGrid (ISO-8601, written back once when the config is first saved).
    /// The home play-heatmap year list runs from this date to the current year.</summary>
    public string? FirstRunDate { get; set; }

    // Nexus cover cache: mod folder name → cover image URL (saved opportunistically during update checks so lists open instantly)
    public Dictionary<string, string> ModCovers { get; set; } = new();

    /// <summary>Remark names users give to mods: mod folder name → remark (shown in lists as "remark (original name)").</summary>
    /// <summary>v1.1.2: mod folder → remark name. Lists show it as "remark (original name)", and it is synced into the mod's
    /// manifest.json (the in-game GMCM title reads from it).</summary>
    public Dictionary<string, string> ModRemarks { get; set; } = new();
    /// <summary>v1.1.2: mod folder → the original Name from the mod's manifest. Archived before a remark is synced into the manifest,
    /// and used to restore the original when the remark is removed, so the original name is never lost.</summary>
    public Dictionary<string, string> ModOriginalNames { get; set; } = new();

    /// <summary>v1.01.0: Nexus page search history (mirrors the site's Recent Searches; up to 10 entries, newest first).</summary>
    public List<string> NexusSearchHistory { get; set; } = new();

    /// <summary>
    /// Filter pornographic (adult) content switch. On by default — Nexus browsing/searching always excludes adult content;
    /// mutually exclusive with "Show adult content only"; toggling either involves no age verification (the birthdate verification from early versions was removed).
    /// </summary>
    public bool FilterAdultContent { get; set; } = true;
    /// <summary>"Show adult content only" switch, mutually exclusive with FilterAdultContent (at most one of the two is on; both may be off). Off by default.</summary>
    public bool OnlyAdultContent { get; set; } = false;
    /// <summary>
    /// Nexus one-click install (no browser popup; downloads in the background and installs straight into Mods). On by default;
    /// when off, the "Install" button on the detail page falls back to opening the built-in browser.
    /// </summary>
    public bool EnableOneClickInstall { get; set; } = true;

    /// <summary>User info cached after Nexus sign-in (from /v1/users/validate.json).</summary>
    public string NexusUserName { get; set; } = "";
    public string NexusUserEmail { get; set; } = "";
    public string NexusProfileUrl { get; set; } = "";
    public bool   NexusIsPremium { get; set; }

    /// <summary>v1.1.1: UI theme ("light" | "dark"). Toggled and persisted via the title bar switch;
    /// at startup TitleBar uses it to align the frontend (localStorage is the fast synchronous path that prevents white flashes).
    /// v1.1.2: default changed to dark (the user mainly views the UI in dark mode).</summary>
    public string Theme { get; set; } = "dark";
    /// <summary>v0.69.0: modId → date (yyyy-MM-dd) of the last file downloaded from that mod. Recorded on local install/update and merged with the Nexus download history.</summary>
    public Dictionary<string, string> ModLastDownload { get; set; } = new();
    /// <summary>v0.69.0: fileId → the file's download date (only for files downloaded on this machine via the app).</summary>
    public Dictionary<string, string> ModFileLastDownload { get; set; } = new();

    /// <summary>v0.68.2: settings-page "Auto-install updates" switch (only Nexus Premium members can enable it).
    /// When on, entering a Mod page with detected updates no longer shows a confirmation dialog; updates install automatically within the app.</summary>
    public bool EnableAutoInstall { get; set; } = false;

    /// <summary>v0.2.2: task manager floating window always-on switch. When on it is always visible; when off it appears only while a download task is running.</summary>
    public bool ShowTaskDock { get; set; } = false;
    public string NexusAvatarDataUri { get; set; } = "";

    /// <summary>Total play time in minutes. LauncherService adds to it when the game process exits.</summary>
    public long TotalPlayMinutes { get; set; }

    /// <summary>v0.46.0: mod profile list ("Default" is the built-in profile and cannot be deleted).</summary>
    public List<ModProfile> ModProfiles { get; set; } = new();
    /// <summary>Name of the currently active profile.</summary>
    public string ActiveProfile { get; set; } = "Default";

    /// <summary>Nexus official category table (category_id → English name); fetched once at runtime with the API Key and cached.</summary>
    public Dictionary<int, string> NexusCategories { get; set; } = new();
    /// <summary>mod folder → official English category name from the site (cached opportunistically during update checks/cover backfill; same lifetime as ModCovers).</summary>
    public Dictionary<string, string> ModCategories { get; set; } = new();

    /// <summary>vNext: update-check fingerprint cache — Nexus modId → (updatedAt fingerprint, latest MAIN file version found by the last deep check, deep-check time).
    /// Entering a Mod page first runs one key-free GraphQL batch fingerprint comparison: mods whose updatedAt is unchanged reuse the cached version number
    /// (the file list is unchanged, so results cannot go stale); only mods with a changed fingerprint/missing cache entry get a per-mod files.json deep check that writes back to this cache.
    /// Persisted in the config so it still hits after an app restart — routine page-entry checks collapse from N requests to ~N/50.
    /// </summary>
    public Dictionary<int, ModUpdateFingerprintEntry> ModUpdateFingerprints { get; set; } = new();

    /// <summary>v1.2.0: one-click dependency install resolution cache — SMAPI UniqueID → Nexus modId.
    /// Recorded after a search hit installs successfully with manifest UniqueID verification, so the next one-click dependency install hits directly
    /// without searching again. Verification still runs after every download; even a wrong cache entry cannot install anything into Mods.</summary>
    public Dictionary<string, int> DependencyNexusIds { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>v0.2.1: unified cache directory (null = each cache type keeps its historical default location).
    /// When set, download/install temp files, SMAPI installers, WebView2 data, and Mods backups all migrate into subdirectories under it.</summary>
    public string? CacheRoot { get; set; }

    /// <summary>v0.2.2: leftover marker for WebView2 data directory migration — when the cache directory changes, WebView2 is in use and cannot move right away;
    /// the old location is recorded, migrated automatically on next startup (before WebView2 initializes), and then cleared.</summary>
    public string? PendingWebView2MoveFrom { get; set; }

    /// <summary>v0.2.1: memory management — scheduled auto-compress switch and interval (minutes).</summary>
    public bool MemTimerEnabled { get; set; } = false;
    public int MemTimerMinutes { get; set; } = 30;

    /// <summary>v0.2.1: memory management — auto-compress when system memory usage reaches the threshold (%).</summary>
    public bool MemThresholdEnabled { get; set; } = false;
    public int MemThresholdPercent { get; set; } = 80;

}

/// <summary>vNext: a single update-check fingerprint. UpdatedAt is compared verbatim against the GraphQL batch result;
/// LatestFileVersion is only written from a successful files.json deep check (the same authoritative source used for installation);
/// CheckedAtUtc gives the cache a fallback validity window (24h, guarding against edge cases outside the updatedAt assumption lingering forever).</summary>
public sealed class ModUpdateFingerprintEntry
{
    public string UpdatedAt { get; set; } = "";
    public string LatestFileVersion { get; set; } = "";
    public DateTime CheckedAtUtc { get; set; } = DateTime.UtcNow;
}
