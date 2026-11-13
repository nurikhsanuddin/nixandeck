using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace WinMacShell.Core.Interop;

public sealed class WindowTracker : IDisposable
{
    // Pinned delegate field so GC never collects the WinEvent callback (AGENT.md §2.2)
    private readonly NativeMethods.WinEventDelegate _winEventDelegate;
    private GCHandle _delegateHandle;
    private readonly List<IntPtr> _hooks = new();
    private readonly HashSet<IntPtr> _ignoredHandles = new();
    private bool _disposed;
    private bool _lastOverlapState;
    private bool _lastFullscreenState;

    public event EventHandler<bool>? DockOverlapChanged;
    public event EventHandler<bool>? FullscreenChanged;
    public event EventHandler<IntPtr>? ForegroundChanged;
    public event EventHandler<IntPtr>? WindowLocationEnforceRequested;
    public event EventHandler? SystemNotificationWindowShown;

    public WindowTracker()
    {
        _winEventDelegate = OnWinEventProc;
        _delegateHandle = GCHandle.Alloc(_winEventDelegate);
    }

    public void AddIgnoredWindow(IntPtr hwnd)
    {
        if (hwnd != IntPtr.Zero)
        {
            _ignoredHandles.Add(hwnd);
        }
    }

    public bool IsIgnoredWindow(IntPtr hwnd) => _ignoredHandles.Contains(hwnd);

    public void Start()
    {
        if (_hooks.Count > 0) return;

        uint flags = NativeMethods.WINEVENT_OUTOFCONTEXT | NativeMethods.WINEVENT_SKIPOWNPROCESS;

        AddHook(NativeMethods.EVENT_SYSTEM_FOREGROUND, NativeMethods.EVENT_SYSTEM_FOREGROUND, flags);
        AddHook(NativeMethods.EVENT_SYSTEM_MINIMIZESTART, NativeMethods.EVENT_SYSTEM_MINIMIZEEND, flags);
        AddHook(NativeMethods.EVENT_OBJECT_CREATE, NativeMethods.EVENT_OBJECT_CREATE, flags);
        AddHook(NativeMethods.EVENT_OBJECT_SHOW, NativeMethods.EVENT_OBJECT_SHOW, flags);
        AddHook(NativeMethods.EVENT_OBJECT_LOCATIONCHANGE, NativeMethods.EVENT_OBJECT_LOCATIONCHANGE, flags);

        EvaluateForegroundState();
    }

    private void AddHook(uint eventMin, uint eventMax, uint flags)
    {
        IntPtr hook = NativeMethods.SetWinEventHook(
            eventMin,
            eventMax,
            IntPtr.Zero,
            _winEventDelegate,
            0,
            0,
            flags);

        if (hook != IntPtr.Zero)
        {
            _hooks.Add(hook);
        }
    }

    private void OnWinEventProc(
        IntPtr hWinEventHook,
        uint eventType,
        IntPtr hwnd,
        int idObject,
        int idChild,
        uint dwEventThread,
        uint dwmsEventTime)
    {
        if (_disposed) return;

        if (eventType is NativeMethods.EVENT_OBJECT_CREATE or NativeMethods.EVENT_OBJECT_SHOW)
        {
            if (idObject == NativeMethods.OBJID_WINDOW && hwnd != IntPtr.Zero && !_ignoredHandles.Contains(hwnd))
            {
                string cls = GetWindowClassName(hwnd);
                if (IsNativeToastBannerWindow(hwnd, cls))
                {
                    SuppressNativeToastBanner(hwnd);
                    SystemNotificationWindowShown?.Invoke(this, EventArgs.Empty);
                }
                else if (cls is "Windows.UI.Core.CoreWindow" or "Xaml_WindowedPopupClass" or "ApplicationFrameWindow")
                {
                    SystemNotificationWindowShown?.Invoke(this, EventArgs.Empty);
                }
            }
            return;
        }

        if (eventType == NativeMethods.EVENT_OBJECT_LOCATIONCHANGE)
        {
            if (idObject != NativeMethods.OBJID_WINDOW || hwnd == IntPtr.Zero)
            {
                return;
            }

            if (!_ignoredHandles.Contains(hwnd))
            {
                string cls = GetWindowClassName(hwnd);
                if (IsNativeToastBannerWindow(hwnd, cls))
                {
                    SuppressNativeToastBanner(hwnd);
                    SystemNotificationWindowShown?.Invoke(this, EventArgs.Empty);
                    return;
                }
            }

            if (hwnd != NativeMethods.GetForegroundWindow())
            {
                return;
            }
        }

        if (eventType == NativeMethods.EVENT_SYSTEM_FOREGROUND)
        {
            ForegroundChanged?.Invoke(this, hwnd);
        }

        EvaluateForegroundState();
    }

    public void EvaluateForegroundState()
    {
        IntPtr fg = NativeMethods.GetForegroundWindow();
        var (isOverlap, isFullscreen) = EvaluateWindowMetrics(fg);

        if (isOverlap != _lastOverlapState)
        {
            _lastOverlapState = isOverlap;
            DockOverlapChanged?.Invoke(this, isOverlap);
        }

        if (isFullscreen != _lastFullscreenState)
        {
            _lastFullscreenState = isFullscreen;
            FullscreenChanged?.Invoke(this, isFullscreen);
        }

        if (!isFullscreen && fg != IntPtr.Zero && !_ignoredHandles.Contains(fg))
        {
            WindowLocationEnforceRequested?.Invoke(this, fg);
        }
    }

