using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using WinMacShell.Core.Interop;
using WinMacShell.Core.Services;

namespace WinMacShell.Core.Safety;

public sealed class TaskbarGuard : IDisposable
{
    public const double TopBarHeightDip = 30.0;

    private ShellService? _shellService;
    private IntPtr _hotkeyHwnd;
    private HwndSource? _hwndSource;
    private bool _hotkeyRegistered;
    private bool _isHidden;
    private bool _safetyHandlersRegistered;
    private bool _appBarRegistered;
    private bool _isApplyingWorkArea;
    private uint _appBarCallbackMsg;
    private int? _originalAppBarState;
    private NativeMethods.RECT? _originalWorkArea;
    private double _dpiScaleY = 1.0;

    public bool IsSystemTaskbarHidden => _isHidden;

    public event EventHandler<bool>? TaskbarVisibilityChanged;
    public event EventHandler<bool>? AppBarFullscreenNotification;

    public void AttachShellService(ShellService shellService)
    {
        _shellService = shellService;
    }

    public void UpdateDpiScale(Visual visual)
    {
        try
        {
            DpiScale dpi = VisualTreeHelper.GetDpi(visual);
            if (dpi.DpiScaleY > 0)
            {
                _dpiScaleY = dpi.DpiScaleY;
            }
        }
        catch { }
    }

    public void HideSystemTaskbar()
    {
        SetTaskbarVisibility(show: false);
    }

    public void ShowSystemTaskbar()
    {
        SetTaskbarVisibility(show: true);
    }

    public void ToggleSystemTaskbar()
    {
        if (_isHidden)
        {
            ShowSystemTaskbar();
        }
        else
        {
            HideSystemTaskbar();
        }
    }

    private void SetTaskbarVisibility(bool show)
    {
        IntPtr primary = NativeMethods.FindWindow("Shell_TrayWnd", null);

        if (primary != IntPtr.Zero)
        {
            var abd = new NativeMethods.APPBARDATA
            {
                cbSize = Marshal.SizeOf<NativeMethods.APPBARDATA>(),
                hWnd = primary
            };

            if (!show)
            {
                if (_originalAppBarState == null)
                {
                    _originalAppBarState = (int)NativeMethods.SHAppBarMessage(NativeMethods.ABM_GETSTATE, ref abd);
                }
                abd.lParam = new IntPtr(NativeMethods.ABS_AUTOHIDE);
                NativeMethods.SHAppBarMessage(NativeMethods.ABM_SETSTATE, ref abd);
            }
            else
            {
                int restoreState = _originalAppBarState ?? NativeMethods.ABS_ALWAYSONTOP;
                abd.lParam = new IntPtr(restoreState);
                NativeMethods.SHAppBarMessage(NativeMethods.ABM_SETSTATE, ref abd);
            }
        }

        _shellService?.SetExplorerTaskbarHidden(!show);

        int cmd = show ? NativeMethods.SW_SHOW : NativeMethods.SW_HIDE;
        if (primary != IntPtr.Zero)
        {
            NativeMethods.ShowWindow(primary, cmd);
        }

        IntPtr secondary = IntPtr.Zero;
        while ((secondary = NativeMethods.FindWindowEx(IntPtr.Zero, secondary, "Shell_SecondaryTrayWnd", null)) != IntPtr.Zero)
        {
            NativeMethods.ShowWindow(secondary, cmd);
        }

        _isHidden = !show;

        if (!show)
        {
            ApplyTopMenuBarWorkArea();
        }
        else
        {
            RestoreWorkArea();
        }

        TaskbarVisibilityChanged?.Invoke(this, _isHidden);
    }

    private void EnsureTopAppBarRegistered()
    {
        if (_appBarRegistered || _hotkeyHwnd == IntPtr.Zero) return;

        if (_appBarCallbackMsg == 0)
        {
            _appBarCallbackMsg = NativeMethods.RegisterWindowMessage("WinMacShell_TopAppBar");
        }

        var abd = new NativeMethods.APPBARDATA
        {
            cbSize = Marshal.SizeOf<NativeMethods.APPBARDATA>(),
            hWnd = _hotkeyHwnd,
            uCallbackMessage = _appBarCallbackMsg
        };

        _appBarRegistered = NativeMethods.SHAppBarMessage(NativeMethods.ABM_NEW, ref abd) != UIntPtr.Zero;
    }

