using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
using WinMacShell.Core.Interop;

namespace WinMacShell.Core.Services;

public sealed record SystemStatusSnapshot(
    int VolumePercent,
    bool IsMuted,
    int BrightnessPercent,
    bool HasBattery,
    int BatteryPercent,
    bool IsCharging,
    bool IsNetworkConnected,
    string NetworkLabel);

public sealed class SystemStatusService : IMMNotificationClient, IDisposable
{
    private MMDeviceEnumerator? _deviceEnumerator;
    private MMDevice? _defaultDevice;
    private int _lastBrightnessPercent = 80;
    private bool _disposed;

    public SystemStatusSnapshot CurrentStatus { get; private set; } = new(50, false, 80, false, 100, true, true, "Connected");

    public event EventHandler<SystemStatusSnapshot>? StatusChanged;

    public void Initialize()
    {
        InitializeAudio();
        SystemEvents.PowerModeChanged += OnPowerModeChanged;
        NetworkChange.NetworkAvailabilityChanged += OnNetworkAvailabilityChanged;
        NetworkChange.NetworkAddressChanged += OnNetworkAddressChanged;

        RefreshAndNotify();
    }

    private void InitializeAudio()
    {
        try
        {
            _deviceEnumerator = new MMDeviceEnumerator();
            _deviceEnumerator.RegisterEndpointNotificationCallback(this);
            AttachDefaultAudioDevice();
        }
        catch
        {
            // Audio endpoint unavailable
        }
    }

    private void AttachDefaultAudioDevice()
    {
        if (_defaultDevice != null)
        {
            try
            {
                _defaultDevice.AudioEndpointVolume.OnVolumeNotification -= OnVolumeNotification;
            }
            catch { }
            _defaultDevice.Dispose();
            _defaultDevice = null;
        }

        if (_deviceEnumerator == null) return;
        try
        {
            _defaultDevice = _deviceEnumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            _defaultDevice.AudioEndpointVolume.OnVolumeNotification += OnVolumeNotification;
        }
        catch
        {
            _defaultDevice = null;
        }
    }

    private void OnVolumeNotification(AudioVolumeNotificationData data)
    {
        if (_disposed) return;
        RefreshAndNotify();
    }

    private void OnPowerModeChanged(object? sender, PowerModeChangedEventArgs e)
    {
        if (_disposed) return;
        RefreshAndNotify();
    }

    private void OnNetworkAvailabilityChanged(object? sender, NetworkAvailabilityEventArgs e)
    {
        if (_disposed) return;
        RefreshAndNotify();
    }

    private void OnNetworkAddressChanged(object? sender, EventArgs e)
    {
        if (_disposed) return;
        RefreshAndNotify();
    }

    public void SetMasterVolume(double percent)
    {
        if (_defaultDevice == null) return;
        try
        {
            float scalar = (float)Math.Clamp(percent / 100.0, 0.0, 1.0);
            _defaultDevice.AudioEndpointVolume.MasterVolumeLevelScalar = scalar;
        }
        catch { }
    }

    public void ToggleMute()
    {
        if (_defaultDevice == null) return;
        try
        {
            _defaultDevice.AudioEndpointVolume.Mute = !_defaultDevice.AudioEndpointVolume.Mute;
        }
        catch { }
    }

    public void SetDisplayBrightness(double percent)
    {
        int clamped = Math.Clamp((int)Math.Round(percent), 5, 100);
        try
        {
            using var handle = NativeMethods.CreateFile(
                @"\\.\LCD",
                NativeMethods.GENERIC_READ | NativeMethods.GENERIC_WRITE,
                NativeMethods.FILE_SHARE_READ | NativeMethods.FILE_SHARE_WRITE,
                IntPtr.Zero,
                NativeMethods.OPEN_EXISTING,
                0,
                IntPtr.Zero);

            if (!handle.IsInvalid)
            {
                var brightness = new NativeMethods.DISPLAY_BRIGHTNESS
                {
                    ucDisplayPolicy = NativeMethods.DISPLAYPOLICY_BOTH,
                    ucACBrightness = (byte)clamped,
                    ucDCBrightness = (byte)clamped
                };
                NativeMethods.DeviceIoControl(
                    handle,
                    NativeMethods.IOCTL_VIDEO_SET_DISPLAY_BRIGHTNESS,
                    ref brightness,
                    (uint)Marshal.SizeOf<NativeMethods.DISPLAY_BRIGHTNESS>(),
                    IntPtr.Zero,
                    0,
                    out _,
                    IntPtr.Zero);
            }
        }
        catch { }

        _lastBrightnessPercent = clamped;
        RefreshAndNotify();
    }

