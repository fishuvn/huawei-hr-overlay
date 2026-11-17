using System.Collections.Concurrent;
using Tmds.DBus;

namespace HuaweiHROverlay.Core.Platforms;

[DBusInterface("org.freedesktop.DBus.ObjectManager")]
public interface IBlueZObjectManager : IDBusObject
{
    Task<IDictionary<ObjectPath, IDictionary<string, IDictionary<string, object>>>> GetManagedObjectsAsync();
    Task<IDisposable> WatchInterfacesAddedAsync(
        Action<(ObjectPath path, IDictionary<string, IDictionary<string, object>> interfaces)> handler,
        Action<Exception>? onError = null);
}

[DBusInterface("org.bluez.Adapter1")]
public interface IBlueZAdapter1 : IDBusObject
{
    Task SetDiscoveryFilterAsync(IDictionary<string, object> properties);
    Task StartDiscoveryAsync();
    Task StopDiscoveryAsync();
    Task<T> GetAsync<T>(string prop);
}

[DBusInterface("org.bluez.Device1")]
public interface IBlueZDevice1 : IDBusObject
{
    Task ConnectAsync();
    Task DisconnectAsync();
    Task<T> GetAsync<T>(string prop);
    Task<IDictionary<string, object>> GetAllAsync();
    Task<IDisposable> WatchPropertiesAsync(Action<PropertyChanges> handler);
}

[DBusInterface("org.bluez.GattCharacteristic1")]
public interface IBlueZGattCharacteristic1 : IDBusObject
{
    Task StartNotifyAsync();
    Task StopNotifyAsync();
    Task<byte[]> ReadValueAsync(IDictionary<string, object> options);
    Task<IDisposable> WatchPropertiesAsync(Action<PropertyChanges> handler);
}

/// <summary>
/// Native Linux Bluetooth LE backend powered by BlueZ over System D-Bus (org.bluez).
/// Works out of the box across Debian/Ubuntu, Arch Linux, Fedora, openSUSE, and derivatives
/// without requiring root privileges.
/// </summary>
public sealed class LinuxBlueZBackend : IBlePlatformBackend
{
    private const string BlueZService = "org.bluez";
    private const string HrServiceUuidStr = "0000180d-0000-1000-8000-00805f9b34fb";
    private const string HrCharUuidStr = "00002a37-0000-1000-8000-00805f9b34fb";

    private Connection? _dbus;
    private IBlueZObjectManager? _objectManager;
    private IBlueZAdapter1? _adapter;
    private IDisposable? _interfacesAddedWatcher;
    private readonly ConcurrentDictionary<string, IDisposable> _deviceWatchers = new();
    private readonly ConcurrentDictionary<string, ObjectPath> _devicePathsById = new(StringComparer.OrdinalIgnoreCase);

    public string PlatformName => "Linux BlueZ (D-Bus)";
    public bool IsScanning { get; private set; }

    public event EventHandler<BleAdvertisementPacket>? AdvertisementReceived;
    public event EventHandler<string>? StatusMessage;
    public event EventHandler? ScanStoppedUnexpectedly;