    private void UnregisterTopAppBar()
    {
        if (!_appBarRegistered || _hotkeyHwnd == IntPtr.Zero) return;

        var abd = new NativeMethods.APPBARDATA
        {
            cbSize = Marshal.SizeOf<NativeMethods.APPBARDATA>(),
            hWnd = _hotkeyHwnd
        };
        NativeMethods.SHAppBarMessage(NativeMethods.ABM_REMOVE, ref abd);
        _appBarRegistered = false;
    }

    private int GetReservedTopPx() => Math.Max(1, (int)Math.Floor(TopBarHeightDip * _dpiScaleY) - 1);

    private void ApplyTopMenuBarWorkArea()
    {
        if (_isApplyingWorkArea) return;
        _isApplyingWorkArea = true;

        try
        {
            if (_originalWorkArea == null)
            {
                var currentWork = new NativeMethods.RECT();
                if (NativeMethods.SystemParametersInfo(NativeMethods.SPI_GETWORKAREA, 0, ref currentWork, 0))
                {
                    _originalWorkArea = currentWork;
                }
            }

            IntPtr monitor = NativeMethods.MonitorFromPoint(new NativeMethods.POINT { X = 0, Y = 0 }, NativeMethods.MONITOR_DEFAULTTOPRIMARY);
            var mi = new NativeMethods.MONITORINFO { cbSize = Marshal.SizeOf<NativeMethods.MONITORINFO>() };
            if (!NativeMethods.GetMonitorInfo(monitor, ref mi))
            {
                return;
            }

            int reservedTopPx = GetReservedTopPx();

            EnsureTopAppBarRegistered();
            if (_appBarRegistered && _hotkeyHwnd != IntPtr.Zero)
            {
                var abd = new NativeMethods.APPBARDATA
                {
                    cbSize = Marshal.SizeOf<NativeMethods.APPBARDATA>(),
                    hWnd = _hotkeyHwnd,
                    uEdge = NativeMethods.ABE_TOP,
                    rc = new NativeMethods.RECT
                    {
                        Left = mi.rcMonitor.Left,
                        Top = mi.rcMonitor.Top,
                        Right = mi.rcMonitor.Right,
                        Bottom = mi.rcMonitor.Top + reservedTopPx
                    }
                };
                NativeMethods.SHAppBarMessage(NativeMethods.ABM_QUERYPOS, ref abd);
                abd.rc.Bottom = abd.rc.Top + reservedTopPx;
                NativeMethods.SHAppBarMessage(NativeMethods.ABM_SETPOS, ref abd);
            }

            var newWorkArea = new NativeMethods.RECT
            {
                Left = mi.rcMonitor.Left,
                Top = mi.rcMonitor.Top + reservedTopPx,
                Right = mi.rcMonitor.Right,
                Bottom = mi.rcMonitor.Bottom
            };

            NativeMethods.SystemParametersInfo(
                NativeMethods.SPI_SETWORKAREA,
                0,
                ref newWorkArea,
                NativeMethods.SPIF_SENDCHANGE);
        }
        catch { }
        finally
        {
            _isApplyingWorkArea = false;
        }

        EnforceTopBarOnAllOpenWindows();
    }

    public void EnforceTopBarOnAllOpenWindows()
    {
        if (!_isHidden) return;

        var visibleWindows = _shellService?.GetVisibleWindows();
        if (visibleWindows != null)
        {
            foreach (var win in visibleWindows)
            {
                EnforceWindowBelowTopBar(win.Handle);
            }
        }
    }

