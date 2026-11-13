using System.Diagnostics;
using System.IO;
using System.Windows;
using Microsoft.Win32;
using Windows.ApplicationModel;
using WinMacShell.Core.Interop;
using WinMacShell.Core.Safety;
using WinMacShell.Core.Services;
using WinMacShell.Persistence;
using WinMacShell.Views;

namespace WinMacShell;

public partial class App : Application
{
    private TaskbarGuard? _taskbarGuard;
    private ConfigManager? _configManager;
    private ShellService? _shellService;
    private WindowTracker? _windowTracker;
    private MediaService? _mediaService;
    private SystemStatusService? _systemStatusService;
    private NotificationService? _notificationService;
    private NotchWindow? _notchWindow;
    private DockWindow? _dockWindow;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _taskbarGuard = new TaskbarGuard();
        _taskbarGuard.RegisterSafetyHandlers(this);
        StartWatchdogProcess();
        _ = EnsureStartupRegistrationAsync();

        _configManager = new ConfigManager();
        _configManager.Load();

        _shellService = new ShellService();
        _taskbarGuard.AttachShellService(_shellService);
        _windowTracker = new WindowTracker();

        _mediaService = new MediaService();
        _systemStatusService = new SystemStatusService();
        _systemStatusService.Initialize();

        _notificationService = new NotificationService();
        _taskbarGuard.AppBarFullscreenNotification += (_, _) =>
        {
            _windowTracker.EvaluateForegroundState();
        };
        _windowTracker.WindowLocationEnforceRequested += (_, hwnd) =>
        {
            _taskbarGuard.EnforceWindowBelowTopBar(hwnd);
        };
        _windowTracker.SystemNotificationWindowShown += (_, _) =>
        {
            _ = _notificationService.CheckForNewNotificationsAsync();
        };
        _shellService.WindowAttentionRequested += (_, _) =>
        {
            _ = _notificationService.CheckForNewNotificationsAsync();
        };

        _ = _mediaService.InitializeAsync();
        _ = _notificationService.InitializeAsync();

        _notchWindow = new NotchWindow(
            _mediaService,
            _systemStatusService,
            _shellService,
            _notificationService,
            _windowTracker,
            _taskbarGuard);
        _notchWindow.SourceInitializedReady += (_, win) =>
        {
            _taskbarGuard.UpdateDpiScale(win);
            _taskbarGuard.RegisterGlobalHotKey(win);
            _taskbarGuard.HideSystemTaskbar();
        };
        _notchWindow.Loaded += (_, _) => _taskbarGuard.HideSystemTaskbar();

        _dockWindow = new DockWindow(_configManager, _shellService, _windowTracker);

        MainWindow = _notchWindow;
        _notchWindow.Show();
        _dockWindow.Show();
        _windowTracker.Start();
    }

    private static void StartWatchdogProcess()
    {
        try
        {
            string baseDir = AppContext.BaseDirectory;
            string watchdogPath = Path.Combine(baseDir, "NixanDeck.Watchdog.exe");
            if (!File.Exists(watchdogPath))
            {
                return;
            }

            using var _ = Process.Start(new ProcessStartInfo
            {
                FileName = watchdogPath,
                Arguments = $"--pid {Environment.ProcessId}",
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = baseDir
            });
        }
        catch
        {
            // Non-fatal if watchdog binary is not present
        }
    }

    private static async Task EnsureStartupRegistrationAsync()
    {
        try
        {
            StartupTask startupTask = await StartupTask.GetAsync("NixanDeckStartupTask");
            if (startupTask.State == StartupTaskState.Disabled)
            {
                await startupTask.RequestEnableAsync();
            }
            return;
        }
        catch
        {
            // Fallback to HKCU\...\Run when running unpackaged
        }

        try
        {
            string? exePath = Environment.ProcessPath;
            if (!string.IsNullOrWhiteSpace(exePath) && File.Exists(exePath))
            {
                using RegistryKey? runKey = Registry.CurrentUser.OpenSubKey(
                    @"Software\Microsoft\Windows\CurrentVersion\Run",
                    writable: true);
                runKey?.SetValue("NixanDeck", $"\"{exePath}\"");
            }
        }
        catch
        {
            // Ignore registry permission errors
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _taskbarGuard?.ShowSystemTaskbar();
        _notificationService?.Dispose();
        _mediaService?.Dispose();
        _systemStatusService?.Dispose();
        _windowTracker?.Dispose();
        _shellService?.Dispose();
        _taskbarGuard?.Dispose();
        base.OnExit(e);
    }
}
