using System.Diagnostics;
using System.Runtime.InteropServices;

namespace JuniGrid.Services;

/// <summary>
/// v0.2.1: memory management — memory snapshots of the system/self/WebView2/game processes, working set
/// compression (self + game), timed and threshold-triggered auto compression, and system-level standby
/// memory page release (requires admin, performed via an elevated sub-instance).
/// A background loop runs for the app's lifetime (started on DI resolution): every 5s it pushes a snapshot and
/// checks the auto-compression conditions, regardless of whether the settings page is open.
/// OnSnapshot may fire on a background thread; UI subscribers must dispatch themselves.
/// </summary>
public sealed class MemoryService
{
    private readonly ConfigService _cfg;
    public MemoryService(ConfigService cfg)
    {
        _cfg = cfg;
        _ = LoopAsync();
    }

    public event Action<MemorySnapshot>? OnSnapshot;

    /// <summary>Memory snapshot. All per-process figures use the private working set measure, matching Task Manager's "Memory (active)" column.</summary>
    public sealed record MemorySnapshot(
        long SysTotalBytes,
        long SysAvailBytes,
        long AppWorkingSet,
        long AppGcBytes,
        long WebViewWorkingSet,
        int WebViewProcCount,
        long GameWorkingSet,
        bool GameRunning)
    {
        public double SysPercent => SysTotalBytes <= 0
            ? 0
            : (SysTotalBytes - SysAvailBytes) * 100.0 / SysTotalBytes;
    }

    public MemorySnapshot GetSnapshot() => Build();

    private DateTime _lastTrimUtc = DateTime.UtcNow;   // observe one cycle after startup before triggering
    private readonly object _trimGate = new();