    public void EnforceWindowBelowTopBar(IntPtr hwnd)
    {
        if (!_isHidden || _isApplyingWorkArea) return;
        if (hwnd == IntPtr.Zero || hwnd == _hotkeyHwnd) return;

        NativeMethods.GetWindowThreadProcessId(hwnd, out uint pid);
        if (pid == Environment.ProcessId) return;

        if (!NativeMethods.IsWindowVisible(hwnd) || NativeMethods.IsIconic(hwnd)) return;
        if (WindowTracker.IsShellOrOverlayWindow(hwnd)) return;

        if (!NativeMethods.GetWindowRect(hwnd, out NativeMethods.RECT winRect)) return;

        IntPtr monitor = NativeMethods.MonitorFromWindow(hwnd, NativeMethods.MONITOR_DEFAULTTONEAREST);
        var mi = new NativeMethods.MONITORINFO { cbSize = Marshal.SizeOf<NativeMethods.MONITORINFO>() };
        if (!NativeMethods.GetMonitorInfo(monitor, ref mi)) return;

        if (mi.rcMonitor.Left != 0 || mi.rcMonitor.Top != 0) return;

        if (WindowTracker.IsTrueBorderlessFullscreen(hwnd, winRect, mi)) return;

        if (NativeMethods.DwmGetWindowAttribute(
                hwnd,
                NativeMethods.DWMWA_EXTENDED_FRAME_BOUNDS,
                out NativeMethods.RECT frameRect,
                Marshal.SizeOf<NativeMethods.RECT>()) != 0)
        {
            frameRect = winRect;
        }

        int leftBorder = frameRect.Left - winRect.Left;
        int topBorder = frameRect.Top - winRect.Top;
        int rightBorder = winRect.Right - frameRect.Right;
        int bottomBorder = winRect.Bottom - frameRect.Bottom;

        int reservedTopPx = GetReservedTopPx();
        int minTop = mi.rcMonitor.Top + reservedTopPx;

        if (NativeMethods.IsZoomed(hwnd))
        {
            if (Math.Abs(frameRect.Top - minTop) > 1 || Math.Abs(frameRect.Bottom - mi.rcMonitor.Bottom) > 1)
            {
                int targetX = mi.rcMonitor.Left - leftBorder;
                int targetY = minTop - topBorder;
                int targetW = mi.rcMonitor.Width + leftBorder + rightBorder;
                int targetH = (mi.rcMonitor.Height - reservedTopPx) + topBorder + bottomBorder;

                NativeMethods.SetWindowPos(
                    hwnd,
                    IntPtr.Zero,
                    targetX,
                    targetY,
                    targetW,
                    targetH,
                    NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_ASYNCWINDOWPOS);
            }
        }
        else if (frameRect.Top < minTop - 1)
        {
            int targetY = minTop - topBorder;
            int maxVisibleHeight = mi.rcMonitor.Bottom - minTop;
            int currentVisibleHeight = frameRect.Height;
            int targetH = Math.Min(currentVisibleHeight, maxVisibleHeight) + topBorder + bottomBorder;

            NativeMethods.SetWindowPos(
                hwnd,
                IntPtr.Zero,
                winRect.Left,
                targetY,
                winRect.Width,
                targetH,
                NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_ASYNCWINDOWPOS);
        }
    }

    private void RestoreWorkArea()
    {
        try
        {
            UnregisterTopAppBar();

            if (_originalWorkArea is NativeMethods.RECT orig)
            {
                NativeMethods.SystemParametersInfo(
                    NativeMethods.SPI_SETWORKAREA,
                    0,
                    ref orig,
                    NativeMethods.SPIF_SENDCHANGE);

                var visibleWindows = _shellService?.GetVisibleWindows();
                if (visibleWindows != null)
                {
                    foreach (var win in visibleWindows)
                    {
                        RestoreMaximizedWindowToWorkArea(win.Handle, orig);
                    }
                }

                _originalWorkArea = null;
            }
        }
        catch { }
    }

