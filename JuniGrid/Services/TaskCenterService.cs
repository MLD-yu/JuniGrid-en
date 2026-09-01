using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using System.Threading;

namespace JuniGrid.Services;

/// <summary>
/// Global download/install task center. Mod direct installs, SMAPI updates, and nxm handling all report progress here.
/// The UI shows all current tasks and their live output via the floating icon in the bottom-right corner + the /tasks page.
/// v1.06.6: tasks persist — changes are debounced to disk (tasks.json), restored automatically on restart, and kept until the user clears them;
/// tasks still marked "running" on disk (interrupted abnormally last time) are marked failed after restore.
/// </summary>
public sealed class TaskCenterService
{
    public ObservableCollection<TaskItem> Items { get; } = new();
    public event Action? OnChanged;

    private readonly object _lock = new();
    private static readonly string PersistPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "JuniGrid", "tasks.json");
    private Timer? _saveTimer;

    public TaskCenterService()
    {
        Load();
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(PersistPath)) return;
            var list = JsonSerializer.Deserialize<List<TaskItem>>(File.ReadAllText(PersistPath));
            if (list is null) return;
            foreach (var t in list)
            {
                if (t.Status == "running") t.Status = "failed";   // a download interrupted last time cannot resume
                Items.Add(t);
            }
        }
        catch (Exception ex) { AppLog.Warn("TaskCenter", "Failed to restore tasks: " + ex.Message); }
    }

    /// <summary>Progress is reported very frequently; persistence is debounced at 800ms — the write happens only after 800ms of silence.</summary>
    private void RequestSave()
    {
        if (_saveTimer is null)
        {
            _saveTimer = new Timer(_ => SaveNow(), null, 800, Timeout.Infinite);
            return;
        }
        _saveTimer.Change(800, Timeout.Infinite);
    }

    private void SaveNow()
    {
        try
        {
            List<TaskItem> snap;
            lock (_lock) snap = Items.ToList();
            Directory.CreateDirectory(Path.GetDirectoryName(PersistPath)!);
            File.WriteAllText(PersistPath, JsonSerializer.Serialize(snap));
        }
        catch (Exception ex) { AppLog.Warn("TaskCenter", "Failed to persist tasks: " + ex.Message); }
    }

    public TaskItem Start(string title, string? kind = null)
    {
        var t = new TaskItem { Id = Guid.NewGuid(), Title = title, Kind = kind ?? "download",
            StartedAt = DateTime.Now, Status = "running" };
        // Insert at the head of the list so the newest created/downloaded task always sits on top (the /tasks page reads top-down).
        lock (_lock) Items.Insert(0, t);
        OnChanged?.Invoke();
        RequestSave();
        return t;
    }

    public void Report(TaskItem t, string line, double? percent = null, double? speedMBps = null)
    {
        t.Log.Add($"[{DateTime.Now:HH:mm:ss}] {line}");
        if (t.Log.Count > 200) t.Log.RemoveAt(0);
        if (percent is not null) t.Percent = percent.Value;
        if (speedMBps is not null) t.SpeedMBps = speedMBps.Value;
        t.LastLine = line;
        OnChanged?.Invoke();
        RequestSave();
    }

    public void Finish(TaskItem t, bool success, string? finalMsg = null)
    {
        t.Status = success ? "done" : "failed";
        t.Percent = success ? 100 : t.Percent;
        t.SpeedMBps = 0;
        if (finalMsg is not null) { t.Log.Add($"[{DateTime.Now:HH:mm:ss}] {finalMsg}"); t.LastLine = finalMsg; }
        OnChanged?.Invoke();
        RequestSave();
    }

    public void Remove(TaskItem t)
    {
        lock (_lock) Items.Remove(t);
        OnChanged?.Invoke();
        RequestSave();
    }

    public void ClearFinished()
    {
        lock (_lock)
        {
            for (int i = Items.Count - 1; i >= 0; i--)
                if (Items[i].Status != "running") Items.RemoveAt(i);
        }
        OnChanged?.Invoke();
        RequestSave();
    }

    /// <summary>v1.06.6: clear by condition (the downloads page's "Clear all" = remove every task under the current filter).</summary>
    public void ClearMatching(Func<TaskItem, bool> match)
    {
        lock (_lock)
        {
            for (int i = Items.Count - 1; i >= 0; i--)
                if (match(Items[i])) Items.RemoveAt(i);
        }
        OnChanged?.Invoke();
        RequestSave();
    }

    public int RunningCount { get { lock (_lock) return Items.Count(t => t.Status == "running"); } }
    public double TotalPercent
    {
        get
        {
            lock (_lock)
            {
                var running = Items.Where(t => t.Status == "running").ToList();
                if (running.Count > 0) return running.Average(t => t.Percent);

                // With no running tasks but finished/failed records remaining, keep showing the top task's
                // final progress (100% on success) so the overall progress doesn't suddenly drop to 0 on completion.
                var top = Items.FirstOrDefault();
                return top?.Percent ?? 0;
            }
        }
    }
    public double TotalSpeedMBps
    {
        get { lock (_lock) return Items.Where(t => t.Status == "running").Sum(t => t.SpeedMBps); }
    }
}

public sealed class TaskItem
{
    public Guid Id { get; set; }
    public string Title { get; set; } = "";
    public string Kind { get; set; } = "download";       // download / install / update
    public string Status { get; set; } = "running";      // running / done / failed
    public double Percent { get; set; }
    public double SpeedMBps { get; set; }
        public string? LastLine { get; set; }
        // v1.06.7: must have a setter — read-only collection properties are not populated during deserialization, so restored tasks would lose all logs
        public List<string> Log { get; set; } = new();
    public DateTime StartedAt { get; set; }
}
