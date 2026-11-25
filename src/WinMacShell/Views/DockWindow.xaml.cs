using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using ManagedShell.WindowsTasks;
using Microsoft.Win32;
using WinMacShell.Core.Interop;
using WinMacShell.Core.Services;
using WinMacShell.Models;
using WinMacShell.Persistence;

namespace WinMacShell.Views;

public partial class DockWindow : Window
{
    private const string WatchdogPipeName = "NixanDeck_WindowControlPipe";

    private readonly ConfigManager _configManager;
    private readonly ShellService _shellService;
    private readonly WindowTracker _windowTracker;
    private readonly ObservableCollection<DockItem> _dockItems = new();
    private readonly Dictionary<string, ImageSource?> _iconCache = new(StringComparer.OrdinalIgnoreCase);

    private static readonly SolidColorBrush HitBridgeBrush = CreateFrozenHitBridgeBrush();

    private static SolidColorBrush CreateFrozenHitBridgeBrush()
    {
        var brush = new SolidColorBrush(Color.FromArgb(1, 0, 0, 0));
        brush.Freeze();
        return brush;
    }

    // Allowed UI interaction timers (hover reveal, smooth hide debounce, & bottom-edge cursor monitor)
    private readonly DispatcherTimer _revealTimer;
    private readonly DispatcherTimer _hideDelayTimer;
    private readonly DispatcherTimer _edgeCursorTimer;

    private ContextMenu? _activeContextMenu;
    private bool _shouldAutoHide;
    private bool _isHiddenByAutoHide;
    private bool _isFullscreenHidden;
    private bool _hasAttemptedElevatedWatchdogLaunch;
    private IntPtr _lastForegroundAppHwnd;
    private IntPtr _mouseDownActiveHwnd;
    private IntPtr _lastDockActivatedHwnd;
    private long _lastDockActivatedTick;

