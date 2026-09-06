using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Threading;

namespace JuniGridInstaller;

public partial class App : Application
{
    private static Mutex? _single;

    public static void Log(string msg)
    {
        try
        {
            File.AppendAllText(Path.Combine(Path.GetTempPath(), "jgsetup-log.txt"),
                $"[{DateTime.Now:HH:mm:ss.fff}] {msg}\r\n");
        }
        catch { }
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        Log("App.OnStartup");

        // Single instance: double-clicking the setup package a second time must not spawn another installer.
        // When an instance already exists, bring its window to the front and exit this one.
        _single = new Mutex(true, "JuniGrid.Setup.SingleInstance", out var createdNew);
        if (!createdNew)
        {
            Log("Another installer instance is already running → activating it and exiting");
            ActivateExistingWindow();
            Shutdown();
            return;
        }

        base.OnStartup(e);

        AppDomain.CurrentDomain.UnhandledException += (_, a) =>
            Log("AppDomain.UnhandledException: " + a.ExceptionObject);
        TaskScheduler.UnobservedTaskException += (_, a) =>
            Log("UnobservedTaskException: " + a.Exception);
        // The installer must not crash silently: fall back to a message box telling the user why
        DispatcherUnhandledException += (_, args) =>
        {
            Log("DispatcherUnhandledException: " + args.Exception);
            try
            {
                MessageBox.Show("The installer encountered an error: " + args.Exception.Message,
                    "JuniGrid Setup", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            catch { }
            args.Handled = true;
        };
        Log("App.OnStartup done");
    }

    /// <summary>Restores the already-running installer window and brings it to the front (a background process cannot take focus directly, so go through Win32).</summary>
    private static void ActivateExistingWindow()
    {
        try
        {
            var w = Current.Windows.OfType<InstallerWindow>().FirstOrDefault();
            if (w is null) return;
            if (w.WindowState == WindowState.Minimized)
                w.WindowState = WindowState.Normal;
            w.Show();
            w.Activate();
            var hwnd = new System.Windows.Interop.WindowInteropHelper(w).Handle;
            if (hwnd != IntPtr.Zero)
                SetForegroundWindow(hwnd);
        }
        catch (Exception ex) { Log("ActivateExistingWindow: " + ex.Message); }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);
}
