using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Windows;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using JuniGrid.Services;
using Microsoft.Extensions.DependencyInjection;

namespace JuniGrid;

public partial class App : Application
{
    private static readonly string LogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "JuniGrid", "crash.log");

    // ---- Single instance + nxm:// forwarding ----
    // When the user clicks "Mod Manager Download" on the Nexus website, Windows launches
    // JuniGrid.exe with an nxm:// link. If an instance is already running, the second
    // instance hands the link to the main instance over a named pipe and then exits.
    private const string MutexName = "JuniGrid.SingleInstance";
    private const string PipeName = "JuniGrid.NxmPipe";
    /// <summary>Activation command sent to the main instance over the pipe on a second launch.</summary>
    private const string ActivateCommand = "jg:activate";
    private static Mutex? _mutex;

    /// <summary>DI container, set by MainWindow right after BuildServiceProvider.</summary>
    public static IServiceProvider? Services { get; set; }

    /// <summary>An nxm:// link that arrived before the DI container was ready.</summary>
    public static string? PendingNxmLink { get; set; }

    private static bool _uninstallMode;

    protected override void OnStartup(StartupEventArgs e)
    {
        // Uninstall mode (JuniGrid.exe --uninstall or the standalone Uninstall.exe in the
        // install directory): skip single-instance/pipe/splash and show only the uninstall wizard.
        // Must branch before the mutex — the uninstaller must be launchable from Control Panel
        // even while the main instance is running.
        var exeName = Path.GetFileName(Environment.ProcessPath) ?? "";
        _uninstallMode = e.Args.Any(a => a.Equals("--uninstall", StringComparison.OrdinalIgnoreCase))
                         || exeName.Equals("Uninstall.exe", StringComparison.OrdinalIgnoreCase);
        if (_uninstallMode)
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            base.OnStartup(e);
            return;
        }

        var nxmArg = e.Args.FirstOrDefault(
            a => a.StartsWith("nxm://", StringComparison.OrdinalIgnoreCase));

        _mutex = new Mutex(true, MutexName, out var createdNew);
        if (!createdNew)
        {
            if (nxmArg is not null)
            {
                try
                {
                    using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
                    client.Connect(2000);
                    using var w = new StreamWriter(client) { AutoFlush = true };
                    w.WriteLine(nxmArg);
                }
                catch { /* main instance unreachable — just exit */ }
                Shutdown();
                return;
            }

            // A second launch without an nxm link = the user clicked the shortcut again. The
            // standard approach is to activate/foreground the existing instance's window (taskbar
            // highlight, immediately usable) and let this instance exit. Only if the main instance
            // cannot be reached (e.g. after an upgrade the old instance still holds the lock, or
            // the pipe is unresponsive) do we fall back to the old "kill the other instances and
            // take over" path, avoiding "clicked with no response, and the old version opens".
            if (TryActivateExistingInstance())
            {
                Shutdown();
                return;
            }
            if (!TryTakeOverSingleInstance())
            {
                Shutdown();
                return;
            }
        }

        // Catch EVERYTHING — UI thread, background threads, unobserved tasks.
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;

        StartPipeServer();
        PendingNxmLink = nxmArg;

        base.OnStartup(e);
    }

    /// <summary>When the single-instance lock is held (no nxm forwarding scenario): kill the other
    /// JuniGrid instances and wait for the lock to be released. Note we only match the process
    /// name "JuniGrid" — the installer is JuniGridSetup and the uninstall wizard is
    /// Uninstall.exe, both different process names, so they are never hit. Returns false =
    /// the lock still could not be acquired within 5 seconds (e.g. an old instance running
    /// elevated that cannot be killed); the caller gives up starting.</summary>
    private static bool TryTakeOverSingleInstance()
    {
        try
        {
            var self = Environment.ProcessId;
            foreach (var p in Process.GetProcessesByName("JuniGrid"))
            {
                if (p.Id == self) { p.Dispose(); continue; }
                try { p.Kill(entireProcessTree: true); } catch { }
                p.Dispose();
            }
        }
        catch { }

        // Once the holder is killed the mutex is abandoned; when WaitOne throws AbandonedMutexException we already own it
        for (var i = 0; i < 20; i++)
        {
            try
            {
                if (_mutex!.WaitOne(TimeSpan.FromMilliseconds(250))) return true;
            }
            catch (AbandonedMutexException)
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>Startup event placeholder — the real splash -> main orchestration lives here.</summary>
    private void OnAppStartup(object sender, StartupEventArgs e)
    {
        // v1.1.2: WebView2 runtime pre-check — preinstalled on normal Win10/11, but Windows
        // Sandbox and LTSC/stripped-down systems may not have it. When missing, the raw
        // exception is a full screen of English stack trace (the UI never appears), so show
        // a friendly dialog telling the user to install it and start again.
        try
        {
            _ = Microsoft.Web.WebView2.Core.CoreWebView2Environment.GetAvailableBrowserVersionString();
        }
        catch (Exception ex)
        {
            LogInfo("WebView2 runtime missing: " + ex.Message);
            System.Windows.MessageBox.Show(
                "Microsoft WebView2 Runtime is missing from this system, and JuniGrid's UI depends on it.\n\n" +
                "Please download and install it once (then restart this app):\n" +
                "https://go.microsoft.com/fwlink/p/?LinkId=2124703\n\n" +
                "Note: Windows Sandbox is a disposable system; the runtime must be reinstalled in every new sandbox.\n" +
                $"Technical details: {ex.Message}",
                "JuniGrid cannot start: WebView2 Runtime is missing",
                System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
            Shutdown();
            return;
        }

        if (_uninstallMode)
        {
            new UninstallWindow().Show();
            return;
        }

        // 1) Show the transparent splash window first (logo holds 0.5s, then fades in over 1.2s)
        var splash = new SplashWindow();
        splash.Show();

        // 2) Construct MainWindow (mounted off-screen; reveal position is centered on its monitor by MainWindow)
        MainWindow? main = null;
        bool uiReady = false;
        bool introDone = false;
        bool revealed = false;

        void RevealMain()
        {
            if (revealed) return;
            revealed = true;
            LogInfo("RevealMain: showing main window");
            // v1.1.5: start windowed by default — the window is revealed windowed and centered,
            // no longer forced maximized. The old logic (v1.1.2/v1.1.7) maximized on startup
            // plus re-checked three times, which would completely overwrite the responsive
            // window size (77.1%×72.7%) computed in OnSourceInitialized from the monitor ratio;
            // maximizing is left to the user's title bar button.
            // v1.1.8: centering is delegated to MainWindow.RevealAtStartupPosition — it reveals
            // using coordinates derived from the same monitor and the same DPI math as the
            // OnSourceInitialized size calculation, so size and position always match the same
            // screen (SystemParameters.WorkArea only describes the primary monitor, and which
            // screen the off-screen-mounted window lands on is not deterministic; with two
            // different screens that produced "size computed for screen A, centered on screen B"
            // misplacement).
            main!.RevealAtStartupPosition();
            main.Activate();
        }

        void TryReveal()
        {
            LogInfo($"TryReveal: introDone={introDone} uiReady={uiReady} mainNull={main is null}");
            if (!(introDone && uiReady) || main is null) return;

            // Staged transition: first fade out the entire Splash (text included). MainWindow
            // must wait until the Splash is fully closed before SW_SHOW reveals it — avoiding
            // "the UI popping up from behind before the animation has finished".
            if (!revealed && splash.Visibility == System.Windows.Visibility.Visible)
            {
                splash.Closed += (_, _) => RevealMain();
                splash.FadeOutAndClose();
            }
            else
            {
                RevealMain();
            }
        }

        splash.IntroCompleted += () =>
        {
            LogInfo("Splash.IntroCompleted fired");
            introDone = true;
            Dispatcher.Invoke(TryReveal);
        };

        // Use Loaded -> BlazorWebView's first UI-ready moment as the uiReady signal:
        // MainLayout.OnAfterRenderAsync calls App.NotifyUiReady() via JS interop.
        UiReadyCallback = () =>
        {
            LogInfo("UiReadyCallback fired");
            uiReady = true;
            Dispatcher.Invoke(TryReveal);
        };

        // Create the main window when the Dispatcher is idle — lets the splash render first
        Dispatcher.BeginInvoke(new Action(() =>
        {
            main = new MainWindow();
            MainWindow = main;
            // v1.1.8: no longer pre-computes the centered position — the window still has the
            // XAML fallback size (1600×1000) at this point; the responsive size is only known
            // once Show triggers OnSourceInitialized, so any position computed now is stale.
            // Reveal coordinates are computed per-monitor in OnSourceInitialized and applied
            // by RevealAtStartupPosition.
            main.WindowStartupLocation = WindowStartupLocation.Manual;
            // v0.23.0: off-screen mounting — WebView2 is an independent child HWND whose
            // DirectComposition output goes straight to the screen; no parent-window
            // transparency trick (Opacity/layered) can hide its black background.
            // DWM does not composite off-screen windows, so start at (-32000,-32000) and move
            // it back to slide in once the animation finishes.
            main.ShowActivated = false;
            main.Left = -32000;
            main.Top = -32000;
            main.Show();

            // Race fallback: whether or not the frontend "ui-ready" handshake or
            // Splash.IntroCompleted arrives in time, the main window must slide in within a
            // bounded time — never "faded out to an empty screen with the process still alive".
            // Re-check every 300ms; stop as soon as the main window is visible. The animation
            // runs about 5s in total and the fallback is relaxed to 6s, so the page switch only
            // happens after the animation truly completes (IntroCompleted), avoiding a premature switch.
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
            long elapsedMs = 0;
            timer.Tick += (_, _) =>
            {
                elapsedMs += 300;
                if (revealed) { timer.Stop(); return; }
                if (elapsedMs >= 6000) { introDone = true; uiReady = true; }
                TryReveal();
            };
            timer.Start();
        }), System.Windows.Threading.DispatcherPriority.Background);
    }

    /// <summary>Triggered by MainLayout.OnAfterRenderAsync → JS → C#.</summary>
    public static Action? UiReadyCallback { get; set; }

    public static void NotifyUiReady()
    {
        UiReadyCallback?.Invoke();
        // v0.2.1: a few seconds after the UI is ready, trim the startup-peak working set —
        // page out only, no GC (no perceptible pause). GC only reclaims a few MB; most of the
        // working set is runtime/framework images. The system pages them back when it needs memory.
        _ = Task.Delay(5000).ContinueWith(_ =>
        {
            try { JuniGrid.Services.MemoryService.TrimWorkingSet(); } catch { }
        });
    }

    /// <summary>Flag for the invisible mount during startup: MainWindow.OnSourceInitialized checks it to decide whether alpha=0.</summary>

    private static void LogInfo(string line)
    {
        try { File.AppendAllText(LogPath, $"[{DateTime.Now:HH:mm:ss.fff}] {line}\n"); } catch { }
    }

    private void StartPipeServer()
    {
        Task.Run(async () =>
        {
            while (true)
            {
                try
                {
                    using var server = new NamedPipeServerStream(PipeName, PipeDirection.In);
                    await server.WaitForConnectionAsync();
                    using var r = new StreamReader(server);
                    var link = await r.ReadLineAsync();
                    if (!string.IsNullOrWhiteSpace(link))
                    {
                        if (link == ActivateCommand)
                            await Dispatcher.InvokeAsync(ActivateExistingWindow);
                        else
                            await Dispatcher.InvokeAsync(() => DispatchNxm(link));
                    }
                }
                catch
                {
                    await Task.Delay(500);
                }
            }
        });
    }

    internal static void DispatchNxm(string link)
    {
        var installer = Services?.GetService<InstallService>();
        if (installer is not null)
            _ = installer.HandleNxmLinkAsync(link);
        else
            PendingNxmLink = link;
    }

    /// <summary>Asks the main instance to activate its window on a second launch. Returns false
    /// if the pipe cannot be connected (main instance missing/hung); the caller then falls back
    /// to the take-over path.</summary>
    private static bool TryActivateExistingInstance()
    {
        try
        {
            using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
            client.Connect(1500);
            using var w = new StreamWriter(client) { AutoFlush = true };
            w.WriteLine(ActivateCommand);
            return true;
        }
        catch { return false; }
    }

    /// <summary>Restores/foregrounds the main window and hands it to the user (= the "selected
    /// state" the user described: taskbar highlighted, window has foreground focus). A background
    /// process has no right to steal focus directly; it must go through the Win32 allow-foreground chain.</summary>
    private static void ActivateExistingWindow()
    {
        try
        {
            var w = Current.MainWindow;
            if (w is null) return;
            if (w.WindowState == WindowState.Minimized)
                w.WindowState = WindowState.Normal;
            w.Show();
            w.Activate();
            var hwnd = new System.Windows.Interop.WindowInteropHelper(w).Handle;
            if (hwnd != IntPtr.Zero)
            {
                SetForegroundWindow(hwnd);
            }
        }
        catch (Exception ex) { LogInfo("ActivateExistingWindow: " + ex.Message); }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        // v0.60.0: swallow "no browser renderer with ID" — when WebView2 switches pages or
        // restores from minimize, leftover JS calls hitting a destroyed renderer get thrown onto
        // the UI thread from here; previously only the TaskScheduler path was swallowed and the
        // WpfDispatcher path kept blowing up the log repeatedly.
        if (e.Exception?.ToString().Contains("no browser renderer") == true)
        {
            e.Handled = true;
            return;
        }
        Log("UI", e.Exception);
        MessageBox.Show($"JuniGrid failed to start\n\n{e.Exception?.Message}\n\nFull log written to:\n{LogPath}",
                        "Startup Error", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
        Shutdown(1);
    }

    private void OnUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception ex) Log("FATAL", ex);
    }

    private void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        Log("TASK", e.Exception);
        e.SetObserved();
    }

    private static void Log(string tag, Exception? ex)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
            File.AppendAllText(LogPath,
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [{tag}] {ex}\n\n");
        }
        catch { /* ignore logging failure */ }
    }
}