    private int ReadDisplayBrightness()
    {
        try
        {
            using var handle = NativeMethods.CreateFile(
                @"\\.\LCD",
                NativeMethods.GENERIC_READ | NativeMethods.GENERIC_WRITE,
                NativeMethods.FILE_SHARE_READ | NativeMethods.FILE_SHARE_WRITE,
                IntPtr.Zero,
                NativeMethods.OPEN_EXISTING,
                0,
                IntPtr.Zero);

            if (!handle.IsInvalid &&
                NativeMethods.DeviceIoControl(
                    handle,
                    NativeMethods.IOCTL_VIDEO_QUERY_DISPLAY_BRIGHTNESS,
                    IntPtr.Zero,
                    0,
                    out NativeMethods.DISPLAY_BRIGHTNESS db,
                    (uint)Marshal.SizeOf<NativeMethods.DISPLAY_BRIGHTNESS>(),
                    out _,
                    IntPtr.Zero))
            {
                int val = Math.Clamp((int)db.ucDCBrightness, 0, 100);
                _lastBrightnessPercent = val;
                return val;
            }
        }
        catch { }

        return _lastBrightnessPercent;
    }

    private void RefreshAndNotify()
    {
        int volumePercent = 50;
        bool isMuted = false;

        if (_defaultDevice != null)
        {
            try
            {
                volumePercent = (int)Math.Round(_defaultDevice.AudioEndpointVolume.MasterVolumeLevelScalar * 100.0f);
                isMuted = _defaultDevice.AudioEndpointVolume.Mute;
            }
            catch { }
        }

        int brightnessPercent = ReadDisplayBrightness();
        var power = System.Windows.Forms.SystemInformation.PowerStatus;
        bool hasBattery = (power.BatteryChargeStatus & System.Windows.Forms.BatteryChargeStatus.NoSystemBattery) == 0;
        int batteryPercent = hasBattery ? (int)Math.Round(power.BatteryLifePercent * 100.0f) : 100;
        bool isCharging = power.PowerLineStatus == System.Windows.Forms.PowerLineStatus.Online;

        var (isConnected, networkLabel) = ReadNetworkStatus();

        CurrentStatus = new SystemStatusSnapshot(
            Math.Clamp(volumePercent, 0, 100),
            isMuted,
            brightnessPercent,
            hasBattery,
            Math.Clamp(batteryPercent, 0, 100),
            isCharging,
            isConnected,
            networkLabel);

        StatusChanged?.Invoke(this, CurrentStatus);
    }

    private static (bool IsConnected, string Label) ReadNetworkStatus()
    {
        try
        {
            if (!NetworkInterface.GetIsNetworkAvailable())
            {
                return (false, "Offline");
            }

            var active = NetworkInterface.GetAllNetworkInterfaces()
                .Where(ni => ni.OperationalStatus == OperationalStatus.Up
                             && ni.NetworkInterfaceType != NetworkInterfaceType.Loopback
                             && ni.NetworkInterfaceType != NetworkInterfaceType.Tunnel)
                .ToList();

            if (active.Any(ni => ni.NetworkInterfaceType == NetworkInterfaceType.Wireless80211))
            {
                return (true, "Wi-Fi");
            }

            if (active.Any(ni => ni.NetworkInterfaceType == NetworkInterfaceType.Ethernet))
            {
                return (true, "Ethernet");
            }

            return active.Count > 0 ? (true, "Online") : (false, "Offline");
        }
        catch
        {
            return (false, "Offline");
        }
    }

    // IMMNotificationClient callbacks (react to default audio device switching)
    public void OnDefaultDeviceChanged(DataFlow flow, Role role, string defaultDeviceId)
    {
        if (_disposed || flow != DataFlow.Render || role != Role.Multimedia) return;
        AttachDefaultAudioDevice();
        RefreshAndNotify();
    }

    public void OnDeviceStateChanged(string deviceId, DeviceState newState) { }
    public void OnDeviceAdded(string pwstrDeviceId) { }
    public void OnDeviceRemoved(string deviceId) { }
    public void OnPropertyValueChanged(string pwstrDeviceId, PropertyKey key) { }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        NetworkChange.NetworkAvailabilityChanged -= OnNetworkAvailabilityChanged;
        NetworkChange.NetworkAddressChanged -= OnNetworkAddressChanged;

        if (_deviceEnumerator != null)
        {
            try { _deviceEnumerator.UnregisterEndpointNotificationCallback(this); } catch { }
        }

        if (_defaultDevice != null)
        {
            try { _defaultDevice.AudioEndpointVolume.OnVolumeNotification -= OnVolumeNotification; } catch { }
            _defaultDevice.Dispose();
            _defaultDevice = null;
        }

        _deviceEnumerator?.Dispose();
        _deviceEnumerator = null;
    }
}
