using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

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

    public static bool GetPrimaryMonitorRect(out NativeMethods.RECT rect)
    {
        IntPtr monitor = NativeMethods.MonitorFromPoint(
            new NativeMethods.POINT { X = 0, Y = 0 },
            NativeMethods.MONITOR_DEFAULTTOPRIMARY);

        var info = new NativeMethods.MONITORINFO
        {
            cbSize = Marshal.SizeOf<NativeMethods.MONITORINFO>()
        };

        if (monitor != IntPtr.Zero && NativeMethods.GetMonitorInfo(monitor, ref info))
        {
            rect = info.rcMonitor;
            return true;
        }

        rect = new NativeMethods.RECT
        {
            Left = 0,
            Top = 0,
            Right = (int)Math.Round(SystemParameters.PrimaryScreenWidth),
            Bottom = (int)Math.Round(SystemParameters.PrimaryScreenHeight)
        };
        return false;
    }

    public static void GetPrimaryMonitorDipBounds(
        Visual visual,
        out double left,
        out double top,
        out double width,
        out double height)
    {
        DpiScale dpi = VisualTreeHelper.GetDpi(visual);
        double scaleX = dpi.DpiScaleX > 0 ? dpi.DpiScaleX : 1.0;
        double scaleY = dpi.DpiScaleY > 0 ? dpi.DpiScaleY : 1.0;

        if (GetPrimaryMonitorRect(out NativeMethods.RECT rect))
        {
            left = rect.Left / scaleX;
            top = rect.Top / scaleY;
            width = (rect.Right - rect.Left) / scaleX;
            height = (rect.Bottom - rect.Top) / scaleY;
        }
        else
        {
            left = 0;
            top = 0;
            width = SystemParameters.PrimaryScreenWidth;
            height = SystemParameters.PrimaryScreenHeight;
        }
    }
}
