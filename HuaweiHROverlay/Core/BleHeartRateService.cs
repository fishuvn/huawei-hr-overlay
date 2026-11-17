using HuaweiHROverlay.Core.Platforms;

namespace HuaweiHROverlay.Core;

public sealed record BleDeviceSessionInfo(string DeviceId, string DeviceName);
public sealed record SessionReconnectStateEventArgs(string DeviceId, string DeviceName, bool IsReconnecting);

/// <summary>
/// Cross-platform Bluetooth LE Heart Rate coordinator.
/// Automatically selects Windows WinRT, Linux BlueZ (D-Bus), or macOS CoreBluetooth at runtime,
/// manages multiple concurrent watch sessions, and handles automatic signal-drop reconnection.
/// </summary>
public sealed class BleHeartRateService : IAsyncDisposable
{
    private readonly IBlePlatformBackend _backend;
    private readonly Dictionary<string, IBleGattSession> _sessions = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, (string Name, CancellationTokenSource Cts)> _reconnecting = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, (string Name, bool HasHr, string? DisplayAddr)> _adCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _adLock = new();

    public event EventHandler<HeartRateReading>? HeartRateChanged;
    public event EventHandler<BleDeviceInfo>? DeviceDiscovered;
    public event EventHandler<string>? StatusChanged;
    public event EventHandler<BleDeviceSessionInfo>? SessionAdded;
    public event EventHandler<BleDeviceSessionInfo>? SessionRemoved;
    public event EventHandler<SessionReconnectStateEventArgs>? SessionReconnectStateChanged;
    public event EventHandler? ScanningStoppedUnexpectedly;

    public string PlatformBackendName => _backend.PlatformName;
    public bool IsScanning => _backend.IsScanning;
    public bool AutoReconnect { get; set; } = true;
    public int SessionCount => _sessions.Count + _reconnecting.Count;

    public bool IsConnectedOrReconnecting(string deviceId) =>
        _sessions.ContainsKey(deviceId) || _reconnecting.ContainsKey(deviceId);

    public BleHeartRateService(IBlePlatformBackend? customBackend = null)
    {
        _backend = customBackend ?? CreateDefaultBackend();
        _backend.AdvertisementReceived += OnBackendAdvertisement;
        _backend.StatusMessage += (_, msg) => StatusChanged?.Invoke(this, msg);
        _backend.ScanStoppedUnexpectedly += (_, _) => ScanningStoppedUnexpectedly?.Invoke(this, EventArgs.Empty);
    }

    private static IBlePlatformBackend CreateDefaultBackend()
    {
#if WINDOWS_BLE
        return new WindowsBleBackend();
#else
        if (OperatingSystem.IsLinux())
            return new LinuxBlueZBackend();
        if (OperatingSystem.IsMacOS())
            return new MacCoreBluetoothBackend();
        return new UnsupportedBleBackend();
#endif
    }

    public async Task StartScanningAsync()
    {
        if (IsScanning) return;
        lock (_adLock) _adCache.Clear();
        StatusChanged?.Invoke(this, $"Scanning via {_backend.PlatformName}… (ensure HR Data Broadcast is ON)");
        await _backend.StartScanningAsync();
    }

    public async Task StopScanningAsync()
    {
        if (!IsScanning) return;
        await _backend.StopScanningAsync();
        StatusChanged?.Invoke(this, "Scan stopped.");
    }

    public async Task<bool> ConnectAsync(string deviceId, string deviceName)
    {
        string key = deviceId.Trim().ToUpperInvariant();
        if (_sessions.ContainsKey(key))
        {
            StatusChanged?.Invoke(this, $"Already connected to {deviceName}.");
            return true;
        }

        CancelReconnectLoop(key);
        StatusChanged?.Invoke(this, $"Connecting to {deviceName}…");

        var session = await _backend.ConnectAsync(key, deviceName);
        if (session == null) return false;

        WireSessionEvents(session);
        _sessions[key] = session;
        StatusChanged?.Invoke(this, $"Connected to {deviceName} ({_sessions.Count} live).");
        SessionAdded?.Invoke(this, new BleDeviceSessionInfo(key, deviceName));
        return true;
    }

