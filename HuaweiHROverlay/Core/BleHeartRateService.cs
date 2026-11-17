using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.Advertisement;
using Windows.Devices.Bluetooth.GenericAttributeProfile;

namespace HuaweiHROverlay.Core;

/// <summary>
/// One live BLE heart rate connection. Multiple sessions run in parallel,
/// one per connected watch/band.
/// </summary>
public class BleDeviceSession : IAsyncDisposable
{
    internal static readonly Guid HeartRateServiceUuid     = new("0000180d-0000-1000-8000-00805f9b34fb");
    private  static readonly Guid HeartRateMeasurementUuid = new("00002a37-0000-1000-8000-00805f9b34fb");

    private readonly SynchronizationContext _syncContext;
    private BluetoothLEDevice? _device;
    private GattDeviceService? _service;
    private GattCharacteristic? _characteristic;
    private bool _disposed;

    public string DeviceId { get; }
    public string DeviceName { get; }
    public bool IsConnected { get; private set; }

    public event EventHandler<HeartRateReading>? HeartRateChanged;
    public event EventHandler? ConnectionLost;
    public event EventHandler<string>? StatusChanged;

    public BleDeviceSession(string deviceId, string deviceName, SynchronizationContext? syncContext = null)
    {
        DeviceId = deviceId.ToUpperInvariant();
        DeviceName = deviceName;
        _syncContext = syncContext ?? SynchronizationContext.Current ?? new SynchronizationContext();
    }

    public async Task<bool> ConnectAsync()
    {
        // DeviceId is the Bluetooth address as a 12-char hex string (e.g. "A4C138F12345")
        if (!ulong.TryParse(DeviceId, System.Globalization.NumberStyles.HexNumber, null, out ulong address))
        {
            RaiseStatus("Invalid device address.");
            return false;
        }

        try
        {
            _device = await BluetoothLEDevice.FromBluetoothAddressAsync(address);
            if (_device == null)
            {
                RaiseStatus($"Could not open {DeviceName}. Try pairing it in Windows Bluetooth settings.");
                return false;
            }

            _device.ConnectionStatusChanged += OnConnectionStatusChanged;

            var servicesResult = await _device.GetGattServicesForUuidAsync(
                HeartRateServiceUuid, BluetoothCacheMode.Uncached);
            if (servicesResult.Status != GattCommunicationStatus.Success || servicesResult.Services.Count == 0)
            {
                RaiseStatus($"Heart Rate Service (0x180D) not found on {DeviceName}. Enable HR Data Broadcast on watch.");
                await CleanupAsync();
                return false;
            }
            _service = servicesResult.Services[0];

            var charsResult = await _service.GetCharacteristicsForUuidAsync(
                HeartRateMeasurementUuid, BluetoothCacheMode.Uncached);
            if (charsResult.Status != GattCommunicationStatus.Success || charsResult.Characteristics.Count == 0)
            {
                RaiseStatus($"Heart Rate Measurement (0x2A37) not found on {DeviceName}.");
                await CleanupAsync();
                return false;
            }
            _characteristic = charsResult.Characteristics[0];

            var cccdResult = await _characteristic.WriteClientCharacteristicConfigurationDescriptorAsync(
                GattClientCharacteristicConfigurationDescriptorValue.Notify);
            if (cccdResult != GattCommunicationStatus.Success)
            {
                RaiseStatus($"Failed to enable notifications on {DeviceName}. Re-enable HR Data Broadcast and retry.");
                await CleanupAsync();
                return false;
            }

            _characteristic.ValueChanged += OnHrValueChanged;
            IsConnected = true;
            RaiseStatus($"✓ Connected to {DeviceName} — streaming heart rate data.");
            return true;
        }
        catch (Exception ex)
        {
            RaiseStatus($"Connection error ({DeviceName}): {ex.Message}");
            await CleanupAsync();
            return false;
        }
    }

    private void OnConnectionStatusChanged(BluetoothLEDevice sender, object args)
    {
        if (_disposed || sender.ConnectionStatus != BluetoothConnectionStatus.Disconnected) return;
        _syncContext.Post(async _ =>
        {
            if (_disposed) return;
            await CleanupAsync();
            ConnectionLost?.Invoke(this, EventArgs.Empty);
        }, null);
    }