    private static void RestoreMaximizedWindowToWorkArea(IntPtr hwnd, NativeMethods.RECT workArea)
    {
        if (hwnd == IntPtr.Zero || !NativeMethods.IsWindowVisible(hwnd) || !NativeMethods.IsZoomed(hwnd))
        {
            return;
        }

        IntPtr monitor = NativeMethods.MonitorFromWindow(hwnd, NativeMethods.MONITOR_DEFAULTTONEAREST);
        var mi = new NativeMethods.MONITORINFO { cbSize = Marshal.SizeOf<NativeMethods.MONITORINFO>() };
        if (!NativeMethods.GetMonitorInfo(monitor, ref mi) || mi.rcMonitor.Left != 0 || mi.rcMonitor.Top != 0)
        {
            return;
        }

        if (!NativeMethods.GetWindowRect(hwnd, out NativeMethods.RECT winRect))
        {
            return;
        }

        if (WindowTracker.IsTrueBorderlessFullscreen(hwnd, winRect, mi))
        {
            return;
        }

        if (NativeMethods.DwmGetWindowAttribute(
                hwnd,
                NativeMethods.DWMWA_EXTENDED_FRAME_BOUNDS,
                out NativeMethods.RECT frameRect,
                Marshal.SizeOf<NativeMethods.RECT>()) != 0)
        {
            frameRect = winRect;
        }

        int leftBorder = frameRect.Left - winRect.Left;
        int topBorder = frameRect.Top - winRect.Top;
        int rightBorder = winRect.Right - frameRect.Right;
        int bottomBorder = winRect.Bottom - frameRect.Bottom;

        NativeMethods.SetWindowPos(
            hwnd,
            IntPtr.Zero,
            workArea.Left - leftBorder,
            workArea.Top - topBorder,
            workArea.Width + leftBorder + rightBorder,
            workArea.Height + topBorder + bottomBorder,
            NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_ASYNCWINDOWPOS);
    }

    public void RegisterGlobalHotKey(Window hostWindow)
    {
        if (_hotkeyRegistered) return;

        UpdateDpiScale(hostWindow);
        var helper = new WindowInteropHelper(hostWindow);
        _hotkeyHwnd = helper.Handle;
        if (_hotkeyHwnd == IntPtr.Zero) return;

        _hwndSource = HwndSource.FromHwnd(_hotkeyHwnd);
        _hwndSource?.AddHook(WndProc);

        uint modifiers = NativeMethods.MOD_CONTROL | NativeMethods.MOD_ALT | NativeMethods.MOD_SHIFT | NativeMethods.MOD_NOREPEAT;
        _hotkeyRegistered = NativeMethods.RegisterHotKey(
            _hotkeyHwnd,
            NativeMethods.HOTKEY_ID_TASKBAR,
            modifiers,
            NativeMethods.VK_R);
    }

    public void UnregisterGlobalHotKey()
    {
        if (_hwndSource != null)
        {
            _hwndSource.RemoveHook(WndProc);
            _hwndSource = null;
        }

        if (_hotkeyRegistered && _hotkeyHwnd != IntPtr.Zero)
        {
            NativeMethods.UnregisterHotKey(_hotkeyHwnd, NativeMethods.HOTKEY_ID_TASKBAR);
            _hotkeyRegistered = false;
        }
    }

    public void RegisterSafetyHandlers(Application app)
    {
        if (_safetyHandlersRegistered) return;
        _safetyHandlersRegistered = true;

        app.Exit += (_, _) => ShowSystemTaskbar();
        AppDomain.CurrentDomain.UnhandledException += (_, _) => ShowSystemTaskbar();
        app.DispatcherUnhandledException += (_, _) => ShowSystemTaskbar();
        TaskScheduler.UnobservedTaskException += (_, _) => ShowSystemTaskbar();
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == NativeMethods.WM_HOTKEY && wParam.ToInt32() == NativeMethods.HOTKEY_ID_TASKBAR)
        {
            ToggleSystemTaskbar();
            handled = true;
        }
        else if (_appBarCallbackMsg != 0 && msg == _appBarCallbackMsg)
        {
            int code = wParam.ToInt32();
            if (code == NativeMethods.ABN_POSCHANGED && _isHidden)
            {
                ApplyTopMenuBarWorkArea();
            }
            else if (code == NativeMethods.ABN_FULLSCREENAPP)
            {
                AppBarFullscreenNotification?.Invoke(this, lParam != IntPtr.Zero);
            }
        }

        return IntPtr.Zero;
    }

    public void Dispose()
    {
        ShowSystemTaskbar();
        UnregisterGlobalHotKey();
    }
}
