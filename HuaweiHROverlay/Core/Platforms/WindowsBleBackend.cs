#if WINDOWS_BLE
using System.Globalization;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.Advertisement;
using Windows.Devices.Bluetooth.GenericAttributeProfile;

namespace HuaweiHROverlay.Core.Platforms;

public sealed class WindowsBleBackend : IBlePlatformBackend
{
    private BluetoothLEAdvertisementWatcher? _watcher;

    public string PlatformName => "Windows WinRT BLE";
    public bool IsScanning { get; private set; }

    public event EventHandler<BleAdvertisementPacket>? AdvertisementReceived;
    public event EventHandler<string>? StatusMessage;
    public event EventHandler? ScanStoppedUnexpectedly;

    public Task StartScanningAsync()
    {
        if (IsScanning) return Task.CompletedTask;

        _watcher = new BluetoothLEAdvertisementWatcher
        {
            ScanningMode = BluetoothLEScanningMode.Active
        };
        _watcher.Received += OnReceived;
        _watcher.Stopped += OnStopped;
        _watcher.Start();
        IsScanning = true;
        return Task.CompletedTask;
    }

    public Task StopScanningAsync()
    {
        if (_watcher == null || !IsScanning) return Task.CompletedTask;
        _watcher.Stop();
        _watcher.Received -= OnReceived;
        _watcher.Stopped -= OnStopped;
        _watcher = null;
        IsScanning = false;
        return Task.CompletedTask;
    }

    public async Task<IBleGattSession?> ConnectAsync(string deviceId, string deviceName, CancellationToken ct = default)
    {
        if (!ulong.TryParse(deviceId, NumberStyles.HexNumber, null, out ulong address))
        {
            StatusMessage?.Invoke(this, "Invalid Bluetooth hex address.");
            return null;
        }

        var session = new WindowsGattSession(deviceId, deviceName);
        bool ok = await session.OpenAsync(address, msg => StatusMessage?.Invoke(this, msg));
        if (!ok)
        {
            await session.DisposeAsync();
            return null;
        }
        return session;
    }

    private void OnReceived(BluetoothLEAdvertisementWatcher sender, BluetoothLEAdvertisementReceivedEventArgs args)
    {
        ulong addr = args.BluetoothAddress;
        string id = addr.ToString("X12");
        string? name = args.Advertisement.LocalName?.Trim();
        bool hasHr = args.Advertisement.ServiceUuids.Contains(BleHrPacketParser.HeartRateServiceUuid);
        int rssi = args.RawSignalStrengthInDBm;

        AdvertisementReceived?.Invoke(this, new BleAdvertisementPacket(id, name, rssi, hasHr));
    }

    private void OnStopped(BluetoothLEAdvertisementWatcher sender, BluetoothLEAdvertisementWatcherStoppedEventArgs args)
    {
        if (!IsScanning) return;
        IsScanning = false;
        StatusMessage?.Invoke(this, $"Windows BLE scanner stopped ({args.Error}).");
        ScanStoppedUnexpectedly?.Invoke(this, EventArgs.Empty);
    }

    public async ValueTask DisposeAsync()
    {
        await StopScanningAsync();
    }

    private sealed class WindowsGattSession : IBleGattSession
    {
        private BluetoothLEDevice? _device;
        private GattDeviceService? _service;
        private GattCharacteristic? _characteristic;
        private bool _disposed;

        public string DeviceId { get; }
        public string DeviceName { get; }

        public event EventHandler<int>? BpmReceived;
        public event EventHandler? DisconnectedUnexpectedly;

        public WindowsGattSession(string deviceId, string deviceName)
        {
            DeviceId = deviceId;
            DeviceName = deviceName;
        }

        public async Task<bool> OpenAsync(ulong address, Action<string> reportStatus)
        {
            try
            {
                _device = await BluetoothLEDevice.FromBluetoothAddressAsync(address);
                if (_device == null)
                {
                    reportStatus($"Could not open {DeviceName}. Try pairing in Windows Bluetooth settings.");
                    return false;
                }

                _device.ConnectionStatusChanged += OnConnectionStatusChanged;

                var svcRes = await _device.GetGattServicesForUuidAsync(
                    BleHrPacketParser.HeartRateServiceUuid, BluetoothCacheMode.Uncached);
                if (svcRes.Status != GattCommunicationStatus.Success || svcRes.Services.Count == 0)
                {
                    reportStatus($"Heart Rate Service (0x180D) not found on {DeviceName}. Enable HR Data Broadcast on watch.");
                    return false;
                }
                _service = svcRes.Services[0];

                var chRes = await _service.GetCharacteristicsForUuidAsync(
                    BleHrPacketParser.HeartRateMeasurementUuid, BluetoothCacheMode.Uncached);
                if (chRes.Status != GattCommunicationStatus.Success || chRes.Characteristics.Count == 0)
                {
                    reportStatus($"Heart Rate Measurement (0x2A37) not found on {DeviceName}.");
                    return false;
                }
                _characteristic = chRes.Characteristics[0];

                var cccd = await _characteristic.WriteClientCharacteristicConfigurationDescriptorAsync(
                    GattClientCharacteristicConfigurationDescriptorValue.Notify);
                if (cccd != GattCommunicationStatus.Success)
                {
                    reportStatus($"Failed to enable GATT notifications on {DeviceName}. Re-enable HR Data Broadcast.");
                    return false;
                }

                _characteristic.ValueChanged += OnValueChanged;
                return true;
            }
            catch (Exception ex)
            {
                reportStatus($"Windows BLE error ({DeviceName}): {ex.Message}");
                return false;
            }
        }

        private void OnConnectionStatusChanged(BluetoothLEDevice sender, object args)
        {
            if (_disposed || sender.ConnectionStatus != BluetoothConnectionStatus.Disconnected) return;
            DisconnectedUnexpectedly?.Invoke(this, EventArgs.Empty);
        }

        private void OnValueChanged(GattCharacteristic sender, GattValueChangedEventArgs args)
        {
            byte[] data = args.CharacteristicValue.ToArray();
            if (BleHrPacketParser.TryParseBpm(data, out int bpm))
                BpmReceived?.Invoke(this, bpm);
        }

        public async ValueTask DisposeAsync()
        {
            _disposed = true;
            if (_characteristic != null)
            {
                var ch = _characteristic;
                _characteristic = null;
                try
                {
                    ch.ValueChanged -= OnValueChanged;
                    await ch.WriteClientCharacteristicConfigurationDescriptorAsync(
                        GattClientCharacteristicConfigurationDescriptorValue.None);
                }
                catch { }
            }

            _service?.Dispose();
            _service = null;

            if (_device != null)
            {
                var d = _device;
                _device = null;
                d.ConnectionStatusChanged -= OnConnectionStatusChanged;
                d.Dispose();
            }
        }
    }
}
#endif
