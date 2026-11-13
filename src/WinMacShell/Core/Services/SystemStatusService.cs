using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
using Windows.Devices.Radios;
using Windows.Networking.Connectivity;
using WinMacShell.Core.Interop;

namespace WinMacShell.Core.Services;

public enum NetworkConnectionKind
{
    Offline = 0,
    Wifi = 1,
    Ethernet = 2
}

public sealed record SystemStatusSnapshot(
    int VolumePercent,
    bool IsMuted,
    int BrightnessPercent,
    bool HasBattery,
    int BatteryPercent,
    bool IsCharging,
    bool IsNetworkConnected,
    NetworkConnectionKind NetworkKind,
    int WifiSignalBars,
    int WifiSignalPercent,
    string NetworkLabel,
    bool IsBluetoothEnabled,
    bool HasBluetooth);

public sealed class SystemStatusService : IMMNotificationClient, IDisposable
{
    private readonly object _audioLock = new();
    private MMDeviceEnumerator? _deviceEnumerator;
    private MMDevice? _defaultDevice;
    private readonly List<Radio> _bluetoothRadios = new();
    private readonly object _bluetoothLock = new();
    private int _isTogglingBluetooth;
    private int _isRefreshing;
    private int _audioDeviceDirty;
    private int _lastBrightnessPercent = 80;
    private int _lastVolumePercent = 50;
    private bool _lastMuted;
    private bool _isBluetoothEnabled;
    private bool _hasBluetooth;
    private bool _disposed;

    public SystemStatusSnapshot CurrentStatus { get; private set; } = new(
        50,
        false,
        80,
        false,
        100,
        true,
        true,
        NetworkConnectionKind.Wifi,
        3,
        100,
        "Connected",
        false,
        true);

    public event EventHandler<SystemStatusSnapshot>? StatusChanged;

    public void Initialize()
    {
        InitializeAudio();
        _ = InitializeBluetoothAsync();

        SystemEvents.PowerModeChanged += OnPowerModeChanged;
        NetworkChange.NetworkAvailabilityChanged += OnNetworkAvailabilityChanged;
        NetworkChange.NetworkAddressChanged += OnNetworkAddressChanged;
        try
        {
            NetworkInformation.NetworkStatusChanged += OnWinRtNetworkStatusChanged;
        }
        catch { }

        RefreshAndNotify();
    }

    private void InitializeAudio()
    {
        lock (_audioLock)
        {
            try
            {
                _deviceEnumerator ??= new MMDeviceEnumerator();
                _deviceEnumerator.RegisterEndpointNotificationCallback(this);
                AttachDefaultAudioDeviceUnsafe();
            }
            catch
            {
                // Audio endpoint unavailable
            }
        }
    }

    private void AttachDefaultAudioDeviceUnsafe()
    {
        if (_defaultDevice != null)
        {
            try
            {
                _defaultDevice.AudioEndpointVolume.OnVolumeNotification -= OnVolumeNotification;
            }
            catch { }
            try { _defaultDevice.Dispose(); } catch { }
            _defaultDevice = null;
        }

        try
        {
            _deviceEnumerator ??= new MMDeviceEnumerator();
            _defaultDevice = _deviceEnumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            _defaultDevice.AudioEndpointVolume.OnVolumeNotification += OnVolumeNotification;
        }
        catch
        {
            _defaultDevice = null;
        }
    }

    private Task InitializeBluetoothAsync()
    {
        return Task.Run(async () =>
        {
            try
            {
                var radios = await Radio.GetRadiosAsync().AsTask().ConfigureAwait(false);
                if (_disposed) return;

                lock (_bluetoothLock)
                {
                    foreach (var r in _bluetoothRadios)
                    {
                        try { r.StateChanged -= OnBluetoothRadioStateChanged; } catch { }
                    }
                    _bluetoothRadios.Clear();

                    foreach (Radio radio in radios)
                    {
                        if (radio.Kind == RadioKind.Bluetooth)
                        {
                            _bluetoothRadios.Add(radio);
                            radio.StateChanged += OnBluetoothRadioStateChanged;
                        }
                    }

                    _hasBluetooth = _bluetoothRadios.Count > 0;
                    _isBluetoothEnabled = _bluetoothRadios.Any(r => r.State == RadioState.On);
                }

                RefreshAndNotify();
            }
            catch
            {
                _hasBluetooth = false;
                _isBluetoothEnabled = false;
            }
        });
    }

