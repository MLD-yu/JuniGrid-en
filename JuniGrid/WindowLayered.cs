using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Animation;

namespace JuniGrid;

/// <summary>
/// Uses Win32 WS_EX_LAYERED + SetLayeredWindowAttributes for DWM-compositor-level transparency control.
///
/// Why not WPF's Window.Opacity:
/// With the WindowStyle=None + AllowsTransparency=False + WindowChrome combination, WPF's
/// Opacity property fails to affect DWM composition for a short period (the GDI fallback path
/// kicks in), showing a one-frame black border flash at Show()/restore-from-minimize
/// (the ghosting seen in v0.20, first screenshot).
///
/// WS_EX_LAYERED is a DWM-compositor-layer attribute: once set, every frame of the window is
/// composited by DWM according to the alpha value, so it is physically impossible for the
/// unpainted background to show through.
/// </summary>
internal static class WindowLayered
{
    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_LAYERED = 0x00080000;
    private const uint LWA_ALPHA = 0x00000002;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int GetWindowLong(IntPtr hwnd, int index);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern int SetWindowLong(IntPtr hwnd, int index, int newStyle);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetLayeredWindowAttributes(IntPtr hwnd, uint crKey, byte bAlpha, uint dwFlags);

    /// <summary>Makes the window layered with alpha=0 (fully transparent). Must be called after the window HWND exists.</summary>
    public static void MakeLayeredInvisible(Window w)
    {
        var hwnd = new WindowInteropHelper(w).Handle;
        if (hwnd == IntPtr.Zero) hwnd = new WindowInteropHelper(w).EnsureHandle();
        var ex = GetWindowLong(hwnd, GWL_EXSTYLE);
        SetWindowLong(hwnd, GWL_EXSTYLE, ex | WS_EX_LAYERED);
        SetLayeredWindowAttributes(hwnd, 0, 0, LWA_ALPHA);
    }

    /// <summary>Sets alpha directly to a value (0..255).</summary>
    public static void SetAlpha(Window w, byte alpha)
    {
        var hwnd = new WindowInteropHelper(w).Handle;
        if (hwnd == IntPtr.Zero) return;
        SetLayeredWindowAttributes(hwnd, 0, alpha, LWA_ALPHA);
    }

    /// <summary>Animates alpha smoothly to 255 (opaque) with a DoubleAnimation.</summary>
    public static void FadeInToOpaque(Window w, int durationMs = 260)
    {
        var hwnd = new WindowInteropHelper(w).Handle;
        if (hwnd == IntPtr.Zero) return;

        // Step alpha manually with a DispatcherTimer — DoubleAnimation can't bind to a Win32 attribute
        var start = DateTime.UtcNow;
        var timer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(16)   // ~60fps
        };
        timer.Tick += (_, _) =>
        {
            var t = (DateTime.UtcNow - start).TotalMilliseconds / durationMs;
            if (t >= 1) { SetLayeredWindowAttributes(hwnd, 0, 255, LWA_ALPHA); timer.Stop(); RemoveLayered(w); return; }
            // CubicEase EaseOut：1 - (1-t)^3
            var eased = 1 - Math.Pow(1 - t, 3);
            byte a = (byte)Math.Clamp(Math.Round(eased * 255), 0, 255);
            SetLayeredWindowAttributes(hwnd, 0, a, LWA_ALPHA);
        };
        timer.Start();
    }

    /// <summary>Removes WS_EX_LAYERED after the animation — leaving it set keeps per-frame DWM composition running and wastes GPU.</summary>
    private static void RemoveLayered(Window w)
    {
        var hwnd = new WindowInteropHelper(w).Handle;
        if (hwnd == IntPtr.Zero) return;
        var ex = GetWindowLong(hwnd, GWL_EXSTYLE);
        SetWindowLong(hwnd, GWL_EXSTYLE, ex & ~WS_EX_LAYERED);
    }
}
