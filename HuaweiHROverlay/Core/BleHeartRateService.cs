using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.Advertisement;
using Windows.Devices.Bluetooth.GenericAttributeProfile;

namespace HuaweiHROverlay.Core;

/// <summary>
/// Scans for Bluetooth LE devices using BluetoothLEAdvertisementWatcher
/// (works for BOTH paired and unpaired/broadcasting devices).
///
/// Connects via BluetoothLEDevice.FromBluetoothAddressAsync and subscribes
/// to the standard GATT Heart Rate Measurement characteristic (0x2A37).
///
/// Huawei watches must have "HR Data Broadcasts" enabled in Settings.
/// </summary>
public class BleHeartRateService : IAsyncDisposable
{
    // Standard BLE GATT UUIDs
    private static readonly Guid HeartRateServiceUuid   = new("0000180d-0000-1000-8000-00805f9b34fb");
    private static readonly Guid HeartRateMeasurementUuid = new("00002a37-0000-1000-8000-00805f9b34fb");

    private BluetoothLEAdvertisementWatcher? _adWatcher;
    private BluetoothLEDevice? _connectedDevice;
    private GattDeviceService? _hrService;
    private GattCharacteristic? _hrCharacteristic;

    // Track discovered addresses so we don't fire duplicate events
    private readonly HashSet<ulong> _seenAddresses = [];
    private readonly SynchronizationContext _syncContext;

    public event EventHandler<HeartRateReading>? HeartRateChanged;
    public event EventHandler<BleDeviceInfo>? DeviceDiscovered;
#pragma warning disable CS0067  // DeviceRemoved kept for API symmetry; advertisement watcher doesn't fire explicit removes
    public event EventHandler<BleDeviceInfo>? DeviceRemoved;
#pragma warning restore CS0067
    public event EventHandler<string>? StatusChanged;
    public event EventHandler? Disconnected;

    public bool IsConnected => _connectedDevice != null && _hrCharacteristic != null;
    public bool IsScanning { get; private set; }
    public string? ConnectedDeviceName { get; private set; }

    public BleHeartRateService()
    {
        _syncContext = SynchronizationContext.Current ?? new SynchronizationContext();
    }

    // ──────────────────────────────────────────────
    // Scanning
    // ──────────────────────────────────────────────

    /// <summary>
    /// Start active BLE advertisement scanning.
    /// Discovers ALL BLE devices (paired and unpaired) including
    /// Huawei watches in HR Data Broadcast mode.
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
    // Connection
    // ──────────────────────────────────────────────

    /// <summary>
    /// Connect to a device by its Bluetooth address (stored as hex string in BleDeviceInfo.Id).
    /// </summary>
    public async Task<bool> ConnectAsync(string deviceId, string deviceName)
    {
        await DisconnectAsync();
        RaiseStatus($"Connecting to {deviceName}…");

        // deviceId is stored as the Bluetooth address hex string (e.g. "A4C138F12345")
        if (!ulong.TryParse(deviceId, System.Globalization.NumberStyles.HexNumber, null, out ulong address))
        {
            RaiseStatus("Invalid device address.");
            return false;
        }

        try
        {
            _connectedDevice = await BluetoothLEDevice.FromBluetoothAddressAsync(address);
            if (_connectedDevice == null)
            {
                RaiseStatus("Could not open BLE device. Try pairing in Windows Bluetooth settings.");
                return false;
            }

            _connectedDevice.ConnectionStatusChanged += OnConnectionStatusChanged;
            RaiseStatus($"Opened device. Getting GATT services…");

            // ── Get Heart Rate service ──
            var servicesResult = await _connectedDevice.GetGattServicesForUuidAsync(
                HeartRateServiceUuid, BluetoothCacheMode.Uncached);

            if (servicesResult.Status != GattCommunicationStatus.Success || servicesResult.Services.Count == 0)
            {
                RaiseStatus($"Heart Rate Service (0x180D) not found on {deviceName}. " +
                            "Is HR Data Broadcast enabled on the watch?");
                await CleanupDeviceAsync();
                return false;
            }

            _hrService = servicesResult.Services[0];

            // ── Get HR Measurement characteristic ──
            var charsResult = await _hrService.GetCharacteristicsForUuidAsync(
                HeartRateMeasurementUuid, BluetoothCacheMode.Uncached);

            if (charsResult.Status != GattCommunicationStatus.Success || charsResult.Characteristics.Count == 0)
            {
                RaiseStatus("Heart Rate Measurement characteristic (0x2A37) not found.");
                await CleanupDeviceAsync();
                return false;
            }

            _hrCharacteristic = charsResult.Characteristics[0];

            // ── Subscribe to GATT notifications ──
            var cccdResult = await _hrCharacteristic.WriteClientCharacteristicConfigurationDescriptorAsync(
                GattClientCharacteristicConfigurationDescriptorValue.Notify);

            if (cccdResult != GattCommunicationStatus.Success)
            {
                RaiseStatus("Failed to enable HR notifications. " +
                            "Re-enable HR Data Broadcast on the watch and try again.");
                await CleanupDeviceAsync();
                return false;
            }

            _hrCharacteristic.ValueChanged += OnHrValueChanged;
            ConnectedDeviceName = deviceName;
            RaiseStatus($"✓ Connected to {deviceName} — waiting for heart rate data…");
            return true;
        }
        catch (Exception ex)
        {
            RaiseStatus($"Connection error: {ex.Message}");
            await CleanupDeviceAsync();
            return false;
        }
    }

