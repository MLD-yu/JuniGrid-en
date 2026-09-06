using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace JuniGridInstaller;

internal static class NativeMethods
{
    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    /// <summary>Adds system rounded corners to the borderless window on Win11; silently ignored on systems without support (Win10).</summary>
    public static void TryRoundCorners(Window window)
    {
        try
        {
            var hwnd = new WindowInteropHelper(window).Handle;
            int pref = 2; // DWMWCP_ROUND
            DwmSetWindowAttribute(hwnd, 33 /* DWMWA_WINDOW_CORNER_PREFERENCE */, ref pref, sizeof(int));
        }
        catch
        {
            // Older systems lack this API; square corners are fine
        }
    }
}