    private async Task LoopAsync()
    {
        while (true)
        {
            try
            {
                var snap = Build();
                OnSnapshot?.Invoke(snap);

                var c = _cfg.Current;
                var now = DateTime.UtcNow;
                var sinceTrim = now - _lastTrimUtc;
                var cooldown = TimeSpan.FromMinutes(Math.Max(5, c.MemTimerMinutes));
                var thresholdHit = c.MemThresholdEnabled && snap.SysPercent >= Math.Max(50, c.MemThresholdPercent)
                                   && sinceTrim >= TimeSpan.FromMinutes(2);   // threshold-triggered trims at least 2 minutes apart to avoid hammering
                var timerHit = c.MemTimerEnabled && sinceTrim >= cooldown;
                if (thresholdHit || timerHit)
                {
                    lock (_trimGate)
                    {
                        // double-check: compress only if the time condition still holds after taking the lock, preventing concurrent double triggers
                        if (now - _lastTrimUtc >= (thresholdHit ? TimeSpan.FromMinutes(2) : cooldown))
                        {
                            _lastTrimUtc = now;
                            var (b, a) = CompressSelf();
                            AppLog.Warn("Memory",
                                $"Auto compression: {ResumableDownload.FormatBytes(b)} → {ResumableDownload.FormatBytes(a)}" +
                                $" ({(thresholdHit ? "threshold" : "timer")} triggered, system {snap.SysPercent:F0}%)");
                        }
                    }
                }
            }
            catch { /* no exception from snapshot/compression may take down the loop */ }
            await Task.Delay(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        }
    }

    /// <summary>Compress self: blocking compacting GC across all generations + swapping out the whole working set. Returns the (before, after) working set.</summary>
    public (long before, long after) CompressSelf()
    {
        var before = Environment.WorkingSet;
        GC.WaitForPendingFinalizers();
        for (var gen = 2; gen >= 0; gen--)
            GC.Collect(gen, GCCollectionMode.Forced, blocking: true, compacting: true);
        TrimWorkingSet();
        var after = Environment.WorkingSet;
        return (before, after);
    }

    /// <summary>Swaps out only this process's working set (no GC, no perceptible pause). The managed heap is small to begin with; most of
    /// the working set is runtime/JIT/framework images — the system pages them back in on demand. Used for gentle slimming after startup and on minimize.</summary>
    public static void TrimWorkingSet() =>
        SetProcessWorkingSetSize(GetCurrentProcess(), new IntPtr(-1), new IntPtr(-1));

    /// <summary>Trims the game process working set (both SMAPI and Steam launch modes are probed). Returns the number of processes handled.</summary>
    public int TrimGameWorkingSet()
    {
        var n = 0;
        foreach (var name in new[] { "StardewModdingAPI", "Stardew Valley" })
        {
            foreach (var p in Process.GetProcessesByName(name))
            {
                using (p)
                {
                    try { if (EmptyWorkingSet(p.Handle)) n++; } catch { /* exited / access denied */ }
                }
            }
        }
        return n;
    }

    /// <summary>
    /// Compresses the WebView2 child process working sets. Renderer processes are sandboxed, so GC can't reach them;
    /// but EmptyWorkingSet can swap their working sets out entirely (the system pages them back on demand,
    /// the UI is unaffected, though the first scroll/interaction after returning may lag slightly). Returns (process count, total working set before/after).
    /// </summary>
    public (int count, long before, long after) TrimWebView2()
    {
        var byPid = ProcTable();
        long before = 0, after = 0;
        var n = 0;
        foreach (var r in byPid.Values)
        {
            if (!r.ImageName.Equals("msedgewebview2.exe", StringComparison.OrdinalIgnoreCase)) continue;
            if (!InTreeOf(byPid, r.Pid)) continue;
            try
            {
                using var p = Process.GetProcessById((int)r.Pid);
                before += p.WorkingSet64;
                if (EmptyWorkingSet(p.Handle)) n++;
                p.Refresh();
                after += p.WorkingSet64;
            }
            catch { /* exited / access denied */ }
        }
        return (n, before, after);
    }

    // ------------------------------------------------------------------
    // Snapshot
    // ------------------------------------------------------------------

    private sealed record ProcRow(long Pid, long ParentPid, long PrivateWs, string ImageName);

    private static MemorySnapshot Build()
    {
        long total = 0, avail = 0;
        var ms = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
        if (GlobalMemoryStatusEx(ref ms)) { total = (long)ms.ullTotalPhys; avail = (long)ms.ullAvailPhys; }

        var byPid = ProcTable();
        var mine = (long)Environment.ProcessId;
        long appWs = 0, wvWs = 0, gameWs = 0;
        var wvCount = 0;
        var gameRunning = false;

        foreach (var r in byPid.Values)
        {
            var name = r.ImageName;
            if (name.Equals("msedgewebview2.exe", StringComparison.OrdinalIgnoreCase))
            {
                if (InTreeOf(byPid, r.Pid)) { wvWs += r.PrivateWs; wvCount++; }
            }
            else if (name.Equals("stardewmoddingapi.exe", StringComparison.OrdinalIgnoreCase) ||
                     name.Equals("stardew valley.exe", StringComparison.OrdinalIgnoreCase))
            {
                gameWs += r.PrivateWs;
                gameRunning = true;
            }
            else if (r.Pid == mine)
            {
                appWs = r.PrivateWs;
            }
        }

        return new MemorySnapshot(total, avail, appWs, GC.GetTotalMemory(forceFullCollection: false),
            wvWs, wvCount, gameWs, gameRunning);
    }

    /// <summary>Whether the pid hangs under [this process] (walks up the parent chain; the chain may pass through other webview processes).</summary>
    private static bool InTreeOf(Dictionary<long, ProcRow> byPid, long pid)
    {
        var mine = (long)Environment.ProcessId;
        var cur = pid;
        for (var hop = 0; hop < 16 && byPid.TryGetValue(cur, out var row); hop++)
        {
            if (row.ParentPid == mine) return true;
            cur = row.ParentPid;
        }
        return false;
    }

    /// <summary>
    /// Full process table (pid → parent pid/private working set/image name), fetched in a single NtQuerySystemInformation call.
    /// The consistent measure is the [private working set] — the same column as Task Manager's "Memory (active)".
    /// Previously the full working set (WorkingSet64) was used, which counted the image shared by the six Chromium
    /// processes once each — hence the card showing 425MB vs Task Manager's 145MB.
    /// </summary>
    private static Dictionary<long, ProcRow> ProcTable()
    {
        var byPid = new Dictionary<long, ProcRow>(96);
        var len = 0x40000;
        for (var attempt = 0; attempt < 4; attempt++)
        {
            var buf = Marshal.AllocHGlobal(len);
            try
            {
                var status = NtQuerySystemInformation(SystemProcessInformation, buf, len, out var needed);
                if (status != 0)
                {
                    // only "buffer too small" is worth retrying with a bigger buffer; give up on other errors
                    if (status != unchecked((int)0xC0000004)) break;
                    len = needed > len ? needed + 65536 : len * 2;
                    continue;
                }

                var cur = buf;
                while (true)
                {
                    // x64 SYSTEM_PROCESS_INFORMATION fixed field offsets (stable since Vista):
                    // +8 WorkingSetPrivateSize (private working set, bytes)
                    // +56 UNICODE_STRING.Length / +64 .Buffer (image name)
                    // +80 UniqueProcessId / +88 InheritedFromUniqueProcessId
                    var pid = Marshal.ReadInt64(cur, 80);
                    var parent = Marshal.ReadInt64(cur, 88);
                    var privateWs = Marshal.ReadInt64(cur, 8);
                    var nameLen = Marshal.ReadInt16(cur, 56);
                    var nameBuf = Marshal.ReadInt64(cur, 64);
                    var name = nameLen > 0 && nameBuf != 0
                        ? Marshal.PtrToStringUni(new IntPtr(nameBuf), nameLen / 2) ?? ""
                        : "";
                    byPid[pid] = new ProcRow(pid, parent, privateWs, name);

                    var next = Marshal.ReadInt32(cur, 0);   // NextEntryOffset; 0 = last entry
                    if (next <= 0 || next > 0x100000) break; // upper bound guards against a dirty-data infinite loop
                    cur = IntPtr.Add(cur, next);
                }
                return byPid;
            }
            finally { Marshal.FreeHGlobal(buf); }
        }
        return byPid;
    }

    private const int SystemProcessInformation = 5;

    // ------------------------------------------------------------------
    // Win32
    // ------------------------------------------------------------------

    [StructLayout(LayoutKind.Sequential)]
    private struct MEMORYSTATUSEX
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX buffer);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetProcessWorkingSetSize(IntPtr process, IntPtr min, IntPtr max);

    [DllImport("psapi.dll")]
    private static extern bool EmptyWorkingSet(IntPtr process);

    [DllImport("ntdll.dll")]
    private static extern int NtQuerySystemInformation(
        int infoClass, IntPtr buffer, int length, out int returnLength);
}
