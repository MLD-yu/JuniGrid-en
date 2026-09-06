using System.IO;
using System.Text.Json;

namespace JuniGrid.Services;

/// <summary>
/// v1.1.5: daily play-time tracking (data source for the GitHub-style heatmap).
/// A background loop polls the game process every 30s (LauncherService.IsGameRunning covers both sessions
/// started by this app and externally started Stardew Valley / StardewModdingAPI); while running, 30s is
/// added to that day's seconds bucket and persisted — session-level Start/Exit hooks (the
/// LauncherService.OnGameExit approach) lose the whole session if JuniGrid is killed mid-way, while
/// per-tick accumulation loses at most the last tick.
/// Data is stored in %APPDATA%/JuniGrid/playtime.json: { "yyyy-MM-dd": seconds }.
/// </summary>
public sealed class PlayTimeService : IDisposable
{
    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "JuniGrid", "playtime.json");

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly LauncherService _launcher;
    private readonly System.Threading.Timer _timer;
    private readonly object _gate = new();
    private Dictionary<string, long> _seconds = new();   // date (local) → play seconds that day

    /// <summary>Raised after the data changes (tick accumulation / manual correction); may come from a background thread, subscribers must dispatch themselves.</summary>
    public event Action? OnChanged;

    public PlayTimeService(LauncherService launcher)
    {
        _launcher = launcher;
        Load();
        System.AppDomain.CurrentDomain.ProcessExit += (_, _) => Save();
        // delay the first tick by 5s to avoid contending for IO with the burst of startup initialization
        _timer = new System.Threading.Timer(_ => Tick(), null, 5000, 30000);
    }

    public IReadOnlyDictionary<string, long> Snapshot { get { lock (_gate) return new Dictionary<string, long>(_seconds); } }

    public long GetSeconds(string dateKey) { lock (_gate) return _seconds.TryGetValue(dateKey, out var s) ? s : 0; }

    /// <summary>Total play seconds across all years (heatmap measure; used by the home page's "Recently played" card).</summary>
    public long TotalSeconds { get { lock (_gate) { var t = 0L; foreach (var v in _seconds.Values) t += v; return t; } } }

    private void Tick()
    {
        try
        {
            if (!_launcher.IsGameRunning) return;
            lock (_gate)
            {
                var key = DateTime.Now.ToString("yyyy-MM-dd");
                _seconds[key] = GetSeconds(key) + 30;
                Save();
            }
            OnChanged?.Invoke();
        }
        catch { /* a failed tick doesn't affect the main flow; retry on the next tick */ }
    }

    private void Load()
    {
        try
        {
            if (File.Exists(FilePath))
                _seconds = JsonSerializer.Deserialize<Dictionary<string, long>>(File.ReadAllText(FilePath), JsonOpts)
                           ?? new Dictionary<string, long>();
        }
        catch (Exception ex) { AppLog.Warn("PlayTime", ex.Message); _seconds = new(); }
    }

    private void Save()
    {
        try
        {
            lock (_gate)
                AtomicFile.WriteAllText(FilePath, JsonSerializer.Serialize(_seconds, JsonOpts));
        }
        catch (Exception ex) { AppLog.Warn("PlayTime", ex.Message); }
    }

    public void Dispose() => _timer.Dispose();
}