    public DockWindow(ConfigManager configManager, ShellService shellService, WindowTracker windowTracker)
    {
        _configManager = configManager;
        _shellService = shellService;
        _windowTracker = windowTracker;

        InitializeComponent();
        DockItemsControl.ItemsSource = _dockItems;

        _revealTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(60) };
        _hideDelayTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(260) };
        _edgeCursorTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(140)
        };

        _revealTimer.Tick += (_, _) =>
        {
            _revealTimer.Stop();
            _hideDelayTimer.Stop();
            if (BottomHoverStrip.IsMouseOver || DockRoot.IsMouseOver || IsCursorNearBottomDockEdge())
            {
                DwmHelper.EnsureTopmost(this);
                SetDockSlideHidden(false);
            }
        };

        _hideDelayTimer.Tick += (_, _) =>
        {
            _hideDelayTimer.Stop();
            if (_shouldAutoHide &&
                !DockRoot.IsMouseOver &&
                !BottomHoverStrip.IsMouseOver &&
                !IsCursorOverVisibleDock() &&
                (_activeContextMenu == null || !_activeContextMenu.IsOpen))
            {
                SetDockSlideHidden(true);
            }
        };

        _edgeCursorTimer.Tick += (_, _) => CheckBottomEdgeCursorHover();

        _shellService.TasksChanged += (_, _) => Dispatcher.BeginInvoke(RebuildDockItems);
        _windowTracker.DockOverlapChanged += (_, overlap) => Dispatcher.BeginInvoke(() => OnDockOverlapChanged(overlap));
        _windowTracker.FullscreenChanged += (_, fs) => Dispatcher.BeginInvoke(() => OnFullscreenChanged(fs));
        _windowTracker.ForegroundChanged += (_, hwnd) => Dispatcher.BeginInvoke(() =>
        {
            if (hwnd != IntPtr.Zero &&
                hwnd != new WindowInteropHelper(this).Handle &&
                !_windowTracker.IsIgnoredWindow(hwnd) &&
                !WindowTracker.IsShellOrOverlayWindow(hwnd))
            {
                _lastForegroundAppHwnd = hwnd;
            }
            CloseActiveContextMenu();
            UpdateActiveStates();
            if (!_isFullscreenHidden)
            {
                DwmHelper.EnsureTopmost(this);
            }
        });

        Deactivated += (_, _) => CloseActiveContextMenu();

        Loaded += (_, _) =>
        {
            RebuildDockItems();
            PositionBottomCenter();
            _edgeCursorTimer.Start();
        };

        SizeChanged += (_, _) => PositionBottomCenter();
        SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;

        Closed += (_, _) =>
        {
            SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
            _revealTimer.Stop();
            _hideDelayTimer.Stop();
            _edgeCursorTimer.Stop();
        };
    }

    private void OnDisplaySettingsChanged(object? sender, EventArgs e)
    {
        Dispatcher.BeginInvoke(() =>
        {
            PositionBottomCenter();
            _windowTracker.EvaluateForegroundState();
        }, DispatcherPriority.Loaded);
    }

    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        PositionBottomCenter();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        DwmHelper.ConfigureShellOverlayWindow(this, enableAcrylic: false, noActivate: true);
        _windowTracker.AddIgnoredWindow(new WindowInteropHelper(this).Handle);
        PositionBottomCenter();
    }

    public void PositionBottomCenter()
    {
        if (ActualWidth <= 0 || ActualHeight <= 0) return;
        DwmHelper.GetPrimaryMonitorDipBounds(
            this,
            out double screenLeft,
            out double screenTop,
            out double screenWidth,
            out double screenHeight);

        Left = screenLeft + (screenWidth - ActualWidth) / 2.0;
        Top = screenTop + screenHeight - ActualHeight;
    }

    private void CheckBottomEdgeCursorHover()
    {
        if (_isFullscreenHidden) return;

        // Also keep foreground tracking up to date for elevated windows (e.g. Task Manager) where WinEventHook is filtered by UIPI
        IntPtr fg = NativeMethods.GetForegroundWindow();
        IntPtr selfHwnd = new WindowInteropHelper(this).Handle;
        if (fg != IntPtr.Zero &&
            fg != selfHwnd &&
            fg != _lastForegroundAppHwnd &&
            !_windowTracker.IsIgnoredWindow(fg) &&
            !WindowTracker.IsShellOrOverlayWindow(fg))
        {
            _lastForegroundAppHwnd = fg;
            _windowTracker.EvaluateForegroundState();
            UpdateActiveStates();
        }

        if (!_shouldAutoHide)
        {
            if (_isHiddenByAutoHide)
            {
                SetDockSlideHidden(false);
            }
            return;
        }

        if (_isHiddenByAutoHide)
        {
            if (IsCursorNearBottomDockEdge())
            {
                _hideDelayTimer.Stop();
                DwmHelper.EnsureTopmost(this);
                SetDockSlideHidden(false);
            }
        }
        else if (!DockRoot.IsMouseOver &&
                 !BottomHoverStrip.IsMouseOver &&
                 !IsCursorOverVisibleDock() &&
                 (_activeContextMenu == null || !_activeContextMenu.IsOpen))
        {
            if (!_hideDelayTimer.IsEnabled)
            {
                _hideDelayTimer.Start();
            }
        }
    }

    private bool IsCursorNearBottomDockEdge()
    {
        if (!NativeMethods.GetCursorPos(out NativeMethods.POINT pt)) return false;
        if (!DwmHelper.GetPrimaryMonitorDpiScale(out double scaleX, out double scaleY))
        {
            DpiScale dpi = VisualTreeHelper.GetDpi(this);
            scaleX = dpi.DpiScaleX > 0 ? dpi.DpiScaleX : 1.0;
            scaleY = dpi.DpiScaleY > 0 ? dpi.DpiScaleY : 1.0;
        }

        DwmHelper.GetPrimaryMonitorRect(out NativeMethods.RECT monitorRect);
        int edgeThresholdPx = (int)Math.Ceiling(6.0 * scaleY);
        int halfDockPx = (int)Math.Ceiling(Math.Max(ActualWidth / 2.0 + 48.0, 220.0) * scaleX);
        int centerX = monitorRect.Left + (monitorRect.Right - monitorRect.Left) / 2;

        return pt.Y >= monitorRect.Bottom - edgeThresholdPx &&
               pt.Y <= monitorRect.Bottom &&
               pt.X >= centerX - halfDockPx &&
               pt.X <= centerX + halfDockPx;
    }

    private bool IsCursorOverVisibleDock()
    {
        if (!NativeMethods.GetCursorPos(out NativeMethods.POINT pt)) return false;
        IntPtr selfHwnd = new WindowInteropHelper(this).Handle;
        if (selfHwnd != IntPtr.Zero && NativeMethods.GetWindowRect(selfHwnd, out NativeMethods.RECT dockRect))
        {
            DpiScale dpi = VisualTreeHelper.GetDpi(this);
            int padX = (int)Math.Ceiling(12.0 * (dpi.DpiScaleX > 0 ? dpi.DpiScaleX : 1.0));
            int padY = (int)Math.Ceiling(8.0 * (dpi.DpiScaleY > 0 ? dpi.DpiScaleY : 1.0));
            return pt.X >= dockRect.Left - padX &&
                   pt.X <= dockRect.Right + padX &&
                   pt.Y >= dockRect.Top - padY &&
                   pt.Y <= dockRect.Bottom + padY;
        }

        return false;
    }

    private void RebuildDockItems()
    {
        var visibleWindows = _shellService.GetVisibleWindows();
        var pinnedApps = _configManager.Config.PinnedApps;
        IntPtr fgHwnd = NativeMethods.GetForegroundWindow();

        var newItems = new List<DockItem>();
        var extraRunningWindows = new List<ApplicationWindow>();
        var matchedWindows = new HashSet<ApplicationWindow>();

        // 1. Pinned apps (attach primary matching window to pinned tile, overflow extra instances so 2+ terminals show separately)
        foreach (PinnedAppItem pinned in pinnedApps)
        {
            string key = GetPinnedItemKey(pinned);
            var appWindows = visibleWindows
                .Where(w => MatchesPinnedApp(w, pinned))
                .ToList();

            ApplicationWindow? primaryWin = appWindows.FirstOrDefault();
            if (primaryWin != null)
            {
                matchedWindows.Add(primaryWin);
            }

            // Extra windows (e.g., 2nd/3rd Terminal) are kept as separate running tiles
            for (int i = 1; i < appWindows.Count; i++)
            {
                matchedWindows.Add(appWindows[i]);
                extraRunningWindows.Add(appWindows[i]);
            }

            ImageSource? icon = primaryWin?.Icon
                ?? GetOrLoadPinnedIcon(pinned)
                ?? (primaryWin != null ? IconHelper.ExtractIconFromWindowHandle(primaryWin.Handle) : null);

            string baseTitle = string.IsNullOrWhiteSpace(pinned.Name)
                ? Path.GetFileNameWithoutExtension(pinned.ExecutablePath)
                : pinned.Name;
            string displayTitle = primaryWin != null && !string.IsNullOrWhiteSpace(primaryWin.Title)
                ? primaryWin.Title
                : baseTitle;

            var item = new DockItem
            {
                Id = $"pin:{key}",
                Title = displayTitle,
                ExecutablePath = pinned.ExecutablePath,
                Arguments = pinned.Arguments,
                AppUserModelId = pinned.AppUserModelId,
                ShortcutPath = pinned.ShortcutPath,
                Icon = icon,
                IsPinned = true,
                IsRunning = primaryWin != null,
                IsActive = primaryWin != null && MatchesWindowOrProcess(primaryWin, fgHwnd)
            };
            if (primaryWin != null)
            {
                item.Windows.Add(primaryWin);
            }
            newItems.Add(item);
        }

        // 2. All unpinned open windows + overflow multi-window instances (each window visible in Alt+Tab gets its own Dock icon)
        var unpinnedWindows = visibleWindows
            .Where(w => !matchedWindows.Contains(w))
            .Concat(extraRunningWindows)
            .ToList();

        if (newItems.Count > 0 && unpinnedWindows.Count > 0)
        {
            newItems.Add(new DockItem { Id = "separator", IsSeparator = true });
        }

        foreach (ApplicationWindow win in unpinnedWindows)
        {
            string exePath = !string.IsNullOrWhiteSpace(win.WinFileName) ? win.WinFileName : string.Empty;
            string title = !string.IsNullOrWhiteSpace(win.Title)
                ? win.Title
                : (!string.IsNullOrWhiteSpace(win.WinFileDescription)
                    ? win.WinFileDescription
                    : Path.GetFileNameWithoutExtension(exePath));

            ImageSource? icon = win.Icon
                ?? (!string.IsNullOrWhiteSpace(win.AppUserModelID) ? IconHelper.ExtractIconFromAumid(win.AppUserModelID, 64) : null)
                ?? IconHelper.ExtractIconFromWindowHandle(win.Handle)
                ?? GetOrLoadPathIcon(exePath);

            var item = new DockItem
            {
                Id = $"win:{win.Handle}",
                Title = title,
                ExecutablePath = exePath,
                AppUserModelId = win.IsUWP ? (win.AppUserModelID ?? string.Empty) : string.Empty,
                Icon = icon,
                IsPinned = false,
                IsRunning = true,
                IsActive = MatchesWindowOrProcess(win, fgHwnd)
            };
            item.Windows.Add(win);
            newItems.Add(item);
        }

        _dockItems.Clear();
        foreach (DockItem item in newItems)
        {
            _dockItems.Add(item);
        }
    }

    private void UpdateActiveStates()
    {
        IntPtr fgHwnd = NativeMethods.GetForegroundWindow();
        foreach (DockItem item in _dockItems)
        {
            if (item.IsSeparator) continue;
            item.IsActive = item.Windows.Any(w => MatchesWindowOrProcess(w, fgHwnd));
        }
    }

    private static bool MatchesWindowOrProcess(ApplicationWindow w, IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero || w.Handle == IntPtr.Zero) return false;
        if (w.Handle == hwnd) return true;

        IntPtr root = NativeMethods.GetAncestor(hwnd, NativeMethods.GA_ROOT);
        if (root != IntPtr.Zero && w.Handle == root) return true;

        IntPtr rootOwner = NativeMethods.GetAncestor(hwnd, NativeMethods.GA_ROOTOWNER);
        if (rootOwner != IntPtr.Zero && w.Handle == rootOwner) return true;

        NativeMethods.GetWindowThreadProcessId(w.Handle, out uint wPid);
        NativeMethods.GetWindowThreadProcessId(hwnd, out uint fgPid);
        return wPid != 0 && wPid == fgPid;
    }

    private static string GetPinnedItemKey(PinnedAppItem pinned)
    {
        if (!string.IsNullOrWhiteSpace(pinned.AppUserModelId))
        {
            return $"aumid:{pinned.AppUserModelId.ToLowerInvariant()}";
        }
        return $"exe:{NormalizeAppKey(pinned.ExecutablePath)}";
    }

    private static string NormalizeAppKey(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return string.Empty;
        return Path.GetFileName(path.Trim('"')).ToLowerInvariant();
    }

    private static bool MatchesPinnedApp(ApplicationWindow win, PinnedAppItem pinned)
    {
        if (!string.IsNullOrWhiteSpace(pinned.AppUserModelId) &&
            !string.IsNullOrWhiteSpace(win.AppUserModelID) &&
            string.Equals(win.AppUserModelID, pinned.AppUserModelId, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        string normalizedPinFile = NormalizeAppKey(pinned.ExecutablePath);
        if (string.IsNullOrEmpty(normalizedPinFile))
        {
            return false;
        }

        string winFile = Path.GetFileName(win.WinFileName ?? string.Empty).ToLowerInvariant();
        if (!string.IsNullOrEmpty(winFile) && winFile == normalizedPinFile)
        {
            return true;
        }

        if (normalizedPinFile == "wt.exe" && winFile == "windowsterminal.exe")
        {
            return true;
        }

        if (normalizedPinFile == "taskmgr.exe" &&
            !string.IsNullOrWhiteSpace(win.Title) &&
            win.Title.Contains("Task Manager", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (!string.IsNullOrEmpty(win.AppUserModelID) &&
            win.AppUserModelID.Contains(Path.GetFileNameWithoutExtension(normalizedPinFile), StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return false;
    }

    private ImageSource? GetOrLoadPinnedIcon(PinnedAppItem pinned)
    {
        string key = GetPinnedItemKey(pinned);
        if (_iconCache.TryGetValue(key, out ImageSource? cached) && cached != null)
        {
            return cached;
        }

        ImageSource? loaded = TaskbarPinHelper.GetPinnedAppIcon(pinned);
        if (loaded != null)
        {
            _iconCache[key] = loaded;
        }
        return loaded;
    }

    private ImageSource? GetOrLoadPathIcon(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        if (_iconCache.TryGetValue(path, out ImageSource? cached) && cached != null)
        {
            return cached;
        }

        ImageSource? loaded = IconHelper.ExtractIconFromPath(path);
        if (loaded != null)
        {
            _iconCache[path] = loaded;
        }
        return loaded;
    }

    private void DockContainer_MouseMove(object sender, MouseEventArgs e)
    {
        System.Windows.Point cursor = e.GetPosition(DockItemsControl);

        for (int i = 0; i < _dockItems.Count; i++)
        {
            DockItem item = _dockItems[i];
            if (item.IsSeparator) continue;

            if (DockItemsControl.ItemContainerGenerator.ContainerFromIndex(i) is not FrameworkElement container)
            {
                continue;
            }

            System.Windows.Point topLeft = container.TranslatePoint(new System.Windows.Point(0, 0), DockItemsControl);
            double centerX = topLeft.X + (container.ActualWidth / 2.0);
            double distance = Math.Abs(cursor.X - centerX);

            const double sigma = 46.0;
            const double maxBoost = 0.32;
            item.Scale = 1.0 + (maxBoost * Math.Exp(-(distance * distance) / (2.0 * sigma * sigma)));
        }
    }

    private void DockContainer_MouseLeave(object sender, MouseEventArgs e)
    {
        ResetIconScales();
    }

    private void ResetIconScales()
    {
        foreach (DockItem item in _dockItems)
        {
            if (!item.IsSeparator)
            {
                item.Scale = 1.0;
            }
        }
    }

    private void DockTile_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        IntPtr fg = NativeMethods.GetForegroundWindow();
        IntPtr selfHwnd = new WindowInteropHelper(this).Handle;
        if (fg != IntPtr.Zero &&
            fg != selfHwnd &&
            !_windowTracker.IsIgnoredWindow(fg) &&
            !WindowTracker.IsShellOrOverlayWindow(fg))
        {
            _mouseDownActiveHwnd = fg;
            _lastForegroundAppHwnd = fg;
        }
        else
        {
            _mouseDownActiveHwnd = _lastForegroundAppHwnd;
        }
    }

    private async void DockTile_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        CloseActiveContextMenu();
        if (sender is not FrameworkElement { Tag: DockItem item }) return;

        if (item.Windows.Count == 0)
        {
            LaunchDockItem(item);
            return;
        }

        IntPtr fg = NativeMethods.GetForegroundWindow();
        IntPtr selfHwnd = new WindowInteropHelper(this).Handle;
        IntPtr activeAppHwnd = _mouseDownActiveHwnd != IntPtr.Zero ? _mouseDownActiveHwnd : fg;
        if (activeAppHwnd == selfHwnd || _windowTracker.IsIgnoredWindow(activeAppHwnd))
        {
            activeAppHwnd = _lastForegroundAppHwnd;
        }
        _mouseDownActiveHwnd = IntPtr.Zero;

        ApplicationWindow? activeMatch = item.Windows.FirstOrDefault(w => MatchesWindowOrProcess(w, activeAppHwnd));
        bool allMinimized = item.Windows.All(w => NativeMethods.IsIconic(w.Handle));

        if (allMinimized)
        {
            ApplicationWindow target = item.Windows[0];
            await RestoreAndActivateWindowAsync(target, item);
            _lastForegroundAppHwnd = target.Handle;
            _lastDockActivatedHwnd = target.Handle;
            _lastDockActivatedTick = Environment.TickCount64;
            return;
        }

        ApplicationWindow primaryTarget = activeMatch
            ?? item.Windows.FirstOrDefault(w => !NativeMethods.IsIconic(w.Handle))
            ?? item.Windows[0];

        bool wasJustActivated = _lastDockActivatedHwnd == primaryTarget.Handle &&
                                (Environment.TickCount64 - _lastDockActivatedTick) < 1500;

        if ((activeMatch != null || wasJustActivated) && !NativeMethods.IsIconic(primaryTarget.Handle))
        {
            await MinimizeWindowAsync(primaryTarget);
            _lastForegroundAppHwnd = IntPtr.Zero;
            _lastDockActivatedHwnd = IntPtr.Zero;
        }
        else
        {
            await RestoreAndActivateWindowAsync(primaryTarget, item);
            _lastForegroundAppHwnd = primaryTarget.Handle;
            _lastDockActivatedHwnd = primaryTarget.Handle;
            _lastDockActivatedTick = Environment.TickCount64;
        }
    }

    private async Task MinimizeWindowAsync(ApplicationWindow target)
    {
        IntPtr hwnd = target.Handle;
        if (hwnd == IntPtr.Zero) return;

        NativeMethods.ShowWindow(hwnd, NativeMethods.SW_MINIMIZE);
        if (!NativeMethods.IsIconic(hwnd))
        {
            NativeMethods.ShowWindowAsync(hwnd, NativeMethods.SW_MINIMIZE);
            NativeMethods.PostMessage(hwnd, NativeMethods.WM_SYSCOMMAND, new IntPtr(NativeMethods.SC_MINIMIZE), IntPtr.Zero);
        }

        if (!NativeMethods.IsIconic(hwnd))
        {
            bool sent = await SendWatchdogWindowCommandAsync($"MINIMIZE {hwnd.ToInt64()}");
            if (sent)
            {
                await Task.Delay(50);
            }

            if (!NativeMethods.IsIconic(hwnd))
            {
                await EnsureElevatedWatchdogAndSendAsync($"MINIMIZE {hwnd.ToInt64()}");
            }
        }
    }

    private async Task RestoreAndActivateWindowAsync(ApplicationWindow target, DockItem item)
    {
        IntPtr hwnd = target.Handle;
        if (hwnd == IntPtr.Zero) return;

        bool wasIconic = NativeMethods.IsIconic(hwnd);
        if (wasIconic)
        {
            NativeMethods.ShowWindow(hwnd, NativeMethods.SW_RESTORE);
            if (NativeMethods.IsIconic(hwnd))
            {
                NativeMethods.ShowWindowAsync(hwnd, NativeMethods.SW_RESTORE);
                NativeMethods.PostMessage(hwnd, NativeMethods.WM_SYSCOMMAND, new IntPtr(NativeMethods.SC_RESTORE), IntPtr.Zero);
            }
        }

        NativeMethods.SetForegroundWindow(hwnd);
        NativeMethods.SwitchToThisWindow(hwnd, true);

        bool stillBlocked = (wasIconic && NativeMethods.IsIconic(hwnd)) ||
                            !MatchesWindowOrProcess(target, NativeMethods.GetForegroundWindow());

        if (stillBlocked)
        {
            bool sent = await SendWatchdogWindowCommandAsync($"RESTORE {hwnd.ToInt64()}");
            if (sent)
            {
                await Task.Delay(50);
            }

            // Built-in Windows auto-elevated single-instance fallback for Task Manager
            if ((wasIconic && NativeMethods.IsIconic(hwnd)) ||
                !MatchesWindowOrProcess(target, NativeMethods.GetForegroundWindow()))
            {
                if (IsTaskManagerWindow(target, item))
                {
                    try
                    {
                        using var _ = Process.Start(new ProcessStartInfo
                        {
                            FileName = "taskmgr.exe",
                            UseShellExecute = true
                        });
                        return;
                    }
                    catch { }
                }

                await EnsureElevatedWatchdogAndSendAsync($"RESTORE {hwnd.ToInt64()}");
            }
        }
    }

    private static bool IsTaskManagerWindow(ApplicationWindow target, DockItem item)
    {
        return (!string.IsNullOrWhiteSpace(target.WinFileName) &&
                target.WinFileName.EndsWith("Taskmgr.exe", StringComparison.OrdinalIgnoreCase)) ||
               (!string.IsNullOrWhiteSpace(item.ExecutablePath) &&
                item.ExecutablePath.EndsWith("taskmgr.exe", StringComparison.OrdinalIgnoreCase)) ||
               (!string.IsNullOrWhiteSpace(target.Title) &&
                target.Title.Contains("Task Manager", StringComparison.OrdinalIgnoreCase));
    }

    private static async Task<bool> SendWatchdogWindowCommandAsync(string command)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", WatchdogPipeName, PipeDirection.Out, PipeOptions.Asynchronous);
            await client.ConnectAsync(80);
            using var writer = new StreamWriter(client) { AutoFlush = true };
            await writer.WriteLineAsync(command);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private async Task EnsureElevatedWatchdogAndSendAsync(string command)
    {
        if (_hasAttemptedElevatedWatchdogLaunch) return;
        _hasAttemptedElevatedWatchdogLaunch = true;

        try
        {
            string baseDir = AppContext.BaseDirectory;
            string watchdogPath = Path.Combine(baseDir, "NixanDeck.Watchdog.exe");
            if (!File.Exists(watchdogPath)) return;

            foreach (Process existing in Process.GetProcessesByName("NixanDeck.Watchdog"))
            {
                try { existing.Kill(); } catch { }
                finally { existing.Dispose(); }
            }

            using var _ = Process.Start(new ProcessStartInfo
            {
                FileName = watchdogPath,
                Arguments = $"--pid {Environment.ProcessId}",
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden
            });

            await Task.Delay(250);
            await SendWatchdogWindowCommandAsync(command);
        }
        catch
        {
            // User declined elevation or watchdog unavailable
        }
    }

    private void DockTile_MouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.Tag is not DockItem item) return;

        CloseActiveContextMenu();

        IntPtr hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd != IntPtr.Zero)
        {
            NativeMethods.SetForegroundWindow(hwnd);
        }
        Activate();

        var menu = new ContextMenu
        {
            Style = (Style)FindResource("GlassContextMenuStyle"),
            StaysOpen = false
        };

        var menuItemStyle = (Style)FindResource("GlassMenuItemStyle");

        var openItem = new MenuItem
        {
            Header = item.IsRunning ? "Open / Activate" : "Open",
            Style = menuItemStyle
        };
        openItem.Click += async (_, _) =>
        {
            if (item.Windows.Count == 0)
            {
                LaunchDockItem(item);
            }
            else
            {
                await RestoreAndActivateWindowAsync(item.Windows[0], item);
            }
        };
        menu.Items.Add(openItem);

        if (item.IsRunning && !string.IsNullOrWhiteSpace(item.ExecutablePath))
        {
            var newWindowItem = new MenuItem
            {
                Header = "New Window",
                Style = menuItemStyle
            };
            newWindowItem.Click += (_, _) => LaunchDockItem(item);
            menu.Items.Add(newWindowItem);
        }

        bool canPin = !string.IsNullOrWhiteSpace(item.ExecutablePath) || !string.IsNullOrWhiteSpace(item.AppUserModelId);
        var pinItem = new MenuItem
        {
            Header = item.IsPinned ? "Unpin from Dock" : "Pin to Dock",
            Style = menuItemStyle,
            IsEnabled = canPin
        };
        pinItem.Click += (_, _) => TogglePin(item);
        menu.Items.Add(pinItem);

        if (item.IsPinned && canPin)
        {
            var unpinTaskbarItem = new MenuItem
            {
                Header = "Unpin from Taskbar",
                Style = menuItemStyle
            };
            unpinTaskbarItem.Click += (_, _) =>
            {
                TaskbarPinHelper.UnpinFromTaskbar(item.ShortcutPath, item.ExecutablePath, item.AppUserModelId);
                TogglePin(item);
            };
            menu.Items.Add(unpinTaskbarItem);
        }

        var syncPinsItem = new MenuItem
        {
            Header = "Sync Pins with Windows Taskbar",
            Style = menuItemStyle
        };
        syncPinsItem.Click += (_, _) =>
        {
            _iconCache.Clear();
            _configManager.SyncWithTaskbarPins(replaceWithTaskbar: true);
            RebuildDockItems();
        };
        menu.Items.Add(syncPinsItem);

        if (item.IsRunning && item.Windows.Count > 0)
        {
            var closeItem = new MenuItem
            {
                Header = "Close Window",
                Style = menuItemStyle
            };
            closeItem.Click += (_, _) =>
            {
                foreach (ApplicationWindow win in item.Windows.ToList())
                {
                    win.Close();
                }
            };
            menu.Items.Add(closeItem);
        }

        menu.Closed += (_, _) =>
        {
            if (ReferenceEquals(_activeContextMenu, menu))
            {
                _activeContextMenu = null;
            }
        };

        _activeContextMenu = menu;
        menu.PlacementTarget = fe;
        menu.IsOpen = true;
        e.Handled = true;
    }

    private void CloseActiveContextMenu()
    {
        if (_activeContextMenu is { IsOpen: true } menu)
        {
            menu.IsOpen = false;
        }
        _activeContextMenu = null;
    }

    private static void LaunchDockItem(DockItem item)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(item.AppUserModelId))
            {
                using var _ = Process.Start(new ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    Arguments = $"shell:AppsFolder\\{item.AppUserModelId}",
                    UseShellExecute = true
                });
                return;
            }

            if (!string.IsNullOrWhiteSpace(item.ShortcutPath) && File.Exists(item.ShortcutPath))
            {
                using var _ = Process.Start(new ProcessStartInfo
                {
                    FileName = item.ShortcutPath,
                    UseShellExecute = true
                });
                return;
            }

            if (string.IsNullOrWhiteSpace(item.ExecutablePath)) return;
            using var __ = Process.Start(new ProcessStartInfo
            {
                FileName = item.ExecutablePath,
                Arguments = item.Arguments ?? string.Empty,
                UseShellExecute = true
            });
        }
        catch
        {
            // Ignore launch failures for missing paths
        }
    }

    private void TogglePin(DockItem item)
    {
        List<PinnedAppItem> pinned = _configManager.Config.PinnedApps;
        int existingIndex = pinned.FindIndex(p =>
            (!string.IsNullOrWhiteSpace(item.AppUserModelId) &&
             string.Equals(p.AppUserModelId, item.AppUserModelId, StringComparison.OrdinalIgnoreCase)) ||
            (!string.IsNullOrWhiteSpace(item.ExecutablePath) &&
             NormalizeAppKey(p.ExecutablePath) == NormalizeAppKey(item.ExecutablePath)));

        if (existingIndex >= 0)
        {
            pinned.RemoveAt(existingIndex);
        }
        else
        {
            pinned.Add(new PinnedAppItem
            {
                Name = item.Title,
                ExecutablePath = item.ExecutablePath,
                Arguments = item.Arguments,
                AppUserModelId = item.AppUserModelId,
                ShortcutPath = item.ShortcutPath
            });
        }

        _configManager.Save();
        RebuildDockItems();
    }

    private void OnDockOverlapChanged(bool overlap)
    {
        _shouldAutoHide = _configManager.Config.AutoHideDock && overlap;

        if (_shouldAutoHide)
        {
            BottomHoverStrip.Visibility = Visibility.Visible;
            if (!DockRoot.IsMouseOver &&
                !IsCursorOverVisibleDock() &&
                (_activeContextMenu == null || !_activeContextMenu.IsOpen))
            {
                SetDockSlideHidden(true);
            }
        }
        else
        {
            _revealTimer.Stop();
            _hideDelayTimer.Stop();
            BottomHoverStrip.Visibility = Visibility.Collapsed;
            DwmHelper.EnsureTopmost(this);
            SetDockSlideHidden(false);
        }
    }

    private void OnFullscreenChanged(bool isFullscreen)
    {
        _isFullscreenHidden = isFullscreen;
        Visibility = _isFullscreenHidden ? Visibility.Collapsed : Visibility.Visible;
        if (!_isFullscreenHidden)
        {
            DwmHelper.EnsureTopmost(this);
        }
    }

    private void BottomHoverStrip_MouseEnter(object sender, MouseEventArgs e)
    {
        _hideDelayTimer.Stop();
        if (_shouldAutoHide && _isHiddenByAutoHide)
        {
            _revealTimer.Start();
        }
    }

    private void DockRoot_MouseEnter(object sender, MouseEventArgs e)
    {
        _hideDelayTimer.Stop();
    }

    private void DockRoot_MouseLeave(object sender, MouseEventArgs e)
    {
        _revealTimer.Stop();
        ResetIconScales();
        if (_shouldAutoHide && !_isHiddenByAutoHide && (_activeContextMenu == null || !_activeContextMenu.IsOpen))
        {
            _hideDelayTimer.Start();
        }
    }

    private void SetDockSlideHidden(bool hide)
    {
        _isHiddenByAutoHide = hide;
        DockContainer.IsHitTestVisible = !hide;
        DockRoot.Background = hide ? null : HitBridgeBrush;
        double targetY = hide ? 90.0 : 0.0;

        var anim = new DoubleAnimation(targetY, TimeSpan.FromMilliseconds(220))
        {
            EasingFunction = new ExponentialEase { EasingMode = EasingMode.EaseOut, Exponent = 4 }
        };
        DockSlideTransform.BeginAnimation(TranslateTransform.YProperty, anim);
    }
}
