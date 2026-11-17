using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace HuaweiHROverlay.Core.Platforms;

/// <summary>
/// Native in-process macOS CoreBluetooth backend via Objective-C runtime (libobjc.A.dylib).
/// Works on both Apple Silicon (osx-arm64) and Intel (osx-x64) without external daemons,
/// allowing macOS TCC to attribute NSBluetoothAlwaysUsageDescription directly to the .app bundle.
/// </summary>
public sealed class MacCoreBluetoothBackend : IBlePlatformBackend
{
    private static MacCoreBluetoothBackend? s_active;
    private static IntPtr s_delegateClass;
    private static IntPtr s_msgSend;

    // Cached Objective-C selectors
    private static IntPtr s_selAlloc, s_selInit, s_selRetain, s_selRelease;
    private static IntPtr s_selUtf8String, s_selStringWithUtf8;
    private static IntPtr s_selUuidWithString, s_selUuidString, s_selIdentifier;
    private static IntPtr s_selArrayWithObject, s_selCount, s_selObjectAtIndex;
    private static IntPtr s_selDictWithObjsForKeysCount, s_selObjectForKey;
    private static IntPtr s_selNumberWithBool, s_selIntValue;
    private static IntPtr s_selBytes, s_selLength;
    private static IntPtr s_selInitWithDelegateQueue, s_selState, s_selScanForPeripherals, s_selStopScan;
    private static IntPtr s_selConnectPeripheral, s_selCancelPeripheralConnection, s_selRetrievePeripherals;
    private static IntPtr s_selInitWithUuidString;
    private static IntPtr s_selName, s_selSetDelegate, s_selDiscoverServices, s_selServices;
    private static IntPtr s_selDiscoverCharacteristics, s_selCharacteristics, s_selUuid;
    private static IntPtr s_selSetNotifyValue, s_selValue;

    // Cached Objective-C classes
    private static IntPtr s_clsNSObject, s_clsNSString, s_clsNSArray, s_clsNSDictionary, s_clsNSNumber, s_clsNSUUID;
    private static IntPtr s_clsCBCentralManager, s_clsCBUUID;

    private readonly ConcurrentDictionary<string, IntPtr> _peripherals = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, PendingConnect> _pendingConnects = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, MacGattSession> _sessions = new(StringComparer.OrdinalIgnoreCase);

    private IntPtr _dispatchQueue;
    private IntPtr _delegateInstance;
    private IntPtr _centralManager;
    private volatile int _adapterState; // 5 == CBManagerStatePoweredOn
    private bool _wantScanning;

    public string PlatformName => "macOS CoreBluetooth";
    public bool IsScanning { get; private set; }

    public event EventHandler<BleAdvertisementPacket>? AdvertisementReceived;
    public event EventHandler<string>? StatusMessage;
    public event EventHandler? ScanStoppedUnexpectedly;

