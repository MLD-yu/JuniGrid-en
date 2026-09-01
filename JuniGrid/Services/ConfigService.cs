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
        // v0.72.6: before process exit, synchronously flush queued (debounced) changes that haven't hit disk — the last change within the debounce window is never lost
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
                Current = new JuniGridConfig();
            }
            SyncAdultFilter();
            SyncStoragePaths();
        }

        /// <summary>Syncs the "filter adult content" toggle to NexusService's static query flag (whether browse GraphQL includes the adult filter condition).</summary>
        private void SyncAdultFilter() =>
            NexusService.IncludeAdultContent = !Current.FilterAdultContent;

        /// <summary>v0.2.1: syncs the unified cache directory into StoragePaths' static entry point — all services pick up new paths immediately with zero changes.</summary>
        private void SyncStoragePaths() =>
            StoragePaths.CacheRoot = string.IsNullOrWhiteSpace(Current.CacheRoot) ? null : Current.CacheRoot;

    // v0.72.6: persistence coordinator — Save() no longer writes the whole file every call; instead a dirty flag +
    // 250ms debounced coalescing + version snapshots + a single-writer background flush + tmp atomic replace + async retries.
    // 100+ save requests from batch-applying 63 mods coalesce into one real disk write (the persistence-side root cause of "slow batch operations").
    // Preserves v0.72.5's correct semantics: serialized writes, tmp + atomic replace, retry on failure, no UnobservedTaskException crashes.
    private int _dirtyVersion;              // incremented on every Save()
    private int _savedVersion;              // version already persisted
    private int _saveRunning;               // single-writer gate (0/1)
    private readonly object _schedGate = new();   // guards scheduling state only, never wraps I/O
    private System.Threading.CancellationTokenSource? _debounceCts;
    private const int DebounceMs = 250;

    /// <summary>Unified save entry point: updates Current + sets the dirty flag + schedules a coalesced write. Returns immediately, never blocks the caller.
    /// All existing call sites (including 4 synchronous ones) need no change — final persistence semantics are guaranteed by debounce + exit Flush.</summary>
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
            _debounceCts?.Cancel();
            _debounceCts = new System.Threading.CancellationTokenSource();
            var token = _debounceCts.Token;
            _ = System.Threading.Tasks.Task.Run(async () =>
            {
                try { await System.Threading.Tasks.Task.Delay(DebounceMs, token).ConfigureAwait(false); }
                catch (OperationCanceledException) { return; }   // coalesced away by a newer save request
                try { await SaveLoopAsync().ConfigureAwait(false); }
                catch (Exception ex) { AppLog.Error("Config", "Background save loop exception (caught, to avoid UnobservedTaskException): " + ex.Message); }
            });
        }
    }

    /// <summary>Write loop: take a version snapshot → serialize (retake the snapshot if a concurrent change collides) → tmp + atomic replace →
    /// after writing, if the dirty version advanced (new changes arrived during the write), immediately write another round — never overwrite new state with a stale snapshot;
    /// on failure keep dirty and retry later, never silently treating it as success.</summary>
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
                    { await System.Threading.Tasks.Task.Delay(25).ConfigureAwait(false); }  // serialization hit a concurrent dictionary change → retake snapshot
                }
                if (json is null)
                { AppLog.Error("Config", "Config serialization failed repeatedly (concurrent changes too frequent); staying dirty until next save"); return; }

                if (await WriteAtomicAsync(json).ConfigureAwait(false))
                    System.Threading.Volatile.Write(ref _savedVersion, v);
                else
                { await System.Threading.Tasks.Task.Delay(800).ConfigureAwait(false); continue; }  // keep dirty on failure, retry later
                // loop back and re-check dirtyVersion — changes made during the write trigger the next round
            }
        }
        finally { System.Threading.Volatile.Write(ref _saveRunning, 0); }
    }

    /// <summary>tmp + atomic replace + async retries (no locks held, never blocks the UI thread).</summary>
    private static async System.Threading.Tasks.Task<bool> WriteAtomicAsync(string json)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                Directory.CreateDirectory(ConfigDir);
                var tmp = ConfigPath + ".tmp";
                await File.WriteAllTextAsync(tmp, json).ConfigureAwait(false);
                File.Move(tmp, ConfigPath, true);   // atomic replace: a crash mid-write never truncates the old config
                return true;
            }
            catch (IOException) when (attempt < 4)
            { await System.Threading.Tasks.Task.Delay(40 * attempt).ConfigureAwait(false); }
            catch (Exception ex)
            { AppLog.Error("Config", $"Config save failed (after {attempt} attempt(s)): " + ex.Message); return false; }
        }
    }

    /// <summary>Exit safety net (called from ProcessExit): cancels the debounce and synchronously writes any unpersisted changes to disk.
    /// Guarantees the last config change is never lost when the app exits.</summary>
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
                try
                {
                    Directory.CreateDirectory(ConfigDir);
                    var tmp = ConfigPath + ".tmp";
                    File.WriteAllText(tmp, json);
                    File.Move(tmp, ConfigPath, true);
                    System.Threading.Volatile.Write(ref _savedVersion, v);
                    return;
                }
                catch (IOException) when (attempt < 4) { System.Threading.Thread.Sleep(40 * attempt); }
                catch (Exception ex) { AppLog.Error("Config", "Exit Flush write failed: " + ex.Message); return; }
            }
        }
        catch (Exception ex) { AppLog.Error("Config", "Flush exception: " + ex.Message); }
    }
}