    public async Task DisconnectAsync()
    {
        await CleanupDeviceAsync();
        ConnectedDeviceName = null;
        RaiseStatus("Disconnected.");
        Disconnected?.Invoke(this, EventArgs.Empty);
    }

    // ──────────────────────────────────────────────
    // Private — Advertisement scanning
    // ──────────────────────────────────────────────

    private void OnAdvertisementReceived(BluetoothLEAdvertisementWatcher sender, BluetoothLEAdvertisementReceivedEventArgs args)
    {
        var address = args.BluetoothAddress;
        var name    = args.Advertisement.LocalName?.Trim();

        // Skip devices with no name (reduce noise) unless the ad contains HR service UUID
        bool hasHrUuid = args.Advertisement.ServiceUuids.Contains(HeartRateServiceUuid);
        if (string.IsNullOrEmpty(name) && !hasHrUuid) return;

        if (string.IsNullOrEmpty(name))
            name = $"BLE HR Device ({address:X12})";

        int rssi = args.RawSignalStrengthInDBm;

        // De-duplicate: update RSSI if already seen, add new entry otherwise
        if (_seenAddresses.Contains(address))
        {
            // Update RSSI on existing entry
            var info = new BleDeviceInfo(address.ToString("X12"), name, rssi);
            _syncContext.Post(_ => DeviceDiscovered?.Invoke(this, info), null);
            return;
        }

        _seenAddresses.Add(address);
        var newInfo = new BleDeviceInfo(address.ToString("X12"), name, rssi);
        _syncContext.Post(_ => DeviceDiscovered?.Invoke(this, newInfo), null);
    }

    private void OnWatcherStopped(BluetoothLEAdvertisementWatcher sender, BluetoothLEAdvertisementWatcherStoppedEventArgs args)
    {
        if (IsScanning) // unexpected stop
        {
            IsScanning = false;
            RaiseStatus($"BLE watcher stopped unexpectedly (error: {args.Error}). Click Scan to retry.");
        }
    }

    // ──────────────────────────────────────────────
    // Private — GATT
    // ──────────────────────────────────────────────

    private void OnConnectionStatusChanged(BluetoothLEDevice sender, object args)
    {
        if (sender.ConnectionStatus == BluetoothConnectionStatus.Disconnected)
        {
            _syncContext.Post(async _ =>
            {
                RaiseStatus("Device disconnected unexpectedly.");
                await DisconnectAsync();
            }, null);
        }
    }

    private void OnHrValueChanged(GattCharacteristic sender, GattValueChangedEventArgs args)
    {
        // Parse Heart Rate Measurement per BT spec §3.1:
        // Byte 0 = flags; bit 0: 0 = UINT8 HR value, 1 = UINT16 HR value
        if (args.CharacteristicValue.Length < 2) return;

        var reader = Windows.Storage.Streams.DataReader.FromBuffer(args.CharacteristicValue);
        reader.ByteOrder = Windows.Storage.Streams.ByteOrder.LittleEndian;

        byte flags = reader.ReadByte();
        int bpm = (flags & 0x01) == 0
            ? reader.ReadByte()    // 8-bit
            : reader.ReadUInt16(); // 16-bit

        if (bpm is <= 0 or > 300) return; // sanity check

        var reading = new HeartRateReading
        {
            Bpm = bpm,
            Timestamp = DateTime.UtcNow,
            DeviceName = ConnectedDeviceName ?? "Unknown"
        };

        _syncContext.Post(_ => HeartRateChanged?.Invoke(this, reading), null);
    }

    private async Task CleanupDeviceAsync()
    {
        if (_hrCharacteristic != null)
        {
            try
            {
                _hrCharacteristic.ValueChanged -= OnHrValueChanged;
                await _hrCharacteristic.WriteClientCharacteristicConfigurationDescriptorAsync(
                    GattClientCharacteristicConfigurationDescriptorValue.None);
            }
            catch { /* best effort */ }
            _hrCharacteristic = null;
        }

        if (_hrService != null)
        {
            _hrService.Dispose();
            _hrService = null;
        }

        if (_connectedDevice != null)
        {
            _connectedDevice.ConnectionStatusChanged -= OnConnectionStatusChanged;
            _connectedDevice.Dispose();
            _connectedDevice = null;
        }
    }

    private void RaiseStatus(string message) =>
        _syncContext.Post(_ => StatusChanged?.Invoke(this, message), null);

    public async ValueTask DisposeAsync()
    {
        StopScanning();
        await CleanupDeviceAsync();
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