    private void OnBluetoothRadioStateChanged(Radio sender, object args)
    {
        if (_disposed) return;
        _ = Task.Run(() =>
        {
            try
            {
                lock (_bluetoothLock)
                {
                    _isBluetoothEnabled = _bluetoothRadios.Any(r => r.State == RadioState.On);
                }
            }
            catch { }
            RefreshAndNotify();
        });
    }

    public async Task<bool> ToggleBluetoothAsync()
    {
        if (Interlocked.CompareExchange(ref _isTogglingBluetooth, 1, 0) != 0)
        {
            return true;
        }

        try
        {
            Task<bool> toggleTask = Task.Run(async () =>
            {
                try
                {
                    RadioAccessStatus access = await Radio.RequestAccessAsync().AsTask().ConfigureAwait(false);
                    if (access != RadioAccessStatus.Allowed)
                    {
                        return false;
                    }

                    List<Radio> radios;
                    lock (_bluetoothLock)
                    {
                        radios = _bluetoothRadios.ToList();
                    }

                    if (radios.Count == 0)
                    {
                        await InitializeBluetoothAsync().ConfigureAwait(false);
                        lock (_bluetoothLock)
                        {
                            radios = _bluetoothRadios.ToList();
                        }
                    }

                    if (radios.Count == 0)
                    {
                        return false;
                    }

                    bool currentlyOn = radios.Any(r => r.State == RadioState.On);
                    RadioState targetState = currentlyOn ? RadioState.Off : RadioState.On;

                    bool anySuccess = false;
                    foreach (Radio btRadio in radios)
                    {
                        var result = await btRadio.SetStateAsync(targetState).AsTask().ConfigureAwait(false);
                        if (result == RadioAccessStatus.Allowed)
                        {
                            anySuccess = true;
                        }
                    }

                    lock (_bluetoothLock)
                    {
                        _isBluetoothEnabled = _bluetoothRadios.Any(r => r.State == RadioState.On);
                    }
                    RefreshAndNotify();
                    return anySuccess;
                }
                catch
                {
                    return false;
                }
            });

            Task completed = await Task.WhenAny(toggleTask, Task.Delay(3500)).ConfigureAwait(false);
            return completed == toggleTask && await toggleTask.ConfigureAwait(false);
        }
        catch
        {
            return false;
        }
        finally
        {
            Interlocked.Exchange(ref _isTogglingBluetooth, 0);
        }
    }