    private async Task<bool> EnsureConnectedToBlueZAsync()
    {
        if (_dbus != null && _objectManager != null && _adapter != null)
            return true;

        try
        {
            _dbus = new Connection(Address.System);
            await _dbus.ConnectAsync();
            _objectManager = _dbus.CreateProxy<IBlueZObjectManager>(BlueZService, new ObjectPath("/"));

            var objects = await _objectManager.GetManagedObjectsAsync();
            var adapterEntry = objects.FirstOrDefault(kv => kv.Value.ContainsKey("org.bluez.Adapter1"));
            if (adapterEntry.Value == null)
            {
                StatusMessage?.Invoke(this, "No Bluetooth adapter found in BlueZ. Plug in a BT 5.0+ adapter and ensure 'bluetooth' service is running.");
                return false;
            }

            _adapter = _dbus.CreateProxy<IBlueZAdapter1>(BlueZService, adapterEntry.Key);
            bool powered = false;
            try { powered = await _adapter.GetAsync<bool>("Powered"); } catch { }
            if (!powered)
            {
                StatusMessage?.Invoke(this, "Bluetooth adapter is powered OFF. Turn Bluetooth on in desktop settings or run 'bluetoothctl power on'.");
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            StatusMessage?.Invoke(this, $"Could not connect to Linux BlueZ D-Bus: {ex.Message}. Ensure 'sudo systemctl start bluetooth' is active.");
            return false;
        }
    }

    public async Task StartScanningAsync()
    {
        if (IsScanning) return;
        if (!await EnsureConnectedToBlueZAsync() || _objectManager == null || _adapter == null || _dbus == null)
        {
            ScanStoppedUnexpectedly?.Invoke(this, EventArgs.Empty);
            return;
        }

        try
        {
            var objects = await _objectManager.GetManagedObjectsAsync();
            foreach (var (path, ifaces) in objects)
            {
                if (ifaces.TryGetValue("org.bluez.Device1", out var props))
                    await TrackAndPublishDeviceAsync(path, props);
            }

            _interfacesAddedWatcher = await _objectManager.WatchInterfacesAddedAsync(
                async tuple =>
                {
                    if (tuple.interfaces.TryGetValue("org.bluez.Device1", out var props))
                        await TrackAndPublishDeviceAsync(tuple.path, props);
                },
                ex =>
                {
                    if (!IsScanning) return;
                    IsScanning = false;
                    StatusMessage?.Invoke(this, $"BlueZ discovery stream interrupted: {ex.Message}");
                    ScanStoppedUnexpectedly?.Invoke(this, EventArgs.Empty);
                });

            try
            {
                await _adapter.SetDiscoveryFilterAsync(new Dictionary<string, object>
                {
                    ["Transport"] = "le",
                    ["DuplicateData"] = true
                });
            }
            catch { /* Some older kernels/controllers reject DuplicateData; standard LE scan still works */ }

            await _adapter.StartDiscoveryAsync();
            IsScanning = true;
        }
        catch (Exception ex)
        {
            StatusMessage?.Invoke(this, $"BlueZ scan failed: {ex.Message}");
            ScanStoppedUnexpectedly?.Invoke(this, EventArgs.Empty);
        }
    }

    public async Task StopScanningAsync()
    {
        if (!IsScanning) return;
        IsScanning = false;

        _interfacesAddedWatcher?.Dispose();
        _interfacesAddedWatcher = null;

        foreach (var d in _deviceWatchers.Values)
            d.Dispose();
        _deviceWatchers.Clear();

        if (_adapter != null)
        {
            try { await _adapter.StopDiscoveryAsync(); } catch { }
        }
    }

    private async Task TrackAndPublishDeviceAsync(ObjectPath path, IDictionary<string, object> initialProps)
    {
        PublishFromProperties(path, initialProps);

        string pathKey = path.ToString();
        if (_dbus == null || _deviceWatchers.ContainsKey(pathKey)) return;

        try
        {
            var devProxy = _dbus.CreateProxy<IBlueZDevice1>(BlueZService, path);
            var watcher = await devProxy.WatchPropertiesAsync(async _ =>
            {
                try
                {
                    var all = await devProxy.GetAllAsync();
                    PublishFromProperties(path, all);
                }
                catch { }
            });

            if (!_deviceWatchers.TryAdd(pathKey, watcher))
                watcher.Dispose();
        }
        catch { }
    }

    private void PublishFromProperties(ObjectPath path, IDictionary<string, object> props)
    {
        if (!props.TryGetValue("Address", out var addrObj) || addrObj is not string rawAddr || string.IsNullOrWhiteSpace(rawAddr))
            return;

        string id = rawAddr.Replace(":", "", StringComparison.Ordinal).Trim().ToUpperInvariant();
        _devicePathsById[id] = path;

        string? name = null;
        if (props.TryGetValue("Name", out var nObj) && nObj is string nStr && !string.IsNullOrWhiteSpace(nStr))
            name = nStr.Trim();
        else if (props.TryGetValue("Alias", out var aObj) && aObj is string aStr && !string.IsNullOrWhiteSpace(aStr)
                 && !string.Equals(aStr.Replace("-", ":"), rawAddr, StringComparison.OrdinalIgnoreCase))
            name = aStr.Trim();

        int rssi = 0;
        if (props.TryGetValue("RSSI", out var rObj))
        {
            rssi = rObj switch
            {
                short s => s,
                int i => i,
                long l => (int)l,
                _ => 0
            };
        }

        bool hasHr = false;
        if (props.TryGetValue("UUIDs", out var uuidsObj) && uuidsObj is IEnumerable<string> uuids)
        {
            hasHr = uuids.Any(u => u.Equals(HrServiceUuidStr, StringComparison.OrdinalIgnoreCase)
                                || u.StartsWith("0000180d", StringComparison.OrdinalIgnoreCase));
        }

        AdvertisementReceived?.Invoke(this, new BleAdvertisementPacket(id, name, rssi, hasHr, rawAddr.ToUpperInvariant()));
    }

    public async Task<IBleGattSession?> ConnectAsync(string deviceId, string deviceName, CancellationToken ct = default)
    {
        if (!await EnsureConnectedToBlueZAsync() || _dbus == null || _objectManager == null)
            return null;

        string normId = deviceId.Replace(":", "", StringComparison.Ordinal).Trim().ToUpperInvariant();
        if (!_devicePathsById.TryGetValue(normId, out ObjectPath devPath))
        {
            // Scan managed objects in case device is already paired/known to BlueZ
            var objects = await _objectManager.GetManagedObjectsAsync();
            foreach (var (p, ifaces) in objects)
            {
                if (ifaces.TryGetValue("org.bluez.Device1", out var props)
                    && props.TryGetValue("Address", out var a) && a is string addr
                    && addr.Replace(":", "", StringComparison.Ordinal).Equals(normId, StringComparison.OrdinalIgnoreCase))
                {
                    devPath = p;
                    _devicePathsById[normId] = p;
                    break;
                }
            }
        }

        if (string.IsNullOrEmpty(devPath.ToString()))
        {
            StatusMessage?.Invoke(this, $"Device {deviceName} not found in BlueZ — run Scan first.");
            return null;
        }

        var session = new BlueZGattSession(_dbus, _objectManager, devPath, normId, deviceName);
        bool ok = await session.OpenAsync(msg => StatusMessage?.Invoke(this, msg), ct);
        if (!ok)
        {
            await session.DisposeAsync();
            return null;
        }
        return session;
    }

    public async ValueTask DisposeAsync()
    {
        await StopScanningAsync();
        _dbus?.Dispose();
        _dbus = null;
    }

    private sealed class BlueZGattSession(
        Connection dbus,
        IBlueZObjectManager objectManager,
        ObjectPath devicePath,
        string deviceId,
        string deviceName) : IBleGattSession
    {
        private IBlueZDevice1? _device;
        private IBlueZGattCharacteristic1? _char;
        private IDisposable? _charPropWatch;
        private IDisposable? _devPropWatch;
        private bool _disposed;

        public string DeviceId { get; } = deviceId;
        public string DeviceName { get; } = deviceName;

        public event EventHandler<int>? BpmReceived;
        public event EventHandler? DisconnectedUnexpectedly;

        public async Task<bool> OpenAsync(Action<string> reportStatus, CancellationToken ct)
        {
            try
            {
                _device = dbus.CreateProxy<IBlueZDevice1>(BlueZService, devicePath);

                bool alreadyConnected = false;
                try { alreadyConnected = await _device.GetAsync<bool>("Connected"); } catch { }
                if (!alreadyConnected)
                {
                    await _device.ConnectAsync();
                }

                // Wait for GATT ServicesResolved == true (typically 0.5s - 4s on Linux BlueZ)
                for (int i = 0; i < 48; i++)
                {
                    ct.ThrowIfCancellationRequested();
                    bool resolved = false;
                    try { resolved = await _device.GetAsync<bool>("ServicesResolved"); } catch { }
                    if (resolved) break;
                    await Task.Delay(250, ct);
                }

                string prefix = devicePath.ToString() + "/";
                var objects = await objectManager.GetManagedObjectsAsync();
                ObjectPath? hrCharPath = null;

                foreach (var (p, ifaces) in objects)
                {
                    if (!p.ToString().StartsWith(prefix, StringComparison.Ordinal)) continue;
                    if (ifaces.TryGetValue("org.bluez.GattCharacteristic1", out var cProps)
                        && cProps.TryGetValue("UUID", out var uObj) && uObj is string uStr
                        && (uStr.Equals(HrCharUuidStr, StringComparison.OrdinalIgnoreCase)
                            || uStr.StartsWith("00002a37", StringComparison.OrdinalIgnoreCase)))
                    {
                        hrCharPath = p;
                        break;
                    }
                }

                if (hrCharPath == null)
                {
                    reportStatus($"Heart Rate Service (0x180D / 0x2A37) not found on {DeviceName}. Enable HR Data Broadcast on watch.");
                    return false;
                }

                _char = dbus.CreateProxy<IBlueZGattCharacteristic1>(BlueZService, hrCharPath.Value);
                _charPropWatch = await _char.WatchPropertiesAsync(changes =>
                {
                    if (_disposed) return;
                    foreach (var kv in changes.Changed)
                    {
                        if (kv.Key == "Value" && kv.Value is byte[] bytes && BleHrPacketParser.TryParseBpm(bytes, out int bpm))
                        {
                            BpmReceived?.Invoke(this, bpm);
                        }
                    }
                });

                await _char.StartNotifyAsync();

                _devPropWatch = await _device.WatchPropertiesAsync(changes =>
                {
                    if (_disposed) return;
                    foreach (var kv in changes.Changed)
                    {
                        if (kv.Key == "Connected" && kv.Value is bool isConn && !isConn)
                        {
                            DisconnectedUnexpectedly?.Invoke(this, EventArgs.Empty);
                        }
                    }
                });

                return true;
            }
            catch (Exception ex)
            {
                reportStatus($"BlueZ GATT connection error ({DeviceName}): {ex.Message}");
                return false;
            }
        }

        public async ValueTask DisposeAsync()
        {
            if (_disposed) return;
            _disposed = true;

            _charPropWatch?.Dispose();
            _devPropWatch?.Dispose();

            if (_char != null)
            {
                try { await _char.StopNotifyAsync(); } catch { }
                _char = null;
            }

            if (_device != null)
            {
                try { await _device.DisconnectAsync(); } catch { }
                _device = null;
            }
        }
    }
}
