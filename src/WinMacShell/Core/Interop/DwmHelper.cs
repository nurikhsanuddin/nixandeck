using System.Windows;
using System.Windows.Interop;

namespace WinMacShell.Core.Interop;

internal static class DwmHelper
{
    public static void ConfigureShellOverlayWindow(Window window, bool enableAcrylic, bool noActivate = false)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero) return;

        int exStyle = NativeMethods.GetWindowLong(hwnd, NativeMethods.GWL_EXSTYLE);
        int targetStyle = exStyle | NativeMethods.WS_EX_TOOLWINDOW;
        if (noActivate)
        {
            targetStyle |= NativeMethods.WS_EX_NOACTIVATE;
        }
        NativeMethods.SetWindowLong(hwnd, NativeMethods.GWL_EXSTYLE, targetStyle);

        // Disable DWM non-client frame, corner rounding, and backdrop on the outer transparent HWND
        // so Windows 11 24H2 does not draw an unwanted outer border/gray box around transparent WPF regions.
        int ncrp = NativeMethods.DWMNCRP_DISABLED;
        NativeMethods.DwmSetWindowAttribute(hwnd, NativeMethods.DWMWA_NCRENDERING_POLICY, ref ncrp, sizeof(int));

        int darkMode = 1;
        NativeMethods.DwmSetWindowAttribute(hwnd, NativeMethods.DWMWA_USE_IMMERSIVE_DARK_MODE, ref darkMode, sizeof(int));

        int cornerPref = NativeMethods.DWMWCP_DONOTROUND;
        NativeMethods.DwmSetWindowAttribute(hwnd, NativeMethods.DWMWA_WINDOW_CORNER_PREFERENCE, ref cornerPref, sizeof(int));

        int backdropNone = NativeMethods.DWMSBT_NONE;
        NativeMethods.DwmSetWindowAttribute(hwnd, NativeMethods.DWMWA_SYSTEMBACKDROP_TYPE, ref backdropNone, sizeof(int));

        _ = enableAcrylic;
    }

    public static void EnableAcrylicBackdrop(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return;
        int backdropType = NativeMethods.DWMSBT_TRANSIENTWINDOW;
        NativeMethods.DwmSetWindowAttribute(hwnd, NativeMethods.DWMWA_SYSTEMBACKDROP_TYPE, ref backdropType, sizeof(int));
    }

    public static void EnsureTopmost(Window window)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero) return;

        NativeMethods.SetWindowPos(
            hwnd,
            NativeMethods.HWND_TOPMOST,
            0,
            0,
            0,
            0,
            NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE);
    }
}
