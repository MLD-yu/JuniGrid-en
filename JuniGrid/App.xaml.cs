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
    // When the user clicks "Mod Manager Download" on the Nexus website, Windows
    // launches JuniGrid.exe via the nxm:// link. If an instance is already running,
    // the second instance passes the link to the main instance over a named pipe
    // and then exits.
    private const string MutexName = "JuniGrid.SingleInstance";
    private const string PipeName = "JuniGrid.NxmPipe";
    private static Mutex? _mutex;

    /// <summary>DI container, set by MainWindow right after BuildServiceProvider.</summary>
    public static IServiceProvider? Services { get; set; }

    /// <summary>An nxm:// link that arrived before the DI container was ready.</summary>
    public static string? PendingNxmLink { get; set; }

    protected override void OnStartup(StartupEventArgs e)
    {
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
            }
            Shutdown();
            return;
        }

        // Catch EVERYTHING — UI thread, background threads, unobserved tasks.
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;

        StartPipeServer();
        PendingNxmLink = nxmArg;

        base.OnStartup(e);
    }

    /// <summary>Placeholder for the Startup event — the real splash → main window choreography lives here.</summary>
    private void OnAppStartup(object sender, StartupEventArgs e)
    {
        // 1) Show the transparent splash window first (logo holds 0.5s → fades in 1.2s)
        var splash = new SplashWindow();
        splash.Show();

        // 2) Build MainWindow in the background (Visibility=Hidden + position preset below the screen)
        MainWindow? main = null;
        bool uiReady = false;
        bool introDone = false;
        bool revealed = false;
        double targetTop = 0;
        double targetLeft = 0;

        void RevealMain()
        {
            if (revealed) return;
            revealed = true;
            LogInfo("RevealMain: showing main window");
            main!.Left = targetLeft;
            main.Top = targetTop + 34;
            main.Activate();

            // Slide-in from below
            var slide = new DoubleAnimation
            {
                From = targetTop + 34, To = targetTop,
                Duration = TimeSpan.FromMilliseconds(460),
                EasingFunction = new System.Windows.Media.Animation.CubicEase
                { EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut }
            };
            main.BeginAnimation(System.Windows.Window.TopProperty, slide);

            // v0.21.0: fade alpha 0 → 255 via the Win32 layered layer (pixels controlled directly by the DWM compositor)
            // Once the fade-in completes, WS_EX_LAYERED is removed automatically, restoring the zero-overhead normal composition path
        }

        void TryReveal()
        {
            LogInfo($"TryReveal: introDone={introDone} uiReady={uiReady} mainNull={main is null}");
            if (!(introDone && uiReady) || main is null) return;

            // Staged transition: first fade out the whole splash (including text). MainWindow waits
            // until the splash is fully closed before appearing via SW_SHOW — avoiding "the UI popping
            // up behind while the animation is still playing".
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

        // Use Loaded → BlazorWebView first UI ready as the uiReady signal:
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
            // Precompute the target position (centered)
            var screenW = SystemParameters.WorkArea.Width;
            var screenH2 = SystemParameters.WorkArea.Height;
            main.Left = (screenW - main.Width) / 2 + SystemParameters.WorkArea.Left;
            targetLeft = main.Left;
            targetTop = (screenH2 - main.Height) / 2 + SystemParameters.WorkArea.Top;
            main.WindowStartupLocation = WindowStartupLocation.Manual;
            // v0.23.0: off-screen mounting — WebView2 is an independent child HWND that writes to the
            // screen via DirectComposition; no parent-window transparency trick (Opacity/layered) can
            // hide its black background. DWM doesn't composite off-screen windows, so start at
            // (-32000,-32000) and slide back in after the animation finishes.
            main.ShowActivated = false;
            main.Left = -32000;
            main.Top = -32000;
            main.Show();

            // Race-condition fallback: regardless of whether the frontend "ui-ready" handshake or
            // Splash.IntroCompleted arrives in time, the main window must slide in within a bounded
            // time — never "splash faded out, blank screen, process still alive". Recheck every 300ms;
            // stop as soon as the main window is visible. The animation takes about 5s in total, so the
            // fallback is relaxed to 6s to ensure the animation truly finishes (IntroCompleted) before
            // switching pages, avoiding a premature switch.
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

    /// <summary>Triggered via MainLayout.OnAfterRenderAsync → JS → C#.</summary>
    public static Action? UiReadyCallback { get; set; }

    public static void NotifyUiReady()
    {
        UiReadyCallback?.Invoke();
        // v0.2.1: a few seconds after the UI is ready, trim the working set from the startup peak —
        // page out only, no GC (no perceived pause). GC only reclaims a few MB; the bulk of the
        // working set is runtime/framework images; the system pages them back when it needs memory.
        _ = Task.Delay(5000).ContinueWith(_ =>
        {
            try { JuniGrid.Services.MemoryService.TrimWorkingSet(); } catch { }
        });
    }

    /// <summary>Flag for invisible mounting during startup: MainWindow.OnSourceInitialized checks it to decide whether alpha=0.</summary>

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
                        await Dispatcher.InvokeAsync(() => DispatchNxm(link));
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

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        // v0.60.0: swallow "no browser renderer with ID" — when WebView2 switches/restores pages,
        // stale JS calls hitting a destroyed renderer throw onto the UI thread here. Previously only
        // the TaskScheduler path was swallowed; the WpfDispatcher path was missed and kept spamming the log.
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
