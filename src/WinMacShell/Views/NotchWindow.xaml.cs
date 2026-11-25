using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Threading;
using ManagedShell.WindowsTasks;
using ManagedShell.WindowsTray;
using Microsoft.Win32;
using WinMacShell.Core.Interop;
using WinMacShell.Core.Safety;
using WinMacShell.Core.Services;
using WinMacShell.Models;

namespace WinMacShell.Views;

public partial class NotchWindow : Window
{
    private enum DynamicIslandState
    {
        Idle,
        MediaPill,
        NotificationToast,
        ExpandedHub
    }

    private readonly MediaService _mediaService;
    private readonly SystemStatusService _systemStatusService;
    private readonly ShellService _shellService;
    private readonly NotificationService _notificationService;
    private readonly WindowTracker _windowTracker;
    private readonly TaskbarGuard _taskbarGuard;

    private readonly DispatcherTimer _clockAlignmentTimer;
    private readonly DispatcherTimer _toastAutoCollapseTimer;
    private readonly DispatcherTimer _hubHoverOpenTimer;
    private readonly DispatcherTimer _hubHoverCloseTimer;
    private readonly Storyboard? _visualizerStoryboard;

    private DynamicIslandState _islandState = DynamicIslandState.Idle;
    private ContextMenu? _activeContextMenu;
    private bool _isPlayingMedia;
    private bool _isUpdatingVolumeSlider;
    private bool _isUpdatingBrightnessSlider;
    private bool _isDraggingVolumeSlider;
    private bool _isDraggingBrightnessSlider;
    private long _lastVolumeInteractTick;
    private long _lastBrightnessInteractTick;
    private double _targetVolumeWheelValue;
    private double _targetBrightnessWheelValue;
    private bool _isAnimatingVolumeWheel;
    private bool _isAnimatingBrightnessWheel;
    private readonly DispatcherTimer _sliderWheelTimer;
    private bool _isCenterHubOpen;
    private bool _isTrayPopupOpen;
    private bool _isVolumePopupOpen;
    private bool _isBrightnessPopupOpen;
    private bool _isClockPopupOpen;
    private bool _showingAlertsTab;
    private bool _isFullscreenHidden;
    private bool _isStartMenuOpen;
    private bool _isWifiFlyoutOpen;
    private bool _suppressNextStartClick;
    private bool _suppressNextWifiClick;
    private DateTime _displayedCalendarMonth = new(DateTime.Now.Year, DateTime.Now.Month, 1);
    private uint _activeToastNotificationId;
    private bool _activeToastIsAlarm;
    private NotificationItem? _lastToastItem;
    private long _lastPopupOpenedTick;
    private long _lastTrayIconInteractTick;
    private long _startOpenedTick;
    private long _lastStartDismissByButtonTick;
    private long _wifiOpenedTick;
    private long _lastWifiDismissByButtonTick;
    private ulong _prevIdleTime;
    private ulong _prevKernelTime;
    private ulong _prevUserTime;

    public event EventHandler<Window>? SourceInitializedReady;

    public NotchWindow(
        MediaService mediaService,
        SystemStatusService systemStatusService,
        ShellService shellService,
        NotificationService notificationService,
        WindowTracker windowTracker,
        TaskbarGuard taskbarGuard)
    {
        _mediaService = mediaService;
        _systemStatusService = systemStatusService;
        _shellService = shellService;
        _notificationService = notificationService;
        _windowTracker = windowTracker;
        _taskbarGuard = taskbarGuard;

        InitializeComponent();

        _visualizerStoryboard = TryFindResource("VisualizerStoryboard") as Storyboard;

        _sliderWheelTimer = new DispatcherTimer(DispatcherPriority.Render)
        {
            Interval = TimeSpan.FromMilliseconds(16)
        };
        _sliderWheelTimer.Tick += SliderWheelTimer_Tick;

        PopupVolumeSlider.AddHandler(
            System.Windows.Controls.Primitives.Thumb.DragStartedEvent,
            new System.Windows.Controls.Primitives.DragStartedEventHandler((_, _) =>
            {
                _isDraggingVolumeSlider = true;
                _isAnimatingVolumeWheel = false;
                _lastVolumeInteractTick = Environment.TickCount64;
            }));
        PopupVolumeSlider.AddHandler(
            System.Windows.Controls.Primitives.Thumb.DragCompletedEvent,
            new System.Windows.Controls.Primitives.DragCompletedEventHandler((_, _) =>
            {
                _isDraggingVolumeSlider = false;
                _lastVolumeInteractTick = Environment.TickCount64;
            }));

        PopupBrightnessSlider.AddHandler(
            System.Windows.Controls.Primitives.Thumb.DragStartedEvent,
            new System.Windows.Controls.Primitives.DragStartedEventHandler((_, _) =>
            {
                _isDraggingBrightnessSlider = true;
                _isAnimatingBrightnessWheel = false;
                _lastBrightnessInteractTick = Environment.TickCount64;
            }));
        PopupBrightnessSlider.AddHandler(
            System.Windows.Controls.Primitives.Thumb.DragCompletedEvent,
            new System.Windows.Controls.Primitives.DragCompletedEventHandler((_, _) =>
            {
                _isDraggingBrightnessSlider = false;
                _lastBrightnessInteractTick = Environment.TickCount64;
            }));

        // Allowed timer #1: 2-second lightweight telemetry & clock tick (CPU/RAM + minute-checked calendar/clock)
        _clockAlignmentTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(2)
        };
        _clockAlignmentTimer.Tick += (_, _) =>
        {
            UpdateClockText();
            UpdateCpuRamMetrics();
            _ = Task.Run(() =>
            {
                _systemStatusService.RefreshAndNotify();
                _notificationService.SyncDoNotDisturbFromSystem();
            });
        };