    public async Task DisconnectAsync(string deviceId)
    {
        string key = deviceId.Trim().ToUpperInvariant();
        string? removedName = null;

        if (_reconnecting.Remove(key, out var pending))
        {
            pending.Cts.Cancel();
            pending.Cts.Dispose();
            removedName = pending.Name;
        }

        if (_sessions.Remove(key, out var session))
        {
            removedName = session.DeviceName;
            await session.DisposeAsync();
        }

        if (removedName != null)
        {
            StatusChanged?.Invoke(this, $"{removedName} disconnected.");
            SessionRemoved?.Invoke(this, new BleDeviceSessionInfo(key, removedName));
        }
    }

    private void WireSessionEvents(IBleGattSession session)
    {
        session.BpmReceived += (_, bpm) =>
        {
            HeartRateChanged?.Invoke(this, new HeartRateReading
            {
                Bpm = bpm,
                Connected = true,
                Timestamp = DateTime.UtcNow,
                DeviceId = session.DeviceId,
                DeviceName = session.DeviceName
            });
        };

        session.DisconnectedUnexpectedly += (_, _) => OnUnexpectedDisconnect(session);
    }

    private async void OnUnexpectedDisconnect(IBleGattSession lostSession)
    {
        string id = lostSession.DeviceId;
        string name = lostSession.DeviceName;

        if (!_sessions.Remove(id)) return;
        await lostSession.DisposeAsync();

        if (!AutoReconnect)
        {
            StatusChanged?.Invoke(this, $"{name} disconnected unexpectedly.");
            SessionRemoved?.Invoke(this, new BleDeviceSessionInfo(id, name));
            return;
        }

        StatusChanged?.Invoke(this, $"{name} signal lost — auto-reconnecting…");
        var cts = new CancellationTokenSource();
        _reconnecting[id] = (name, cts);
        SessionReconnectStateChanged?.Invoke(this, new SessionReconnectStateEventArgs(id, name, IsReconnecting: true));
        _ = ReconnectLoopAsync(id, name, cts.Token);
    }

