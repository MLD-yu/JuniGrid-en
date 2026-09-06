using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;

namespace JuniGrid.Services;

/// <summary>
/// Global download/install task center. Direct mod installs, SMAPI updates and nxm links all report progress here.
/// The UI shows all current tasks and their live output on the floating bottom-right icon and the /tasks page.
/// v1.06.6: tasks are long-lived — changes are debounced to disk (tasks.json), restored automatically on restart,
/// and kept until the user clears them; tasks still "running" when saved (interrupted abnormally last time) are marked failed after restore.
/// </summary>
public sealed class TaskCenterService
{
    public ObservableCollection<TaskItem> Items { get; } = new();
    public event Action? OnChanged;

    private readonly object _lock = new();
    private static readonly string PersistPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "JuniGrid", "tasks.json");
    // v1.1.2: created in the constructor, fired only from RequestSave — the old lazy init was lock-free,
    // so concurrent first calls could create two Timers and leak one
    private readonly Timer _saveTimer;

    public TaskCenterService()
    {
        Load();
        _saveTimer = new Timer(_ => SaveNow(), null, Timeout.Infinite, Timeout.Infinite);
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

    /// <summary>Progress reports are very frequent; persisting is debounced by 800ms: the disk is written only after 800ms of silence.</summary>
    private void RequestSave() => _saveTimer.Change(800, Timeout.Infinite);

    private void SaveNow()
    {
        try
        {
            // v1.1.2: serialize inside the lock — previously the snapshot was shallow-copied under the lock but serialized
            // outside it, while download threads appended to t.Log concurrently, hitting "collection was modified" and
            // dropping that whole save; tasks.json is tiny (a few hundred KB at most) and written only once per 800ms
            // debounce, so the cost of copying + writing under the lock is negligible
            lock (_lock)
                AtomicFile.WriteAllText(PersistPath, JsonSerializer.Serialize(Items.ToList()));
        }
        catch (Exception ex) { AppLog.Warn("TaskCenter", "Failed to save tasks: " + ex.Message); }
    }

    /// <summary>Thread-safe snapshot of the task list — UI rendering must use this and never enumerate Items directly
    /// (download threads Insert/Remove at any time; enumerating on the render thread hits "collection was modified" and crashes the whole page).</summary>
    public List<TaskItem> Snapshot() { lock (_lock) return Items.ToList(); }

    /// <summary>Thread-safe copy of a single task's log (used to render the dropdown panel, avoiding races with background Report calls).</summary>
    public List<string> CopyLog(TaskItem t) { lock (_lock) return t.Log.ToList(); }

    public TaskItem Start(string title, string? kind = null)
    {
        var t = new TaskItem { Id = Guid.NewGuid(), Title = title, Kind = kind ?? "download",
            StartedAt = DateTime.Now, Status = "running" };
        // Insert at the head of the list so the newest created/downloaded task always sits on top (the /tasks page reads top to bottom).
        lock (_lock) Items.Insert(0, t);
        OnChanged?.Invoke();
        RequestSave();
        return t;
    }

    public void Report(TaskItem t, string line, double? percent = null, double? speedMBps = null)
    {
        // v1.08.2: download progress lines ("Downloading… x MB / y MB") are high-frequency repeated heartbeats —
        // they overwrite the previous heartbeat line in the log instead of being appended, otherwise a long download
        // easily exceeds the 200-line cap and squeezes the real step logs (backup/extraction etc.) out from the top.
        // Event lines (mirror switch/resume/phase change) are still appended.
        // v1.1.6: reads and writes of t.Log are done under _lock — the save timer thread serializes all of Items
        // (including every t.Log) inside the lock; an Add here outside the lock would hit "collection was modified"
        // and silently drop that save.
        lock (_lock)
        {
            if (IsDownloadHeartbeat(line)
                && t.Log.Count > 0 && IsDownloadHeartbeat(t.Log[t.Log.Count - 1]))
            {
                t.Log[t.Log.Count - 1] = $"[{DateTime.Now:HH:mm:ss}] {line}";
            }
            else
            {
                t.Log.Add($"[{DateTime.Now:HH:mm:ss}] {line}");
                if (t.Log.Count > 200) t.Log.RemoveAt(0);
            }
        }
        if (percent is not null) t.Percent = percent.Value;
        if (speedMBps is not null) t.SpeedMBps = speedMBps.Value;
        t.LastLine = line;
        OnChanged?.Invoke();
        RequestSave();
    }

    /// <summary>Whether a timestamped log line is a download heartbeat line (Report adds the "[HH:mm:ss] " prefix when writing).</summary>
    private static bool IsDownloadHeartbeat(string logLine)
    {
        var i = logLine.IndexOf("] ", StringComparison.Ordinal);
        var body = i >= 0 && logLine.StartsWith("[") ? logLine[(i + 2)..] : logLine;
        return body.StartsWith("Downloading…", StringComparison.Ordinal);
    }

    public void Finish(TaskItem t, bool success, string? finalMsg = null)
    {
        t.Status = success ? "done" : "failed";
        t.Percent = success ? 100 : t.Percent;
        t.SpeedMBps = 0;
        if (finalMsg is not null)
        {
            lock (_lock)
            {
                t.Log.Add($"[{DateTime.Now:HH:mm:ss}] {finalMsg}");
                if (t.Log.Count > 200) t.Log.RemoveAt(0);
            }
            t.LastLine = finalMsg;
        }
        OnChanged?.Invoke();
        RequestSave();
    }

    public void Remove(TaskItem t)
    {
        lock (_lock) Items.Remove(t);
        // v1.1.3: removing = cancelling the background download/install synchronously — previously only the entry was
        // deleted and the pipeline kept running to the end, so the mod still showed up on the Mod Manager page
        // (removing mid-download even "resurrected" it).
        try { t.Cts.Cancel(); } catch { }
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

    /// <summary>v1.06.6: clear by condition (the tasks page "Clear all" = remove every task matching the current filter).</summary>
    public void ClearMatching(Func<TaskItem, bool> match)
    {
        List<TaskItem>? killed = null;
        lock (_lock)
        {
            for (int i = Items.Count - 1; i >= 0; i--)
                if (match(Items[i]))
                {
                    if (Items[i].Status == "running") (killed ??= new()).Add(Items[i]);
                    Items.RemoveAt(i);
                }
        }
        // v1.1.3: batch clear also cancels tasks still running, so a cleared task does not keep installing
        if (killed is not null)
            foreach (var k in killed) { try { k.Cts.Cancel(); } catch { } }
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

                // When nothing is running but finished/failed records remain, keep showing the top task's
                // final percent (100 when it succeeded) so total progress does not suddenly drop to 0 on completion.
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
        // v1.06.7: a setter is required — a read-only collection property is not populated during deserialization,
        // so tasks restored after a restart would lose all their logs
        public List<string> Log { get; set; } = new();
    public DateTime StartedAt { get; set; }

    /// <summary>v1.1.3: task cancellation source — cancels the background download/install on "Remove/Clear"; not persisted.</summary>
    [JsonIgnore]
    public CancellationTokenSource Cts { get; } = new();
}
