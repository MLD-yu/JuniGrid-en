using System.Diagnostics;
using System.IO;
using System.Text;

namespace JuniGrid.Services;

/// <summary>
/// Launches Stardew Valley (SMAPI modded or Steam vanilla) and streams SMAPI
/// output to the Logs page: the leveled SMAPI-latest.txt is tailed for
/// colored log lines, stderr is kept for native errors, and [JuniGrid] lines
/// mark launcher events.
/// </summary>
public sealed class LauncherService
{
    private readonly ConfigService _cfg;
    public LauncherService(ConfigService cfg) { _cfg = cfg; }

    private DateTime? _sessionStart;

    private void OnGameExit()
    {
        if (_sessionStart is null) return;
        var mins = (long)Math.Round((DateTime.Now - _sessionStart.Value).TotalMinutes);
        if (mins > 0)
        {
            var c = _cfg.Current;
            c.TotalPlayMinutes += mins;
            _cfg.Save(c);
        }
        _sessionStart = null;
    }

    /// <summary>Raised for every stdout/stderr line SMAPI prints.</summary>
    public event Action<string>? OnLogLine;

    // The Logs page component is destroyed on every navigation, so the line
    // history must live here or the log "clears" whenever you leave /logs.
    private const int MaxLogLines = 2000;
    private readonly List<string> _logBuffer = new();
    private readonly object _logLock = new();

    private void RaiseLog(string line)
    {
        lock (_logLock)
        {
            _logBuffer.Add(line);
            if (_logBuffer.Count > MaxLogLines)
                _logBuffer.RemoveRange(0, _logBuffer.Count - MaxLogLines);
        }
        OnLogLine?.Invoke(line);
    }

    /// <summary>Copy of the buffered log lines, oldest first.</summary>
    public IReadOnlyList<string> GetLogSnapshot()
    {
        lock (_logLock) return _logBuffer.ToArray();
    }

    public void ClearLog()
    {
        lock (_logLock) _logBuffer.Clear();
    }