    private sealed class PendingConnect(string id, string name)
    {
        public string DeviceId { get; } = id;
        public string DeviceName { get; } = name;
        public TaskCompletionSource<MacGattSession?> Tcs { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    public unsafe MacCoreBluetoothBackend()
    {
        EnsureRuntimeInitialized();
        s_active = this;

        byte[] queueLabel = "com.huaweihroverlay.ble\0"u8.ToArray();
        fixed (byte* pLabel = queueLabel)
        {
            _dispatchQueue = dispatch_queue_create(pLabel, IntPtr.Zero);
        }

        _delegateInstance = Send0(Send0(s_delegateClass, s_selAlloc), s_selInit);
        _centralManager = Send2(Send0(s_clsCBCentralManager, s_selAlloc), s_selInitWithDelegateQueue, _delegateInstance, _dispatchQueue);
    }

    public async Task StartScanningAsync()
    {
        if (IsScanning) return;
        _wantScanning = true;

        for (int i = 0; i < 20 && _adapterState == 0; i++)
            await Task.Delay(100);

        if (_adapterState != 5)
        {
            StatusMessage?.Invoke(this, DescribeAdapterState(_adapterState));
            if (_adapterState is 2 or 3 or 4)
            {
                _wantScanning = false;
                return;
            }
        }

        StartNativeScan();
        IsScanning = true;
    }

    private unsafe void StartNativeScan()
    {
        if (_centralManager == IntPtr.Zero) return;

        // Options: @{ CBCentralManagerScanOptionAllowDuplicatesKey : @YES }
        IntPtr keyAllowDup = CreateNSString("kCBScanOptionAllowDuplicates");
        IntPtr valYes = Send1Bool(s_clsNSNumber, s_selNumberWithBool, 1);
        IntPtr keysPtr = keyAllowDup;
        IntPtr objsPtr = valYes;
        IntPtr optionsDict = Send3PtrPtrUInt(s_clsNSDictionary, s_selDictWithObjsForKeysCount, &objsPtr, &keysPtr, 1);

        // Pass nil services so Huawei watches that omit 0x180D in primary ad packet still appear
        Send2(_centralManager, s_selScanForPeripherals, IntPtr.Zero, optionsDict);
    }

    public Task StopScanningAsync()
    {
        _wantScanning = false;
        if (!IsScanning) return Task.CompletedTask;
        if (_centralManager != IntPtr.Zero)
            Send0Void(_centralManager, s_selStopScan);
        IsScanning = false;
        return Task.CompletedTask;
    }

    public async Task<IBleGattSession?> ConnectAsync(string deviceId, string deviceName, CancellationToken ct = default)
    {
        string normId = NormalizeId(deviceId);
        if (!_peripherals.TryGetValue(normId, out IntPtr peripheral) || peripheral == IntPtr.Zero)
        {
            peripheral = TryRetrievePeripheralByUuid(normId);
            if (peripheral == IntPtr.Zero)
            {
                StatusMessage?.Invoke(this, $"Device {deviceName} not in cache — scan first to discover it.");
                return null;
            }
            _peripherals[normId] = peripheral;
        }

        var pending = new PendingConnect(normId, deviceName);
        _pendingConnects[normId] = pending;

        Send1Void(peripheral, s_selSetDelegate, _delegateInstance);
        Send2Void(_centralManager, s_selConnectPeripheral, peripheral, IntPtr.Zero);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(15));
        try
        {
            return await pending.Tcs.Task.WaitAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException)
        {
            _pendingConnects.TryRemove(normId, out _);
            Send1Void(_centralManager, s_selCancelPeripheralConnection, peripheral);
            StatusMessage?.Invoke(this, $"Timed out connecting to {deviceName}. Ensure HR Data Broadcast is ON.");
            return null;
        }
    }

    private IntPtr TryRetrievePeripheralByUuid(string normId)
    {
        if (normId.Length != 32) return IntPtr.Zero;
        string dashed = $"{normId[..8]}-{normId[8..12]}-{normId[12..16]}-{normId[16..20]}-{normId[20..]}";
        IntPtr nsStr = CreateNSString(dashed);
        IntPtr nsUuid = Send1(Send0(s_clsNSUUID, s_selAlloc), s_selInitWithUuidString, nsStr);
        if (nsUuid == IntPtr.Zero) return IntPtr.Zero;

        IntPtr arr = Send1(s_clsNSArray, s_selArrayWithObject, nsUuid);
        IntPtr found = Send1(_centralManager, s_selRetrievePeripherals, arr);
        if (found == IntPtr.Zero || Send0UInt(found, s_selCount) == 0) return IntPtr.Zero;

        IntPtr p = Send1UInt(found, s_selObjectAtIndex, 0);
        if (p != IntPtr.Zero) Send0(p, s_selRetain);
        return p;
    }

    private static string DescribeAdapterState(int state) => state switch
    {
        2 => "This Mac does not support Bluetooth LE.",
        3 => "Bluetooth permission denied. Grant access in System Settings → Privacy & Security → Bluetooth.",
        4 => "macOS Bluetooth is turned off. Enable Bluetooth in Control Center.",
        _ => "Initializing macOS CoreBluetooth…"
    };