    private void OnHrValueChanged(GattCharacteristic sender, GattValueChangedEventArgs args)
    {
        // Heart Rate Measurement per BT spec §3.1: byte 0 = flags, bit 0 = UINT16 bpm
        if (args.CharacteristicValue.Length < 2) return;

        var reader = Windows.Storage.Streams.DataReader.FromBuffer(args.CharacteristicValue);
        reader.ByteOrder = Windows.Storage.Streams.ByteOrder.LittleEndian;

        byte flags = reader.ReadByte();
        if ((flags & 0x01) != 0 && args.CharacteristicValue.Length < 3) return;
        int bpm = (flags & 0x01) == 0 ? reader.ReadByte() : reader.ReadUInt16();
        if (bpm is <= 0 or > 300) return;

        var reading = new HeartRateReading
        {
            Bpm = bpm,
            Connected = true,
            Timestamp = DateTime.UtcNow,
            DeviceId = DeviceId,
            DeviceName = DeviceName
        };
        _syncContext.Post(_ => HeartRateChanged?.Invoke(this, reading), null);
    }

    private async Task CleanupAsync()
    {
        IsConnected = false;
        if (_characteristic != null)
        {
            var ch = _characteristic;
            _characteristic = null;
            try
            {
                ch.ValueChanged -= OnHrValueChanged;
                await ch.WriteClientCharacteristicConfigurationDescriptorAsync(
                    GattClientCharacteristicConfigurationDescriptorValue.None);
            }
            catch { /* best effort */ }
        }

        _service?.Dispose();
        _service = null;

        if (_device != null)
        {
            var dev = _device;
            _device = null;
            dev.ConnectionStatusChanged -= OnConnectionStatusChanged;
            dev.Dispose();
        }
    }

    private void RaiseStatus(string message) =>
        _syncContext.Post(_ => StatusChanged?.Invoke(this, message), null);

    public async ValueTask DisposeAsync()
    {
        _disposed = true;
        await CleanupAsync();
    }
}

public sealed record SessionReconnectStateEventArgs(string DeviceId, string DeviceName, bool IsReconnecting);

/// <summary>
/// Scans for Bluetooth LE devices (paired and unpaired/broadcasting) and manages
/// multiple concurrent heart rate connections — one <see cref="BleDeviceSession"/> per watch,
/// with optional automatic reconnection when a watch briefly goes out of range.
/// </summary>
public class BleHeartRateService : IAsyncDisposable
{
    private BluetoothLEAdvertisementWatcher? _adWatcher;
    private readonly Dictionary<string, BleDeviceSession> _sessions = new();
    private readonly Dictionary<string, (string Name, CancellationTokenSource Cts)> _reconnecting = new();
    private readonly Dictionary<ulong, (string Name, bool HasHr)> _adCache = new();
    private readonly object _adLock = new();
    private readonly SynchronizationContext _syncContext;

    public event EventHandler<HeartRateReading>? HeartRateChanged;
    public event EventHandler<BleDeviceInfo>? DeviceDiscovered;
    public event EventHandler<string>? StatusChanged;
    public event EventHandler<BleDeviceSession>? SessionAdded;
    public event EventHandler<(string DeviceId, string DeviceName)>? SessionRemoved;
    public event EventHandler<SessionReconnectStateEventArgs>? SessionReconnectStateChanged;
    public event EventHandler? ScanningStoppedUnexpectedly;

    public bool IsScanning { get; private set; }
    public bool AutoReconnect { get; set; } = true;
    public int SessionCount => _sessions.Count + _reconnecting.Count;
    public IReadOnlyCollection<BleDeviceSession> Sessions => _sessions.Values;

    public bool IsConnectedOrReconnecting(string deviceId)
    {
        string key = deviceId.ToUpperInvariant();
        return _sessions.ContainsKey(key) || _reconnecting.ContainsKey(key);
    }

    public BleHeartRateService()
    {
        _syncContext = SynchronizationContext.Current ?? new SynchronizationContext();
    }

    // ──────────────────────────────────────────────
    // Scanning
    // ──────────────────────────────────────────────

    public void StartScanning()
    {
        if (IsScanning) return;

        lock (_adLock) _adCache.Clear();

        _adWatcher = new BluetoothLEAdvertisementWatcher
        {
            ScanningMode = BluetoothLEScanningMode.Active
        };

        _adWatcher.Received += OnAdvertisementReceived;
        _adWatcher.Stopped  += OnWatcherStopped;
        _adWatcher.Start();

        IsScanning = true;
        RaiseStatus("Scanning for Bluetooth LE devices… (ensure HR Data Broadcast is ON)");
    }

