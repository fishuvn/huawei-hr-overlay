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
    private static readonly Guid HeartRateServiceUuid     = new("0000180d-0000-1000-8000-00805f9b34fb");
    private static readonly Guid HeartRateMeasurementUuid = new("00002a37-0000-1000-8000-00805f9b34fb");

    private readonly SynchronizationContext _syncContext;
    private BluetoothLEDevice? _device;
    private GattDeviceService? _service;
    private GattCharacteristic? _characteristic;

    public string DeviceId { get; }
    public string DeviceName { get; }
    public bool IsConnected { get; private set; }

    public event EventHandler<HeartRateReading>? HeartRateChanged;
    public event EventHandler? ConnectionLost;
    public event EventHandler<string>? StatusChanged;

    public BleDeviceSession(string deviceId, string deviceName)
    {
        DeviceId = deviceId.ToUpperInvariant();
        DeviceName = deviceName;
        _syncContext = SynchronizationContext.Current ?? new SynchronizationContext();
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
                RaiseStatus($"Heart Rate Service (0x180D) not found on {DeviceName}. Is HR Data Broadcast enabled?");
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
            RaiseStatus($"✓ Connected to {DeviceName} — waiting for heart rate data…");
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
        if (sender.ConnectionStatus != BluetoothConnectionStatus.Disconnected) return;
        _syncContext.Post(async _ =>
        {
            RaiseStatus($"{DeviceName} disconnected unexpectedly.");
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
            try
            {
                _characteristic.ValueChanged -= OnHrValueChanged;
                await _characteristic.WriteClientCharacteristicConfigurationDescriptorAsync(
                    GattClientCharacteristicConfigurationDescriptorValue.None);
            }
            catch { /* best effort */ }
            _characteristic = null;
        }

        _service?.Dispose();
        _service = null;

        if (_device != null)
        {
            _device.ConnectionStatusChanged -= OnConnectionStatusChanged;
            _device.Dispose();
            _device = null;
        }
    }

    private void RaiseStatus(string message) =>
        _syncContext.Post(_ => StatusChanged?.Invoke(this, message), null);

    public async ValueTask DisposeAsync() => await CleanupAsync();
}

/// <summary>
/// Scans for Bluetooth LE devices (paired and unpaired/broadcasting) and manages
/// multiple concurrent heart rate connections — one <see cref="BleDeviceSession"/> per watch.
/// Huawei watches must have "HR Data Broadcasts" enabled in Settings.
/// </summary>
public class BleHeartRateService : IAsyncDisposable
{
    private BluetoothLEAdvertisementWatcher? _adWatcher;
    private readonly Dictionary<string, BleDeviceSession> _sessions = new();
    private readonly HashSet<ulong> _seenAddresses = [];
    private readonly SynchronizationContext _syncContext;

    public event EventHandler<HeartRateReading>? HeartRateChanged;
    public event EventHandler<BleDeviceInfo>? DeviceDiscovered;
    public event EventHandler<string>? StatusChanged;
    public event EventHandler<BleDeviceSession>? SessionAdded;
    public event EventHandler<BleDeviceSession>? SessionRemoved;
    public event EventHandler? ScanningStoppedUnexpectedly;

    public bool IsScanning { get; private set; }
    public int SessionCount => _sessions.Count;
    public IReadOnlyCollection<BleDeviceSession> Sessions => _sessions.Values;
    public bool IsConnectedTo(string deviceId) => _sessions.ContainsKey(deviceId.ToUpperInvariant());

    public BleHeartRateService()
    {
        _syncContext = SynchronizationContext.Current ?? new SynchronizationContext();
    }

    // ──────────────────────────────────────────────
    // Scanning
    // ──────────────────────────────────────────────