    // ── Objective-C Unmanaged Delegate Callbacks ──────────────────────────

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnCentralDidUpdateState(IntPtr self, IntPtr cmd, IntPtr central)
    {
        var inst = s_active;
        if (inst == null) return;
        int state = (int)Send0NInt(central, s_selState);
        inst._adapterState = state;

        if (state == 5 && inst._wantScanning)
        {
            inst.StartNativeScan();
            inst.IsScanning = true;
            inst.StatusMessage?.Invoke(inst, "Scanning via macOS CoreBluetooth…");
        }
        else if (state != 5 && inst.IsScanning)
        {
            inst.IsScanning = false;
            inst.StatusMessage?.Invoke(inst, DescribeAdapterState(state));
            inst.ScanStoppedUnexpectedly?.Invoke(inst, EventArgs.Empty);
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnDidDiscoverPeripheral(IntPtr self, IntPtr cmd, IntPtr central, IntPtr peripheral, IntPtr advData, IntPtr rssiNum)
    {
        var inst = s_active;
        if (inst == null || peripheral == IntPtr.Zero) return;

        string id = GetPeripheralId(peripheral);
        if (string.IsNullOrEmpty(id)) return;

        if (!inst._peripherals.ContainsKey(id))
        {
            Send0(peripheral, s_selRetain);
            inst._peripherals[id] = peripheral;
        }

        string? name = ReadNSString(Send0(peripheral, s_selName));
        if (string.IsNullOrWhiteSpace(name) && advData != IntPtr.Zero)
        {
            IntPtr kLocalName = CreateNSString("kCBAdvDataLocalName");
            name = ReadNSString(Send1(advData, s_selObjectForKey, kLocalName));
        }

        bool hasHr = false;
        if (advData != IntPtr.Zero)
        {
            IntPtr kUuids = CreateNSString("kCBAdvDataServiceUUIDs");
            IntPtr uuidArr = Send1(advData, s_selObjectForKey, kUuids);
            if (uuidArr != IntPtr.Zero)
            {
                nuint count = Send0UInt(uuidArr, s_selCount);
                for (nuint i = 0; i < count; i++)
                {
                    IntPtr u = Send1UInt(uuidArr, s_selObjectAtIndex, i);
                    string? us = ReadNSString(Send0(u, s_selUuidString));
                    if (us != null && (us.Equals("180D", StringComparison.OrdinalIgnoreCase)
                        || us.StartsWith("0000180d", StringComparison.OrdinalIgnoreCase)))
                    {
                        hasHr = true;
                        break;
                    }
                }
            }
        }

        int rssi = rssiNum != IntPtr.Zero ? Send0Int(rssiNum, s_selIntValue) : 0;
        if (rssi == 127) rssi = 0; // CoreBluetooth unavailable RSSI sentinel

        string shortDisplay = id.Length >= 12 ? $"UUID {id[..12]}…" : id;
        inst.AdvertisementReceived?.Invoke(inst, new BleAdvertisementPacket(id, name?.Trim(), rssi, hasHr, shortDisplay));
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnDidConnectPeripheral(IntPtr self, IntPtr cmd, IntPtr central, IntPtr peripheral)
    {
        IntPtr hrUuid = Send1(s_clsCBUUID, s_selUuidWithString, CreateNSString("180D"));
        IntPtr servicesArr = Send1(s_clsNSArray, s_selArrayWithObject, hrUuid);
        Send1Void(peripheral, s_selDiscoverServices, servicesArr);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnDidFailToConnect(IntPtr self, IntPtr cmd, IntPtr central, IntPtr peripheral, IntPtr error)
    {
        var inst = s_active;
        if (inst == null) return;
        string id = GetPeripheralId(peripheral);
        if (inst._pendingConnects.TryRemove(id, out var pending))
        {
            inst.StatusMessage?.Invoke(inst, $"macOS failed to connect to {pending.DeviceName}.");
            pending.Tcs.TrySetResult(null);
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnDidDisconnectPeripheral(IntPtr self, IntPtr cmd, IntPtr central, IntPtr peripheral, IntPtr error)
    {
        var inst = s_active;
        if (inst == null) return;
        string id = GetPeripheralId(peripheral);

        if (inst._pendingConnects.TryRemove(id, out var pending))
            pending.Tcs.TrySetResult(null);

        if (inst._sessions.TryRemove(id, out var session))
            session.NotifyUnexpectedDisconnect();
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnDidDiscoverServices(IntPtr self, IntPtr cmd, IntPtr peripheral, IntPtr error)
    {
        var inst = s_active;
        if (inst == null) return;
        string id = GetPeripheralId(peripheral);

        IntPtr services = Send0(peripheral, s_selServices);
        nuint count = services != IntPtr.Zero ? Send0UInt(services, s_selCount) : 0;
        for (nuint i = 0; i < count; i++)
        {
            IntPtr svc = Send1UInt(services, s_selObjectAtIndex, i);
            string? uuidStr = ReadNSString(Send0(Send0(svc, s_selUuid), s_selUuidString));
            if (uuidStr != null && (uuidStr.Equals("180D", StringComparison.OrdinalIgnoreCase)
                || uuidStr.StartsWith("0000180d", StringComparison.OrdinalIgnoreCase)))
            {
                IntPtr chUuid = Send1(s_clsCBUUID, s_selUuidWithString, CreateNSString("2A37"));
                IntPtr chArr = Send1(s_clsNSArray, s_selArrayWithObject, chUuid);
                Send2Void(peripheral, s_selDiscoverCharacteristics, chArr, svc);
                return;
            }
        }

        if (inst._pendingConnects.TryRemove(id, out var pending))
        {
            inst.StatusMessage?.Invoke(inst, $"Heart Rate Service (0x180D) not found on {pending.DeviceName}. Enable HR Data Broadcast.");
            Send1Void(inst._centralManager, s_selCancelPeripheralConnection, peripheral);
            pending.Tcs.TrySetResult(null);
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnDidDiscoverCharacteristics(IntPtr self, IntPtr cmd, IntPtr peripheral, IntPtr service, IntPtr error)
    {
        var inst = s_active;
        if (inst == null) return;
        string id = GetPeripheralId(peripheral);

        IntPtr chars = Send0(service, s_selCharacteristics);
        nuint count = chars != IntPtr.Zero ? Send0UInt(chars, s_selCount) : 0;
        for (nuint i = 0; i < count; i++)
        {
            IntPtr ch = Send1UInt(chars, s_selObjectAtIndex, i);
            string? uuidStr = ReadNSString(Send0(Send0(ch, s_selUuid), s_selUuidString));
            if (uuidStr != null && (uuidStr.Equals("2A37", StringComparison.OrdinalIgnoreCase)
                || uuidStr.StartsWith("00002a37", StringComparison.OrdinalIgnoreCase)))
            {
                Send2BoolCharVoid(peripheral, s_selSetNotifyValue, 1, ch);
                if (inst._pendingConnects.TryRemove(id, out var pending))
                {
                    var session = new MacGattSession(inst, id, pending.DeviceName, peripheral, ch);
                    inst._sessions[id] = session;
                    pending.Tcs.TrySetResult(session);
                }
                return;
            }
        }

        if (inst._pendingConnects.TryRemove(id, out var failed))
        {
            inst.StatusMessage?.Invoke(inst, $"Heart Rate Measurement (0x2A37) not found on {failed.DeviceName}.");
            Send1Void(inst._centralManager, s_selCancelPeripheralConnection, peripheral);
            failed.Tcs.TrySetResult(null);
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe void OnDidUpdateValue(IntPtr self, IntPtr cmd, IntPtr peripheral, IntPtr characteristic, IntPtr error)
    {
        var inst = s_active;
        if (inst == null || error != IntPtr.Zero) return;

        string id = GetPeripheralId(peripheral);
        if (!inst._sessions.TryGetValue(id, out var session)) return;

        IntPtr nsData = Send0(characteristic, s_selValue);
        if (nsData == IntPtr.Zero) return;

        nuint len = Send0UInt(nsData, s_selLength);
        IntPtr ptr = Send0(nsData, s_selBytes);
        if (len < 2 || len > 64 || ptr == IntPtr.Zero) return;

        var span = new ReadOnlySpan<byte>((void*)ptr, (int)len);
        if (BleHrPacketParser.TryParseBpm(span, out int bpm))
            session.RaiseBpm(bpm);
    }

    private sealed class MacGattSession(
        MacCoreBluetoothBackend owner,
        string deviceId,
        string deviceName,
        IntPtr peripheral,
        IntPtr characteristic) : IBleGattSession
    {
        private bool _disposed;
        public string DeviceId { get; } = deviceId;
        public string DeviceName { get; } = deviceName;
        public event EventHandler<int>? BpmReceived;
        public event EventHandler? DisconnectedUnexpectedly;

        public void RaiseBpm(int bpm)
        {
            if (!_disposed) BpmReceived?.Invoke(this, bpm);
        }

        public void NotifyUnexpectedDisconnect()
        {
            if (!_disposed) DisconnectedUnexpectedly?.Invoke(this, EventArgs.Empty);
        }

        public ValueTask DisposeAsync()
        {
            if (_disposed) return ValueTask.CompletedTask;
            _disposed = true;
            owner._sessions.TryRemove(DeviceId, out _);
            try
            {
                if (peripheral != IntPtr.Zero && owner._centralManager != IntPtr.Zero)
                {
                    if (characteristic != IntPtr.Zero)
                        Send2BoolCharVoid(peripheral, s_selSetNotifyValue, 0, characteristic);
                    Send1Void(owner._centralManager, s_selCancelPeripheralConnection, peripheral);
                }
            }
            catch { }
            return ValueTask.CompletedTask;
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopScanningAsync();
        foreach (var s in _sessions.Values.ToArray())
            await s.DisposeAsync();
        _sessions.Clear();

        foreach (var p in _peripherals.Values)
        {
            if (p != IntPtr.Zero) Send0Void(p, s_selRelease);
        }
        _peripherals.Clear();

        if (_centralManager != IntPtr.Zero)
        {
            Send0Void(_centralManager, s_selRelease);
            _centralManager = IntPtr.Zero;
        }
        if (_delegateInstance != IntPtr.Zero)
        {
            Send0Void(_delegateInstance, s_selRelease);
            _delegateInstance = IntPtr.Zero;
        }
        if (s_active == this) s_active = null;
    }

    // ── Objective-C Runtime Plumbing ──────────────────────────────────────

    private static unsafe void EnsureRuntimeInitialized()
    {
        if (s_delegateClass != IntPtr.Zero) return;

        IntPtr objc = NativeLibrary.Load("/usr/lib/libobjc.A.dylib");
        NativeLibrary.Load("/System/Library/Frameworks/Foundation.framework/Foundation");
        NativeLibrary.Load("/System/Library/Frameworks/CoreBluetooth.framework/CoreBluetooth");

        s_msgSend = NativeLibrary.GetExport(objc, "objc_msgSend");

        s_clsNSObject         = GetClass("NSObject");
        s_clsNSString         = GetClass("NSString");
        s_clsNSArray          = GetClass("NSArray");
        s_clsNSDictionary     = GetClass("NSDictionary");
        s_clsNSNumber         = GetClass("NSNumber");
        s_clsNSUUID           = GetClass("NSUUID");
        s_clsCBCentralManager = GetClass("CBCentralManager");
        s_clsCBUUID           = GetClass("CBUUID");

        s_selAlloc                   = Sel("alloc");
        s_selInit                    = Sel("init");
        s_selRetain                  = Sel("retain");
        s_selRelease                 = Sel("release");
        s_selUtf8String              = Sel("UTF8String");
        s_selStringWithUtf8          = Sel("stringWithUTF8String:");
        s_selUuidWithString          = Sel("UUIDWithString:");
        s_selUuidString              = Sel("UUIDString");
        s_selIdentifier              = Sel("identifier");
        s_selInitWithUuidString      = Sel("initWithUUIDString:");
        s_selArrayWithObject         = Sel("arrayWithObject:");
        s_selCount                   = Sel("count");
        s_selObjectAtIndex           = Sel("objectAtIndex:");
        s_selDictWithObjsForKeysCount = Sel("dictionaryWithObjects:forKeys:count:");
        s_selObjectForKey            = Sel("objectForKey:");
        s_selNumberWithBool          = Sel("numberWithBool:");
        s_selIntValue                = Sel("intValue");
        s_selBytes                   = Sel("bytes");
        s_selLength                  = Sel("length");
        s_selInitWithDelegateQueue   = Sel("initWithDelegate:queue:");
        s_selState                   = Sel("state");
        s_selScanForPeripherals      = Sel("scanForPeripheralsWithServices:options:");
        s_selStopScan                = Sel("stopScan");
        s_selConnectPeripheral       = Sel("connectPeripheral:options:");
        s_selCancelPeripheralConnection = Sel("cancelPeripheralConnection:");
        s_selRetrievePeripherals     = Sel("retrievePeripheralsWithIdentifiers:");
        s_selName                    = Sel("name");
        s_selSetDelegate             = Sel("setDelegate:");
        s_selDiscoverServices        = Sel("discoverServices:");
        s_selServices                = Sel("services");
        s_selDiscoverCharacteristics = Sel("discoverCharacteristics:forService:");
        s_selCharacteristics         = Sel("characteristics");
        s_selUuid                    = Sel("UUID");
        s_selSetNotifyValue          = Sel("setNotifyValue:forCharacteristic:");
        s_selValue                   = Sel("value");

        IntPtr cls = AllocClassPair(s_clsNSObject, "HuaweiHrBleDelegate", 0);
        AddMethod(cls, Sel("centralManagerDidUpdateState:"), (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr, void>)&OnCentralDidUpdateState, "v@:@");
        AddMethod(cls, Sel("centralManager:didDiscoverPeripheral:advertisementData:RSSI:"), (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr, IntPtr, IntPtr, IntPtr, void>)&OnDidDiscoverPeripheral, "v@:@@@@");
        AddMethod(cls, Sel("centralManager:didConnectPeripheral:"), (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr, IntPtr, void>)&OnDidConnectPeripheral, "v@:@@");
        AddMethod(cls, Sel("centralManager:didFailToConnectPeripheral:error:"), (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr, IntPtr, IntPtr, void>)&OnDidFailToConnect, "v@:@@@");
        AddMethod(cls, Sel("centralManager:didDisconnectPeripheral:error:"), (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr, IntPtr, IntPtr, void>)&OnDidDisconnectPeripheral, "v@:@@@");
        AddMethod(cls, Sel("peripheral:didDiscoverServices:"), (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr, IntPtr, void>)&OnDidDiscoverServices, "v@:@@");
        AddMethod(cls, Sel("peripheral:didDiscoverCharacteristicsForService:error:"), (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr, IntPtr, IntPtr, void>)&OnDidDiscoverCharacteristics, "v@:@@@");
        AddMethod(cls, Sel("peripheral:didUpdateValueForCharacteristic:error:"), (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr, IntPtr, IntPtr, void>)&OnDidUpdateValue, "v@:@@@");
        objc_registerClassPair(cls);
        s_delegateClass = cls;
    }

    private static string NormalizeId(string raw) =>
        raw.Replace("-", "", StringComparison.Ordinal).Replace(":", "", StringComparison.Ordinal).Trim().ToUpperInvariant();

    private static string GetPeripheralId(IntPtr peripheral)
    {
        if (peripheral == IntPtr.Zero) return "";
        IntPtr nsUuid = Send0(peripheral, s_selIdentifier);
        string? raw = ReadNSString(Send0(nsUuid, s_selUuidString));
        return raw != null ? NormalizeId(raw) : "";
    }

    private static unsafe IntPtr CreateNSString(string value)
    {
        byte[] utf8 = System.Text.Encoding.UTF8.GetBytes(value + '\0');
        fixed (byte* p = utf8)
            return ((delegate* unmanaged[Cdecl]<IntPtr, IntPtr, byte*, IntPtr>)s_msgSend)(s_clsNSString, s_selStringWithUtf8, p);
    }

    private static string? ReadNSString(IntPtr nsStr)
    {
        if (nsStr == IntPtr.Zero) return null;
        IntPtr utf8 = Send0(nsStr, s_selUtf8String);
        return utf8 != IntPtr.Zero ? Marshal.PtrToStringUTF8(utf8) : null;
    }

    private static unsafe IntPtr GetClass(string name)
    {
        byte[] b = System.Text.Encoding.ASCII.GetBytes(name + '\0');
        fixed (byte* p = b) return objc_getClass(p);
    }

    private static unsafe IntPtr Sel(string name)
    {
        byte[] b = System.Text.Encoding.ASCII.GetBytes(name + '\0');
        fixed (byte* p = b) return sel_registerName(p);
    }

    private static unsafe IntPtr AllocClassPair(IntPtr superCls, string name, nuint extra)
    {
        byte[] b = System.Text.Encoding.ASCII.GetBytes(name + '\0');
        fixed (byte* p = b) return objc_allocateClassPair(superCls, p, extra);
    }

    private static unsafe void AddMethod(IntPtr cls, IntPtr sel, IntPtr imp, string types)
    {
        byte[] b = System.Text.Encoding.ASCII.GetBytes(types + '\0');
        fixed (byte* p = b) class_addMethod(cls, sel, imp, p);
    }

    private static unsafe IntPtr Send0(IntPtr recv, IntPtr sel) =>
        ((delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr>)s_msgSend)(recv, sel);
    private static unsafe void Send0Void(IntPtr recv, IntPtr sel) =>
        ((delegate* unmanaged[Cdecl]<IntPtr, IntPtr, void>)s_msgSend)(recv, sel);
    private static unsafe nint Send0NInt(IntPtr recv, IntPtr sel) =>
        ((delegate* unmanaged[Cdecl]<IntPtr, IntPtr, nint>)s_msgSend)(recv, sel);
    private static unsafe nuint Send0UInt(IntPtr recv, IntPtr sel) =>
        ((delegate* unmanaged[Cdecl]<IntPtr, IntPtr, nuint>)s_msgSend)(recv, sel);
    private static unsafe int Send0Int(IntPtr recv, IntPtr sel) =>
        ((delegate* unmanaged[Cdecl]<IntPtr, IntPtr, int>)s_msgSend)(recv, sel);
    private static unsafe IntPtr Send1(IntPtr recv, IntPtr sel, IntPtr a1) =>
        ((delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr, IntPtr>)s_msgSend)(recv, sel, a1);
    private static unsafe void Send1Void(IntPtr recv, IntPtr sel, IntPtr a1) =>
        ((delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr, void>)s_msgSend)(recv, sel, a1);
    private static unsafe IntPtr Send1UInt(IntPtr recv, IntPtr sel, nuint idx) =>
        ((delegate* unmanaged[Cdecl]<IntPtr, IntPtr, nuint, IntPtr>)s_msgSend)(recv, sel, idx);
    private static unsafe IntPtr Send1Bool(IntPtr recv, IntPtr sel, byte b) =>
        ((delegate* unmanaged[Cdecl]<IntPtr, IntPtr, byte, IntPtr>)s_msgSend)(recv, sel, b);
    private static unsafe IntPtr Send2(IntPtr recv, IntPtr sel, IntPtr a1, IntPtr a2) =>
        ((delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr, IntPtr, IntPtr>)s_msgSend)(recv, sel, a1, a2);
    private static unsafe void Send2Void(IntPtr recv, IntPtr sel, IntPtr a1, IntPtr a2) =>
        ((delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr, IntPtr, void>)s_msgSend)(recv, sel, a1, a2);
    private static unsafe void Send2BoolCharVoid(IntPtr recv, IntPtr sel, byte b, IntPtr ch) =>
        ((delegate* unmanaged[Cdecl]<IntPtr, IntPtr, byte, IntPtr, void>)s_msgSend)(recv, sel, b, ch);
    private static unsafe IntPtr Send3PtrPtrUInt(IntPtr recv, IntPtr sel, IntPtr* objs, IntPtr* keys, nuint count) =>
        ((delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr*, IntPtr*, nuint, IntPtr>)s_msgSend)(recv, sel, objs, keys, count);

    [DllImport("/usr/lib/libobjc.A.dylib")] private static extern unsafe IntPtr objc_getClass(byte* name);
    [DllImport("/usr/lib/libobjc.A.dylib")] private static extern unsafe IntPtr sel_registerName(byte* name);
    [DllImport("/usr/lib/libobjc.A.dylib")] private static extern unsafe IntPtr objc_allocateClassPair(IntPtr superclass, byte* name, nuint extraBytes);
    [DllImport("/usr/lib/libobjc.A.dylib")] private static extern unsafe byte class_addMethod(IntPtr cls, IntPtr name, IntPtr imp, byte* types);
    [DllImport("/usr/lib/libobjc.A.dylib")] private static extern void objc_registerClassPair(IntPtr cls);
    [DllImport("/usr/lib/system/libdispatch.dylib")] private static extern unsafe IntPtr dispatch_queue_create(byte* label, IntPtr attr);
}
