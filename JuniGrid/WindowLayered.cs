using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Animation;

namespace JuniGrid;

/// <summary>
/// Uses Win32 WS_EX_LAYERED + SetLayeredWindowAttributes for transparency control at the DWM compositor level.
///
/// Why not WPF's Window.Opacity:
/// with the WindowStyle=None + AllowsTransparency=False + WindowChrome combination,
/// the WPF Opacity property does not take effect for DWM composition for a short while
/// (it goes through the GDI fallback path), showing as a one-frame black border flash
/// on Show()/restore from minimize (the ghosting seen in v0.20, screenshot one).
///
/// WS_EX_LAYERED is a DWM compositor-level attribute; once set, every frame of the
/// window is composited by DWM according to the alpha value, so unpainted background
/// can physically never show through.
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

    /// <summary>Makes the window layered and sets alpha=0 (fully transparent). Must be called after the window's HWND exists.</summary>
    public static void MakeLayeredInvisible(Window w)
    {
        var hwnd = new WindowInteropHelper(w).Handle;
        if (hwnd == IntPtr.Zero) hwnd = new WindowInteropHelper(w).EnsureHandle();
        var ex = GetWindowLong(hwnd, GWL_EXSTYLE);
        SetWindowLong(hwnd, GWL_EXSTYLE, ex | WS_EX_LAYERED);
        SetLayeredWindowAttributes(hwnd, 0, 0, LWA_ALPHA);
    }

    /// <summary>Sets alpha directly to a given value (0..255).</summary>
    public static void SetAlpha(Window w, byte alpha)
    {
        var hwnd = new WindowInteropHelper(w).Handle;
        if (hwnd == IntPtr.Zero) return;
        SetLayeredWindowAttributes(hwnd, 0, alpha, LWA_ALPHA);
    }

    /// <summary>Smoothly animates alpha to 255 (opaque) with a DoubleAnimation.</summary>
    public static void FadeInToOpaque(Window w, int durationMs = 260)
    {
        var hwnd = new WindowInteropHelper(w).Handle;
        if (hwnd == IntPtr.Zero) return;

        // Step alpha manually with a DispatcherTimer — a DoubleAnimation cannot bind to a Win32 attribute
        var start = DateTime.UtcNow;
        var timer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(16)   // ~60fps
        };
        timer.Tick += (_, _) =>
        {
            var t = (DateTime.UtcNow - start).TotalMilliseconds / durationMs;
            if (t >= 1) { SetLayeredWindowAttributes(hwnd, 0, 255, LWA_ALPHA); timer.Stop(); RemoveLayered(w); return; }
            // CubicEase EaseOut: 1 - (1-t)^3
            var eased = 1 - Math.Pow(1 - t, 3);
            byte a = (byte)Math.Clamp(Math.Round(eased * 255), 0, 255);
            SetLayeredWindowAttributes(hwnd, 0, a, LWA_ALPHA);
        };
        timer.Start();
    }

    /// <summary>Removes WS_EX_LAYERED after the animation finishes — leaving it on keeps DWM compositing frame by frame, wasting GPU.</summary>
    private static void RemoveLayered(Window w)
    {
        var hwnd = new WindowInteropHelper(w).Handle;
        if (hwnd == IntPtr.Zero) return;
        var ex = GetWindowLong(hwnd, GWL_EXSTYLE);
        SetWindowLong(hwnd, GWL_EXSTYLE, ex & ~WS_EX_LAYERED);
    }
}
