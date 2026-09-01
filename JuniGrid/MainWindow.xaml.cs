using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using JuniGrid.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.FluentUI.AspNetCore.Components;
using Microsoft.Web.WebView2.Core;

namespace JuniGrid;

public partial class MainWindow : Window
{
    private static readonly string LogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "JuniGrid", "startup.log");

    internal static void Log(string line)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
            File.AppendAllText(LogPath, $"[{DateTime.Now:HH:mm:ss.fff}] {line}\n");
        }
        catch { }
    }

    public MainWindow()
    {
        try
        {
            Log("=== JuniGrid boot ===");
            Log($"BaseDir = {AppContext.BaseDirectory}");
            var wwwrootPath = Path.Combine(AppContext.BaseDirectory, "wwwroot", "index.html");
            Log($"wwwroot/index.html exists? {File.Exists(wwwrootPath)} @ {wwwrootPath}");

            // v0.2.2: config loads first — it determines cache locations (including the WebView2 directory)
            var configService = new ConfigService();

            // v0.2.2: last time the cache directory changed, WebView2 was locked and couldn't move →
            // run the pending migration now, before WebView2 is initialized
            var wv2Use = StoragePaths.WebView2Dir;
            var wv2From = configService.Current.PendingWebView2MoveFrom;
            if (!string.IsNullOrWhiteSpace(wv2From) && Directory.Exists(wv2From))
            {
                if (string.Equals(Path.GetFullPath(wv2From), Path.GetFullPath(wv2Use), StringComparison.OrdinalIgnoreCase))
                {
                    configService.Current.PendingWebView2MoveFrom = null;
                }
                else
                {
                    try
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(wv2Use)!);
                        if (Services.StorageService.TryMoveTree(wv2From, wv2Use))
                        {
                            Log($"WebView2 data migrated to {wv2Use}");
                            configService.Current.PendingWebView2MoveFrom = null;
                        }
                        else
                        {
                            wv2Use = wv2From;   // some files locked → keep using the old directory this session, retry next launch
                            Log("WebView2 data migration incomplete (some files locked), continuing with " + wv2From + " this session");
                        }
                    }
                    catch (Exception ex)
                    {
                        wv2Use = wv2From;
                        Log("WebView2 data migration failed: " + ex.Message + " (continuing with " + wv2From + " this session)");
                    }
                }
            }
            else if (!string.IsNullOrWhiteSpace(wv2From))
            {
                configService.Current.PendingWebView2MoveFrom = null;   // original directory no longer exists
            }
            if (configService.Current.PendingWebView2MoveFrom is null && wv2From is not null)
            {
                configService.Save(configService.Current);
            }

            // v0.2.2: default locations for migratable items unified under %TEMP%\JuniGrid —
            // when no cache directory is set, move existing data from the old default
            // location (LocalAppData) over once (WebView2 must be migrated before init)
            if (StoragePaths.CacheRoot is null)
            {
                var legacyPairs = new (string From, string To)[]
                {
                    (Path.Combine(StoragePaths.LocalAppDataDir, "smapi-installer"), StoragePaths.SmapiInstallerDir),
                    (Path.Combine(StoragePaths.LocalAppDataDir, "mods-backup"), StoragePaths.ModsBackupDir),
                    (Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "JuniGrid_WV2"), StoragePaths.WebView2Dir),
                };
                foreach (var (from, to) in legacyPairs)
                {
                    try
                    {
                        if (!Directory.Exists(from) || Directory.Exists(to)) continue;
                        Directory.CreateDirectory(Path.GetDirectoryName(to)!);
                        if (Services.StorageService.TryMoveTree(from, to))
                            Log($"Legacy default cache migrated to {to}");
                        else
                            Log($"Legacy default cache migration incomplete (some files locked), left in place for later cleanup: {from}");
                    }
                    catch (Exception ex) { Log("Legacy default cache migration failed: " + ex.Message); }
                }
            }

            // Isolate the Blazor WebView2 user-data folder.
            // Note: do NOT pin WEBVIEW2_BROWSER_EXECUTABLE_FOLDER —
            // once the WebView2 runtime auto-updates, the old version directory is deleted,
            // leaving the pinned path invalid and failing init with 0x8007139F
            // (STATUS_INVALID_PARAMETER / state error). Let the system locate the runtime.
            Environment.SetEnvironmentVariable("WEBVIEW2_USER_DATA_FOLDER", wv2Use);

            var services = new ServiceCollection();
            services.AddWpfBlazorWebView();
#if DEBUG
            services.AddBlazorWebViewDeveloperTools();