    private void OnVolumeNotification(AudioVolumeNotificationData data)
    {
        if (_disposed) return;
        _lastVolumePercent = Math.Clamp((int)Math.Round(data.MasterVolume * 100.0f), 0, 100);
        _lastMuted = data.Muted;
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

    private void OnWinRtNetworkStatusChanged(object? sender)
    {
        if (_disposed) return;
        RefreshAndNotify();
    }

    public void SetMasterVolume(double percent)
    {
        float scalar = (float)Math.Clamp(percent / 100.0, 0.0, 1.0);
        if (Monitor.TryEnter(_audioLock, 120))
        {
            try
            {
                for (int attempt = 0; attempt < 2; attempt++)
                {
                    try
                    {
                        if (_defaultDevice == null || Interlocked.Exchange(ref _audioDeviceDirty, 0) == 1)
                        {
                            AttachDefaultAudioDeviceUnsafe();
                        }
                        if (_defaultDevice != null)
                        {
                            _defaultDevice.AudioEndpointVolume.MasterVolumeLevelScalar = scalar;
                            _lastVolumePercent = Math.Clamp((int)Math.Round(percent), 0, 100);
                            break;
                        }
                    }
                    catch
                    {
                        AttachDefaultAudioDeviceUnsafe();
                    }
                }
            }
            finally
            {
                Monitor.Exit(_audioLock);
            }
        }
        _ = Task.Run(RefreshAndNotify);
    }

    public void ToggleMute()
    {
        if (Monitor.TryEnter(_audioLock, 120))
        {
            try
            {
                for (int attempt = 0; attempt < 2; attempt++)
                {
                    try
                    {
                        if (_defaultDevice == null || Interlocked.Exchange(ref _audioDeviceDirty, 0) == 1)
                        {
                            AttachDefaultAudioDeviceUnsafe();
                        }
                        if (_defaultDevice != null)
                        {
                            bool nextMute = !_defaultDevice.AudioEndpointVolume.Mute;
                            _defaultDevice.AudioEndpointVolume.Mute = nextMute;
                            _lastMuted = nextMute;
                            break;
                        }
                    }
                    catch
                    {
                        AttachDefaultAudioDeviceUnsafe();
                    }
                }
            }
            finally
            {
                Monitor.Exit(_audioLock);
            }
        }
        _ = Task.Run(RefreshAndNotify);
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

    private (int VolumePercent, bool IsMuted) ReadVolumeState()
    {
        if (Monitor.TryEnter(_audioLock, 120))
        {
            try
            {
                for (int attempt = 0; attempt < 2; attempt++)
                {
                    try
                    {
                        if (_defaultDevice == null || Interlocked.Exchange(ref _audioDeviceDirty, 0) == 1)
                        {
                            AttachDefaultAudioDeviceUnsafe();
                        }
                        if (_defaultDevice != null)
                        {
                            int vol = (int)Math.Round(_defaultDevice.AudioEndpointVolume.MasterVolumeLevelScalar * 100.0f);
                            bool muted = _defaultDevice.AudioEndpointVolume.Mute;
                            _lastVolumePercent = Math.Clamp(vol, 0, 100);
                            _lastMuted = muted;
                            return (_lastVolumePercent, _lastMuted);
                        }
                    }
                    catch
                    {
                        AttachDefaultAudioDeviceUnsafe();
                    }
                }
            }
            finally
            {
                Monitor.Exit(_audioLock);
            }
        }

        return (_lastVolumePercent, _lastMuted);
    }

    public void RefreshAndNotify()
    {
        if (_disposed) return;
        if (Interlocked.CompareExchange(ref _isRefreshing, 1, 0) != 0) return;

        try
        {
            var (volumePercent, isMuted) = ReadVolumeState();
            int brightnessPercent = ReadDisplayBrightness();

            var power = System.Windows.Forms.SystemInformation.PowerStatus;
            bool hasBattery = (power.BatteryChargeStatus & System.Windows.Forms.BatteryChargeStatus.NoSystemBattery) == 0;
            int batteryPercent = hasBattery ? (int)Math.Round(power.BatteryLifePercent * 100.0f) : 100;
            bool isCharging = power.PowerLineStatus == System.Windows.Forms.PowerLineStatus.Online;

            var (isConnected, networkKind, signalBars, signalPercent, networkLabel) = ReadNetworkStatus();

            if (_bluetoothRadios.Count > 0)
            {
                try
                {
                    if (Monitor.TryEnter(_bluetoothLock, 50))
                    {
                        try
                        {
                            _isBluetoothEnabled = _bluetoothRadios.Any(r => r.State == RadioState.On);
                        }
                        finally
                        {
                            Monitor.Exit(_bluetoothLock);
                        }
                    }
                }
                catch { }
            }

            CurrentStatus = new SystemStatusSnapshot(
                Math.Clamp(volumePercent, 0, 100),
                isMuted,
                brightnessPercent,
                hasBattery,
                Math.Clamp(batteryPercent, 0, 100),
                isCharging,
                isConnected,
                networkKind,
                signalBars,
                signalPercent,
                networkLabel,
                _isBluetoothEnabled,
                _hasBluetooth);

            StatusChanged?.Invoke(this, CurrentStatus);
        }
        finally
        {
            Interlocked.Exchange(ref _isRefreshing, 0);
        }
    }

    private static (bool IsConnected, NetworkConnectionKind Kind, int SignalBars, int SignalPercent, string Label) ReadNetworkStatus()
    {
        try
        {
            if (!NetworkInterface.GetIsNetworkAvailable())
            {
                return (false, NetworkConnectionKind.Offline, 0, 0, "Offline");
            }

            // 1. Check native WLAN API first for exact real-time Wi-Fi signal quality (0..100%) and SSID
            if (TryQueryWlanSignal(out int wlanPercent, out string wlanSsid))
            {
                int bars = wlanPercent >= 72 ? 3 : (wlanPercent >= 40 ? 2 : 1);
                string strengthName = bars == 3 ? "Strong" : (bars == 2 ? "Good" : "Weak");
                string label = string.IsNullOrWhiteSpace(wlanSsid)
                    ? $"Wi-Fi ({wlanPercent}% — {strengthName})"
                    : $"{wlanSsid} ({wlanPercent}% — {strengthName})";
                return (true, NetworkConnectionKind.Wifi, bars, wlanPercent, label);
            }

            // 2. Check WinRT Internet Connection Profile
            try
            {
                ConnectionProfile? profile = NetworkInformation.GetInternetConnectionProfile();
                if (profile != null)
                {
                    NetworkConnectivityLevel level = profile.GetNetworkConnectivityLevel();
                    if (level != NetworkConnectivityLevel.None)
                    {
                        if (profile.IsWlanConnectionProfile)
                        {
                            byte? rawBars = profile.GetSignalBars();
                            int pct = rawBars.HasValue ? Math.Clamp(rawBars.Value * 25, 20, 100) : 85;
                            int bars = pct >= 72 ? 3 : (pct >= 40 ? 2 : 1);
                            string ssid = profile.WlanConnectionProfileDetails?.GetConnectedSsid() ?? "Wi-Fi";
                            string strengthName = bars == 3 ? "Strong" : (bars == 2 ? "Good" : "Weak");
                            return (true, NetworkConnectionKind.Wifi, bars, pct, $"{ssid} ({pct}% — {strengthName})");
                        }

                        uint ianaType = profile.NetworkAdapter?.IanaInterfaceType ?? 0;
                        if (ianaType == 6)
                        {
                            return (true, NetworkConnectionKind.Ethernet, 3, 100, "Ethernet (Connected)");
                        }
                    }
                }
            }
            catch { }

            // 3. Fallback to NetworkInterface inspection
            var active = NetworkInterface.GetAllNetworkInterfaces()
                .Where(ni => ni.OperationalStatus == OperationalStatus.Up
                             && ni.NetworkInterfaceType != NetworkInterfaceType.Loopback
                             && ni.NetworkInterfaceType != NetworkInterfaceType.Tunnel)
                .ToList();

            var wifiNi = active.FirstOrDefault(ni => ni.NetworkInterfaceType == NetworkInterfaceType.Wireless80211);
            if (wifiNi != null)
            {
                return (true, NetworkConnectionKind.Wifi, 3, 85, $"Wi-Fi ({wifiNi.Name})");
            }

            var ethNi = active.FirstOrDefault(ni =>
                ni.NetworkInterfaceType == NetworkInterfaceType.Ethernet ||
                ni.NetworkInterfaceType == NetworkInterfaceType.GigabitEthernet ||
                ni.NetworkInterfaceType == NetworkInterfaceType.FastEthernetT);
            if (ethNi != null)
            {
                return (true, NetworkConnectionKind.Ethernet, 3, 100, $"Ethernet ({ethNi.Name})");
            }

            return active.Count > 0
                ? (true, NetworkConnectionKind.Ethernet, 3, 100, "Connected")
                : (false, NetworkConnectionKind.Offline, 0, 0, "Offline");
        }
        catch
        {
            return (false, NetworkConnectionKind.Offline, 0, 0, "Offline");
        }
    }

    private static bool TryQueryWlanSignal(out int signalPercent, out string ssid)
    {
        signalPercent = 0;
        ssid = string.Empty;
        IntPtr clientHandle = IntPtr.Zero;
        IntPtr ifaceListPtr = IntPtr.Zero;

        try
        {
            if (NativeMethods.WlanOpenHandle(NativeMethods.WLAN_API_VERSION_2_0, IntPtr.Zero, out _, out clientHandle) != 0 ||
                clientHandle == IntPtr.Zero)
            {
                return false;
            }

            if (NativeMethods.WlanEnumInterfaces(clientHandle, IntPtr.Zero, out ifaceListPtr) != 0 ||
                ifaceListPtr == IntPtr.Zero)
            {
                return false;
            }

            int numItems = Marshal.ReadInt32(ifaceListPtr, 0);
            IntPtr itemPtr = IntPtr.Add(ifaceListPtr, 8);
            int infoSize = Marshal.SizeOf<NativeMethods.WLAN_INTERFACE_INFO>();

            for (int i = 0; i < numItems; i++)
            {
                var ifaceInfo = Marshal.PtrToStructure<NativeMethods.WLAN_INTERFACE_INFO>(itemPtr);
                itemPtr = IntPtr.Add(itemPtr, infoSize);

                // isState == 1 is wlan_interface_state_connected
                if (ifaceInfo.isState != 1) continue;

                Guid guid = ifaceInfo.InterfaceGuid;
                if (NativeMethods.WlanQueryInterface(
                        clientHandle,
                        ref guid,
                        NativeMethods.wlan_intf_opcode_current_connection,
                        IntPtr.Zero,
                        out _,
                        out IntPtr connDataPtr,
                        IntPtr.Zero) == 0 && connDataPtr != IntPtr.Zero)
                {
                    try
                    {
                        var conn = Marshal.PtrToStructure<NativeMethods.WLAN_CONNECTION_ATTRIBUTES>(connDataPtr);
                        signalPercent = Math.Clamp((int)conn.wlanAssociationAttributes.wlanSignalQuality, 1, 100);
                        uint ssidLen = Math.Min(conn.wlanAssociationAttributes.dot11Ssid.uSSIDLength, 32u);
                        if (ssidLen > 0 && conn.wlanAssociationAttributes.dot11Ssid.ucSSID != null)
                        {
                            ssid = Encoding.UTF8.GetString(conn.wlanAssociationAttributes.dot11Ssid.ucSSID, 0, (int)ssidLen).Trim('\0');
                        }
                        if (string.IsNullOrWhiteSpace(ssid))
                        {
                            ssid = conn.strProfileName;
                        }
                        return true;
                    }
                    finally
                    {
                        NativeMethods.WlanFreeMemory(connDataPtr);
                    }
                }
            }
        }
        catch
        {
            // Ignore if Wireless LAN service is not running on desktop PCs
        }
        finally
        {
            if (ifaceListPtr != IntPtr.Zero)
            {
                NativeMethods.WlanFreeMemory(ifaceListPtr);
            }
            if (clientHandle != IntPtr.Zero)
            {
                NativeMethods.WlanCloseHandle(clientHandle, IntPtr.Zero);
            }
        }

        return false;
    }

    // IMMNotificationClient callbacks: NEVER call GetDefaultAudioEndpoint or acquire locks synchronously inside CoreAudio callbacks!
    public void OnDefaultDeviceChanged(DataFlow flow, Role role, string defaultDeviceId)
    {
        if (_disposed || flow != DataFlow.Render || role != Role.Multimedia) return;
        Interlocked.Exchange(ref _audioDeviceDirty, 1);
        _ = Task.Run(RefreshAndNotify);
    }

    public void OnDeviceStateChanged(string deviceId, DeviceState newState)
    {
        if (_disposed) return;
        Interlocked.Exchange(ref _audioDeviceDirty, 1);
        _ = Task.Run(RefreshAndNotify);
    }

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
        try
        {
            NetworkInformation.NetworkStatusChanged -= OnWinRtNetworkStatusChanged;
        }
        catch { }

        foreach (var r in _bluetoothRadios)
        {
            try { r.StateChanged -= OnBluetoothRadioStateChanged; } catch { }
        }
        _bluetoothRadios.Clear();

        lock (_audioLock)
        {
            if (_deviceEnumerator != null)
            {
                try { _deviceEnumerator.UnregisterEndpointNotificationCallback(this); } catch { }
            }

            if (_defaultDevice != null)
            {
                try { _defaultDevice.AudioEndpointVolume.OnVolumeNotification -= OnVolumeNotification; } catch { }
                try { _defaultDevice.Dispose(); } catch { }
                _defaultDevice = null;
            }

            try { _deviceEnumerator?.Dispose(); } catch { }
            _deviceEnumerator = null;
        }
    }
}