    public void StopScanning()
    {
        if (_adWatcher == null || !IsScanning) return;
        _adWatcher.Stop();
        _adWatcher.Received -= OnAdvertisementReceived;
        _adWatcher.Stopped  -= OnWatcherStopped;
        _adWatcher = null;
        IsScanning = false;
        RaiseStatus("Scan stopped.");
    }

    // ──────────────────────────────────────────────
    // Connections & Auto-Reconnect
    // ──────────────────────────────────────────────

    public async Task<bool> ConnectAsync(string deviceId, string deviceName)
    {
        deviceId = deviceId.ToUpperInvariant();
        if (_sessions.ContainsKey(deviceId))
        {
            RaiseStatus($"Already connected to {deviceName}.");
            return true;
        }

        // Cancel any pending reconnect loop if user explicitly connects
        CancelReconnectLoop(deviceId);

        RaiseStatus($"Connecting to {deviceName}…");
        var session = CreateWiredSession(deviceId, deviceName);

        bool ok = await session.ConnectAsync();
        if (ok)
        {
            _sessions[deviceId] = session;
            RaiseStatus($"✓ {deviceName} connected ({_sessions.Count} live).");
            _syncContext.Post(_ => SessionAdded?.Invoke(this, session), null);
        }
        else
        {
            await session.DisposeAsync();
        }
        return ok;
    }

    public async Task DisconnectAsync(string deviceId)
    {
        deviceId = deviceId.ToUpperInvariant();

        string? removedName = null;
        if (_reconnecting.Remove(deviceId, out var pending))
        {
            pending.Cts.Cancel();
            pending.Cts.Dispose();
            removedName = pending.Name;
        }

        if (_sessions.Remove(deviceId, out var session))
        {
            removedName = session.DeviceName;
            await session.DisposeAsync();
        }

        if (removedName != null)
        {
            RaiseStatus($"{removedName} disconnected.");
            _syncContext.Post(_ => SessionRemoved?.Invoke(this, (deviceId, removedName)), null);
        }
    }

    private BleDeviceSession CreateWiredSession(string deviceId, string deviceName)
    {
        var session = new BleDeviceSession(deviceId, deviceName, _syncContext);
        session.StatusChanged += (_, msg) => RaiseStatus(msg);
        session.HeartRateChanged += (_, reading) => HeartRateChanged?.Invoke(this, reading);
        session.ConnectionLost += (_, _) => OnUnexpectedDisconnect(session);
        return session;
    }

    private async void OnUnexpectedDisconnect(BleDeviceSession lostSession)
    {
        string id = lostSession.DeviceId;
        string name = lostSession.DeviceName;

        if (!_sessions.Remove(id)) return;
        await lostSession.DisposeAsync();

        if (!AutoReconnect)
        {
            RaiseStatus($"{name} disconnected unexpectedly.");
            _syncContext.Post(_ => SessionRemoved?.Invoke(this, (id, name)), null);
            return;
        }

        RaiseStatus($"{name} signal lost — auto-reconnecting…");
        var cts = new CancellationTokenSource();
        _reconnecting[id] = (name, cts);
        _syncContext.Post(_ => SessionReconnectStateChanged?.Invoke(
            this, new SessionReconnectStateEventArgs(id, name, IsReconnecting: true)), null);

        _ = ReconnectLoopAsync(id, name, cts.Token);
    }

    private async Task ReconnectLoopAsync(string deviceId, string deviceName, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(4), ct);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            if (ct.IsCancellationRequested || !_reconnecting.ContainsKey(deviceId))
                return;

            var candidate = CreateWiredSession(deviceId, deviceName);
            bool ok = await candidate.ConnectAsync();
            if (ct.IsCancellationRequested || !_reconnecting.ContainsKey(deviceId))
            {
                await candidate.DisposeAsync();
                return;
            }

            if (ok)
            {
                CancelReconnectLoop(deviceId);
                _sessions[deviceId] = candidate;
                RaiseStatus($"✓ Reconnected to {deviceName}.");
                _syncContext.Post(_ => SessionReconnectStateChanged?.Invoke(
                    this, new SessionReconnectStateEventArgs(deviceId, deviceName, IsReconnecting: false)), null);
                return;
            }