    internal static bool IsTrueBorderlessFullscreen(IntPtr hwnd, NativeMethods.RECT winRect, NativeMethods.MONITORINFO mi)
    {
        bool coversMonitor =
            winRect.Left <= mi.rcMonitor.Left &&
            winRect.Top <= mi.rcMonitor.Top &&
            winRect.Right >= mi.rcMonitor.Right &&
            winRect.Bottom >= mi.rcMonitor.Bottom;

        if (!coversMonitor)
        {
            return false;
        }

        int style = NativeMethods.GetWindowLong(hwnd, NativeMethods.GWL_STYLE);
        bool hasNoThickFrame = (style & NativeMethods.WS_THICKFRAME) == 0;
        bool matchesExactMonitorBounds =
            winRect.Left == mi.rcMonitor.Left &&
            winRect.Top == mi.rcMonitor.Top &&
            winRect.Right == mi.rcMonitor.Right &&
            winRect.Bottom == mi.rcMonitor.Bottom;

        return hasNoThickFrame || matchesExactMonitorBounds;
    }

    private (bool DockOverlap, bool Fullscreen) EvaluateWindowMetrics(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero || _ignoredHandles.Contains(hwnd)) return (false, false);
        if (!NativeMethods.IsWindowVisible(hwnd) || NativeMethods.IsIconic(hwnd)) return (false, false);
        if (IsShellOrOverlayWindow(hwnd)) return (false, false);

        if (!NativeMethods.GetWindowRect(hwnd, out NativeMethods.RECT winRect))
        {
            return (false, false);
        }

        IntPtr monitor = NativeMethods.MonitorFromWindow(hwnd, NativeMethods.MONITOR_DEFAULTTONEAREST);
        var mi = new NativeMethods.MONITORINFO { cbSize = Marshal.SizeOf<NativeMethods.MONITORINFO>() };
        if (!NativeMethods.GetMonitorInfo(monitor, ref mi))
        {
            return (false, false);
        }

        if (mi.rcMonitor.Left != 0 || mi.rcMonitor.Top != 0)
        {
            return (false, false);
        }

        // True fullscreen (YouTube F11/fullscreen, media players, games) covers the entire monitor borderlessly
        if (IsTrueBorderlessFullscreen(hwnd, winRect, mi))
        {
            return (true, true);
        }

        if (NativeMethods.IsZoomed(hwnd))
        {
            return (true, false);
        }

        int dockTopThreshold = mi.rcMonitor.Bottom - 78;
        int centerMinX = mi.rcMonitor.Width / 4;
        int centerMaxX = (mi.rcMonitor.Width * 3) / 4;

        bool overlapsHorizontally = winRect.Right > centerMinX && winRect.Left < centerMaxX;
        bool overlapsVertically = winRect.Bottom >= dockTopThreshold && winRect.Top < mi.rcMonitor.Bottom;

        return (overlapsHorizontally && overlapsVertically, false);
    }

    internal static bool IsShellOrOverlayWindow(IntPtr hwnd)
    {
        string cls = GetWindowClassName(hwnd);
        if (cls is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd"
            or "Windows.UI.Core.CoreWindow" or "XamlExplorerHostIslandWindow")
        {
            return true;
        }

        return IsWindowsShellOverlayProcess(hwnd);
    }

    private static bool IsNativeToastBannerWindow(IntPtr hwnd, string cls)
    {
        if (cls != "Windows.UI.Core.CoreWindow")
        {
            return false;
        }

        string title = GetWindowTitle(hwnd);
        return string.Equals(title, "New notification", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(title, "Notifikasi baru", StringComparison.OrdinalIgnoreCase);
    }

    private static void SuppressNativeToastBanner(IntPtr hwnd)
    {
        NativeMethods.ShowWindow(hwnd, NativeMethods.SW_HIDE);
        NativeMethods.SetWindowPos(
            hwnd,
            IntPtr.Zero,
            -32000,
            -32000,
            0,
            0,
            NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE);
    }

    internal static bool IsStartMenuWindow(IntPtr hwnd)
    {
        string procName = GetProcessName(hwnd);
        return procName is "StartMenuExperienceHost" or "SearchHost";
    }

    internal static bool IsQuickSettingsWindow(IntPtr hwnd)
    {
        string procName = GetProcessName(hwnd);
        if (procName != "ShellExperienceHost")
        {
            return false;
        }

        string cls = GetWindowClassName(hwnd);
        return !IsNativeToastBannerWindow(hwnd, cls);
    }

    private static string GetWindowClassName(IntPtr hwnd)
    {
        var className = new StringBuilder(128);
        NativeMethods.GetClassName(hwnd, className, className.Capacity);
        return className.ToString();
    }

    private static string GetWindowTitle(IntPtr hwnd)
    {
        var title = new StringBuilder(256);
        NativeMethods.GetWindowText(hwnd, title, title.Capacity);
        return title.ToString();
    }

    private static string GetProcessName(IntPtr hwnd)
    {
        try
        {
            if (hwnd == IntPtr.Zero) return string.Empty;
            NativeMethods.GetWindowThreadProcessId(hwnd, out uint pid);
            if (pid == 0) return string.Empty;
            using Process proc = Process.GetProcessById((int)pid);
            return proc.ProcessName;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static bool IsWindowsShellOverlayProcess(IntPtr hwnd)
    {
        string procName = GetProcessName(hwnd);
        return procName is "StartMenuExperienceHost" or "SearchHost" or "ShellExperienceHost";
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        foreach (IntPtr hook in _hooks)
        {
            NativeMethods.UnhookWinEvent(hook);
        }
        _hooks.Clear();

        if (_delegateHandle.IsAllocated)
        {
            _delegateHandle.Free();
        }
    }
}