/// <summary>v0.46.0: mod save profile (modeled after Stardrop profiles) — records which mods (by UniqueID) are enabled under this profile.</summary>
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

    // Nexus cover cache: mod folder name → cover image URL (saved opportunistically during update checks so lists open instantly)
    public Dictionary<string, string> ModCovers { get; set; } = new();

    /// <summary>User-assigned remark per mod: mod folder name → remark (shown in the list as "remark (original name)").</summary>
    public Dictionary<string, string> ModRemarks { get; set; } = new();

    /// <summary>v1.01.0: Nexus page search history (mirrors the site's Recent Searches; up to 10 entries, newest first).</summary>
    public List<string> NexusSearchHistory { get; set; } = new();

    /// <summary>
    /// Filter adult content toggle. On by default — Nexus browsing/search always excludes adult content;
    /// when turned off, the settings page asks for a date of birth to verify age 18+ (local check only, no online verification).
    /// </summary>
    public bool FilterAdultContent { get; set; } = true;
    /// <summary>
    /// Nexus one-click install (no browser popup; downloads in the background and installs straight into Mods). On by default;
    /// when turned off, the "Install" button on the detail page falls back to opening the built-in browser.
    /// </summary>
    public bool EnableOneClickInstall { get; set; } = true;

    /// <summary>User info cached after Nexus login (from /v1/users/validate.json).</summary>
    public string NexusUserName { get; set; } = "";
    public string NexusUserEmail { get; set; } = "";
    public string NexusProfileUrl { get; set; } = "";
    public bool   NexusIsPremium { get; set; }
    /// <summary>v0.69.0: modId → date (yyyy-MM-dd) of the last file downloaded from that mod. Recorded on local install/update and merged with the Nexus download history.</summary>
    public Dictionary<string, string> ModLastDownload { get; set; } = new();
    /// <summary>v0.69.0: fileId → download date for that file (only files downloaded through the app itself).</summary>
    public Dictionary<string, string> ModFileLastDownload { get; set; } = new();

    /// <summary>v0.68.2: settings-page "auto-install updates" toggle (Nexus Premium members only).
    /// When on, entering the Mods page installs detected updates automatically instead of showing a confirmation dialog.</summary>
    public bool EnableAutoInstall { get; set; } = false;

    /// <summary>v0.2.2: keep the task manager floating window always visible. When on it stays visible; when off it shows only while download tasks run.</summary>
    public bool ShowTaskDock { get; set; } = true;
    public string NexusAvatarDataUri { get; set; } = "";

    /// <summary>Total play time (minutes). LauncherService accumulates when the game process exits.</summary>
    public long TotalPlayMinutes { get; set; }

    /// <summary>v0.46.0: mod save profiles ("Default" is the built-in profile and cannot be deleted).</summary>
    public List<ModProfile> ModProfiles { get; set; } = new();
    /// <summary>Name of the currently active profile.</summary>
    public string ActiveProfile { get; set; } = "Default";

    /// <summary>Official Nexus category table (category_id → English name), fetched once at runtime with an API key and cached.</summary>
    public Dictionary<int, string> NexusCategories { get; set; } = new();
    /// <summary>mod folder → English category name on the site (cached opportunistically during update checks / cover fetches; same lifetime as ModCovers).</summary>
    public Dictionary<string, string> ModCategories { get; set; } = new();

    /// <summary>
    /// vNext: update-check fingerprint cache — Nexus modId → (updatedAt fingerprint, latest MAIN file version found in the last deep check, check time).
    /// Entering the Mods page first runs a keyless GraphQL batch fingerprint comparison: mods whose updatedAt is unchanged reuse the cached version
    /// (the file list hasn't changed, so the result can't be stale); only those with a changed/missing fingerprint get an individual files.json deep check,
    /// whose result is written back to this cache.
    /// Persisted in the config so it survives app restarts — a routine page visit collapses from N requests to ~N/50.
    /// </summary>
    public Dictionary<int, ModUpdateFingerprintEntry> ModUpdateFingerprints { get; set; } = new();

    /// <summary>v0.2.1: unified cache directory (null = each cache type uses its historical default location).
    /// When set, download/install temp, SMAPI installer, WebView2 data, and Mods backups all move into subdirectories under it.</summary>
    public string? CacheRoot { get; set; }

    /// <summary>v0.2.2: pending WebView2 data directory migration marker — when changing the cache directory, WebView2 is in use and can't move right away,
    /// so the old location is recorded; it migrates automatically on next startup (before WebView2 initialization) and the marker is cleared.</summary>
    public string? PendingWebView2MoveFrom { get; set; }

    /// <summary>v0.2.1: memory management — timed automatic compaction toggle and interval (minutes).</summary>
    public bool MemTimerEnabled { get; set; } = false;
    public int MemTimerMinutes { get; set; } = 30;

    /// <summary>v0.2.1: memory management — compact automatically when system memory usage reaches the threshold (%).</summary>
    public bool MemThresholdEnabled { get; set; } = false;
    public int MemThresholdPercent { get; set; } = 80;

}

/// <summary>vNext: a single update-check fingerprint. UpdatedAt is compared verbatim against the GraphQL batch result;
/// LatestFileVersion is only written after a successful files.json deep check (same authoritative source as installation);
/// CheckedAtUtc gives the cache a validity floor (24h, guarding against extreme cases beyond the updatedAt assumption).</summary>
public sealed class ModUpdateFingerprintEntry
{
    public string UpdatedAt { get; set; } = "";
    public string LatestFileVersion { get; set; } = "";
    public DateTime CheckedAtUtc { get; set; } = DateTime.UtcNow;
}