    private async Task ReconnectLoopAsync(string deviceId, string deviceName, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { await Task.Delay(TimeSpan.FromSeconds(4), ct); }
            catch (OperationCanceledException) { return; }

            if (ct.IsCancellationRequested || !_reconnecting.ContainsKey(deviceId))
                return;

            var candidate = await _backend.ConnectAsync(deviceId, deviceName, ct);
            if (candidate == null) continue;

            if (ct.IsCancellationRequested || !_reconnecting.ContainsKey(deviceId))
            {
                await candidate.DisposeAsync();
                return;
            }

            CancelReconnectLoop(deviceId);
            WireSessionEvents(candidate);
            _sessions[deviceId] = candidate;
            StatusChanged?.Invoke(this, $"Reconnected to {deviceName}.");
            SessionReconnectStateChanged?.Invoke(this, new SessionReconnectStateEventArgs(deviceId, deviceName, IsReconnecting: false));
            return;
        }
    }

    private void CancelReconnectLoop(string key)
    {
        if (_reconnecting.Remove(key, out var pending))
        {
            pending.Cts.Cancel();
            pending.Cts.Dispose();
        }
    }

    private void OnBackendAdvertisement(object? sender, BleAdvertisementPacket pkt)
    {
        string id = pkt.DeviceId.Trim().ToUpperInvariant();
        if (string.IsNullOrEmpty(id)) return;

        string? adName = pkt.LocalName?.Trim();
        string resolvedName;
        bool resolvedHr;
        string? resolvedAddr;

        lock (_adLock)
        {
            bool existed = _adCache.TryGetValue(id, out var prev);
            resolvedHr = pkt.HasHrServiceUuid || (existed && prev.HasHr);
            resolvedAddr = pkt.DisplayAddress ?? (existed ? prev.DisplayAddr : null);

            if (!string.IsNullOrEmpty(adName))
                resolvedName = adName;
            else if (existed && !string.IsNullOrEmpty(prev.Name))
                resolvedName = prev.Name;
            else if (resolvedHr)
                resolvedName = $"BLE HR Sensor ({(id.Length > 8 ? id[..8] : id)})";
            else
                return; // Filter unnamed non-HR background beacons

            _adCache[id] = (resolvedName, resolvedHr, resolvedAddr);
        }

        DeviceDiscovered?.Invoke(this, new BleDeviceInfo(id, resolvedName, pkt.Rssi, resolvedHr, resolvedAddr));
    }

    public async ValueTask DisposeAsync()
    {
        await StopScanningAsync();
        foreach (var (_, cts) in _reconnecting.Values)
        {
            cts.Cancel();
            cts.Dispose();
        }
        _reconnecting.Clear();

        foreach (var s in _sessions.Values)
            await s.DisposeAsync();
        _sessions.Clear();

        await _backend.DisposeAsync();
    }

    private sealed class UnsupportedBleBackend : IBlePlatformBackend
    {
        public string PlatformName => "Unsupported OS";
        public bool IsScanning => false;
        public event EventHandler<BleAdvertisementPacket>? AdvertisementReceived { add { } remove { } }
        public event EventHandler<string>? StatusMessage;
        public event EventHandler? ScanStoppedUnexpectedly { add { } remove { } }

        public Task StartScanningAsync()
        {
            StatusMessage?.Invoke(this, "Bluetooth LE is supported on Windows 10/11, Linux (BlueZ), and macOS.");
            return Task.CompletedTask;
        }
        public Task StopScanningAsync() => Task.CompletedTask;
        public Task<IBleGattSession?> ConnectAsync(string deviceId, string deviceName, CancellationToken ct = default) =>
            Task.FromResult<IBleGattSession?>(null);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

public record BleDeviceInfo(
    string Id,
    string Name,
    int Rssi,
    bool HasHrService = false,
    string? CustomDisplayAddress = null)
{
    public string DisplayName => string.IsNullOrWhiteSpace(Name) ? $"Unknown ({Id})" : Name;

    public string FormattedAddress => !string.IsNullOrWhiteSpace(CustomDisplayAddress)
        ? CustomDisplayAddress
        : (Id.Length == 12
            ? $"{Id[0..2]}:{Id[2..4]}:{Id[4..6]}:{Id[6..8]}:{Id[8..10]}:{Id[10..12]}"
            : Id);

    public string RssiDisplay => Rssi != 0 ? $"{Rssi} dBm" : "—";

    public int SignalBars => Rssi switch
    {
        0 => 2,
        >= -62 => 4,
        >= -74 => 3,
        >= -86 => 2,
        _ => 1
    };

    public bool IsLikelyHrWatch => HasHrService
        || Name.Contains("Huawei", StringComparison.OrdinalIgnoreCase)
        || Name.Contains("Watch", StringComparison.OrdinalIgnoreCase)
        || Name.Contains("Band", StringComparison.OrdinalIgnoreCase)
        || Name.Contains("Polar", StringComparison.OrdinalIgnoreCase)
        || Name.Contains("TICKR", StringComparison.OrdinalIgnoreCase)
        || Name.Contains("Garmin", StringComparison.OrdinalIgnoreCase)
        || Name.Contains("Coospo", StringComparison.OrdinalIgnoreCase)
        || Name.Contains("HR", StringComparison.OrdinalIgnoreCase);

    public string BadgeText => HasHrService ? "HR 0x180D" : (IsLikelyHrWatch ? "WEARABLE" : "BLE");
}
