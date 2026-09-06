using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media.Animation;
using Microsoft.Win32;

namespace JuniGrid;

/// <summary>
/// GUI uninstall wizard (JuniGrid.exe --uninstall): dark confirm page -> key-art
/// uninstalling page -> white completion page. Directory deletion is handled by a delayed
/// cmd self-delete (rd /s /q after this process exits), same mechanism as the old uninstall.ps1.
/// </summary>
public partial class UninstallWindow : Window
{
    // Keep in sync with installer/JuniGridInstaller/InstallerEngine.cs (same uninstall AppId)
    private const string UninstallKeyName = "{7E1B2C64-9A4D-4C0E-9F61-3A5D8B2C4E10}_is1";
    private static readonly string UninstallKeyPath =
        @"Software\Microsoft\Windows\CurrentVersion\Uninstall\" + UninstallKeyName;

    private readonly string _installDir;
    private bool _working;
    private bool _confirmed;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    public UninstallWindow()
    {
        InitializeComponent();
        _installDir = Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory;
        SourceInitialized += (_, _) =>
        {
            try
            {
                var hwnd = new WindowInteropHelper(this).Handle;
                int pref = 2; // DWMWCP_ROUND
                DwmSetWindowAttribute(hwnd, 33, ref pref, sizeof(int));
            }
            catch { }
        };
        MouseLeftButtonDown += (_, e) =>
        {
            if (e.ButtonState == MouseButtonState.Pressed) { try { DragMove(); } catch { } }
        };
        // In uninstall mode this is the only window: closing it ends the app; if an uninstall was confirmed, schedule the directory self-delete before exiting
        Closed += (_, _) =>
        {
            if (_confirmed) ScheduleSelfDelete();
            Application.Current?.Shutdown();
        };

        // App is running -> blocked page (mirrors the Riot launcher: cannot uninstall, close the app first)
        if (IsAppRunning())
        {
            ConfirmScreen.Visibility = Visibility.Collapsed;
            BlockedScreen.Visibility = Visibility.Visible;
        }
    }

    private static bool IsAppRunning()
    {
        try
        {
            var self = Environment.ProcessId;
            return Process.GetProcessesByName("JuniGrid").Any(p => p.Id != self);
        }
        catch { return false; }
    }

    private void OnMinimize(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void OnClose(object sender, RoutedEventArgs e) => Close();
    private void OnCancel(object sender, RoutedEventArgs e) => Close();

    private void OnUninstall(object sender, RoutedEventArgs e)
    {
        if (_working) return;
        _working = true;
        _confirmed = true;
        BtnUninstall.IsEnabled = false;
        BtnCancel.IsEnabled = false;

        ConfirmScreen.Visibility = Visibility.Collapsed;
        WorkingScreen.Visibility = Visibility.Visible;
        StartIndeterminate();

        Task.Run(() => { try { DoUninstall(); } catch { } })
            .ContinueWith(_ =>
            {
                StopIndeterminate();
                WorkingScreen.Visibility = Visibility.Collapsed;
                DoneScreen.Visibility = Visibility.Visible;
            }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    private void OnDone(object sender, RoutedEventArgs e) => Close();

    private void DoUninstall()
    {
        // 1) Kill other running JuniGrid instances (not this one)
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
        Thread.Sleep(400);

        // 2) Shortcuts
        try
        {
            File.Delete(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "JuniGrid.lnk"));
            File.Delete(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory), "JuniGrid.lnk"));
            var group = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "JuniGrid");
            if (Directory.Exists(group)) Directory.Delete(group, true);
        }
        catch { }

        // 3) Registry uninstall entry
        try { Registry.CurrentUser.DeleteSubKeyTree(UninstallKeyPath, false); } catch { }
    }

    /// <summary>Schedules the directory self-delete: must only run once the window has closed
    /// (the process is about to exit). WebView2 child processes release file locks with a
    /// delay and a failed rd is skipped silently, so it retries three times over ~15s.</summary>
    private void ScheduleSelfDelete()
    {
        try
        {
            var dir = _installDir;
            Process.Start(new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/c ping -n 3 127.0.0.1 > nul & rd /s /q \"{dir}\"" +
                            $" & ping -n 4 127.0.0.1 > nul & rd /s /q \"{dir}\"" +
                            $" & ping -n 8 127.0.0.1 > nul & rd /s /q \"{dir}\"",
                CreateNoWindow = true,
                UseShellExecute = false,
            });
        }
        catch { }
    }

    private void StartIndeterminate()
    {
        IndBar.BeginAnimation(System.Windows.Controls.Canvas.LeftProperty, new DoubleAnimation
        {
            From = -150,
            To = 520,
            Duration = TimeSpan.FromMilliseconds(900),
            RepeatBehavior = RepeatBehavior.Forever,
        });
    }

    private void StopIndeterminate()
        => IndBar.BeginAnimation(System.Windows.Controls.Canvas.LeftProperty, null);
}