            await candidate.DisposeAsync();
        }
    }

    private void CancelReconnectLoop(string deviceId)
    {
        if (_reconnecting.Remove(deviceId, out var pending))
        {
            pending.Cts.Cancel();
            pending.Cts.Dispose();
        }
    }

    // ──────────────────────────────────────────────
    // Private — Advertisement scanning
    // ──────────────────────────────────────────────

    private void OnAdvertisementReceived(BluetoothLEAdvertisementWatcher sender, BluetoothLEAdvertisementReceivedEventArgs args)
    {
        ulong address = args.BluetoothAddress;
        string? adName = args.Advertisement.LocalName?.Trim();
        bool packetHasHr = args.Advertisement.ServiceUuids.Contains(BleDeviceSession.HeartRateServiceUuid);

        string resolvedName;
        bool resolvedHr;

        lock (_adLock)
        {
            bool existed = _adCache.TryGetValue(address, out var prev);
            resolvedHr = packetHasHr || (existed && prev.HasHr);

            if (!string.IsNullOrEmpty(adName))
                resolvedName = adName;
            else if (existed && !string.IsNullOrEmpty(prev.Name))
                resolvedName = prev.Name;
            else if (resolvedHr)
                resolvedName = $"BLE HR Sensor ({address:X12})";
            else
                return; // Skip unnamed non-HR noise

            _adCache[address] = (resolvedName, resolvedHr);
        }

        int rssi = args.RawSignalStrengthInDBm;
        var info = new BleDeviceInfo(address.ToString("X12"), resolvedName, rssi, resolvedHr);
        _syncContext.Post(_ => DeviceDiscovered?.Invoke(this, info), null);
    }

    private void OnWatcherStopped(BluetoothLEAdvertisementWatcher sender, BluetoothLEAdvertisementWatcherStoppedEventArgs args)
    {
        if (IsScanning)
        {
            IsScanning = false;
            RaiseStatus($"BLE scanner stopped ({args.Error}). Click Scan to retry.");
            _syncContext.Post(_ => ScanningStoppedUnexpectedly?.Invoke(this, EventArgs.Empty), null);
        }
    }

    private void RaiseStatus(string message) =>
        _syncContext.Post(_ => StatusChanged?.Invoke(this, message), null);

    public async ValueTask DisposeAsync()
    {
        StopScanning();
        foreach (var (_, cts) in _reconnecting.Values)
        {
            cts.Cancel();
            cts.Dispose();
        }
        _reconnecting.Clear();

        foreach (var session in _sessions.Values)
        {
            await session.DisposeAsync();
        }
        _sessions.Clear();
    }
}

/// <summary>
/// Discovered BLE device metadata with signal quality and HR service indicators.
/// </summary>
public record BleDeviceInfo(string Id, string Name, int Rssi, bool HasHrService = false)
{
    public string DisplayName => string.IsNullOrWhiteSpace(Name)
        ? $"Unknown ({Id})"
        : Name;

    public string FormattedAddress => Id.Length == 12
        ? $"{Id[0..2]}:{Id[2..4]}:{Id[4..6]}:{Id[6..8]}:{Id[8..10]}:{Id[10..12]}"
        : Id;

    public string RssiDisplay => Rssi != 0 ? $"{Rssi} dBm" : "—";

    public int SignalBars => Rssi switch
    {
        0 => 2,
        >= -62 => 4,
        >= -74 => 3,
        >= -86 => 2,
        _ => 1
    };

    public string SignalBarsGlyph => SignalBars switch
    {
        4 => "▂▄▆█",
        3 => "▂▄▆░",
        2 => "▂▄░░",
        _ => "▂░░░"
    };

    public bool IsLikelyHrWatch => HasHrService
        || Name.Contains("Huawei", StringComparison.OrdinalIgnoreCase)
        || Name.Contains("Watch", StringComparison.OrdinalIgnoreCase)
        || Name.Contains("Band", StringComparison.OrdinalIgnoreCase)
        || Name.Contains("Polar", StringComparison.OrdinalIgnoreCase)
        || Name.Contains("TICKR", StringComparison.OrdinalIgnoreCase)
        || Name.Contains("Garmin", StringComparison.OrdinalIgnoreCase)
        || Name.Contains("HR", StringComparison.OrdinalIgnoreCase);

    public string BadgeText => HasHrService ? "♥ HR 0x180D" : (IsLikelyHrWatch ? "⌚ Wearable" : "BLE");
}