    /// <summary>
    /// Start active BLE advertisement scanning. Stays on while devices connect
    /// so more watches can be added.
    /// </summary>
    public void StartScanning()
    {
        if (IsScanning) return;

        _seenAddresses.Clear();

        _adWatcher = new BluetoothLEAdvertisementWatcher
        {
            ScanningMode = BluetoothLEScanningMode.Active
        };

        // No service UUID filter — scan everything so we catch Huawei devices
        // that may not include the Heart Rate UUID in their advertisement packets.
        _adWatcher.Received += OnAdvertisementReceived;
        _adWatcher.Stopped  += OnWatcherStopped;
        _adWatcher.Start();

        IsScanning = true;
        RaiseStatus("Scanning for Bluetooth LE devices… (make sure HR Data Broadcast is ON)");
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
    // Connections
    // ──────────────────────────────────────────────

    public async Task<bool> ConnectAsync(string deviceId, string deviceName)
    {
        deviceId = deviceId.ToUpperInvariant();
        if (_sessions.ContainsKey(deviceId))
        {
            RaiseStatus($"Already connected to {deviceName}.");
            return true;
        }

        RaiseStatus($"Connecting to {deviceName}…");
        var session = new BleDeviceSession(deviceId, deviceName);
        session.StatusChanged += (_, msg) => RaiseStatus(msg);
        session.HeartRateChanged += (_, reading) => HeartRateChanged?.Invoke(this, reading);
        session.ConnectionLost += (_, _) => RemoveSession(session.DeviceId);

        bool ok = await session.ConnectAsync();
        if (ok)
        {
            _sessions[deviceId] = session;
            RaiseStatus($"✓ {deviceName} connected. {_sessions.Count} device(s) live — keep scanning to add more.");
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
        if (!_sessions.Remove(deviceId, out var session)) return;
        await session.DisposeAsync();
        RaiseStatus($"{session.DeviceName} disconnected.");
        _syncContext.Post(_ => SessionRemoved?.Invoke(this, session), null);
    }

    private void RemoveSession(string deviceId)
    {
        if (!_sessions.Remove(deviceId, out var session)) return;
        _syncContext.Post(_ => SessionRemoved?.Invoke(this, session), null);
    }

    // ──────────────────────────────────────────────
    // Private — Advertisement scanning
    // ──────────────────────────────────────────────

    private void OnAdvertisementReceived(BluetoothLEAdvertisementWatcher sender, BluetoothLEAdvertisementReceivedEventArgs args)
    {
        var address = args.BluetoothAddress;
        var name    = args.Advertisement.LocalName?.Trim();

        // Skip devices with no name (reduce noise) unless the ad contains HR service UUID
        bool hasHrUuid = args.Advertisement.ServiceUuids.Contains(new Guid("0000180d-0000-1000-8000-00805f9b34fb"));
        if (string.IsNullOrEmpty(name) && !hasHrUuid) return;

        if (string.IsNullOrEmpty(name))
            name = $"BLE HR Device ({address:X12})";

        int rssi = args.RawSignalStrengthInDBm;

        // The UI de-duplicates by Id and refreshes existing entries (incl. RSSI),
        // so re-posting a known address is safe.
        _seenAddresses.Add(address);
        var info = new BleDeviceInfo(address.ToString("X12"), name, rssi);
        _syncContext.Post(_ => DeviceDiscovered?.Invoke(this, info), null);
    }

    private void OnWatcherStopped(BluetoothLEAdvertisementWatcher sender, BluetoothLEAdvertisementWatcherStoppedEventArgs args)
    {
        if (IsScanning) // unexpected stop
        {
            IsScanning = false;
            RaiseStatus($"BLE watcher stopped unexpectedly (error: {args.Error}). Click Scan to retry.");
            _syncContext.Post(_ => ScanningStoppedUnexpectedly?.Invoke(this, EventArgs.Empty), null);
        }
    }

    private void RaiseStatus(string message) =>
        _syncContext.Post(_ => StatusChanged?.Invoke(this, message), null);

    public async ValueTask DisposeAsync()
    {
        StopScanning();
        foreach (var session in _sessions.Values)
        {
            await session.DisposeAsync();
        }
        _sessions.Clear();
    }
}

/// <summary>
/// Lightweight info record for a discovered BLE device.
/// Id = Bluetooth address as 12-char hex string (e.g. "A4C138F12345").
/// </summary>
public record BleDeviceInfo(string Id, string Name, int Rssi)
{
    public string DisplayName => string.IsNullOrWhiteSpace(Name)
        ? $"Unknown ({Id})"
        : Name;

    public string RssiDisplay => Rssi != 0 ? $"{Rssi} dBm" : "—";
}
