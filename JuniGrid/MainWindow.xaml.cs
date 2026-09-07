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

            // v0.2.2: config loads earliest — the cache locations (including the WebView2 folder) depend on it
            var configService = new ConfigService();

            // v0.2.2: last time the cache directory changed, WebView2 was busy and could not be moved -> while WebView2 is not yet initialized, run the pending legacy migration first
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
                            wv2Use = wv2From;   // some files are in use -> keep the old directory for this session and retry on next start
                            Log("WebView2 data migration incomplete (some files are in use); continuing with " + wv2From + " for this session");
                        }
                    }
                    catch (Exception ex)
                    {
                        wv2Use = wv2From;
                        Log("WebView2 data migration failed: " + ex.Message + " (continuing with " + wv2From + " for this session)");
                    }
                }
            }
            else if (!string.IsNullOrWhiteSpace(wv2From))
            {
                configService.Current.PendingWebView2MoveFrom = null;   // the original directory no longer exists
            }
            if (configService.Current.PendingWebView2MoveFrom is null && wv2From is not null)
            {
                configService.Save(configService.Current);
            }

            // v0.2.2: movable items' default locations all moved to %TEMP%\JuniGrid — when no
            // cache directory is set, move existing data from the old default location
            // (LocalAppData) over in one go (WebView2 must finish moving before initialization)
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
                            Log($"Legacy default cache migration incomplete (some files are in use); left in place for later cleanup: {from}");
                    }
                    catch (Exception ex) { Log("Legacy default cache migration failed: " + ex.Message); }
                }
            }

            // Isolate the Blazor WebView2 user-data folder.
            // Note: do not pin WEBVIEW2_BROWSER_EXECUTABLE_FOLDER again —
            // after the WebView2 runtime auto-updates, the old version folder gets deleted, so a
            // pinned path becomes an invalid directory and initialization fails outright with
            // 0x8007139F (status error). Just let the system locate the runtime automatically.
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
            services.AddSingleton<NexusOAuthService>();
            // v0.2.1: cache and storage management + memory management
            services.AddSingleton<StorageService>();
            services.AddSingleton<MemoryService>();
            // v1.0.2: app self-update check
            services.AddSingleton<SelfUpdateService>();
            // v1.08: local cache for Nexus covers/images (direct CDN connections from China are extremely slow)
            services.AddSingleton<CoverCacheService>();
            // v1.1.5: daily playtime statistics (data source for the GitHub-style heatmap on the home page)
            services.AddSingleton<PlayTimeService>();
            var provider = services.BuildServiceProvider();
            Resources.Add("services", provider);
            App.Services = provider;
            Log("DI configured");

            // v1.1.3: restore the persisted OAuth2 session (if any) before any Nexus data loads —
            // expired tokens are refreshed in the background so saved logins survive restarts
            provider.GetRequiredService<NexusOAuthService>().RestoreSession();

            // The game is running but was not started by this app (e.g. a JuniGrid restart) -> attach to the existing SMAPI log
            provider.GetRequiredService<LauncherService>().AttachIfGameRunning();

            // v0.2.1: the memory management background loop runs from startup — scheduled/threshold auto-trim does not depend on the settings page having been opened
            _ = provider.GetRequiredService<MemoryService>();

            // v1.1.5: the playtime statistics loop runs from startup — it accumulates whether or not the home page is open
            _ = provider.GetRequiredService<PlayTimeService>();

            // v1.0.2: check once in the background at startup for a new app version (does not block the UI; fails silently)
            provider.GetRequiredService<SelfUpdateService>().StartBackgroundCheck();

            InitializeComponent();
            // v1.1.2b: the drag minimum size is enforced entirely by the WM_GETMINMAXINFO hook
            // (computed from the current monitor's work area ratio since v1.1.8, see
            // WndProcClampMaximized).
            // Must come after InitializeComponent — the XAML MinWidth/MinHeight (1100/650 DIP)
            // converts to larger physical values at high DPI, resizing the window again and
            // overriding the settings zeroed here.
            // Zeroing the WPF properties yields to the hook.
            MinWidth = 0;
            MinHeight = 0;
        // v0.35.0: swallow the "no browser renderer with ID" unobserved exception (leftover JS calls from page switches hitting a destroyed renderer)
        // v0.43.0: project-wide unhandled exceptions / unobserved task exceptions are all written to juni-grid.log
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

            // v0.19.0: listen for the frontend postMessage('ui-ready') so the App layer can fade out the Splash and slide in the main window
            blazorWebView.BlazorWebViewInitialized += (_, args) =>
            {
                try
                {
                    _wv2 = args.WebView;   // v0.2.1: keep the reference to suspend WebView2 on minimize and save memory
                    // The fallback color for unrendered frames defaults to white: at the moment of
                    // restore from minimize / visibility switches it would flash white before content
                    // appears (a "white -> content" jump in the light theme). After setting it to the
                    // shell theme color, every "no content yet" frame already looks like the light UI,
                    // with no color jump during the whole restore.
                    args.WebView.DefaultBackgroundColor =
                        System.Drawing.Color.FromArgb(0xFF, 0xF3, 0xF6, 0xFB);
                    args.WebView.CoreWebView2.WebMessageReceived += (_, e) =>
                    {
                        try
                        {
                            var msg = e.TryGetWebMessageAsString();
                            if (msg == "ui-ready") App.NotifyUiReady();
                        }
                        catch { }
                    };
                    // v1.0.9: WebView2 child-process crash self-healing — when the render process
                    // dies, Reload restarts it; when the browser process dies, log it (only a full
                    // window rebuild can recover then; at minimum do not die silently)
                    args.WebView.CoreWebView2.ProcessFailed += (_, pf) =>
                    {
                        try
                        {
                            Log($"WebView2 process failure: kind={pf.ProcessFailedKind}, exitCode={pf.ExitCode}, reason={pf.FailureSourceModulePath}");
                            if (pf.ProcessFailedKind == CoreWebView2ProcessFailedKind.RenderProcessUnresponsive
                                || pf.ProcessFailedKind == CoreWebView2ProcessFailedKind.RenderProcessExited)
                            {
                                Dispatcher.BeginInvoke(() =>
                                {
                                    try { args.WebView.CoreWebView2.Reload(); Log("WebView2 render process recovered via Reload"); }
                                    catch (Exception rex) { Log("Reload recovery failed: " + rex.Message); }
                                });
                            }
                        }
                        catch (Exception pex) { Log("Exception in the ProcessFailed handler: " + pex.Message); }
                    };
                }
                catch (Exception ex) { Log("WebMessageReceived hook failed: " + ex.Message); }
            };

            // The main window's entrance is handled entirely by SplashWindow; no Opacity fade
            // in/out here anymore.
            // The earlier Opacity=0+fade-in in Loaded got reset to 0 again by some second
            // Loaded/switch, leaving the main window Visible but fully transparent — appearing
            // as "the main UI never shows up while the process is alive".
            // That fade-in was removed: the main window shows fully opaque by default.

            // Fix for the "leftover ghost" of minimized borderless windows
            // After minimizing to the taskbar, although the WPF main window has retracted, the
            // borderless window + WebView2 render host window (an independent Chrome_Widget HWND)
            // does not necessarily get withdrawn from the screen along with it, leaving an
            // "invisible hit-testable window" at the desktop level that eats mouse clicks
            // (symptom: after minimizing, only the desktop/desktop icons are unclickable while
            // apps/Start/taskbar work fine).
            // Here, when entering Minimized, the WebView2 host is forcibly hidden (no longer
            // lingering on screen) and made visible again on restore, eliminating that leftover
            // hit-test region.
            //
            // Restore flicker fix (v1.0.9): previously the WebView was delayed 60ms before showing
            // on restore, exposing the window background meanwhile; WebView2 was also suspended
            // with TrySuspendAsync, and after Resume the renderer needed hundreds of milliseconds
            // to produce a new frame while unrendered frames show in the default white —
            // a black flash, then a white flash, then content: "flicker, flicker". Now:
            // (1) the window background and WebView2 DefaultBackgroundColor both equal the light
            //     theme color;
            // (2) the WebView shows immediately on restore (no more waiting);
            // (3) only MemoryUsageTargetLevel Low/Normal is used to save memory (the official
            //     docs explicitly forbid mixing it with TrySuspendAsync/Resume), without
            //     interrupting frame rendering —
            //     the restore instantly re-shows the last frame from before minimizing, with no
            //     color jump throughout.
            StateChanged += (_, e2) =>
            {
                var isMin = WindowState == System.Windows.WindowState.Minimized;
                Dispatcher.BeginInvoke(() =>
                {
                    var target = isMin ? Visibility.Collapsed : Visibility.Visible;
                    if (blazorWebView.Visibility == target) return;
                    try { blazorWebView.Visibility = target; }
                    catch (Exception ex) { Log("WebView visibility sync exception: " + ex.Message); }
                    if (isMin)
                        _ = EnterLowMemoryModeAsync();
                    else
                        ExitLowMemoryMode();
                });
            };

            // ---- Close fade-out (the open fade-in was removed; Opacity=0 left the main window invisibly transparent) ----
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

            // If this launch itself was triggered by an nxm:// link, DI is ready now — hand it to the install service
            if (App.PendingNxmLink is { } pending)
            {
                App.PendingNxmLink = null;
                _ = provider.GetRequiredService<InstallService>().HandleNxmLinkAsync(pending);
            }

            if (!File.Exists(wwwrootPath))
            {
                System.Windows.MessageBox.Show(
                    $"Required file is missing!\n\nwwwroot/index.html was not packaged into:\n{wwwrootPath}\n\n" +
                    "This is what causes the white screen. Please check that the csproj correctly includes the wwwroot folder.",
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

    // Win32 window state commands (most reliable for minimizing/restoring borderless windows).
    // When borderless (WindowStyle=None + CaptionHeight=0), WindowState.Minimized
    // does not truly withdraw the window from the screen on some systems, leaving a
    // transparent interactive window behind that intercepts mouse clicks on the desktop
    // below (the original area is unclickable after minimizing). ShowWindow forces a
    // system-level minimize/restore, which correctly handles the WebView child windows too.
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

        // v1.1.5: default startup size = 77.1% × 72.7% of the current monitor (responsive).
        // On a 2560×1600 screen that is 1974×1163 physical pixels; DIP values are converted
        // by DPI scaling, automatically adapting to other users' wildly varying monitors and
        // scale factors. The 1600×1000 in XAML is only a fallback.
        // v1.1.8: once the size is computed, derive the centering coordinates on the same
        // monitor [and store them] — the window is still mounted off-screen (-32000) at this
        // point and must not be moved back yet, otherwise the main window would appear before
        // the Splash finishes; RevealAtStartupPosition applies it at reveal time. The old flow
        // pre-computed the position in App.RevealMain from the creation-time XAML size
        // (1600×1000); when the size later changed, the position was not recomputed -> opened
        // off-center.
        try
        {
            var monitor = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
            var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
            if (GetMonitorInfo(monitor, ref mi))
            {
                var dpiT = HwndSource.FromHwnd(hwnd).CompositionTarget.TransformToDevice;
                var monW = (double)(mi.rcMonitor.Right - mi.rcMonitor.Left);
                var monH = (double)(mi.rcMonitor.Bottom - mi.rcMonitor.Top);
                Width = Math.Max(MinWidth, monW * 0.7711 / dpiT.M11);
                Height = Math.Max(MinHeight, monH * 0.7269 / dpiT.M22);
                // Work-area physical pixels -> DIP: when WPF applies Left/Top it converts back to
                // physical pixels using the window's current DPI; here we take the inverse
                // transform, giving exact centering on single screens and equally-scaled multi-screen setups
                _startupLeft = mi.rcWork.Left / dpiT.M11
                               + ((mi.rcWork.Right - mi.rcWork.Left) / dpiT.M11 - Width) / 2;
                _startupTop = mi.rcWork.Top / dpiT.M22
                              + ((mi.rcWork.Bottom - mi.rcWork.Top) / dpiT.M22 - Height) / 2;
                _startupCentered = true;
                Log($"startup size = {Width:0}x{Height:0} DIP (monitor {monW:0}x{monH:0} px @ {dpiT.M11:0.00}), centered target ({_startupLeft:0},{_startupTop:0})");
            }
        }
        catch { }

        // A maximized borderless window overshoots the work area (about 8px, clipped by the
        // system), cutting off WebView bottom content (the last rows when scrolled all the way
        // down) and preventing full scrolling.
        // Intercept WM_GETMINMAXINFO to clamp the maximum size/position to the system work area
        // (avoiding the taskbar).
        var src = HwndSource.FromHwnd(hwnd);
        src?.AddHook(WndProcClampMaximized);
        // v1.1.8: dragging across screens while maximized -> DPI change; the maximized geometry needs to be recomputed for the new screen
        DpiChanged += (_, _) =>
        {
            if (WindowState == System.Windows.WindowState.Maximized)
                VerifyMaximizedPlacement();
        };
        // v1.1.2b: window size checkpoints — log the window state when reaching 1974×1383PX / 1536×864PX
        SizeChanged += OnWindowSizeChanged;
    }

    // ─── v1.1.8: startup reveal position ───
    // Centering coordinates (DIP) computed in OnSourceInitialized for the monitor the window is
    // on, while the window is still mounted off-screen.
    // The old flow pre-computed the position in App.RevealMain using the creation-time XAML size
    // (1600×1000), but the window was subsequently changed to 77.1%×72.7% of the monitor; the
    // size changed without the position being recomputed -> opened off-center overall.
    private double _startupLeft, _startupTop;
    private bool _startupCentered;

    /// <summary>Called by the App startup flow when revealing the main window: moves it to the center of the current monitor's work area.</summary>
    public void RevealAtStartupPosition()
    {
        if (_startupCentered)
        {
            Left = _startupLeft;
            Top = _startupTop;
            return;
        }
        // Fallback: per-screen centering was not computed (GetMonitorInfo failure and other error paths) — use the primary screen work area + the current actual size
        var wa = SystemParameters.WorkArea;
        Left = wa.Left + (wa.Width - ActualWidth) / 2;
        Top = wa.Top + (wa.Height - ActualHeight) / 2;
    }

    // Lock the maximized bounds to the work area, eliminating the bottom overshoot clipping of maximized borderless windows.
    private const int WM_GETMINMAXINFO = 0x0024;
    private const int MONITOR_DEFAULTTONEAREST = 2;
    private IntPtr WndProcClampMaximized(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != WM_GETMINMAXINFO) return IntPtr.Zero;
        // Multi-monitor: use the work area of the screen the window is currently on (avoiding the taskbar).
        var monitor = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
        var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (!GetMonitorInfo(monitor, ref mi)) return IntPtr.Zero;
        var wa = mi.rcWork;
        var mm = Marshal.PtrToStructure<MINMAXINFO>(lParam);
        // v1.1.8b: ptMaxPosition means [an offset relative to the monitor origin], not absolute
        // coordinates! (DefWindowProc adds it on top of the monitor origin.) Previously the
        // absolute values wa.Left/Top were written: on a primary screen with origin (0,0),
        // absolute==relative happened to be correct; maximizing on any secondary screen landed
        // at "origin×2" (tablet @(2560,0) measured (5120,0), exactly one screen width off-screen
        // -> "disappears after maximizing"). The subsequent SetWindowPos fallback correction
        // then made Windows treat the maximize as a manual resize and WPF synced back to
        // Normal -> triggered the restore logic, "clicked maximize and it shrank back again".
        // After switching to a relative offset it lands correctly in one shot.
        mm.ptMaxPosition = new POINT32(wa.Left - mi.rcMonitor.Left, wa.Top - mi.rcMonitor.Top);
        mm.ptMaxSize = new POINT32(wa.Right - wa.Left, wa.Bottom - wa.Top);
        mm.ptMaxTrackSize = new POINT32(wa.Right - wa.Left, wa.Bottom - wa.Top);
        // v1.1.5: when the taskbar is [auto-hide], rcWork == the whole monitor, so the maximized
        // window exactly covers the screen and Windows suppresses the auto-hide taskbar's edge
        // reveal (hovering the bottom edge does nothing). Here the maximized height is reduced
        // by 1 physical pixel — the window no longer "exactly covers the full screen", the edge
        // reveal works again immediately, and that 1px is visually invisible. With a visible
        // taskbar, rcWork already excludes the taskbar, so no impact.
        if (AutoHideBottomBarHeight(mi.rcMonitor) is int barH && barH > 0)
        {
            var maxH = Math.Max(0, wa.Bottom - wa.Top - 1);
            mm.ptMaxSize = new POINT32(wa.Right - wa.Left, maxH);
            mm.ptMaxTrackSize = new POINT32(wa.Right - wa.Left, maxH);
        }
        // v1.1.2: enforce the drag minimum size — every coordinate in the hook is already in
        // physical pixels; do not multiply by DPI again.
        // Even earlier versions did not set this at all, so the window could be dragged down to
        // a few hundred pixels wide.
        // v1.1.8b: the minimum size follows the monitor — 60% × 54% of the current monitor's
        // resolution. Calibration baseline (design value confirmed by the user): on a 2560×1600
        // screen = 1536×864PX.
        // The minimum scales up proportionally on large screens and down on small ones; every
        // WM_GETMINMAXINFO recomputes it for the screen the window is currently on, so
        // cross-screen dragging follows automatically with no DPI conversion needed.
        try
        {
            var (minW, minH) = MonitorMinTrackSize(mi.rcMonitor);
            mm.ptMinTrackSize = new POINT32(minW, minH);
        }
        catch { }
        Marshal.StructureToPtr(mm, lParam, false);
        handled = true;
        return IntPtr.Zero;
    }

    // v1.1.8b: this screen's drag minimum size (physical pixels) — 60% × 54% of the current
    // monitor's resolution. Calibration baseline (user-confirmed): 2560×1600 screen = 1536×864PX.
    // Note the base is [the whole monitor], not the work area; otherwise on screens with a
    // taskbar the height would come out noticeably smaller than on screens without one.
    // Shared by WndProcClampMaximized and the size checkpoint logs, keeping both always consistent.
    private static (int W, int H) MonitorMinTrackSize(RECT32 monitorRect) =>
        ((int)((monitorRect.Right - monitorRect.Left) * 60 / 100),
         (int)((monitorRect.Bottom - monitorRect.Top) * 54 / 100));



    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

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

    // ─── v1.1.5: auto-hide taskbar detection (for maximized WebView2 bottom avoidance) ───
    private const int ABM_GETAUTOHIDEBAR = 0x0007;
    private const int ABE_BOTTOM = 3;

    [StructLayout(LayoutKind.Sequential)]
    private struct APPBARDATA
    {
        public int cbSize;
        public IntPtr hWnd;
        public uint uCallbackMessage;
        public uint uEdge;
        public RECT32 rc;
        public IntPtr lParam;
    }

    [DllImport("shell32.dll")]
    private static extern IntPtr SHAppBarMessage(uint dwMessage, ref APPBARDATA pData);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT32 lpRect);

    /// <summary>Queries whether the bottom of the given monitor has an [auto-hide] taskbar and
    /// returns its thickness (pixels); returns 0 if none; returns null on query errors
    /// (the caller treats it as no auto-hide).</summary>
    private static int? AutoHideBottomBarHeight(RECT32 monitorRect)
    {
        try
        {
            var abd = new APPBARDATA
            {
                cbSize = Marshal.SizeOf<APPBARDATA>(),
                uEdge = (uint)ABE_BOTTOM,
                rc = monitorRect,
            };
            var hBar = SHAppBarMessage(ABM_GETAUTOHIDEBAR, ref abd);
            if (hBar == IntPtr.Zero) return 0;   // no auto-hide taskbar at the bottom of this screen
            if (GetWindowRect(hBar, out var r))
                return Math.Max(0, r.Bottom - r.Top);
            return null;
        }
        catch { return null; }
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

    // v0.2.1: save memory on minimize — WebView2 is the biggest resident memory consumer
    // (the multi-process Chromium rendering the entire UI).
    // v1.0.9: only switch MemoryUsageTargetLevel Low/Normal (the official memory-saving posture
    // for background windows; the docs explicitly require choosing between it and
    // TrySuspendAsync/Resume, never mixing them). WebView2 is no longer suspended:
    // suspending stops frame rendering and the renderer takes hundreds of milliseconds to wake
    // on restore — one of the main causes of the "flicker when restoring from minimize";
    // the Low level similarly pages a large share of the browser process memory out to disk
    // without interrupting rendering, so the last frame shows immediately on restore.
    // The host's own working set is still trimmed along with it.
    private Microsoft.Web.WebView2.Wpf.WebView2CompositionControl? _wv2;

    private async Task EnterLowMemoryModeAsync()
    {
        // Note: the CoreWebView2 getter throws directly when WebView2 is not yet initialized or
        // the browser process has crashed, so the whole thing must be wrapped in try/catch;
        // otherwise a minimize/restore instant would blow up the UI thread
        try
        {
            var core = _wv2?.CoreWebView2;
            if (core is not null)
            {
                try { core.MemoryUsageTargetLevel = CoreWebView2MemoryUsageTargetLevel.Low; }
                catch { /* older WebView2 runtimes do not support this property; skip */ }
            }
        }
        catch (Exception ex) { Log("EnterLowMemoryMode skipped: " + ex.Message); }
        try { Services.MemoryService.TrimWorkingSet(); } catch { }
    }

    private void ExitLowMemoryMode()
    {
        try
        {
            var core = _wv2?.CoreWebView2;
            if (core is null) return;
            try { core.MemoryUsageTargetLevel = CoreWebView2MemoryUsageTargetLevel.Normal; }
            catch { /* same as above */ }
        }
        catch (Exception ex) { Log("ExitLowMemoryMode skipped: " + ex.Message); }
    }

    // ══════════════════════════════════════════════════════════════
    // v1.1.2: theme-switch circular reveal (CapturePreview snapshot + WPF hole-punch animation)
    // The View Transitions snapshot layer sporadically shows "a completely blank layer /
    // stalled renderer" on WebView2's composited rendering path, with no permanent fix —
    // switch to a completely ordinary drawing path:
    //   1) CoreWebView2.CapturePreviewAsync captures the current (old-theme) page;
    //   2) the screenshot is laid into the overlay Image above the WebView (covering the page);
    //   3) JS is told to switch to the new theme immediately (no animation);
    //   4) the overlay's OpacityMask carves a constantly growing circular hole from the toggle
    //      position, revealing the new theme;
    //   5) when the animation ends the overlay is removed. Both directions share the same code —
    //      symmetric and never blank.
    // ══════════════════════════════════════════════════════════════
    private static bool _themeRevealing;

    /// <summary>Duration of the circular reveal animation.</summary>
    public static int ThemeRevealMs = 400;

    /// <summary>Called by TitleBar: starts a circular reveal to the new theme from (cssX, cssY) (CSS pixels = WPF DIP).</summary>
    public static async Task RevealThemeSwitchAsync(
        double cssX, double cssY, string nextTheme, Func<string, Task> applyThemeJs)
    {
        var app = System.Windows.Application.Current;
        var w = app?.MainWindow as MainWindow;
        if (w is null || MainWindow._themeRevealing)
        {
            await applyThemeJs(nextTheme);   // already revealing / no window: switch instantly
            return;
        }
        MainWindow._themeRevealing = true;
        try
        {
            var core = w._wv2?.CoreWebView2;
            var overlay = w.ThemeRevealOverlay;
            if (core is null || overlay is null)
            {
                await applyThemeJs(nextTheme);   // fallback: switch instantly when a capture is not possible
                return;
            }

            // 1. Capture the current (old-theme) page
            using var ms = new System.IO.MemoryStream();
            await core.CapturePreviewAsync(
                Microsoft.Web.WebView2.Core.CoreWebView2CapturePreviewImageFormat.Png, ms);
            ms.Position = 0;
            var bmp = new System.Windows.Media.Imaging.BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
            bmp.StreamSource = ms;
            bmp.EndInit();
            bmp.Freeze();   // usable across threads

            // 2. Lay the old frame into the overlay (covering the page)
            overlay.Source = bmp;
            overlay.Visibility = Visibility.Visible;

            // 3. Switch the page to the new theme immediately (no animation) and let the host background follow
            await applyThemeJs(nextTheme);
            ApplyShellTheme(nextTheme == "dark");
            await Task.Delay(60);   // give the new theme at least one frame of drawing time

            // 4. OpacityMask carves a circular hole from the toggle position (transparent inside the hole revealing the new theme, opaque old frame outside)
            double wd = overlay.ActualWidth, ht = overlay.ActualHeight;
            if (wd < 1 || ht < 1)
            {
                await applyThemeJs(nextTheme);
                return;
            }
            double endR = Math.Sqrt(
                Math.Pow(Math.Max(cssX, wd - cssX), 2) +
                Math.Pow(Math.Max(cssY, ht - cssY), 2));
            var brush = new System.Windows.Media.RadialGradientBrush
            {
                MappingMode = System.Windows.Media.BrushMappingMode.Absolute,
                Center = new System.Windows.Point(cssX, cssY),
                GradientOrigin = new System.Windows.Point(cssX, cssY)
            };
            System.Windows.Media.Animation.DoubleAnimation anim;
            if (nextTheme == "dark")
            {
                // Light to dark: the old light screenshot [shows only inside a shrinking circle
                // centered on the toggle] (transparent outside the circle, exposing the already
                // flipped real dark page) — the circle radius shrinks from full screen to 0,
                // the white pulls back into the toggle with the circle and night falls from the
                // edges first, matching the official site's "light collapsing back into the button"
                brush.GradientStops.Add(new System.Windows.Media.GradientStop(
                    System.Windows.Media.Colors.Black, 0.0));
                brush.GradientStops.Add(new System.Windows.Media.GradientStop(
                    System.Windows.Media.Colors.Black, 0.985));
                brush.GradientStops.Add(new System.Windows.Media.GradientStop(
                    System.Windows.Media.Colors.Transparent, 1.0));
                brush.RadiusX = endR;
                brush.RadiusY = endR;
                overlay.OpacityMask = brush;
                anim = new System.Windows.Media.Animation.DoubleAnimation(
                    endR, 0.001, TimeSpan.FromMilliseconds(ThemeRevealMs))
                {
                    EasingFunction = new System.Windows.Media.Animation.CubicEase
                    {
                        EasingMode = System.Windows.Media.Animation.EasingMode.EaseIn
                    }
                };
            }
            else
            {
                // Dark to light: the old dark screenshot fills the view and the circular hole
                // expands from the toggle, revealing the new light page (light radiates outward from the button)
                brush.GradientStops.Add(new System.Windows.Media.GradientStop(
                    System.Windows.Media.Colors.Transparent, 0.0));
                brush.GradientStops.Add(new System.Windows.Media.GradientStop(
                    System.Windows.Media.Colors.Transparent, 0.985));
                brush.GradientStops.Add(new System.Windows.Media.GradientStop(
                    System.Windows.Media.Colors.Black, 1.0));
                brush.RadiusX = 0.001;
                brush.RadiusY = 0.001;
                overlay.OpacityMask = brush;
                anim = new System.Windows.Media.Animation.DoubleAnimation(
                    0.001, endR, TimeSpan.FromMilliseconds(ThemeRevealMs))
                {
                    EasingFunction = new System.Windows.Media.Animation.CubicEase
                    {
                        EasingMode = System.Windows.Media.Animation.EasingMode.EaseIn
                    }
                };
            }
            var tcs = new TaskCompletionSource();
            anim.Completed += (_, _) => tcs.TrySetResult();
            brush.BeginAnimation(System.Windows.Media.RadialGradientBrush.RadiusXProperty, anim);
            brush.BeginAnimation(System.Windows.Media.RadialGradientBrush.RadiusYProperty, anim);
            await tcs.Task;

            // 5. Cleanup
            overlay.OpacityMask = null;
            overlay.Source = null;
            overlay.Visibility = Visibility.Collapsed;
        }
        catch (Exception ex)
        {
            Log("Theme circular reveal failed, falling back to an instant switch: " + ex.Message);
            try { await applyThemeJs(nextTheme); } catch { }
        }
        finally
        {
            var w2 = app?.MainWindow as MainWindow;
            if (w2 is not null)
            {
                MainWindow._themeRevealing = false;
                var o = w2.ThemeRevealOverlay;
                if (o is not null)
                {
                    o.OpacityMask = null;
                    o.Source = null;
                    o.Visibility = Visibility.Collapsed;
                }
            }
        }
    }

    /// <summary>Called by the Blazor page: opens the Nexus browsing overlay inside the main window.</summary>
    public static void OpenNexusOverlay(string url, bool queueMode = false)
    {
        var w = System.Windows.Application.Current?.MainWindow as MainWindow;
        if (w is null) return;
        _ = queueMode; // built-in browser removed: everything opens in the system browser
        try
        {
            System.Diagnostics.Process.Start(
                new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex) { Log("Failed to open the system browser: " + ex.Message); }
    }

    /// <summary>Called when routing away from "Mod Management": collapses the overlay (keeping the WebView2 instance to avoid re-initialization).</summary>
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

    // ─── v1.1.5/v1.1.8: window sizing policy ───
    // Starts windowed and reveals centered at 77.1%×72.7% of the current screen (see
    // OnSourceInitialized/RevealAtStartupPosition);
    // the drag minimum size is 60%×54% of the current monitor (2560×1600 screen = 1536×864PX,
    // see WndProcClampMaximized).
    // "Restoring" from maximized returns to 77.1%×72.6% of the current screen's work area, not
    // the old size recorded in the system RestoreBounds (that may be a small window casually
    // dragged long ago, which would look jarring on restore).
    private bool _wasMaximized;

    protected override void OnStateChanged(EventArgs e)
    {
        base.OnStateChanged(e);
        if (WindowState == System.Windows.WindowState.Maximized)
        {
            _wasMaximized = true;
            VerifyMaximizedPlacement();
            return;
        }
        if (WindowState == System.Windows.WindowState.Normal && _wasMaximized)
        {
            _wasMaximized = false;
            // Changing the size directly during the restore animation gets reverted by the state machine; schedule it to run after this layout pass
            Dispatcher.BeginInvoke(new Action(() =>
            {
                // v1.1.2c: restore size (general, open-source edition) — locked to [work area
                // ratios], not fixed physical pixels.
                // Calibration baseline: 1974×1110PX (16:9) on a 2560×1528 work area, i.e. 77.1%
                // wide, 72.6% high.
                // Occupies the same proportion of the work area at any resolution/scaling:
                // exactly 1974×1110PX on the author's screen; scales down proportionally on
                // 1080p screens without clipping; scales up on 4K screens to keep the same look
                // (mainstream software behavior).
                // v1.1.8: the work area comes from [the screen the window is actually on]
                // (MonitorFromWindow + GetMonitorInfo, physical pixels, no DPI conversion) —
                // the original SystemParameters.WorkArea only described the primary monitor, so
                // restoring after maximizing on a secondary screen computed the size from the
                // primary screen (too small) and the out-of-bounds check even dragged the window
                // back to the primary screen.
                var hwnd = new WindowInteropHelper(this).Handle;
                var dpi = hwnd != IntPtr.Zero ? (int)GetDpiForWindow(hwnd) : 96;
                if (dpi <= 0) dpi = 96;
                var scale = dpi / 96.0;
                double waPhysW, waPhysH, waLeft, waTop;
                var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
                if (hwnd != IntPtr.Zero && GetMonitorInfo(MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST), ref mi))
                {
                    waPhysW = mi.rcWork.Right - mi.rcWork.Left;
                    waPhysH = mi.rcWork.Bottom - mi.rcWork.Top;
                    waLeft = mi.rcWork.Left;
                    waTop = mi.rcWork.Top;
                }
                else
                {
                    // Fallback: the current screen is unavailable (should not happen) — fall back to the primary screen work area
                    var wa = SystemParameters.WorkArea;
                    waPhysW = wa.Width * scale;
                    waPhysH = wa.Height * scale;
                    waLeft = wa.Left * scale;
                    waTop = wa.Top * scale;
                }

                var physW = Math.Min(waPhysW - 16, Math.Max(400.0, waPhysW * (1974.0 / 2560.0)));
                var physH = Math.Min(waPhysH - 16, Math.Max(300.0, waPhysH * (1110.0 / 1528.0)));

                Width = Math.Max(MinWidth, Math.Round(physW / scale));
                Height = Math.Max(MinHeight, Math.Round(physH / scale));
                _restoreTargetPhysW = physW;   // for size checkpoint comparison (this screen's expected restore size)
                _restoreTargetPhysH = physH;

                // Restore-position out-of-bounds fallback (same screen's work area, physical
                // pixels) — if the Normal position is still near the off-screen mount point
                // -32000 (left over from old-version startups), the window would end up entirely
                // off-screen after restore, appearing as "the window vanished".
                var leftPhys = Left * scale;
                var topPhys = Top * scale;
                if (leftPhys < waLeft - 100 || leftPhys + Width * scale > waLeft + waPhysW + 100
                    || topPhys < waTop - 100 || topPhys + Height * scale > waTop + waPhysH + 100)
                {
                    Left = Math.Round((waLeft + (waPhysW - Width * scale) / 2) / scale);
                    Top = Math.Round((waTop + (waPhysH - Height * scale) / 2) / scale);
                }
                Log($"[restore] 77.1%×72.6% of the current screen work area {waPhysW:F0}×{waPhysH:F0}PX (={physW:F0}×{physH:F0}PX) -> actual " +
                    $"Width={Width:F0} Height={Height:F0} DIP = {Width * scale:F0}×{Height * scale:F0}PX (dpi={dpi}, scale={scale:0.##})");
                // v1.1.2c: the checkpoint is guaranteed to be logged here (the SizeChanged version can miss due to layout timing)
                Log($"[size checkpoint] ★ reached the standard restore size (expected for this screen {physW:F0}×{physH:F0}PX, " +
                    $"actual {Width * scale:F0}×{Height * scale:F0}PX, Width={Width:F0} Height={Height:F0} DIP, " +
                    $"dpi={dpi}, WindowState={WindowState}, Left={Left:F0} Top={Top:F0})");
            }));
        }
    }

    // This screen's expected restore size (physical pixels), written by the restore logic for size checkpoint comparison
    private double _restoreTargetPhysW, _restoreTargetPhysH;

    // ─── v1.1.8: maximize placement safety net ───
    // Two layers of problems, one fallback:
    // (1) without a manifest the process is System DPI aware and secondary-screen coordinates
    //     get virtualized by the system, so maximizing lands a full screen width off-screen
    //     (measured (5120,0)) -> switching to PerMonitorV2 in app.manifest fixed it at the root;
    // (2) under PMv2, cross-screen dragging (drag to a secondary screen with a different DPI and
    //     maximize immediately) has a race between WM_DPICHANGED and SC_MAXIMIZE: the maximize
    //     message is processed with the old DPI environment and the geometry is scaled by the
    //     old/new DPI ratio (measured landing at (1707,0) with size ÷1.5, stuck at the primary
    //     screen's right edge).
    // Fallback: after maximizing settles, if the geometry ≠ this screen's work area, force it
    // into place. The two-stage check covers the race: once after the layout pass + one recheck
    // after 120ms (once the DPI change settles).
    private void VerifyMaximizedPlacement()
    {
        Dispatcher.BeginInvoke(() => CheckMaximizedGeometry());
        var t = new System.Windows.Threading.DispatcherTimer
        { Interval = TimeSpan.FromMilliseconds(120) };
        t.Tick += (_, _) => { t.Stop(); CheckMaximizedGeometry(); };
        t.Start();
    }

    private void CheckMaximizedGeometry()
    {
        try
        {
            if (WindowState != System.Windows.WindowState.Maximized) return;
            var hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd == IntPtr.Zero || !GetWindowRect(hwnd, out var r)) return;
            var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
            if (!GetMonitorInfo(MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST), ref mi)) return;
            // Target geometry uses exactly the same rules as the WM_GETMINMAXINFO hook: work area + the 1px concession for an auto-hide taskbar
            var barH = AutoHideBottomBarHeight(mi.rcMonitor);
            var targetW = mi.rcWork.Right - mi.rcWork.Left;
            var targetH = mi.rcWork.Bottom - mi.rcWork.Top - (barH > 0 ? 1 : 0);
            var off = Math.Abs(r.Left - mi.rcWork.Left) > 2 || Math.Abs(r.Top - mi.rcWork.Top) > 2
                   || Math.Abs(r.Right - r.Left - targetW) > 2 || Math.Abs(r.Bottom - r.Top - targetH) > 2;
            if (!off) return;
            Log($"[maximize fallback] window ({r.Left},{r.Top})-({r.Right},{r.Bottom}) ≠ current screen work area " +
                $"({mi.rcWork.Left},{mi.rcWork.Top}) {targetW}×{targetH} -> forcing into place");
            SetWindowPos(hwnd, IntPtr.Zero,
                mi.rcWork.Left, mi.rcWork.Top, targetW, targetH,
                SWP_NOZORDER | SWP_NOACTIVATE);
        }
        catch (Exception ex) { Log("Maximized placement check failed: " + ex.Message); }
    }

    private const uint SWP_NOZORDER = 0x0004;
    private const uint SWP_NOACTIVATE = 0x0010;

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter,
        int X, int Y, int cx, int cy, uint uFlags);

    // v1.1.2b: user request — log the state when the window reaches the specified sizes.
    // Restore size checkpoint = this screen's expected restore value (1974×1110PX on the
    // 2560×1528 reference screen);
    // minimum size checkpoint = this screen's minimum size (43%×42.5% of the work area, same
    // rule as the drag lower limit).
    private void OnWindowSizeChanged(object sender, SizeChangedEventArgs e)
    {
        try
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd == IntPtr.Zero) return;
            var dpi = (int)GetDpiForWindow(hwnd);
            if (dpi <= 0) return;
            var scale = dpi / 96.0;
            var pw = ActualWidth * scale;
            var ph = ActualHeight * scale;
            if (_restoreTargetPhysW > 0 && Math.Abs(pw - _restoreTargetPhysW) < 4 && Math.Abs(ph - _restoreTargetPhysH) < 4)
                Log($"[size checkpoint] ★ reached the standard restore size (expected for this screen {_restoreTargetPhysW:F0}×{_restoreTargetPhysH:F0}PX, " +
                    $"actual {pw:F0}×{ph:F0}PX, Width={ActualWidth:F0} Height={ActualHeight:F0} DIP, dpi={dpi}, WindowState={WindowState}, Left={Left:F0} Top={Top:F0})" +
                    (_restoreTargetPhysW < 1970 ? " ≈ 1974×1110PX baseline" : ""));
            else
            {
                // v1.1.8b: minimum size checkpoint — same rule as WndProcClampMaximized, computed
                // as 60%×54% of the window's current monitor.
                var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
                if (GetMonitorInfo(MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST), ref mi))
                {
                    var (minW, minH) = MonitorMinTrackSize(mi.rcMonitor);
                    if (Math.Abs(pw - minW) < 4 && Math.Abs(ph - minH) < 4)
                        Log($"[size checkpoint] ★ reached this screen's minimum size {minW}×{minH}PX (actual {pw:F0}×{ph:F0}PX, " +
                            $"Width={ActualWidth:F0} Height={ActualHeight:F0} DIP, dpi={dpi}, WindowState={WindowState}, Left={Left:F0} Top={Top:F0})");
                }
            }
        }
        catch { }
    }

    /// <summary>Uses Win32 ShowWindow to force a minimize, ensuring the borderless window is truly withdrawn from the screen so no leftover transparent interactive window intercepts mouse clicks.</summary>
    public static void MinimizeWindow()
    {
        var w = System.Windows.Application.Current?.MainWindow as MainWindow;
        if (w is null) return;
        var hwnd = new System.Windows.Interop.WindowInteropHelper(w).Handle;
        if (hwnd != IntPtr.Zero) ShowWindow(hwnd, SW_MINIMIZE);
        else w.WindowState = System.Windows.WindowState.Minimized;
    }

    // ─── v1.1.1: light/dark theme — the window background and WebView2 fallback frame color follow the frontend data-theme ───
    // The four corners outside the rounded shell expose the WPF window background; the instant
    // before WebView2 produces a frame, DefaultBackgroundColor shows.
    // Both must match the frontend shell background color, otherwise the corners/the restore
    // instant would flash light in the dark theme.
    private static readonly System.Windows.Media.Color ThemeColorLight =
        System.Windows.Media.Color.FromArgb(0xFF, 0xF3, 0xF6, 0xFB);
    private static readonly System.Windows.Media.Color ThemeColorDark =
        System.Windows.Media.Color.FromArgb(0xFF, 0x1C, 0x1E, 0x23);

    /// <summary>Called after the frontend switches theme (TitleBar): syncs the WPF window background + WebView2 fallback frame color.</summary>
    public static void ApplyShellTheme(bool dark)
    {
        var app = System.Windows.Application.Current;
        var w = app?.MainWindow as MainWindow;
        if (w is null) return;
        w.Dispatcher.Invoke(() =>
        {
            var color = dark ? ThemeColorDark : ThemeColorLight;
            w.Background = new System.Windows.Media.SolidColorBrush(color);
            try
            {
                if (w._wv2 is not null)
                    w._wv2.DefaultBackgroundColor = System.Drawing.Color.FromArgb(color.A, color.R, color.G, color.B);
            }
            catch { }
        });
    }

    /// <summary>
    /// Forcibly shows the main window via Win32. WPF's Visibility=Visible does not necessarily
    /// trigger the HWND SW_SHOW for a window that has already been Show()n/Hidden
    /// (leaving the window visible=False and the main UI not appearing).
    /// This sends ShowWindow(SW_SHOW) directly to the HWND, bypassing that case and
    /// guaranteeing the system actually shows it.
    /// </summary>
    public static void ShowMainWindow()
    {
        var w = Application.Current?.MainWindow as MainWindow;
        if (w is null) return;
        // First make WPF's state machine consider the window visible — otherwise the ShowWindow
        // call gets treated as "still Hidden" by WPF's layout pass and undone (the root cause of
        // the persistent visible=False).
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
