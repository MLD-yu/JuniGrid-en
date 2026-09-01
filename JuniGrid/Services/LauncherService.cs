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
        _logTailCts?.Cancel();
        var cts = new CancellationTokenSource();
        _logTailCts = cts;
        var token = cts.Token;
        ++_logTailGen;
        var path = SmapiLogPath;

        // Baseline: SMAPI rewrites the entire file on every launch, so the timestamped first line always changes.
        // A changed first line / shorter file identifies a new session, in which case we read from the start.
        // readFromStart: when attaching to an already-running game, read from the file head to backfill this session's history.
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
        catch { /* baseline unreadable — just read from 0 */ }

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
                            pos = 0;                    // file was rewritten by a new session
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
                            // Consume only up to the last newline; leave a partial line for the next round.
                            // \n never appears inside a UTF-8 multi-byte sequence, so searching bytes for a newline is safe.
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
                catch { /* transient errors like the file being locked: retry next round */ }
                try { await Task.Delay(250, token); }
                catch (TaskCanceledException) { break; }
            }
        });
    }

    private void StopLogTail()
    {
        _logTailCts?.Cancel();
        _logTailCts = null;
    }

    /// <summary>
    /// The game is running but wasn't launched by this app (e.g. JuniGrid was restarted) → attach to the existing
    /// SMAPI log file and backfill this session's content into the log view from the file head.
    /// </summary>
    public void AttachIfGameRunning()
    {
        if (_smapiProcess is { HasExited: false }) return;   // launched by us, already tracked
        var running = Process.GetProcessesByName("StardewModdingAPI").Length > 0
                   || Process.GetProcessesByName("Stardew Valley").Length > 0;
        if (running) StartLogTail(readFromStart: true);
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
        Process.GetProcessesByName("StardewModdingAPI").Length > 0 ||
        Process.GetProcessesByName("Stardew Valley").Length > 0;

    /// <summary>Whether commands can be sent to the SMAPI console: the game must have been launched by this app and not exited
    /// (an externally attached process has no stdin, so the input box is greyed out).</summary>
    public bool CanSendCommand => _smapiProcess is { HasExited: false };

    // Commands SMAPI recognizes directly: core commands + the Console Commands mod (TrainerMod) shipped with SMAPI.
    // Input outside the whitelist is treated as a built-in game debug command (e.g. money 5000, warp …) and forwarded with an automatic debug prefix.
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

    /// <summary>Writes a command to the SMAPI console, equivalent to typing it in the SMAPI window and pressing Enter.
    /// Non-built-in SMAPI commands automatically get a debug prefix (game debug commands must go through debug to take effect).</summary>
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
    /// Closes the game process, same as Steam closing a game itself — first request a graceful exit so the game saves,
    /// then reap any process that still hasn't exited. Covers both SMAPI and official Steam launch modes.
    /// </summary>
    public void KillGame()
    {
        // Prefer a gentle close of the SMAPI child process we own
        if (_smapiProcess is { HasExited: false })
        {
            try { _smapiProcess.CloseMainWindow(); } catch (Exception __ex) { AppLog.Warn("LauncherService", __ex.Message); }
            try { _smapiProcess.Kill(true); } catch (Exception __ex) { AppLog.Warn("LauncherService", __ex.Message); }
            _smapiProcess = null;
        }

        // Main game processes (StardewModdingAPI.exe / Stardew Valley.exe): request a save first
        foreach (var name in new[] { "Stardew Valley", "StardewModdingAPI" })
            foreach (var p in Process.GetProcessesByName(name))
                try { p.CloseMainWindow(); } catch (Exception __ex) { AppLog.Warn("LauncherService", __ex.Message); }

        // Give the main UI process some time to write to disk, then force-reap whatever remains
        Task.Delay(300).ContinueWith(_ =>
        {
            foreach (var name in new[] { "Stardew Valley", "StardewModdingAPI" })
                foreach (var p in Process.GetProcessesByName(name))
                    try { p.Kill(true); } catch (Exception __ex) { AppLog.Warn("LauncherService", __ex.Message); }
        });
    }

    // ------------------------------------------------------------------
    // Pre-flight checks
    // ------------------------------------------------------------------
    public PreFlightResult CheckSmapi(string gamePath)
    {
        if (string.IsNullOrWhiteSpace(gamePath))
            return PreFlightResult.Fail("Game path is not set. Please choose one on the Settings page first.");

        if (!Directory.Exists(gamePath))
            return PreFlightResult.Fail($"Game directory does not exist: {gamePath}");

        var exe = Path.Combine(gamePath, "StardewModdingAPI.exe");
        if (!File.Exists(exe))
            return PreFlightResult.Fail(
                $"SMAPI not found: {exe}\n\nDownload and install it from smapi.io, or switch to \"Official Steam\" launch mode on the home page.");

        return PreFlightResult.Ok();
    }

    public PreFlightResult CheckSteam()
    {
        var steamRunning = Process.GetProcessesByName("steam").Length > 0
                        || Process.GetProcessesByName("steamwebhelper").Length > 0;
        if (!steamRunning)
            return PreFlightResult.Warn(
                "The Steam client doesn't appear to be running; launching via the steam:// protocol (may be slightly slower).");
        return PreFlightResult.Ok();
    }

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
                    // SMAPI's LogManager sets Console.InputEncoding to UTF-16LE (fixed Windows behavior).
                    // When stdin is redirected it decodes pipe bytes as UTF-16 — we must write UTF-16LE WITHOUT a BOM,
                    // otherwise SMAPI reads nothing but garbage and console commands never work (same mechanism as the NUL chars on the output side).
                    StandardInputEncoding = new UnicodeEncoding(bigEndian: false, byteOrderMark: false),
                    StandardOutputEncoding = Encoding.UTF8,
                    StandardErrorEncoding = Encoding.UTF8,
                    CreateNoWindow = true
                },
                EnableRaisingEvents = true
            };

            // The log view's content source is SMAPI-latest.txt (each line carries its own level, which drives the colors);
            // stdout lines are just "[SMAPI] message" without a level, but must still be drained continuously to keep the pipe from filling up and blocking the game.
            _smapiProcess.OutputDataReceived += (_, _) => { };
            _smapiProcess.ErrorDataReceived += (_, e) =>
            {
                if (e.Data is not null) RaiseLog("[ERR] " + CleanSmapiLine(e.Data));
            };
            _smapiProcess.Exited += (_, _) =>
            {
                OnGameExit();
                RaiseLog($"[JuniGrid] Game process exited with code {_smapiProcess?.ExitCode}");
                // Wait 3 seconds to finish reading the tail of the log before stopping
                var gen = _logTailGen;
                _ = Task.Delay(3000).ContinueWith(_ =>
                {
                    if (_logTailGen == gen) StopLogTail();
                });
            };

            _smapiProcess.Start();
            _sessionStart = DateTime.Now;
            // New session, new view: clear the previous session's log so old and new content don't mix
            ClearLog();
            RaiseLog($"[JuniGrid] Started SMAPI process (PID {_smapiProcess.Id})");
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
    /// SMAPI's redirected output is written as UTF-16LE (each ASCII char followed by a 0x00 byte).
    /// Decoding it as UTF-8 leaves NUL (\0) characters in the string — invisible, but in a monospace font with
    /// pre-wrap each one takes a full character width, so the log looks like "a space between every letter".
    /// SMAPI console output is pure ASCII, so stripping NUL/zero-width chars fully restores it.
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
            if (System.Diagnostics.Process.GetProcessesByName("Stardew Valley").Length > 0) break;
            await Task.Delay(1000);
        }
        while (_sessionStart is not null &&
               System.Diagnostics.Process.GetProcessesByName("Stardew Valley").Length > 0)
        {
            await Task.Delay(3000);
        }
        OnGameExit();
    }

    public LaunchResult LaunchSteam(string steamAppId)
    {
        // Pre-check: no path at all → most likely the Steam account doesn't own the game or it isn't installed
        if (string.IsNullOrWhiteSpace(_cfg.Current.GamePath) || !Directory.Exists(_cfg.Current.GamePath))
            return LaunchResult.Fail(
                "Stardew Valley game directory not detected.\n\n" +
                "Possible causes:\n" +
                "  · The current Steam account doesn't own this game (purchase it on Steam first)\n" +
                "  · The game isn't installed or the path is wrong → set the directory manually in Settings\n\n" +
                "The launcher will try the steam:// protocol; if Steam says \"this account doesn't own the game\", that's the cause.");

        // Steam mode also starts the play timer (in official Steam mode we can't see the child process exit, so we poll for Stardew Valley.exe)
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
            return LaunchResult.Fail("Failed to launch via Steam: " + ex.Message);
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