    // ------------------------------------------------------------------
    // SMAPI log-file tailing
    // ------------------------------------------------------------------
    private static string SmapiLogPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "StardewValley", "ErrorLogs", "SMAPI-latest.txt");

    private CancellationTokenSource? _logTailCts;
    private int _logTailGen;

    private void StartLogTail(bool readFromStart = false)
    {
        var old = _logTailCts;
        var cts = new CancellationTokenSource();
        _logTailCts = cts;
        try { old?.Cancel(); old?.Dispose(); } catch { }   // v1.1.6: the old CTS is disposed instead of leaking one on every launch
        var token = cts.Token;
        ++_logTailGen;
        var path = SmapiLogPath;

        // Baseline: SMAPI rewrites the whole file on every launch, so the first line's
        // timestamp always changes. Detect a new session by "first line changed /
        // file got shorter" and read from the start when that happens.
        // readFromStart: when attaching to an already-running game, read from the
        // file head to backfill this session's history into the view.
        long pos = 0;
        string? baseFirstLine = null;
        try
        {
            if (File.Exists(path))
            {
                baseFirstLine = FirstLineOf(path);
                pos = readFromStart ? 0 : new FileInfo(path).Length;
            }
        }
        catch { /* If the baseline can't be read, start from 0 */ }

        _ = Task.Run(async () =>
        {
            var buf = new byte[64 * 1024];
            while (!token.IsCancellationRequested)
            {
                try
                {
                    if (File.Exists(path))
                    {
                        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read,
                            FileShare.ReadWrite | FileShare.Delete);
                        var first = FirstLineOf(path);
                        if (pos > fs.Length || (first is not null && first != baseFirstLine))
                        {
                            pos = 0;                    // file rewritten by a new session
                            baseFirstLine = first;
                        }
                        if (fs.Length > pos)
                        {
                            fs.Seek(pos, SeekOrigin.Begin);
                            using var ms = new MemoryStream();
                            int n;
                            while ((n = fs.Read(buf, 0, buf.Length)) > 0)
                                ms.Write(buf, 0, n);
                            var bytes = ms.ToArray();
                            // Consume only up to the last newline, leaving a partial line for the next round;
                            // \n never appears in the middle of a UTF-8 multi-byte sequence, so scanning
                            // bytes for newlines is safe
                            var lastNl = -1;
                            for (var i = bytes.Length - 1; i >= 0; i--)
                                if (bytes[i] == (byte)'\n') { lastNl = i; break; }
                            if (lastNl >= 0)
                            {
                                foreach (var raw in Encoding.UTF8.GetString(bytes, 0, lastNl + 1).Split('\n'))
                                {
                                    var line = raw.TrimEnd('\r');
                                    if (line.Length > 0) RaiseLog(line);
                                }
                                pos += lastNl + 1;
                            }
                        }
                    }
                }
                catch { /* Transient errors such as the file being locked: retry next round */ }
                try { await Task.Delay(250, token); }
                catch (TaskCanceledException) { break; }
            }
        });
    }

    private void StopLogTail()
    {
        var old = _logTailCts;
        _logTailCts = null;
        try { old?.Cancel(); old?.Dispose(); } catch { }
    }

    /// <summary>
    /// The game is running but wasn't launched by this app (e.g. JuniGrid was restarted)
    /// → attach to the existing SMAPI log file and backfill the current session's
    /// content into the log view from the file head.
    /// </summary>
    /// <summary>Whether any process with one of the given names exists. v1.1.6: each Process
    /// returned by GetProcessesByName holds an OS handle, and previously these were never
    /// disposed, relying on the finalizer — IsGameRunning is shared by 1.5s polling +
    /// 30s stats + the watchdog, making it a resident hot path where handle/GC pressure kept building up.</summary>
    private static bool AnyProcess(params string[] names)
    {
        foreach (var name in names)
            foreach (var p in Process.GetProcessesByName(name))
                using (p) return true;
        return false;
    }

    /// <summary>Performs an action on all processes with the given names (each result is disposed; a failure on one process doesn't affect the rest).</summary>
    private static void ForEachProcess(string[] names, Action<Process> action)
    {
        foreach (var name in names)
            foreach (var p in Process.GetProcessesByName(name))
                using (p)
                    try { action(p); }
                    catch (Exception ex) { AppLog.Warn("LauncherService", ex.Message); }
    }

    public void AttachIfGameRunning()
    {
        if (_smapiProcess is { HasExited: false }) return;   // launched by us, already tracked
        if (AnyProcess("StardewModdingAPI", "Stardew Valley")) StartLogTail(readFromStart: true);
    }

    private static string? FirstLineOf(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            using var sr = new StreamReader(fs, Encoding.UTF8);
            return sr.ReadLine();
        }
        catch { return null; }
    }

    private Process? _smapiProcess;

    public bool IsGameRunning =>
        _smapiProcess is { HasExited: false } ||
        AnyProcess("StardewModdingAPI", "Stardew Valley");

    /// <summary>Whether commands can be sent to the SMAPI console: the game must have been
    /// launched by this app and not yet exited (an attached external process has no stdin,
    /// so the input box is greyed out).</summary>
    public bool CanSendCommand => _smapiProcess is { HasExited: false };

    // Commands SMAPI recognizes directly: core commands + the Console Commands mod (TrainerMod) installed with SMAPI.
    // Anything outside the whitelist is treated as a built-in game debug command (e.g. money 5000, warp ...) and forwarded with a debug prefix added automatically.
    private static readonly HashSet<string> SmapiKnownCommands = new(StringComparer.OrdinalIgnoreCase)
    {
        // SMAPI core
        "help", "harmony_summary", "reload_i18n",
        // Console Commands mod
        "apply_save_fix", "debug", "hurry_all", "list_items", "log_context",
        "player_add", "player_changecolor", "player_changestyle", "player_sethealth",
        "player_setmaxhealth", "player_setmaxstamina", "player_setmoney", "player_setname",
        "player_setstamina", "regenerate_bundles", "set_farm_type", "set_verbose",
        "show_data_files", "show_game_files",
        "world_clear", "world_downminelevel", "world_freezetime", "world_setday",
        "world_setminelevel", "world_setseason", "world_settime", "world_setyear"
    };

    /// <summary>Writes a command to the SMAPI console, equivalent to typing it into the SMAPI window and pressing Enter.
    /// Commands not built into SMAPI get a debug prefix automatically (game debug commands only take effect when forwarded via debug).</summary>
    public bool SendCommand(string command)
    {
        var p = _smapiProcess;
        if (p is not { HasExited: false }) return false;
        try
        {
            var trimmed = command.Trim();
            var first = trimmed.Split(' ', 2)[0];
            var actual = SmapiKnownCommands.Contains(first) ? trimmed : "debug " + trimmed;
            RaiseLog("[JuniGrid] > " + actual);
            p.StandardInput.WriteLine(actual);
            return true;
        }
        catch (Exception ex)
        {
            AppLog.Warn("LauncherService", ex.Message);
            RaiseLog("[JuniGrid] Failed to send command: " + ex.Message);
            return false;
        }
    }

    /// <summary>
    /// Cleanup watchdog for a cancelled launch: Steam's launch pipeline may spawn the game
    /// process only after "Cancel" is clicked (family-sharing checks and preloading take a
    /// few seconds), so a one-shot KillGame can miss it → the game still ends up running.
    /// Keeps watching for windowMs milliseconds and closes any SMAPI/game process that
    /// appears in the meantime; ends early once no process has been seen for 2.5s straight,
    /// or when stopWhen() returns true (the user clicked Launch again).
    /// </summary>
    public async Task KillGameWatchdogAsync(int windowMs = 20000, Func<bool>? stopWhen = null)
    {
        KillGame();
        var deadline = Environment.TickCount64 + windowMs;
        var lastActive = Environment.TickCount64;
        while (Environment.TickCount64 < deadline)
        {
            if (stopWhen?.Invoke() == true) return;
            var found = false;
            foreach (var name in new[] { "Stardew Valley", "StardewModdingAPI" })
            {
                var procs = Process.GetProcessesByName(name);
                if (procs.Length > 0) found = true;
                foreach (var p in procs)
                using (p)
                {
                    try { p.CloseMainWindow(); } catch (Exception __ex) { AppLog.Warn("LauncherService", __ex.Message); }
                    try { p.Kill(true); } catch (Exception __ex) { AppLog.Warn("LauncherService", __ex.Message); }
                }
            }
            if (found) lastActive = Environment.TickCount64;
            else if (Environment.TickCount64 - lastActive > 2500) return;   // no process for 2.5s straight → cleanup done
            await Task.Delay(400);
        }
    }

    /// <summary>
    /// Closes the game process: same as how Steam itself closes a game — first request a
    /// graceful exit so the game can write to disk, then reap processes that still haven't
    /// exited. Covers both the SMAPI and official Steam launch modes.
    /// </summary>
    public void KillGame()
    {
        // First close the SMAPI child process we own gracefully
        if (_smapiProcess is { HasExited: false })
        {
            try { _smapiProcess.CloseMainWindow(); } catch (Exception __ex) { AppLog.Warn("LauncherService", __ex.Message); }
            try { _smapiProcess.Kill(true); } catch (Exception __ex) { AppLog.Warn("LauncherService", __ex.Message); }
            _smapiProcess = null;
        }

        // Main game process (StardewModdingAPI.exe / Stardew Valley.exe): request a save first
        foreach (var name in new[] { "Stardew Valley", "StardewModdingAPI" })
            ForEachProcess(new[] { name }, p => p.CloseMainWindow());

        // Give the main UI process a moment to write to disk, then force-kill any that remain
        Task.Delay(300).ContinueWith(_ =>
        {
            foreach (var name in new[] { "Stardew Valley", "StardewModdingAPI" })
                ForEachProcess(new[] { name }, p => p.Kill(true));
        });
    }

    // ------------------------------------------------------------------
    // Pre-flight checks
    // ------------------------------------------------------------------
    public PreFlightResult CheckSmapi(string gamePath)
    {
        if (string.IsNullOrWhiteSpace(gamePath))
            return PreFlightResult.Fail("No game path is set yet. Please pick one on the settings page first.");

        if (!Directory.Exists(gamePath))
            return PreFlightResult.Fail($"Game directory does not exist: {gamePath}");

        var exe = Path.Combine(gamePath, "StardewModdingAPI.exe");
        if (!File.Exists(exe))
            return PreFlightResult.Fail(
                $"SMAPI not found: {exe}\n\nDownload and install it from smapi.io, or switch to 'Official Steam' launch on the home page.");

        return PreFlightResult.Ok();
    }

    public PreFlightResult CheckSteam()
    {
        if (!IsSteamRunning)
            return PreFlightResult.Warn(
                "The Steam client doesn't seem to be running; the game will be launched via the steam:// protocol (may be a bit slower).");
        return PreFlightResult.Ok();
    }

    /// <summary>Whether the Steam client is running (webhelper included). Used to detect "Steam was closed" while waiting for the game to launch.</summary>
    public static bool IsSteamRunning => AnyProcess("steam", "steamwebhelper");

    // ------------------------------------------------------------------
    // Launch
    // ------------------------------------------------------------------
    public LaunchResult LaunchSmapi(string gamePath)
    {
        var check = CheckSmapi(gamePath);
        if (!check.Success) return LaunchResult.Fail(check.Message!);

        var exe = Path.Combine(gamePath, "StardewModdingAPI.exe");
        try
        {
            _smapiProcess = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = exe,
                    WorkingDirectory = gamePath,
                    UseShellExecute = false,
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    // SMAPI sets Console.InputEncoding to UTF-16LE in LogManager (fixed behavior on Windows).
                    // With stdin redirected it decodes the pipe bytes as UTF-16 — so we must write UTF-16LE
                    // without a BOM, otherwise everything SMAPI reads is garbled and console commands never
                    // work (same mechanism as the NUL characters on the output side).
                    StandardInputEncoding = new UnicodeEncoding(bigEndian: false, byteOrderMark: false),
                    StandardOutputEncoding = Encoding.UTF8,
                    StandardErrorEncoding = Encoding.UTF8,
                    CreateNoWindow = true
                },
                EnableRaisingEvents = true
            };

            // The log view's content source is SMAPI-latest.txt (each line carries its own level, which drives the color classification);
            // each stdout line is just "[SMAPI] message" without a level, but it still has to be drained continuously so the pipe never fills up and blocks the game.
            _smapiProcess.OutputDataReceived += (_, _) => { };
            _smapiProcess.ErrorDataReceived += (_, e) =>
            {
                if (e.Data is not null) RaiseLog("[ERR] " + CleanSmapiLine(e.Data));
            };
            _smapiProcess.Exited += (_, _) =>
            {
                OnGameExit();
                RaiseLog($"[JuniGrid] Game process exited, code {_smapiProcess?.ExitCode}");
                // Leave 3 seconds to finish reading the tail of the log before stopping
                var gen = _logTailGen;
                _ = Task.Delay(3000).ContinueWith(_ =>
                {
                    if (_logTailGen == gen) StopLogTail();
                });
            };

            _smapiProcess.Start();
            _sessionStart = DateTime.Now;
            // New session, new view: clear the previous session's logs so old and new content don't mix
            ClearLog();
            RaiseLog($"[JuniGrid] SMAPI process started (PID {_smapiProcess.Id})");
            StartLogTail();
            _smapiProcess.BeginOutputReadLine();
            _smapiProcess.BeginErrorReadLine();

            return LaunchResult.Ok(_smapiProcess.Id);
        }
        catch (Exception ex)
        {
            return LaunchResult.Fail(ex.Message);
        }
    }

    /// <summary>
    /// SMAPI's redirected output is written as UTF-16LE (each ASCII character is followed by a 0x00 byte),
    /// and decoding it as UTF-8 leaves NUL (\0) characters embedded in the string — NULs are invisible,
    /// but in a monospace font + pre-wrap each one occupies a full character cell, so the log looks like
    /// "a space between every letter".
    /// SMAPI console output is pure ASCII, so stripping the NUL/zero-width characters restores it exactly.
    /// </summary>
    private static string CleanSmapiLine(string line)
    {
        if (line.IndexOf('\0') < 0) return line;
        return line.Replace("\0", "");
    }

    private async Task TrackSteamExitAsync()
    {
        // Wait for the game process to start, then wait for it to exit
        for (int i = 0; i < 60 && _sessionStart is not null; i++)
        {
            if (AnyProcess("Stardew Valley")) break;
            await Task.Delay(1000);
        }
        while (_sessionStart is not null && AnyProcess("Stardew Valley"))
        {
            await Task.Delay(3000);
        }
        OnGameExit();
    }

    public LaunchResult LaunchSteam(string steamAppId)
    {
        // Pre-check: no path at all → almost certainly the Steam account doesn't own the game, or it isn't installed
        if (string.IsNullOrWhiteSpace(_cfg.Current.GamePath) || !Directory.Exists(_cfg.Current.GamePath))
            return LaunchResult.Fail(
                "No Stardew Valley game directory detected.\n\n" +
                "Possible causes:\n" +
                "  · The current Steam account doesn't own this game (buy it on Steam first)\n" +
                "  · The game isn't installed or the path is wrong → set the folder manually in \"Settings\"\n\n" +
                "The launcher will try to start the game via the steam:// protocol; if Steam pops up \"this account does not own this game\", that is the cause.");

        // Start the stopwatch in Steam mode too (in official Steam mode we can't see the child process exit, so we detect it via Stardew Valley.exe)
        _sessionStart = DateTime.Now;
        _ = TrackSteamExitAsync();

        var check = CheckSteam();
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = $"steam://rungameid/{steamAppId}",
                UseShellExecute = true
            });
            return check.Success
                ? LaunchResult.Ok(null)
                : LaunchResult.Ok(null, check.Message);
        }
        catch (Exception ex)
        {
            return LaunchResult.Fail("Could not launch via Steam: " + ex.Message);
        }
    }
}

public readonly record struct PreFlightResult(bool Success, bool IsWarning, string? Message)
{
    public static PreFlightResult Ok() => new(true, false, null);
    public static PreFlightResult Warn(string msg) => new(true, true, msg);
    public static PreFlightResult Fail(string msg) => new(false, false, msg);
}

public readonly record struct LaunchResult(bool Success, int? Pid, string? Error, string? Warning = null)
{
    public static LaunchResult Ok(int? pid, string? warning = null) => new(true, pid, null, warning);
    public static LaunchResult Fail(string err) => new(false, null, err);
}