#endif
            services.AddFluentUIComponents();
            services.AddSingleton(configService);   // v0.2.2: register the earliest-loaded instance directly to avoid a second instantiation
            services.AddSingleton<GameService>();
            services.AddSingleton<ModService>();
            services.AddSingleton<LauncherService>();
            services.AddSingleton<SteamService>();
            services.AddSingleton<UpdateService>();
            services.AddSingleton<NexusService>();
            services.AddSingleton<UpdateQueueService>();
            services.AddSingleton<PageRefreshService>();
            services.AddSingleton<TaskCenterService>();
            services.AddSingleton<InstallService>();
            services.AddSingleton<NexusSsoService>();
            services.AddSingleton<NexusOAuthService>();
            // v0.2.1: cache/storage management + memory management
            services.AddSingleton<StorageService>();
            services.AddSingleton<MemoryService>();
            var provider = services.BuildServiceProvider();
            Resources.Add("services", provider);
            App.Services = provider;
            Log("DI configured");

            // Game is running but wasn't launched by this app (e.g. a JuniGrid restart) → attach to the existing SMAPI log
            provider.GetRequiredService<LauncherService>().AttachIfGameRunning();

            // v0.2.1: memory-management background loop runs for the app's lifetime —
            // timed/threshold-triggered compaction doesn't depend on the settings page having been opened
            _ = provider.GetRequiredService<MemoryService>();

            InitializeComponent();
        // v0.35.0: swallow the unobserved "no browser renderer with ID" exception (stale JS calls from page switches hitting a destroyed renderer)
        // v0.43.0: all unhandled app / unobserved task exceptions are written to juni-grid.log
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            Services.AppLog.Error("AppDomain", e.ExceptionObject?.ToString() ?? "unknown");
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Services.AppLog.Error("Task", e.Exception?.ToString() ?? "unknown");
            if (e.Exception?.ToString().Contains("no browser renderer") == true)
            {
                e.SetObserved();
                Log("swallowed renderer-ID exception");
            }
        };

            Log("InitializeComponent done");

            // v0.19.0: listen for the frontend postMessage('ui-ready') so the App layer can fade out the splash and slide in the main window
            blazorWebView.BlazorWebViewInitialized += (_, args) =>
            {
                try
                {
                    _wv2 = args.WebView;   // v0.2.1: keep the reference so WebView2 can be suspended on minimize to save memory
                    args.WebView.CoreWebView2.WebMessageReceived += (_, e) =>
                    {
                        try
                        {
                            var msg = e.TryGetWebMessageAsString();
                            if (msg == "ui-ready") App.NotifyUiReady();
                        }
                        catch { }
                    };
                }
                catch (Exception ex) { Log("WebMessageReceived hook failed: " + ex.Message); }
            };

            // The main window's entrance is fully handled by SplashWindow; no Opacity fade-in here anymore.
            // Previously Loaded did Opacity=0 + fade-in, but a second Loaded/state change reset it to 0,
            // leaving the main window visible yet fully transparent — presented as "UI never appears,
            // but the process is alive". Removed the fade-in: the main window now shows fully opaque.

            // Fix for the "ghost hit-test window" when minimizing a borderless window.
            // After minimizing to the taskbar, the WPF main window is collapsed, but for a
            // borderless window + WebView2 the rendering host window (a separate
            // Chrome_Widget HWND) may not be withdrawn from the screen along with it,
            // leaving an "invisible but hittable window" on the desktop that swallows mouse clicks
            // (symptom: after minimizing, only the desktop/desktop icons can't be clicked; apps/Start/taskbar are fine).
            // Here, when entering Minimized we force-hide the WebView2 host (no longer lingering on screen)
            // and restore its visibility when restored, eliminating the leftover hit-test region.
            StateChanged += (_, e2) =>
            {
                var isMin = WindowState == System.Windows.WindowState.Minimized;
                Dispatcher.BeginInvoke(() =>
                {
                    var target = isMin ? Visibility.Collapsed : Visibility.Visible;
                    if (blazorWebView.Visibility == target) return;
                    if (isMin)
                    {
                        try { blazorWebView.Visibility = target; }
                        catch (Exception ex) { Log("WebView visibility sync error: " + ex.Message); }
                        // v0.2.1: once the window is collapsed, set WebView2 to a low memory target, suspend it, and trim the host working set
                        _ = EnterLowMemoryModeAsync();
                    }
                    else
                    {
                        // v0.2.1: wake the suspended WebView2 before showing it again
                        ExitLowMemoryMode();
                        // v0.52.0: on restore, delay 60ms before showing the WebView (letting it produce a content frame);
                        // the window's light background covers the gap, turning the black flash into a seamless light transition.
                        // v0.52.0: 160ms was too long (noticeable black screen); 60ms is enough for WebView2 to produce the first frame.
                        Task.Delay(60).ContinueWith(_ => Dispatcher.Invoke(() =>
                        {
                            try { blazorWebView.Visibility = Visibility.Visible; }
                            catch (Exception ex) { Log("WebView visibility sync error: " + ex.Message); }
                        }));
                    }

                });
            };

            // ---- Close fade-out (open fade-in removed so Opacity=0 can't leave the main window invisible) ----
            Closing += (_, e) =>
            {
                if (_closing) return;
                _closing = true;
                e.Cancel = true;
                var fadeOut = new System.Windows.Media.Animation.DoubleAnimation(
                    Opacity, 0, new Duration(TimeSpan.FromMilliseconds(180)))
                {
                    EasingFunction = new System.Windows.Media.Animation.CubicEase
                    { EasingMode = System.Windows.Media.Animation.EasingMode.EaseIn }
                };
                fadeOut.Completed += (_, _) => Close();
                BeginAnimation(OpacityProperty, fadeOut);
            };

            // If this launch was triggered by an nxm:// link, DI is ready now — hand it to the install service
            if (App.PendingNxmLink is { } pending)
            {
                App.PendingNxmLink = null;
                _ = provider.GetRequiredService<InstallService>().HandleNxmLinkAsync(pending);
            }

            if (!File.Exists(wwwrootPath))
            {
                System.Windows.MessageBox.Show(
                    $"Missing critical file!\n\nwwwroot/index.html was not packaged into:\n{wwwrootPath}\n\n" +
                    "This is what causes the white screen. Check that the csproj includes the wwwroot folder correctly.",
                    "JuniGrid Diagnostics", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
        catch (Exception ex)
        {
            Log($"CRASH: {ex}");
            System.Windows.MessageBox.Show(
                $"JuniGrid failed to initialize\n\n{ex.Message}\n\nFull log: {LogPath}",
                "Startup Error", MessageBoxButton.OK, MessageBoxImage.Error);
            throw;
        }
    }

    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    private const int DWMWCP_ROUND = 2;

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(
        IntPtr hwnd, int attr, ref int attrValue, int attrSize);

    // Win32 window state commands (most reliable way to minimize/restore a borderless window).
    // When borderless (WindowStyle=None + CaptionHeight=0), WindowState.Minimized
    // may not actually withdraw the window from the screen on some systems, leaving a
    // transparent interactive window behind that intercepts desktop clicks
    // (the original area can't be clicked after minimizing). Using ShowWindow forces
    // a system-level minimize/restore, which handles the WebView child windows correctly too.
    private const int SW_MINIMIZE = 6;
    private const int SW_RESTORE = 9;
    private const int SW_SHOW = 5;

    [DllImport("user32.dll", PreserveSig = true, SetLastError = true)]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        Log("OnSourceInitialized (WPF window HWND created)");

        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;
        int preference = DWMWCP_ROUND;
        DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref preference, sizeof(int));
        Log("DWM rounded corners applied");

        // A borderless window maximized exceeds the work area (by ~8px, clipped by the system),
        // cutting off bottom WebView content (the last few rows when scrolled) and preventing full scrolling.
        // Hook WM_GETMINMAXINFO to clamp the maximized size/position to the monitor work area (avoiding the taskbar).
        var src = HwndSource.FromHwnd(hwnd);
        src?.AddHook(WndProcClampMaximized);
    }

    // Clamp the maximized bounds to the work area, eliminating bottom clipping of a maximized borderless window.
    private const int WM_GETMINMAXINFO = 0x0024;
    private const int MONITOR_DEFAULTTONEAREST = 2;
    private IntPtr WndProcClampMaximized(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != WM_GETMINMAXINFO) return IntPtr.Zero;
        // Multi-monitor: use the work area of the monitor the window is currently on (avoids the taskbar).
        var monitor = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
        var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (!GetMonitorInfo(monitor, ref mi)) return IntPtr.Zero;
        var wa = mi.rcWork;
        var mm = Marshal.PtrToStructure<MINMAXINFO>(lParam);
        mm.ptMaxPosition = new POINT32(wa.Left, wa.Top);
        mm.ptMaxSize = new POINT32(wa.Right - wa.Left, wa.Bottom - wa.Top);
        mm.ptMaxTrackSize = new POINT32(wa.Right - wa.Left, wa.Bottom - wa.Top);
        Marshal.StructureToPtr(mm, lParam, false);
        handled = true;
        return IntPtr.Zero;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT32 { public int X, Y; public POINT32(int x, int y) { X = x; Y = y; } }
    [StructLayout(LayoutKind.Sequential)]
    private struct RECT32 { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public int cbSize;
        public RECT32 rcMonitor;
        public RECT32 rcWork;
        public uint dwFlags;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct MINMAXINFO
    {
        public POINT32 ptReserved, ptMaxSize, ptMaxPosition, ptMinTrackSize, ptMaxTrackSize;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

    // ==================================================================
    // Built-in Nexus browser (main window overlay)
    // ==================================================================
    private bool _closing;

    // v0.2.1: save memory on minimize — WebView2 is the biggest memory consumer (a multi-process Chromium rendering the whole UI).
    // On minimize, set the memory usage target to Low and suspend (Microsoft's official memory-saving posture for
    // background windows; supported since SDK 1.0.3179). On restore, Resume first, then show after a delay
    // (reusing the existing 60ms anti-black-flash choreography). The host's own working set is trimmed too.
    private Microsoft.Web.WebView2.Wpf.WebView2CompositionControl? _wv2;

    private async Task EnterLowMemoryModeAsync()
    {
        var core = _wv2?.CoreWebView2;
        if (core is not null)
        {
            try { core.MemoryUsageTargetLevel = CoreWebView2MemoryUsageTargetLevel.Low; }
            catch { /* older WebView2 runtimes don't support this property — skip */ }
            try { await core.TrySuspendAsync(); }
            catch (Exception ex) { Log("WebView2 suspend failed: " + ex.Message); }
        }
        try { Services.MemoryService.TrimWorkingSet(); } catch { }
    }

    private void ExitLowMemoryMode()
    {
        var core = _wv2?.CoreWebView2;
        if (core is null) return;
        try { core.Resume(); } catch { }
        try { core.MemoryUsageTargetLevel = CoreWebView2MemoryUsageTargetLevel.Normal; }
        catch { /* same as above */ }
    }

    /// <summary>Called from Blazor pages: opens the Nexus browser overlay inside the main window.</summary>
    public static void OpenNexusOverlay(string url, bool queueMode = false)
    {
        var w = System.Windows.Application.Current?.MainWindow as MainWindow;
        if (w is null) return;
        _ = queueMode; // built-in browser removed: always open in the system browser
        try
        {
            System.Diagnostics.Process.Start(
                new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex) { Log("Failed to open system browser: " + ex.Message); }
    }

    /// <summary>Collapses the overlay when routing away from the Mods page (keeps the WebView2 instance to avoid re-initialization).</summary>
    public static void HideNexusOverlay()
    {
        var w = System.Windows.Application.Current?.MainWindow as MainWindow;
        if (w is null) return;
        w.Dispatcher.Invoke(() =>
        {
            {
                    App.Services?.GetService<UpdateQueueService>()?.Stop();
            }
        });
    }

    private void Toolbar_Drag(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed)
        {
            try { DragMove(); } catch { }
        }
    }

    /// <summary>Forces a minimize via Win32 ShowWindow so a borderless window truly leaves the screen, preventing a leftover transparent interactive window from intercepting clicks.</summary>
    public static void MinimizeWindow()
    {
        var w = System.Windows.Application.Current?.MainWindow as MainWindow;
        if (w is null) return;
        var hwnd = new System.Windows.Interop.WindowInteropHelper(w).Handle;
        if (hwnd != IntPtr.Zero) ShowWindow(hwnd, SW_MINIMIZE);
        else w.WindowState = System.Windows.WindowState.Minimized;
    }

    /// <summary>
    /// Forces the main window visible via Win32. WPF's Visibility=Visible doesn't always
    /// trigger an HWND SW_SHOW for a window that has already been Show()n and Hidden
    /// (leaving the window visible=False and the UI never appearing).
    /// This sends ShowWindow(SW_SHOW) directly to the HWND, bypassing that path.
    /// </summary>
    public static void ShowMainWindow()
    {
        var w = Application.Current?.MainWindow as MainWindow;
        if (w is null) return;
        // First make WPF's state machine consider the window visible — otherwise ShowWindow
        // gets undone by WPF's layout pass treating it as "still Hidden" (the root cause of it
        // staying visible=False before).
        w.Visibility = Visibility.Visible;
        var hwnd = new WindowInteropHelper(w).EnsureHandle();
        Log($"ShowMainWindow: Visibility={w.Visibility} hwnd=0x{hwnd.ToInt64():X}");
        bool r = ShowWindow(hwnd, SW_SHOW);
        Log($"ShowMainWindow: SW_SHOW={r} IsWindowVisible={IsWindowVisible(hwnd)} style=0x{GetWindowLong(hwnd, GWL_STYLE) & (WS_VISIBLE | WS_MINIMIZE):X}");
        w.Activate();
    }
    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);
    private const int GWL_STYLE = -16, WS_VISIBLE = 0x10000000, WS_MINIMIZE = 0x20000000;

    private void QueueSkip_Click(object sender, RoutedEventArgs e) =>
        App.Services?.GetService<UpdateQueueService>()?.Skip();

}