        // Allowed timer #2: Notification toast auto-collapse (4500ms)
        _toastAutoCollapseTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(4500) };
        _toastAutoCollapseTimer.Tick += (_, _) =>
        {
            _toastAutoCollapseTimer.Stop();
            if (_islandState == DynamicIslandState.NotificationToast && !DynamicIslandShell.IsMouseOver)
            {
                TransitionDynamicIsland(GetDefaultIslandState());
            }
        };

        // Allowed UI hover timers for unified morphing Dynamic Island -> Expanded Hub
        _hubHoverOpenTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(130) };
        _hubHoverOpenTimer.Tick += (_, _) =>
        {
            _hubHoverOpenTimer.Stop();
            if (DynamicIslandShell.IsMouseOver &&
                !_isCenterHubOpen &&
                _islandState != DynamicIslandState.NotificationToast &&
                (_activeContextMenu == null || !_activeContextMenu.IsOpen))
            {
                SetCenterHubOpen(true);
            }
        };

        _hubHoverCloseTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(260) };
        _hubHoverCloseTimer.Tick += (_, _) =>
        {
            _hubHoverCloseTimer.Stop();
            if (!DynamicIslandShell.IsMouseOver &&
                _isCenterHubOpen &&
                (_activeContextMenu == null || !_activeContextMenu.IsOpen))
            {
                SetCenterHubOpen(false);
            }
        };

        _mediaService.MediaStateChanged += (_, snapshot) => Dispatcher.BeginInvoke(() => ApplyMediaSnapshot(snapshot));
        _systemStatusService.StatusChanged += (_, status) => Dispatcher.BeginInvoke(() => ApplySystemStatus(status));
        _notificationService.NotificationReceived += (_, item) => Dispatcher.BeginInvoke(() => OnNotificationReceived(item));
        _notificationService.HistoryChanged += (_, _) => Dispatcher.BeginInvoke(RefreshNotificationDrawer);
        _windowTracker.FullscreenChanged += (_, fs) => Dispatcher.BeginInvoke(() => OnFullscreenChanged(fs));
        _windowTracker.ForegroundChanged += (_, hwnd) => Dispatcher.BeginInvoke(() =>
        {
            IntPtr myHwnd = new WindowInteropHelper(this).Handle;
            if (hwnd != myHwnd)
            {
                if (_isStartMenuOpen && Environment.TickCount64 - _startOpenedTick >= 650)
                {
                    if (WindowsStartButton.IsMouseOver)
                    {
                        _lastStartDismissByButtonTick = Environment.TickCount64;
                    }
                    else if (!WindowTracker.IsStartMenuWindow(hwnd))
                    {
                        _isStartMenuOpen = false;
                    }
                }

                if (_isWifiFlyoutOpen && Environment.TickCount64 - _wifiOpenedTick >= 650)
                {
                    if (TopWifiButton.IsMouseOver)
                    {
                        _lastWifiDismissByButtonTick = Environment.TickCount64;
                    }
                    else if (!WindowTracker.IsQuickSettingsWindow(hwnd))
                    {
                        _isWifiFlyoutOpen = false;
                    }
                }

                if (Environment.TickCount64 - _lastPopupOpenedTick > 280)
                {
                    bool keepTrayOpen = _isTrayPopupOpen &&
                                        (TrayPopupShell.IsMouseOver ||
                                         (Environment.TickCount64 - _lastTrayIconInteractTick < 1500));
                    CloseTransientPopups(preserveTrayPopup: keepTrayOpen);
                }
            }

            if (!_isFullscreenHidden)
            {
                DwmHelper.EnsureTopmost(this);
            }
        });

        Deactivated += (_, _) =>
        {
            if (Environment.TickCount64 - _lastPopupOpenedTick > 280)
            {
                bool keepTrayOpen = _isTrayPopupOpen &&
                                    (TrayPopupShell.IsMouseOver ||
                                     (Environment.TickCount64 - _lastTrayIconInteractTick < 1500));
                CloseTransientPopups(preserveTrayPopup: keepTrayOpen);
            }
        };

        Loaded += (_, _) =>
        {
            PositionTopBar();
            UpdateClockText();
            RenderMonthCalendar(_displayedCalendarMonth);
            UpdateCpuRamMetrics();
            _clockAlignmentTimer.Start();
            ApplyMediaSnapshot(_mediaService.CurrentState);
            ApplySystemStatus(_systemStatusService.CurrentStatus);
            TrayIconsControl.ItemsSource = _shellService.NotificationArea.TrayIcons;
            RefreshNotificationDrawer();
        };

        SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;

        Closed += (_, _) =>
        {
            SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
            _clockAlignmentTimer.Stop();
            _toastAutoCollapseTimer.Stop();
            _hubHoverOpenTimer.Stop();
            _hubHoverCloseTimer.Stop();
            _sliderWheelTimer.Stop();
            _visualizerStoryboard?.Stop();
        };
    }

    private void OnDisplaySettingsChanged(object? sender, EventArgs e)
    {
        Dispatcher.BeginInvoke(() =>
        {
            PositionTopBar();
            _taskbarGuard.UpdateDpiScale(this);
            _taskbarGuard.RefreshForDisplayChange();
        }, DispatcherPriority.Loaded);
    }

    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        PositionTopBar();
        _taskbarGuard.UpdateDpiScale(this);
        _taskbarGuard.RefreshForDisplayChange();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        DwmHelper.ConfigureShellOverlayWindow(this, enableAcrylic: false, noActivate: true);
        _windowTracker.AddIgnoredWindow(new WindowInteropHelper(this).Handle);
        PositionTopBar();
        SourceInitializedReady?.Invoke(this, this);
    }

    private void PositionTopBar()
    {
        DwmHelper.GetPrimaryMonitorDipBounds(
            this,
            out double screenLeft,
            out double screenTop,
            out double screenWidth,
            out _);

        Left = screenLeft;
        Top = screenTop;
        Width = screenWidth;
    }

    private void UpdateClockText()
    {
        DateTime now = DateTime.Now;
        TopClockText.Text = now.ToString("ddd, dd MMM  HH:mm", CultureInfo.InvariantCulture);
        CalendarTodayHeaderText.Text = now.ToString("dddd, dd MMMM yyyy", CultureInfo.InvariantCulture);
    }

    private void UpdateCpuRamMetrics()
    {
        if (NativeMethods.GetSystemTimes(out ulong idle, out ulong kernel, out ulong user))
        {
            if (_prevKernelTime != 0 || _prevUserTime != 0)
            {
                ulong idleDelta = idle - _prevIdleTime;
                ulong kernelDelta = kernel - _prevKernelTime;
                ulong userDelta = user - _prevUserTime;
                ulong total = kernelDelta + userDelta;
                if (total > 0)
                {
                    int cpuPercent = (int)Math.Clamp(Math.Round(((total - idleDelta) * 100.0) / total), 0, 100);
                    TopCpuText.Text = $"CPU {cpuPercent}%";
                }
            }

            _prevIdleTime = idle;
            _prevKernelTime = kernel;
            _prevUserTime = user;
        }

        var memStatus = new NativeMethods.MEMORYSTATUSEX
        {
            dwLength = (uint)Marshal.SizeOf<NativeMethods.MEMORYSTATUSEX>()
        };
        if (NativeMethods.GlobalMemoryStatusEx(ref memStatus))
        {
            TopRamText.Text = $"RAM {memStatus.dwMemoryLoad}%";
        }
    }

    private DynamicIslandState GetDefaultIslandState() =>
        _isPlayingMedia ? DynamicIslandState.MediaPill : DynamicIslandState.Idle;

    private void TransitionDynamicIsland(DynamicIslandState newState)
    {
        bool wasCollapsed = _islandState is DynamicIslandState.Idle or DynamicIslandState.MediaPill;
        bool isExpanding = newState is DynamicIslandState.NotificationToast or DynamicIslandState.ExpandedHub;

        _islandState = newState;
        _isCenterHubOpen = newState == DynamicIslandState.ExpandedHub;
        DynamicIslandShell.Visibility = Visibility.Visible;
        DynamicIslandShell.Opacity = 1.0;

        IdleIslandView.Visibility = newState == DynamicIslandState.Idle ? Visibility.Visible : Visibility.Collapsed;
        MediaPillView.Visibility = newState == DynamicIslandState.MediaPill ? Visibility.Visible : Visibility.Collapsed;
        ToastView.Visibility = newState == DynamicIslandState.NotificationToast ? Visibility.Visible : Visibility.Collapsed;
        ExpandedHubView.Visibility = newState == DynamicIslandState.ExpandedHub ? Visibility.Visible : Visibility.Collapsed;

        if (newState == DynamicIslandState.MediaPill)
        {
            _visualizerStoryboard?.Begin(this, true);
        }
        else
        {
            _visualizerStoryboard?.Stop(this);
        }

        double targetWidth = newState switch
        {
            DynamicIslandState.ExpandedHub => 530,
            DynamicIslandState.NotificationToast => _activeToastIsAlarm ? 464 : 424,
            DynamicIslandState.MediaPill => 292,
            _ => 140
        };
        double targetHeight = newState switch
        {
            DynamicIslandState.ExpandedHub => _showingAlertsTab ? 252 : 148,
            DynamicIslandState.NotificationToast => 84,
            DynamicIslandState.MediaPill => 26,
            _ => 30
        };
        Thickness targetMargin = newState == DynamicIslandState.MediaPill
            ? new Thickness(0, 2, 0, 0)
            : new Thickness(0);

        DynamicIslandShell.CornerRadius = isExpanding
            ? new CornerRadius(0, 0, newState == DynamicIslandState.ExpandedHub ? 24 : 20, newState == DynamicIslandState.ExpandedHub ? 24 : 20)
            : (newState == DynamicIslandState.MediaPill
                ? new CornerRadius(13)
                : new CornerRadius(15));

        LeftTopEarFlare.Visibility = isExpanding ? Visibility.Visible : Visibility.Collapsed;
        RightTopEarFlare.Visibility = isExpanding ? Visibility.Visible : Visibility.Collapsed;

        DynamicIslandShell.BorderBrush = Brushes.Transparent;
        DynamicIslandShell.BorderThickness = new Thickness(0);
        if (newState == DynamicIslandState.Idle)
        {
            DynamicIslandShell.Background = Brushes.Transparent;
            DynamicIslandShell.Effect = null;
        }
        else
        {
            DynamicIslandShell.Background = (Brush)FindResource("NotchBackgroundBrush");
            DynamicIslandShell.Effect = isExpanding ? TryFindResource("NotchShadowEffect") as Effect : null;
        }

        var morphDuration = TimeSpan.FromMilliseconds(isExpanding || !wasCollapsed ? 300 : 240);
        var morphEasing = new ExponentialEase { EasingMode = EasingMode.EaseOut, Exponent = 4.2 };

        DynamicIslandShell.BeginAnimation(WidthProperty, new DoubleAnimation(targetWidth, morphDuration) { EasingFunction = morphEasing });
        DynamicIslandShell.BeginAnimation(HeightProperty, new DoubleAnimation(targetHeight, morphDuration) { EasingFunction = morphEasing });
        DynamicIslandShell.BeginAnimation(MarginProperty, new ThicknessAnimation(targetMargin, morphDuration) { EasingFunction = morphEasing });

        var contentEase = new QuadraticEase { EasingMode = EasingMode.EaseOut };
        if (isExpanding)
        {
            IslandContentContainer.BeginAnimation(OpacityProperty, new DoubleAnimation(0.45, 1.0, TimeSpan.FromMilliseconds(220)) { EasingFunction = contentEase });
            IslandContentScale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.95, 1.0, TimeSpan.FromMilliseconds(280)) { EasingFunction = morphEasing });
            IslandContentScale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.88, 1.0, TimeSpan.FromMilliseconds(280)) { EasingFunction = morphEasing });
        }
        else
        {
            IslandContentContainer.BeginAnimation(OpacityProperty, new DoubleAnimation(1.0, TimeSpan.FromMilliseconds(160)) { EasingFunction = contentEase });
            IslandContentScale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(1.0, TimeSpan.FromMilliseconds(200)) { EasingFunction = morphEasing });
            IslandContentScale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(1.0, TimeSpan.FromMilliseconds(200)) { EasingFunction = morphEasing });
        }
    }

    private void SetCenterHubOpen(bool open, bool showAlerts = false)
    {
        if (open)
        {
            if (_islandState == DynamicIslandState.ExpandedHub)
            {
                SetExpandedTab(showAlerts);
                return;
            }
            SetTrayPopupOpen(false);
            SetVolumePopupOpen(false);
            SetBrightnessPopupOpen(false);
            SetClockPopupOpen(false);
            _toastAutoCollapseTimer.Stop();
            SetExpandedTab(showAlerts, animateResize: false);
            _notificationService.MarkAllRead();
            TransitionDynamicIsland(DynamicIslandState.ExpandedHub);
        }
        else
        {
            if (_islandState == DynamicIslandState.ExpandedHub)
            {
                TransitionDynamicIsland(GetDefaultIslandState());
            }
        }
    }

    private void SetTrayPopupOpen(bool open)
    {
        if (_isTrayPopupOpen == open && (TrayPopupShell.Visibility == Visibility.Visible) == open) return;
        _isTrayPopupOpen = open;
        TopTrayChevronPath.Data = (Geometry)FindResource(open ? "IconChevronUp" : "IconChevronDown");

        if (open)
        {
            _lastPopupOpenedTick = Environment.TickCount64;
            SetCenterHubOpen(false);
            SetVolumePopupOpen(false);
            SetBrightnessPopupOpen(false);
            SetClockPopupOpen(false);

            AlignPopupUnderButton(TrayPopupShell, TopTrayDropdownButton, 180);

            IntPtr hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd != IntPtr.Zero)
            {
                NativeMethods.SetForegroundWindow(hwnd);
            }
            Activate();

            TrayPopupShell.BeginAnimation(OpacityProperty, null);
            TrayPopupShell.Visibility = Visibility.Visible;
            TrayPopupShell.BeginAnimation(OpacityProperty, new DoubleAnimation(0.0, 1.0, TimeSpan.FromMilliseconds(160))
            {
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
            });
            TrayPopupSlideTransform.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(-8.0, 0.0, TimeSpan.FromMilliseconds(200))
            {
                EasingFunction = new ExponentialEase { EasingMode = EasingMode.EaseOut, Exponent = 4 }
            });
        }
        else
        {
            var fadeOut = new DoubleAnimation(0.0, TimeSpan.FromMilliseconds(140));
            fadeOut.Completed += (_, _) =>
            {
                if (!_isTrayPopupOpen)
                {
                    TrayPopupShell.Visibility = Visibility.Collapsed;
                }
            };
            TrayPopupShell.BeginAnimation(OpacityProperty, fadeOut);
        }
    }

    private void AlignPopupUnderButton(Border popup, FrameworkElement button, double estimatedPopupWidth)
    {
        try
        {
            Point btnCenter = button.TranslatePoint(new Point(button.ActualWidth / 2.0, 0), this);
            double rightEdgeDistance = Math.Max(10.0, ActualWidth - btnCenter.X - (estimatedPopupWidth / 2.0));
            popup.Margin = new Thickness(0, 34, rightEdgeDistance, 0);
        }
        catch { }
    }

    private void SetVolumePopupOpen(bool open)
    {
        if (_isVolumePopupOpen == open && (VolumePopupShell.Visibility == Visibility.Visible) == open) return;
        _isVolumePopupOpen = open;

        if (open)
        {
            _lastPopupOpenedTick = Environment.TickCount64;
            SetCenterHubOpen(false);
            SetTrayPopupOpen(false);
            SetBrightnessPopupOpen(false);
            SetClockPopupOpen(false);
            _ = Task.Run(_systemStatusService.RefreshAndNotify);

            AlignPopupUnderButton(VolumePopupShell, TopVolumeButton, VolumePopupShell.Width);

            IntPtr hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd != IntPtr.Zero)
            {
                NativeMethods.SetForegroundWindow(hwnd);
            }
            Activate();

            VolumePopupShell.BeginAnimation(OpacityProperty, null);
            VolumePopupShell.Visibility = Visibility.Visible;
            VolumePopupShell.BeginAnimation(OpacityProperty, new DoubleAnimation(0.0, 1.0, TimeSpan.FromMilliseconds(160))
            {
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
            });
            VolumePopupSlideTransform.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(-8.0, 0.0, TimeSpan.FromMilliseconds(200))
            {
                EasingFunction = new ExponentialEase { EasingMode = EasingMode.EaseOut, Exponent = 4 }
            });
        }
        else
        {
            var fadeOut = new DoubleAnimation(0.0, TimeSpan.FromMilliseconds(140));
            fadeOut.Completed += (_, _) =>
            {
                if (!_isVolumePopupOpen)
                {
                    VolumePopupShell.Visibility = Visibility.Collapsed;
                }
            };
            VolumePopupShell.BeginAnimation(OpacityProperty, fadeOut);
        }
    }

    private void SetBrightnessPopupOpen(bool open)
    {
        if (_isBrightnessPopupOpen == open && (BrightnessPopupShell.Visibility == Visibility.Visible) == open) return;
        _isBrightnessPopupOpen = open;

        if (open)
        {
            _lastPopupOpenedTick = Environment.TickCount64;
            SetCenterHubOpen(false);
            SetTrayPopupOpen(false);
            SetVolumePopupOpen(false);
            SetClockPopupOpen(false);

            AlignPopupUnderButton(BrightnessPopupShell, TopBrightnessButton, BrightnessPopupShell.Width);

            IntPtr hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd != IntPtr.Zero)
            {
                NativeMethods.SetForegroundWindow(hwnd);
            }
            Activate();

            BrightnessPopupShell.BeginAnimation(OpacityProperty, null);
            BrightnessPopupShell.Visibility = Visibility.Visible;
            BrightnessPopupShell.BeginAnimation(OpacityProperty, new DoubleAnimation(0.0, 1.0, TimeSpan.FromMilliseconds(160))
            {
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
            });
            BrightnessPopupSlideTransform.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(-8.0, 0.0, TimeSpan.FromMilliseconds(200))
            {
                EasingFunction = new ExponentialEase { EasingMode = EasingMode.EaseOut, Exponent = 4 }
            });
        }
        else
        {
            var fadeOut = new DoubleAnimation(0.0, TimeSpan.FromMilliseconds(140));
            fadeOut.Completed += (_, _) =>
            {
                if (!_isBrightnessPopupOpen)
                {
                    BrightnessPopupShell.Visibility = Visibility.Collapsed;
                }
            };
            BrightnessPopupShell.BeginAnimation(OpacityProperty, fadeOut);
        }
    }

    private void SetClockPopupOpen(bool open)
    {
        if (_isClockPopupOpen == open && (ClockCalendarPopupShell.Visibility == Visibility.Visible) == open) return;
        _isClockPopupOpen = open;

        if (open)
        {
            _lastPopupOpenedTick = Environment.TickCount64;
            SetCenterHubOpen(false);
            SetTrayPopupOpen(false);
            SetVolumePopupOpen(false);
            SetBrightnessPopupOpen(false);
            _displayedCalendarMonth = new DateTime(DateTime.Now.Year, DateTime.Now.Month, 1);
            RenderMonthCalendar(_displayedCalendarMonth);
            _notificationService.MarkAllRead();

            IntPtr hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd != IntPtr.Zero)
            {
                NativeMethods.SetForegroundWindow(hwnd);
            }
            Activate();

            ClockCalendarPopupShell.BeginAnimation(OpacityProperty, null);
            ClockCalendarPopupShell.Visibility = Visibility.Visible;
            ClockCalendarPopupShell.BeginAnimation(OpacityProperty, new DoubleAnimation(0.0, 1.0, TimeSpan.FromMilliseconds(180))
            {
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
            });
            ClockPopupSlideTransform.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(-8.0, 0.0, TimeSpan.FromMilliseconds(220))
            {
                EasingFunction = new ExponentialEase { EasingMode = EasingMode.EaseOut, Exponent = 4 }
            });
        }
        else
        {
            var fadeOut = new DoubleAnimation(0.0, TimeSpan.FromMilliseconds(150));
            fadeOut.Completed += (_, _) =>
            {
                if (!_isClockPopupOpen)
                {
                    ClockCalendarPopupShell.Visibility = Visibility.Collapsed;
                }
            };
            ClockCalendarPopupShell.BeginAnimation(OpacityProperty, fadeOut);
        }
    }

    private void RenderMonthCalendar(DateTime monthStart)
    {
        CalendarMonthYearText.Text = monthStart.ToString("MMMM yyyy", CultureInfo.InvariantCulture);
        MonthCalendarGrid.Children.Clear();

        DateTime today = DateTime.Today;
        int firstDow = ((int)monthStart.DayOfWeek + 6) % 7; // Monday = 0 .. Sunday = 6
        DateTime gridStart = monthStart.AddDays(-firstDow);

        for (int i = 0; i < 42; i++)
        {
            DateTime cellDate = gridStart.AddDays(i);
            bool isCurrentMonth = cellDate.Month == monthStart.Month && cellDate.Year == monthStart.Year;
            bool isToday = cellDate.Date == today;

            var cellBorder = new Border
            {
                Width = 26,
                Height = 24,
                CornerRadius = new CornerRadius(12),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Background = isToday
                    ? (Brush)FindResource("AccentBrush")
                    : Brushes.Transparent
            };

            var dayText = new TextBlock
            {
                Text = cellDate.Day.ToString(CultureInfo.InvariantCulture),
                FontFamily = (FontFamily)FindResource("AppFontFamily"),
                FontSize = 11.5,
                FontWeight = isToday ? FontWeights.Bold : FontWeights.Medium,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = isToday
                    ? Brushes.White
                    : (isCurrentMonth
                        ? (Brush)FindResource("PrimaryTextBrush")
                        : new SolidColorBrush(Color.FromArgb(0x55, 0xFF, 0xFF, 0xFF)))
            };

            cellBorder.Child = dayText;
            MonthCalendarGrid.Children.Add(cellBorder);
        }
    }

    private void CalendarPrevMonth_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        _displayedCalendarMonth = _displayedCalendarMonth.AddMonths(-1);
        RenderMonthCalendar(_displayedCalendarMonth);
    }

    private void CalendarNextMonth_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        _displayedCalendarMonth = _displayedCalendarMonth.AddMonths(1);
        RenderMonthCalendar(_displayedCalendarMonth);
    }

    private void CalendarTodayHeader_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        _displayedCalendarMonth = new DateTime(DateTime.Now.Year, DateTime.Now.Month, 1);
        RenderMonthCalendar(_displayedCalendarMonth);
    }

    private void CloseTransientPopups(bool preserveTrayPopup = false)
    {
        if (_activeContextMenu is { IsOpen: true } menu)
        {
            menu.IsOpen = false;
        }
        _activeContextMenu = null;

        if (_isCenterHubOpen)
        {
            SetCenterHubOpen(false);
        }

        if (_isTrayPopupOpen && !preserveTrayPopup)
        {
            SetTrayPopupOpen(false);
        }

        if (_isVolumePopupOpen)
        {
            SetVolumePopupOpen(false);
        }

        if (_isBrightnessPopupOpen)
        {
            SetBrightnessPopupOpen(false);
        }

        if (_isClockPopupOpen)
        {
            SetClockPopupOpen(false);
        }
    }

    private void OnFullscreenChanged(bool isFullscreen)
    {
        _isFullscreenHidden = isFullscreen;
        if (_isFullscreenHidden)
        {
            CloseTransientPopups();
            Visibility = Visibility.Collapsed;
        }
        else
        {
            Visibility = Visibility.Visible;
            PositionTopBar();
            DwmHelper.EnsureTopmost(this);
        }
    }

    private void ApplyMediaSnapshot(MediaSnapshot snapshot)
    {
        _isPlayingMedia = snapshot.IsPlaying;

        PillAlbumArt.Source = snapshot.Thumbnail;
        PillTrackTitle.Text = snapshot.Title;
        PillPlayPausePath.Data = (Geometry)FindResource(snapshot.IsPlaying ? "IconPause" : "IconPlay");

        ExpandedAlbumArt.Source = snapshot.Thumbnail;
        ExpandedTrackTitle.Text = snapshot.Title;
        ExpandedTrackArtist.Text = snapshot.Artist;
        PlayPauseIconPath.Data = (Geometry)FindResource(snapshot.IsPlaying ? "IconPause" : "IconPlay");

        if (_islandState != DynamicIslandState.NotificationToast &&
            _islandState != DynamicIslandState.ExpandedHub)
        {
            TransitionDynamicIsland(GetDefaultIslandState());
        }
    }

    private void ApplySystemStatus(SystemStatusSnapshot status)
    {
        long nowTick = Environment.TickCount64;
        bool volumeInteracting = _isDraggingVolumeSlider || _isAnimatingVolumeWheel || (nowTick - _lastVolumeInteractTick <= 300);
        if (!volumeInteracting)
        {
            _isUpdatingVolumeSlider = true;
            PopupVolumeSlider.Value = status.VolumePercent;
            _isUpdatingVolumeSlider = false;

            string volLabel = status.IsMuted ? "Muted" : $"{status.VolumePercent}%";
            PopupVolumePercentText.Text = volLabel;
            TopVolumeText.Text = volLabel;

            var speakerGeom = (Geometry)FindResource(status.IsMuted || status.VolumePercent == 0 ? "IconSpeakerMuted" : "IconSpeaker");
            PopupMuteIconPath.Data = speakerGeom;
            TopVolumeIcon.Data = speakerGeom;
        }

        bool brightnessInteracting = _isDraggingBrightnessSlider || _isAnimatingBrightnessWheel || (nowTick - _lastBrightnessInteractTick <= 300);
        if (!brightnessInteracting)
        {
            _isUpdatingBrightnessSlider = true;
            PopupBrightnessSlider.Value = status.BrightnessPercent;
            _isUpdatingBrightnessSlider = false;

            string brightLabel = $"{status.BrightnessPercent}%";
            PopupBrightnessPercentText.Text = brightLabel;
            TopBrightnessButton.ToolTip = $"Brightness: {brightLabel} (Click: Slider | Scroll: Adjust | Right-Click: Display Settings)";
        }

        if (status.HasBattery)
        {
            double fillW = Math.Clamp(status.BatteryPercent / 100.0 * 11.4, 1.5, 11.4);
            TopBatteryFillBar.Width = fillW;
            TopBatteryText.Text = $"{status.BatteryPercent}%";

            if (status.IsCharging)
            {
                TopBatteryFillBar.Background = new SolidColorBrush(Color.FromRgb(0x22, 0xC5, 0x5E));
                TopBatteryBoltIcon.Visibility = Visibility.Visible;
                TopBatteryButton.ToolTip = $"Battery: {status.BatteryPercent}% (Charging — Click: Power Settings)";
            }
            else if (status.BatteryPercent <= 20)
            {
                TopBatteryFillBar.Background = new SolidColorBrush(Color.FromRgb(0xEF, 0x44, 0x44));
                TopBatteryBoltIcon.Visibility = Visibility.Collapsed;
                TopBatteryButton.ToolTip = $"Battery: {status.BatteryPercent}% (Low — Click: Power Settings)";
            }
            else
            {
                TopBatteryFillBar.Background = Brushes.White;
                TopBatteryBoltIcon.Visibility = Visibility.Collapsed;
                TopBatteryButton.ToolTip = $"Battery: {status.BatteryPercent}% (Click: Power Settings)";
            }
        }
        else
        {
            TopBatteryFillBar.Width = 11.4;
            TopBatteryFillBar.Background = new SolidColorBrush(Color.FromRgb(0x22, 0xC5, 0x5E));
            TopBatteryBoltIcon.Visibility = Visibility.Visible;
            TopBatteryText.Text = "AC";
            TopBatteryButton.ToolTip = "AC Power (Click: Power Settings)";
        }

        // Real-Time Network Status (Wi-Fi Signal Strength Bars vs Ethernet vs Offline)
        if (!status.IsNetworkConnected || status.NetworkKind == NetworkConnectionKind.Offline)
        {
            TopWifiBarsPanel.Visibility = Visibility.Collapsed;
            TopEthernetIcon.Visibility = Visibility.Collapsed;
            TopWifiOfflineIcon.Visibility = Visibility.Visible;
            TopWifiButton.ToolTip = "Network: Offline (Click: Flyout | Right-Click: Network Settings)";
        }
        else if (status.NetworkKind == NetworkConnectionKind.Ethernet)
        {
            TopWifiBarsPanel.Visibility = Visibility.Collapsed;
            TopWifiOfflineIcon.Visibility = Visibility.Collapsed;
            TopEthernetIcon.Visibility = Visibility.Visible;
            TopWifiButton.ToolTip = $"{status.NetworkLabel} (Click: Flyout | Right-Click: Network Settings)";
        }
        else
        {
            TopEthernetIcon.Visibility = Visibility.Collapsed;
            TopWifiOfflineIcon.Visibility = Visibility.Collapsed;
            TopWifiBarsPanel.Visibility = Visibility.Visible;

            WifiInnerArc.Opacity = 1.0;
            WifiMidArc.Opacity = status.WifiSignalBars >= 2 ? 1.0 : 0.25;
            WifiOuterArc.Opacity = status.WifiSignalBars >= 3 ? 1.0 : 0.25;
            TopWifiButton.ToolTip = $"{status.NetworkLabel} (Click: Flyout | Right-Click: Network Settings)";
        }

        // Real-Time Bluetooth Control Hub Tile Status
        bool btOn = status.IsBluetoothEnabled;
        QuickBluetoothText.Text = btOn ? "BT On" : "Bluetooth";
        QuickBluetoothIcon.Stroke = (Brush)FindResource(btOn ? "AccentBrush" : "PrimaryTextBrush");
        QuickBluetoothButton.Background = new SolidColorBrush(btOn
            ? Color.FromArgb(0x36, 0x3B, 0x82, 0xF6)
            : Color.FromArgb(0x18, 0xFF, 0xFF, 0xFF));
    }

    private void OnNotificationReceived(NotificationItem item)
    {
        RefreshNotificationDrawer();

        _activeToastNotificationId = item.Id;
        _activeToastIsAlarm = item.IsAlarm;
        _lastToastItem = item;

        ToastAppIcon.Source = item.AppIcon;
        ToastFallbackIcon.Visibility = item.AppIcon == null ? Visibility.Visible : Visibility.Collapsed;
        ToastAppName.Text = item.AppName;
        ToastTimeText.Text = item.TimeText;
        ToastTitleText.Text = item.Title;
        ToastBodyText.Text = item.Body;
        ToastAlarmActionButton.Visibility = item.IsAlarm ? Visibility.Visible : Visibility.Collapsed;

        SetCenterHubOpen(false);
        SetTrayPopupOpen(false);
        SetVolumePopupOpen(false);
        SetBrightnessPopupOpen(false);
        SetClockPopupOpen(false);
        TransitionDynamicIsland(DynamicIslandState.NotificationToast);

        _toastAutoCollapseTimer.Stop();
        _toastAutoCollapseTimer.Interval = TimeSpan.FromMilliseconds(item.IsAlarm ? 15000 : 4500);
        if (!DynamicIslandShell.IsMouseOver)
        {
            _toastAutoCollapseTimer.Start();
        }
    }

    private void RefreshNotificationDrawer()
    {
        var recentTray = _notificationService.GetRecentNotifications(8);
        var recentClock = _notificationService.GetRecentNotifications(15);
        NotificationHistoryList.ItemsSource = recentTray;
        ClockNotificationList.ItemsSource = recentClock;

        if (recentTray.Count == 0)
        {
            string emptyMsg = _notificationService.IsListenerActive
                ? "No recent notifications."
                : _notificationService.StatusMessage;
            NotificationEmptyText.Text = emptyMsg;
            NotificationEmptyText.Visibility = Visibility.Visible;
            ClockNotificationEmptyText.Visibility = Visibility.Visible;
        }
        else
        {
            NotificationEmptyText.Visibility = Visibility.Collapsed;
            ClockNotificationEmptyText.Visibility = Visibility.Collapsed;
        }

        int count = _notificationService.HistoryCount;
        AlertsTabLabel.Text = count > 0 ? $"Tray ({count})" : "Tray";
        ClockNotifCountBadge.Text = count.ToString(CultureInfo.InvariantCulture);

        bool dnd = _notificationService.IsDoNotDisturb;
        QuickDndText.Text = dnd ? "DND On" : "DND";
        QuickDndIcon.Fill = (Brush)FindResource(dnd ? "AccentBrush" : "PrimaryTextBrush");
        QuickDndButton.Background = new SolidColorBrush(dnd
            ? Color.FromArgb(0x36, 0x3B, 0x82, 0xF6)
            : Color.FromArgb(0x18, 0xFF, 0xFF, 0xFF));
    }

    private void TopBarBorder_RightClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement fe) return;
        ShowGlassContextMenu(fe, new[]
        {
            ("Restore Default Windows & Exit", (Action)(() =>
            {
                _taskbarGuard.ShowSystemTaskbar();
                Application.Current.Shutdown();
            })),
            ("Toggle Windows Taskbar (Ctrl+Alt+Shift+R)", (Action)(() => _taskbarGuard.ToggleSystemTaskbar())),
            ("Open Control Hub", (Action)(() => SetCenterHubOpen(true))),
            ("Task Manager", (Action)(() => LaunchShellUri("taskmgr.exe"))),
            ("Windows Settings", (Action)(() => LaunchShellUri("ms-settings:")))
        });
        e.Handled = true;
    }

    // --- Left Top Bar Shortcuts & Windows Start ---
    private static void SendEscapeKey()
    {
        NativeMethods.keybd_event(NativeMethods.VK_ESCAPE, 0, 0, UIntPtr.Zero);
        NativeMethods.keybd_event(NativeMethods.VK_ESCAPE, 0, NativeMethods.KEYEVENTF_KEYUP, UIntPtr.Zero);
    }

    private void WindowsStart_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        IntPtr fg = NativeMethods.GetForegroundWindow();
        _suppressNextStartClick = WindowTracker.IsStartMenuWindow(fg) || _isStartMenuOpen;
    }

    private void WindowsStart_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        CloseTransientPopups();

        if (_suppressNextStartClick || _isStartMenuOpen ||
            (Environment.TickCount64 - _lastStartDismissByButtonTick < 450))
        {
            _suppressNextStartClick = false;
            _isStartMenuOpen = false;
            _lastStartDismissByButtonTick = 0;
            SendEscapeKey();
            return;
        }

        _isStartMenuOpen = true;
        _startOpenedTick = Environment.TickCount64;
        NativeMethods.keybd_event(NativeMethods.VK_LWIN, 0, 0, UIntPtr.Zero);
        NativeMethods.keybd_event(NativeMethods.VK_LWIN, 0, NativeMethods.KEYEVENTF_KEYUP, UIntPtr.Zero);
    }

    private void WindowsStart_RightClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement fe) return;
        ShowGlassContextMenu(fe, new[]
        {
            ("Settings", (Action)(() => LaunchShellUri("ms-settings:"))),
            ("Task Manager", (Action)(() => LaunchShellUri("taskmgr.exe"))),
            ("Toggle Windows Taskbar (Ctrl+Alt+Shift+R)", (Action)(() => _taskbarGuard.ToggleSystemTaskbar())),
            ("Exit WinMacShell", (Action)(() => Application.Current.Shutdown()))
        });
        e.Handled = true;
    }

    private void ShortcutExplorer_Click(object sender, RoutedEventArgs e) => LaunchShellUri("explorer.exe");
    private void ShortcutSettings_Click(object sender, RoutedEventArgs e) => LaunchShellUri("ms-settings:");
    private void ShortcutTaskManager_Click(object sender, RoutedEventArgs e) => LaunchShellUri("taskmgr.exe");
    private void ShortcutTerminal_Click(object sender, RoutedEventArgs e) => LaunchShellUri("wt.exe");

    // --- Interactive Wi-Fi, Brightness, Volume, Battery, & Hub Toggle ---
    private void TopWifi_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        IntPtr fg = NativeMethods.GetForegroundWindow();
        _suppressNextWifiClick = WindowTracker.IsQuickSettingsWindow(fg) || _isWifiFlyoutOpen;
    }

    private void TopWifi_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        CloseTransientPopups();

        if (_suppressNextWifiClick || _isWifiFlyoutOpen ||
            (Environment.TickCount64 - _lastWifiDismissByButtonTick < 450))
        {
            _suppressNextWifiClick = false;
            _isWifiFlyoutOpen = false;
            _lastWifiDismissByButtonTick = 0;
            SendEscapeKey();
            return;
        }

        _isWifiFlyoutOpen = true;
        _wifiOpenedTick = Environment.TickCount64;
        LaunchShellUri("ms-availablenetworks:");
    }

    private void TopBrightness_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        SetBrightnessPopupOpen(!_isBrightnessPopupOpen);
    }

    private void TopBrightness_RightClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement fe) return;
        ShowGlassContextMenu(fe, new[]
        {
            ("Display Settings", (Action)(() => LaunchShellUri("ms-settings:display"))),
            ("Night Light Settings", (Action)(() => LaunchShellUri("ms-settings:nightlight"))),
            ("Power & Battery", (Action)(() => LaunchShellUri("ms-settings:powersleep")))
        });
        e.Handled = true;
    }

    private void TopBrightness_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (_isBrightnessPopupOpen)
        {
            BrightnessPopup_MouseWheel(sender, e);
            return;
        }

        double delta = (e.Delta / 120.0) * 4.0;
        if (Math.Abs(delta) < 1.0 && e.Delta != 0)
        {
            delta = e.Delta > 0 ? 1.0 : -1.0;
        }

        double current = _systemStatusService.CurrentStatus.BrightnessPercent;
        double next = Math.Clamp(current + delta, 5, 100);
        _lastBrightnessInteractTick = Environment.TickCount64;
        _systemStatusService.SetDisplayBrightness(next);
        e.Handled = true;
    }

    private void BrightnessPopup_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        double delta = (e.Delta / 120.0) * 3.0;
        double baseVal = _isAnimatingBrightnessWheel ? _targetBrightnessWheelValue : PopupBrightnessSlider.Value;
        _targetBrightnessWheelValue = Math.Clamp(baseVal + delta, 5, 100);
        _isAnimatingBrightnessWheel = true;
        _lastBrightnessInteractTick = Environment.TickCount64;
        if (!_sliderWheelTimer.IsEnabled)
        {
            _sliderWheelTimer.Start();
        }
        e.Handled = true;
    }

    private void BrightnessSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_isUpdatingBrightnessSlider || !IsLoaded) return;
        _lastBrightnessInteractTick = Environment.TickCount64;
        int rounded = Math.Clamp((int)Math.Round(e.NewValue), 5, 100);
        string brightLabel = $"{rounded}%";
        PopupBrightnessPercentText.Text = brightLabel;
        TopBrightnessButton.ToolTip = $"Brightness: {brightLabel} (Click: Slider | Scroll: Adjust | Right-Click: Display Settings)";
        _systemStatusService.SetDisplayBrightness(e.NewValue);
    }

    private async void QuickBluetooth_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        QuickBluetoothButton.IsEnabled = false;
        try
        {
            bool toggled = await _systemStatusService.ToggleBluetoothAsync();
            if (!toggled)
            {
                CloseTransientPopups();
                LaunchShellUri("ms-settings:bluetooth");
            }
        }
        finally
        {
            QuickBluetoothButton.IsEnabled = true;
        }
    }

    private void QuickBluetooth_RightClick(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        CloseTransientPopups();
        LaunchShellUri("ms-settings:bluetooth");
    }

    private void QuickDnd_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        _notificationService.ToggleDoNotDisturb();
    }

    private void QuickDnd_RightClick(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        CloseTransientPopups();
        LaunchShellUri("ms-settings:quiethours");
    }

    private void QuickProject_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        CloseTransientPopups();
        LaunchShellUri("DisplaySwitch.exe");
    }

    private void QuickProject_RightClick(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        CloseTransientPopups();
        LaunchShellUri("ms-settings:display");
    }

    private void QuickNightLight_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        CloseTransientPopups();
        LaunchShellUri("ms-settings:nightlight");
    }

    private void QuickHotspot_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        CloseTransientPopups();
        LaunchShellUri("ms-settings:network-mobilehotspot");
    }

    private void QuickLockScreen_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        CloseTransientPopups();
        NativeMethods.LockWorkStation();
    }

    private void QuickSnip_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        CloseTransientPopups();
        LaunchShellUri("ms-screenclip:");
    }

    private void QuickSnip_RightClick(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        CloseTransientPopups();
        LaunchShellUri("snippingtool.exe");
    }

    private void QuickWinA_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        CloseTransientPopups();
        NativeMethods.keybd_event(NativeMethods.VK_LWIN, 0, 0, UIntPtr.Zero);
        NativeMethods.keybd_event(NativeMethods.VK_A, 0, 0, UIntPtr.Zero);
        NativeMethods.keybd_event(NativeMethods.VK_A, 0, NativeMethods.KEYEVENTF_KEYUP, UIntPtr.Zero);
        NativeMethods.keybd_event(NativeMethods.VK_LWIN, 0, NativeMethods.KEYEVENTF_KEYUP, UIntPtr.Zero);
    }

    private void TopWifi_RightClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement fe) return;
        ShowGlassContextMenu(fe, new[]
        {
            ("Open Wi-Fi Flyout", (Action)(() => LaunchShellUri("ms-availablenetworks:"))),
            ("Wi-Fi Settings", (Action)(() => LaunchShellUri("ms-settings:network-wifi"))),
            ("Network & Internet", (Action)(() => LaunchShellUri("ms-settings:network"))),
            ("Network Adapters", (Action)(() => LaunchShellUri("ncpa.cpl")))
        });
        e.Handled = true;
    }

    private void TopVolume_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        SetVolumePopupOpen(!_isVolumePopupOpen);
    }

    private void TopVolume_RightClick(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        _systemStatusService.ToggleMute();
    }

    private void TopVolume_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (_isVolumePopupOpen)
        {
            VolumePopup_MouseWheel(sender, e);
            return;
        }

        double delta = (e.Delta / 120.0) * 4.0;
        if (Math.Abs(delta) < 1.0 && e.Delta != 0)
        {
            delta = e.Delta > 0 ? 1.0 : -1.0;
        }

        double current = _systemStatusService.CurrentStatus.VolumePercent;
        double next = Math.Clamp(current + delta, 0, 100);
        _lastVolumeInteractTick = Environment.TickCount64;
        _systemStatusService.SetMasterVolume(next);
        e.Handled = true;
    }

    private void VolumePopup_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        double delta = (e.Delta / 120.0) * 3.0;
        double baseVal = _isAnimatingVolumeWheel ? _targetVolumeWheelValue : PopupVolumeSlider.Value;
        _targetVolumeWheelValue = Math.Clamp(baseVal + delta, 0, 100);
        _isAnimatingVolumeWheel = true;
        _lastVolumeInteractTick = Environment.TickCount64;
        if (!_sliderWheelTimer.IsEnabled)
        {
            _sliderWheelTimer.Start();
        }
        e.Handled = true;
    }

    private void TopBattery_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        CloseTransientPopups();
        LaunchShellUri("ms-settings:powersleep");
    }

    private void TopBattery_RightClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement fe) return;
        ShowGlassContextMenu(fe, new[]
        {
            ("Power & Battery Settings", (Action)(() => LaunchShellUri("ms-settings:powersleep"))),
            ("Battery Usage Details", (Action)(() => LaunchShellUri("ms-settings:batterysaver-usagedetails"))),
            ("Power Options (Control Panel)", (Action)(() => LaunchShellUri("powercfg.cpl")))
        });
        e.Handled = true;
    }

    private void ToggleHub_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        _hubHoverOpenTimer.Stop();
        _hubHoverCloseTimer.Stop();
        SetClockPopupOpen(!_isClockPopupOpen);
    }

    private void ShowGlassContextMenu(FrameworkElement target, IEnumerable<(string Header, Action Action)> items)
    {
        CloseTransientPopups();

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
        var itemStyle = (Style)FindResource("GlassMenuItemStyle");

        foreach (var (header, action) in items)
        {
            var mi = new MenuItem { Header = header, Style = itemStyle };
            mi.Click += (_, _) => action();
            menu.Items.Add(mi);
        }

        menu.Closed += (_, _) =>
        {
            if (ReferenceEquals(_activeContextMenu, menu))
            {
                _activeContextMenu = null;
            }
        };

        _activeContextMenu = menu;
        menu.PlacementTarget = target;
        menu.IsOpen = true;
    }

    private static void LaunchShellUri(string target)
    {
        try
        {
            using var _ = Process.Start(new ProcessStartInfo
            {
                FileName = target,
                UseShellExecute = true
            });
        }
        catch
        {
            // Fallback for URI schemes via explorer
            try
            {
                using var _ = Process.Start(new ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    Arguments = target,
                    UseShellExecute = true
                });
            }
            catch { }
        }
    }

    // --- Unified Morphing Dynamic Island Interactions ---
    private void DynamicIsland_MouseEnter(object sender, MouseEventArgs e)
    {
        _hubHoverCloseTimer.Stop();
        if (_islandState == DynamicIslandState.NotificationToast)
        {
            _toastAutoCollapseTimer.Stop();
            return;
        }

        if (_islandState != DynamicIslandState.ExpandedHub)
        {
            _hubHoverOpenTimer.Start();
        }
    }

    private void DynamicIsland_MouseLeave(object sender, MouseEventArgs e)
    {
        _hubHoverOpenTimer.Stop();
        if (_islandState == DynamicIslandState.NotificationToast)
        {
            _toastAutoCollapseTimer.Start();
            return;
        }

        if (_islandState == DynamicIslandState.ExpandedHub)
        {
            _hubHoverCloseTimer.Start();
        }
    }

    private void DynamicIsland_Click(object sender, MouseButtonEventArgs e)
    {
        _hubHoverOpenTimer.Stop();
        _hubHoverCloseTimer.Stop();

        if (_islandState == DynamicIslandState.NotificationToast)
        {
            _toastAutoCollapseTimer.Stop();
            if (_lastToastItem != null)
            {
                ActivateNotificationApp(_lastToastItem);
            }
            else
            {
                SetCenterHubOpen(true, showAlerts: true);
            }
            return;
        }

        if (_islandState != DynamicIslandState.ExpandedHub)
        {
            SetCenterHubOpen(true, showAlerts: false);
        }
    }

    private void OpenClockSettings_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        CloseTransientPopups();
        LaunchShellUri("ms-clock:");
    }

    private void NotificationItem_Click(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        if (sender is FrameworkElement { Tag: NotificationItem item })
        {
            ActivateNotificationApp(item);
        }
    }

    private void ActivateNotificationApp(NotificationItem item)
    {
        _toastAutoCollapseTimer.Stop();
        CloseTransientPopups();
        if (_islandState == DynamicIslandState.NotificationToast)
        {
            ToastAlarmActionButton.Visibility = Visibility.Collapsed;
            TransitionDynamicIsland(GetDefaultIslandState());
        }

        string appId = item.AppId ?? string.Empty;
        string appName = item.AppName ?? string.Empty;

        // 0. Direct protocol / action URI extracted from the toast payload XML
        if (!string.IsNullOrWhiteSpace(item.LaunchUri))
        {
            LaunchShellUri(item.LaunchUri);
            return;
        }

        // 1. Fast path for Windows Clock / Alarms
        if (item.IsAlarm ||
            appName.Equals("Clock", StringComparison.OrdinalIgnoreCase) ||
            appName.Equals("WindowsAlarms", StringComparison.OrdinalIgnoreCase) ||
            appId.Contains("WindowsAlarms", StringComparison.OrdinalIgnoreCase))
        {
            ApplicationWindow? clockWin = FindMatchingWindow(appId, "Clock");
            if (clockWin != null)
            {
                BringWindowToFront(clockWin.Handle);
                return;
            }
            LaunchShellUri("ms-clock:");
            return;
        }

        // 2. Windows system & Bluetooth pairing/device setup toasts
        string? systemUri = TryResolveSystemNotificationUri(appId, appName, item.Title, item.Body);
        if (!string.IsNullOrWhiteSpace(systemUri))
        {
            LaunchShellUri(systemUri);
            return;
        }

        // 3. Match against live taskbar/shell windows (handles Win32 & UWP ApplicationFrameHost windows)
        ApplicationWindow? match = FindMatchingWindow(appId, appName);
        if (match != null)
        {
            BringWindowToFront(match.Handle);
            return;
        }

        // 4. Fallback scan of running processes with a main window handle
        if (!string.IsNullOrWhiteSpace(appName) && appName.Length >= 3)
        {
            foreach (Process proc in Process.GetProcesses())
            {
                try
                {
                    if (proc.MainWindowHandle == IntPtr.Zero) continue;
                    if (proc.ProcessName.Contains(appName, StringComparison.OrdinalIgnoreCase) ||
                        appName.Contains(proc.ProcessName, StringComparison.OrdinalIgnoreCase) ||
                        (!string.IsNullOrWhiteSpace(proc.MainWindowTitle) &&
                         proc.MainWindowTitle.Contains(appName, StringComparison.OrdinalIgnoreCase)))
                    {
                        BringWindowToFront(proc.MainWindowHandle);
                        return;
                    }
                }
                catch { }
                finally
                {
                    proc.Dispose();
                }
            }
        }

        // 5. Launch app cleanly if no existing window is open (e.g. minimized to tray or background notification)
        if (!string.IsNullOrWhiteSpace(appId))
        {
            if (appId.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && File.Exists(appId))
            {
                LaunchShellUri(appId);
                return;
            }
            if (!appId.StartsWith("Windows.System", StringComparison.OrdinalIgnoreCase))
            {
                LaunchShellUri($"shell:AppsFolder\\{appId}");
                return;
            }
        }

        if (appName.Contains("WhatsApp", StringComparison.OrdinalIgnoreCase))
        {
            LaunchShellUri("whatsapp:");
        }
        else if (appName.Contains("Telegram", StringComparison.OrdinalIgnoreCase))
        {
            LaunchShellUri("tg:");
        }
        else if (appName.Contains("Discord", StringComparison.OrdinalIgnoreCase))
        {
            LaunchShellUri("discord:");
        }
    }

    private static string? TryResolveSystemNotificationUri(string appId, string appName, string title, string body)
    {
        if (appId.StartsWith("Windows.SystemToast.Bth", StringComparison.OrdinalIgnoreCase) ||
            appId.Equals("Windows.SystemToast.DevicesFlow", StringComparison.OrdinalIgnoreCase) ||
            appId.Equals("Windows.SystemToast.NfpDevicePairing", StringComparison.OrdinalIgnoreCase) ||
            appId.Equals("Windows.SystemToast.Devices", StringComparison.OrdinalIgnoreCase) ||
            appId.Equals("Windows.SystemToast.DeviceConsent", StringComparison.OrdinalIgnoreCase) ||
            appId.Equals("Windows.SystemToast.DeviceManagement", StringComparison.OrdinalIgnoreCase))
        {
            return "ms-settings:bluetooth";
        }

        if (appId.Equals("Windows.SystemToast.AudioTroubleshooter", StringComparison.OrdinalIgnoreCase) ||
            appId.Equals("Windows.SystemToast.DefaultAudioEndpoint", StringComparison.OrdinalIgnoreCase) ||
            appId.Equals("Windows.System.Audio", StringComparison.OrdinalIgnoreCase))
        {
            return "ms-settings:sound";
        }

        if (appId.Equals("Windows.SystemToast.DisplayBroker", StringComparison.OrdinalIgnoreCase))
        {
            return "ms-settings:display";
        }

        if (appId.Equals("Windows.System.MiracastReceiver", StringComparison.OrdinalIgnoreCase) ||
            appId.Equals("Windows.System.NearShareExperienceReceive", StringComparison.OrdinalIgnoreCase) ||
            appId.Equals("Windows.System.ShareExperience", StringComparison.OrdinalIgnoreCase))
        {
            return "ms-settings:nearbysharing";
        }

        if (appId.Equals("Windows.SystemToast.LowDisk", StringComparison.OrdinalIgnoreCase))
        {
            return "ms-settings:storagesense";
        }

        if (appId.Equals("Windows.Defender", StringComparison.OrdinalIgnoreCase))
        {
            return "windowsdefender:";
        }

        if (appId.Equals("Windows.SystemToast.BackgroundAccess", StringComparison.OrdinalIgnoreCase))
        {
            return "ms-settings:privacy-backgroundapps";
        }

        if (appId.Equals("Windows.SystemToast.LocationManager", StringComparison.OrdinalIgnoreCase))
        {
            return "ms-settings:privacy-location";
        }

        if (appId.Equals("Windows.SystemToast.LanguageSettings", StringComparison.OrdinalIgnoreCase))
        {
            return "ms-settings:regionlanguage";
        }

        if (appId.Equals("Windows.SystemToast.AutoPlay", StringComparison.OrdinalIgnoreCase))
        {
            return "ms-settings:autoplay";
        }

        if (appId.Equals("Windows.SystemToast.Explorer", StringComparison.OrdinalIgnoreCase) ||
            appId.StartsWith("Microsoft.Explorer.Notification.", StringComparison.OrdinalIgnoreCase))
        {
            return "explorer.exe";
        }

        if (appId.Equals("Microsoft.Windows.DynamicLightingToastHandler", StringComparison.OrdinalIgnoreCase))
        {
            return "ms-settings:personalization-lighting";
        }

        if (appId.Equals("Windows.SystemToast.HelloFace", StringComparison.OrdinalIgnoreCase))
        {
            return "ms-settings:signinoptions";
        }

        bool isSystemSource = string.IsNullOrWhiteSpace(appId) ||
            appId.StartsWith("Windows.SystemToast.", StringComparison.OrdinalIgnoreCase) ||
            appId.StartsWith("Windows.System.", StringComparison.OrdinalIgnoreCase) ||
            appId.StartsWith("Microsoft.Windows.", StringComparison.OrdinalIgnoreCase);

        if (isSystemSource)
        {
            string combined = $"{appName} {title} {body}";
            if (combined.Contains("bluetooth", StringComparison.OrdinalIgnoreCase) ||
                combined.Contains("quick pair", StringComparison.OrdinalIgnoreCase) ||
                combined.Contains("swift pair", StringComparison.OrdinalIgnoreCase) ||
                combined.Contains("pair device", StringComparison.OrdinalIgnoreCase) ||
                combined.Contains("add a device", StringComparison.OrdinalIgnoreCase) ||
                combined.Contains("set up", StringComparison.OrdinalIgnoreCase))
            {
                return "ms-settings:bluetooth";
            }

            if (appId.StartsWith("Windows.SystemToast.", StringComparison.OrdinalIgnoreCase) ||
                appId.StartsWith("Windows.System.", StringComparison.OrdinalIgnoreCase))
            {
                return "ms-settings:";
            }
        }

        return null;
    }

    private ApplicationWindow? FindMatchingWindow(string appId, string appName)
    {
        var windows = _shellService.Tasks.GroupedWindows.OfType<ApplicationWindow>().ToList();
        string packagePrefix = string.Empty;
        if (!string.IsNullOrWhiteSpace(appId))
        {
            int bang = appId.IndexOf('!');
            string rawPkg = bang > 0 ? appId[..bang] : appId;
            int under = rawPkg.IndexOf('_');
            packagePrefix = under > 0 ? rawPkg[..under] : rawPkg;
        }

        foreach (ApplicationWindow win in windows)
        {
            string winAumid = win.AppUserModelID ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(appId) && !string.IsNullOrWhiteSpace(winAumid))
            {
                if (winAumid.Equals(appId, StringComparison.OrdinalIgnoreCase))
                {
                    return win;
                }
                if (packagePrefix.Length >= 5 && winAumid.Contains(packagePrefix, StringComparison.OrdinalIgnoreCase))
                {
                    return win;
                }
            }

            if (!string.IsNullOrWhiteSpace(appName) && appName.Length >= 3)
            {
                string exeName = string.IsNullOrWhiteSpace(win.WinFileName)
                    ? string.Empty
                    : Path.GetFileNameWithoutExtension(win.WinFileName);

                if ((!string.IsNullOrWhiteSpace(exeName) &&
                     (exeName.Contains(appName, StringComparison.OrdinalIgnoreCase) ||
                      appName.Contains(exeName, StringComparison.OrdinalIgnoreCase))) ||
                    (!string.IsNullOrWhiteSpace(win.WinFileDescription) &&
                     win.WinFileDescription.Contains(appName, StringComparison.OrdinalIgnoreCase)) ||
                    (!string.IsNullOrWhiteSpace(win.Title) &&
                     win.Title.Contains(appName, StringComparison.OrdinalIgnoreCase)))
                {
                    return win;
                }
            }
        }

        return null;
    }

    private static void BringWindowToFront(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return;
        if (NativeMethods.IsIconic(hwnd))
        {
            NativeMethods.ShowWindow(hwnd, NativeMethods.SW_RESTORE);
        }
        NativeMethods.SetForegroundWindow(hwnd);
    }

    private void StopAlarmToast_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        _toastAutoCollapseTimer.Stop();
        _notificationService.DismissNotification(_activeToastNotificationId);
        ToastAlarmActionButton.Visibility = Visibility.Collapsed;
        TransitionDynamicIsland(GetDefaultIslandState());
    }

    private void OpenClockApp_RightClick(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        _toastAutoCollapseTimer.Stop();
        TransitionDynamicIsland(GetDefaultIslandState());
        LaunchShellUri("ms-clock:");
    }

    private void DismissToast_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        _toastAutoCollapseTimer.Stop();
        ToastAlarmActionButton.Visibility = Visibility.Collapsed;
        TransitionDynamicIsland(GetDefaultIslandState());
    }

    // --- Media & Volume Inside Hub ---
    private async void PrevMedia_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        await _mediaService.SkipPreviousAsync();
    }

    private async void PlayPauseMedia_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        await _mediaService.PlayPauseAsync();
    }

    private async void NextMedia_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        await _mediaService.SkipNextAsync();
    }

    private void MuteButton_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        _systemStatusService.ToggleMute();
    }

    private void VolumeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_isUpdatingVolumeSlider || !IsLoaded) return;
        _lastVolumeInteractTick = Environment.TickCount64;
        int rounded = Math.Clamp((int)Math.Round(e.NewValue), 0, 100);
        bool isMuted = _systemStatusService.CurrentStatus.IsMuted;
        string volLabel = isMuted ? "Muted" : $"{rounded}%";
        PopupVolumePercentText.Text = volLabel;
        TopVolumeText.Text = volLabel;
        var speakerGeom = (Geometry)FindResource(isMuted || rounded == 0 ? "IconSpeakerMuted" : "IconSpeaker");
        PopupMuteIconPath.Data = speakerGeom;
        TopVolumeIcon.Data = speakerGeom;
        _systemStatusService.SetMasterVolume(e.NewValue);
    }

    private void SliderWheelTimer_Tick(object? sender, EventArgs e)
    {
        if (_isAnimatingVolumeWheel)
        {
            _isAnimatingVolumeWheel = StepSliderWheel(PopupVolumeSlider, _targetVolumeWheelValue);
        }

        if (_isAnimatingBrightnessWheel)
        {
            _isAnimatingBrightnessWheel = StepSliderWheel(PopupBrightnessSlider, _targetBrightnessWheelValue);
        }

        if (!_isAnimatingVolumeWheel && !_isAnimatingBrightnessWheel)
        {
            _sliderWheelTimer.Stop();
        }
    }

    private static bool StepSliderWheel(Slider slider, double target)
    {
        double current = slider.Value;
        double diff = target - current;
        if (Math.Abs(diff) < 0.15)
        {
            slider.Value = target;
            return false;
        }

        slider.Value = current + diff * 0.35;
        return true;
    }

    private void HubTabButton_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        SetExpandedTab(showAlerts: false);
    }

    private void AlertsTabButton_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        SetExpandedTab(showAlerts: true);
    }

    private void SetExpandedTab(bool showAlerts, bool animateResize = true)
    {
        _showingAlertsTab = showAlerts;
        HubTabButton.IsChecked = !_showingAlertsTab;
        AlertsTabButton.IsChecked = _showingAlertsTab;

        ControlCenterPanel.Visibility = _showingAlertsTab ? Visibility.Collapsed : Visibility.Visible;
        NotificationDrawerPanel.Visibility = _showingAlertsTab ? Visibility.Visible : Visibility.Collapsed;

        if (_showingAlertsTab)
        {
            _notificationService.MarkAllRead();
        }

        if (animateResize && _islandState == DynamicIslandState.ExpandedHub)
        {
            double targetHeight = _showingAlertsTab ? 252 : 148;
            var resizeEasing = new ExponentialEase { EasingMode = EasingMode.EaseOut, Exponent = 4.2 };
            DynamicIslandShell.BeginAnimation(
                HeightProperty,
                new DoubleAnimation(targetHeight, TimeSpan.FromMilliseconds(300)) { EasingFunction = resizeEasing });
        }
    }

    private void ClearNotifications_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        _notificationService.ClearAll();
    }

    private void TestNotification_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        SetCenterHubOpen(false);
        _notificationService.TriggerTestNotification();
    }

    // --- System Tray Dropdown & Icons Forwarding ---
    private void TopTrayDropdown_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        SetTrayPopupOpen(!_isTrayPopupOpen);
    }

    private static uint GetPackedCursorPosition()
    {
        NativeMethods.GetCursorPos(out NativeMethods.POINT pt);
        return unchecked((uint)(((pt.Y & 0xFFFF) << 16) | (pt.X & 0xFFFF)));
    }

    private void TrayIcon_MouseEnter(object sender, MouseEventArgs e)
    {
        if (sender is FrameworkElement { Tag: NotifyIcon icon })
        {
            icon.IconMouseEnter(GetPackedCursorPosition());
        }
    }

    private void TrayIcon_MouseLeave(object sender, MouseEventArgs e)
    {
        if (sender is FrameworkElement { Tag: NotifyIcon icon })
        {
            icon.IconMouseLeave(GetPackedCursorPosition());
        }
    }

    private void TrayIcon_MouseDown(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        _lastTrayIconInteractTick = Environment.TickCount64;
        if (sender is FrameworkElement { Tag: NotifyIcon icon })
        {
            icon.IconMouseDown(e.ChangedButton, GetPackedCursorPosition(), System.Windows.Forms.SystemInformation.DoubleClickTime);
        }
    }

    private void TrayIcon_MouseUp(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        _lastTrayIconInteractTick = Environment.TickCount64;
        if (sender is FrameworkElement { Tag: NotifyIcon icon })
        {
            icon.IconMouseUp(e.ChangedButton, GetPackedCursorPosition(), System.Windows.Forms.SystemInformation.DoubleClickTime);
        }
    }
}
