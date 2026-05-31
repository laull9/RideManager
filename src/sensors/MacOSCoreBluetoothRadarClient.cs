using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Text;
using RideManager.Utils;

namespace RideManager.Sensors;

/// <summary>
/// 使用 macOS CoreBluetooth 原生 API 连接 ESP32-C6 雷达 BLE 服务。
/// </summary>
public sealed class MacOSCoreBluetoothRadarClient : IRadarClient
{
    private readonly SensorEndpointOptions _options;
    private readonly TimeSpan _discoveryTimeout;
    private readonly TimeSpan _servicesTimeout;
    private readonly TimeSpan _reconnectDelay;
    private readonly CancellationTokenSource _stop = new();
    private readonly object _sync = new();
    private Task? _runTask;
    private TaskCompletionSource<RadarFrame>? _nextFrame;

    public MacOSCoreBluetoothRadarClient(SensorEndpointOptions options)
    {
        _options = options;
        ValidateConfiguration(options);
        _discoveryTimeout = TimeSpan.FromSeconds(Math.Max(1.0, options.ScanTimeoutSeconds));
        _servicesTimeout = TimeSpan.FromSeconds(Math.Max(1.0, options.ServicesTimeoutSeconds));
        _reconnectDelay = TimeSpan.FromSeconds(Math.Max(0.2, options.ReconnectDelaySeconds));
    }

    public event EventHandler<RadarFrame>? FrameReceived;

    public event EventHandler<RadarHealth>? HealthReceived;

    public event EventHandler<RadarConnectionState>? StateChanged;

    public RadarFrame? LatestFrame { get; private set; }

    public RadarHealth? LatestHealth { get; private set; }

    public RadarConnectionState State { get; private set; } = RadarConnectionState.Idle();

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (_runTask is not null)
        {
            return Task.CompletedTask;
        }

        _runTask = Task.Run(() => RunAsync(_stop.Token), CancellationToken.None);
        return Task.CompletedTask;
    }

    public async Task<RadarFrame?> WaitForFrameAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        if (LatestFrame is not null)
        {
            return LatestFrame;
        }

        var completion = new TaskCompletionSource<RadarFrame>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_sync)
        {
            _nextFrame = completion;
        }

        try
        {
            var timeoutTask = Task.Delay(timeout, cancellationToken);
            var finished = await Task.WhenAny(completion.Task, timeoutTask).ConfigureAwait(false);
            return finished == completion.Task ? await completion.Task.ConfigureAwait(false) : null;
        }
        finally
        {
            lock (_sync)
            {
                if (ReferenceEquals(_nextFrame, completion))
                {
                    _nextFrame = null;
                }
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        if (_runTask is not null)
        {
            try
            {
                await _runTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }

        _stop.Dispose();
    }

    private static void ValidateConfiguration(SensorEndpointOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.ServiceUuid))
        {
            throw new InvalidOperationException("Radar BLE config missing sensors.radar.service_uuid.");
        }

        if (string.IsNullOrWhiteSpace(options.NotifyUuid))
        {
            throw new InvalidOperationException("Radar BLE config missing sensors.radar.notify_uuid.");
        }

        if (RadarProtocol.IsPlaceholderAddress(options.Address)
            && string.IsNullOrWhiteSpace(options.DeviceName)
            && !options.MatchByService)
        {
            throw new InvalidOperationException("Radar BLE config needs sensors.radar.address, sensors.radar.device_name, or match_by_service=true.");
        }
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            MacOSCoreBluetoothSession? session = null;
            string? lastError = null;
            try
            {
                session = new MacOSCoreBluetoothSession(this, _options, _discoveryTimeout, _servicesTimeout);
                var device = await session.ConnectAndSubscribeAsync(cancellationToken).ConfigureAwait(false);
                PublishState("connected", device.Name, device.Identifier, "notifications subscribed");
                await session.WaitForDisconnectAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                lastError = $"{ex.GetType().Name}: {ex.Message}";
                PublishState("error", null, null, lastError);
            }
            finally
            {
                session?.Dispose();
            }

            if (!cancellationToken.IsCancellationRequested)
            {
                var message = lastError is null
                    ? $"retry in {_reconnectDelay.TotalSeconds:F0}s"
                    : $"retry in {_reconnectDelay.TotalSeconds:F0}s after error: {lastError}";
                PublishState("reconnecting", null, null, message);
                await Task.Delay(_reconnectDelay, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private void PublishState(string phase, string? deviceName, string? deviceAddress, string? message)
    {
        State = new RadarConnectionState(phase, deviceName, deviceAddress, message, DateTimeOffset.UtcNow);
        StateChanged?.Invoke(this, State);
    }

    private void PublishScanning()
    {
        PublishState("scanning", _options.DeviceName, null, "searching CoreBluetooth advertisement");
    }

    private void PublishConnecting(MacOSBleDevice device)
    {
        PublishState("connecting", device.Name, device.Identifier, "connecting GATT");
    }

    private void OnNotifyPayload(byte[] payload)
    {
        try
        {
            var frame = RadarProtocol.ParseFrame(payload, DateTimeOffset.UtcNow);
            LatestFrame = frame;
            FrameReceived?.Invoke(this, frame);

            lock (_sync)
            {
                _nextFrame?.TrySetResult(frame);
                _nextFrame = null;
            }
        }
        catch (Exception ex)
        {
            PublishState("parse_error", State.DeviceName, State.DeviceAddress, DecodePayloadError(payload, ex));
        }
    }

    private void OnHealthPayload(byte[] payload)
    {
        try
        {
            var health = RadarProtocol.ParseHealth(payload, DateTimeOffset.UtcNow);
            LatestHealth = health;
            HealthReceived?.Invoke(this, health);
        }
        catch (Exception ex)
        {
            PublishState("parse_error", State.DeviceName, State.DeviceAddress, DecodePayloadError(payload, ex));
        }
    }

    private static string DecodePayloadError(byte[] payload, Exception ex)
    {
        var text = Encoding.ASCII.GetString(payload);
        return $"failed to parse '{text}': {ex.Message}";
    }

    private sealed record MacOSBleDevice(string Name, string Identifier);

    private sealed class MacOSCoreBluetoothSession : IDisposable
    {
        private const long CentralManagerStatePoweredOn = 5;
        private static readonly ConcurrentDictionary<IntPtr, MacOSCoreBluetoothSession> Sessions = new();
        private static readonly object ClassSync = new();
        private static IntPtr _delegateClass;

        private static readonly ObjCNoArgDelegate CentralManagerDidUpdateStateDelegate = CentralManagerDidUpdateState;
        private static readonly ObjCDidDiscoverPeripheralDelegate DidDiscoverPeripheralDelegate = DidDiscoverPeripheral;
        private static readonly ObjCTwoObjectDelegate DidConnectPeripheralDelegate = DidConnectPeripheral;
        private static readonly ObjCThreeObjectDelegate DidFailToConnectPeripheralDelegate = DidFailToConnectPeripheral;
        private static readonly ObjCThreeObjectDelegate DidDisconnectPeripheralDelegate = DidDisconnectPeripheral;
        private static readonly ObjCTwoObjectDelegate DidDiscoverServicesDelegate = DidDiscoverServices;
        private static readonly ObjCThreeObjectDelegate DidDiscoverCharacteristicsDelegate = DidDiscoverCharacteristics;
        private static readonly ObjCThreeObjectDelegate DidUpdateValueDelegate = DidUpdateValueForCharacteristic;

        private readonly MacOSCoreBluetoothRadarClient _owner;
        private readonly SensorEndpointOptions _options;
        private readonly TimeSpan _discoveryTimeout;
        private readonly TimeSpan _servicesTimeout;
        private readonly TaskCompletionSource _poweredOn = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<MacOSBleDevice> _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _disconnected = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly IntPtr _delegate;
        private readonly IntPtr _queue;
        private readonly IntPtr _centralManager;
        private IntPtr _peripheral;
        private IntPtr _notifyCharacteristic;
        private IntPtr _healthCharacteristic;
        private bool _disposed;

        public MacOSCoreBluetoothSession(
            MacOSCoreBluetoothRadarClient owner,
            SensorEndpointOptions options,
            TimeSpan discoveryTimeout,
            TimeSpan servicesTimeout)
        {
            _owner = owner;
            _options = options;
            _discoveryTimeout = discoveryTimeout;
            _servicesTimeout = servicesTimeout;

            Native.EnsureCoreBluetoothLoaded();
            _delegate = Native.IntPtr_objc_msgSend(EnsureDelegateClass(), Native.Sel("new"));
            Sessions[_delegate] = this;
            _queue = Native.dispatch_queue_create("RideManager.Radar.CoreBluetooth", IntPtr.Zero);

            var centralManagerClass = Native.Class("CBCentralManager");
            var allocated = Native.IntPtr_objc_msgSend(centralManagerClass, Native.Sel("alloc"));
            _centralManager = Native.IntPtr_objc_msgSend_IntPtr_IntPtr_IntPtr(
                allocated,
                Native.Sel("initWithDelegate:queue:options:"),
                _delegate,
                _queue,
                IntPtr.Zero);
        }

        public async Task<MacOSBleDevice> ConnectAndSubscribeAsync(CancellationToken cancellationToken)
        {
            _owner.PublishScanning();
            await _poweredOn.Task.WaitAsync(TimeSpan.FromSeconds(8), cancellationToken).ConfigureAwait(false);
            StartScan();
            return await _ready.Task.WaitAsync(_discoveryTimeout + _servicesTimeout, cancellationToken).ConfigureAwait(false);
        }

        public Task WaitForDisconnectAsync(CancellationToken cancellationToken)
        {
            return _disconnected.Task.WaitAsync(cancellationToken);
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            try
            {
                Native.Void_objc_msgSend(_centralManager, Native.Sel("stopScan"));
                if (_peripheral != IntPtr.Zero)
                {
                    Native.Void_objc_msgSend_IntPtr(_centralManager, Native.Sel("cancelPeripheralConnection:"), _peripheral);
                }
            }
            catch (Exception)
            {
            }

            Sessions.TryRemove(_delegate, out _);
            Native.release(_centralManager);
            Native.release(_delegate);
        }

        private static IntPtr EnsureDelegateClass()
        {
            lock (ClassSync)
            {
                if (_delegateClass != IntPtr.Zero)
                {
                    return _delegateClass;
                }

                var nsObject = Native.Class("NSObject");
                var cls = Native.objc_allocateClassPair(nsObject, "RideManagerCoreBluetoothDelegate", 0);
                AddMethod(cls, "centralManagerDidUpdateState:", CentralManagerDidUpdateStateDelegate, "v@:@");
                AddMethod(cls, "centralManager:didDiscoverPeripheral:advertisementData:RSSI:", DidDiscoverPeripheralDelegate, "v@:@@@@");
                AddMethod(cls, "centralManager:didConnectPeripheral:", DidConnectPeripheralDelegate, "v@:@@");
                AddMethod(cls, "centralManager:didFailToConnectPeripheral:error:", DidFailToConnectPeripheralDelegate, "v@:@@@");
                AddMethod(cls, "centralManager:didDisconnectPeripheral:error:", DidDisconnectPeripheralDelegate, "v@:@@@");
                AddMethod(cls, "peripheral:didDiscoverServices:", DidDiscoverServicesDelegate, "v@:@@");
                AddMethod(cls, "peripheral:didDiscoverCharacteristicsForService:error:", DidDiscoverCharacteristicsDelegate, "v@:@@@");
                AddMethod(cls, "peripheral:didUpdateValueForCharacteristic:error:", DidUpdateValueDelegate, "v@:@@@");
                Native.objc_registerClassPair(cls);
                _delegateClass = cls;
                return cls;
            }
        }

        private static void AddMethod(IntPtr cls, string selector, Delegate handler, string types)
        {
            var ok = Native.class_addMethod(
                cls,
                Native.Sel(selector),
                Marshal.GetFunctionPointerForDelegate(handler),
                types);
            if (!ok)
            {
                throw new InvalidOperationException($"Unable to add CoreBluetooth delegate method {selector}.");
            }
        }

        private void StartScan()
        {
            var services = _options.MatchByService ? Native.CreateUuidArray(_options.ServiceUuid) : IntPtr.Zero;
            Native.Void_objc_msgSend_IntPtr_IntPtr(
                _centralManager,
                Native.Sel("scanForPeripheralsWithServices:options:"),
                services,
                IntPtr.Zero);
        }

        private void OnCentralManagerDidUpdateState(IntPtr central)
        {
            var state = Native.Int64_objc_msgSend(central, Native.Sel("state"));
            if (state == CentralManagerStatePoweredOn)
            {
                _poweredOn.TrySetResult();
            }
            else if (state > CentralManagerStatePoweredOn)
            {
                _poweredOn.TrySetException(new InvalidOperationException($"CoreBluetooth state is {state}."));
            }
        }

        private void OnDidDiscoverPeripheral(IntPtr peripheral, IntPtr advertisementData)
        {
            var device = ReadDevice(peripheral, advertisementData);
            if (!MatchesDevice(device))
            {
                return;
            }

            _peripheral = peripheral;
            Native.retain(_peripheral);
            Native.Void_objc_msgSend_IntPtr(_peripheral, Native.Sel("setDelegate:"), _delegate);
            Native.Void_objc_msgSend(_centralManager, Native.Sel("stopScan"));
            _owner.PublishConnecting(device);
            Native.Void_objc_msgSend_IntPtr_IntPtr(
                _centralManager,
                Native.Sel("connectPeripheral:options:"),
                _peripheral,
                IntPtr.Zero);
        }

        private void OnDidConnectPeripheral(IntPtr peripheral)
        {
            Native.Void_objc_msgSend_IntPtr(peripheral, Native.Sel("discoverServices:"), Native.CreateUuidArray(_options.ServiceUuid));
        }

        private void OnDidFailToConnectPeripheral(IntPtr error)
        {
            _ready.TrySetException(new InvalidOperationException($"CoreBluetooth connect failed: {Native.ErrorDescription(error)}"));
        }

        private void OnDidDisconnectPeripheral(IntPtr error)
        {
            if (error != IntPtr.Zero)
            {
                _ready.TrySetException(new InvalidOperationException($"CoreBluetooth disconnected: {Native.ErrorDescription(error)}"));
            }

            _disconnected.TrySetResult();
        }

        private void OnDidDiscoverServices(IntPtr peripheral, IntPtr error)
        {
            if (error != IntPtr.Zero)
            {
                _ready.TrySetException(new InvalidOperationException($"Service discovery failed: {Native.ErrorDescription(error)}"));
                return;
            }

            var service = Native.FindService(peripheral, _options.ServiceUuid);
            if (service == IntPtr.Zero)
            {
                _ready.TrySetException(new InvalidOperationException($"Missing radar service {_options.ServiceUuid}."));
                return;
            }

            Native.Void_objc_msgSend_IntPtr_IntPtr(
                peripheral,
                Native.Sel("discoverCharacteristics:forService:"),
                Native.CreateUuidArray(_options.NotifyUuid, _options.HealthUuid),
                service);
        }

        private void OnDidDiscoverCharacteristics(IntPtr peripheral, IntPtr service, IntPtr error)
        {
            if (error != IntPtr.Zero)
            {
                _ready.TrySetException(new InvalidOperationException($"Characteristic discovery failed: {Native.ErrorDescription(error)}"));
                return;
            }

            _notifyCharacteristic = Native.FindCharacteristic(service, _options.NotifyUuid);
            if (_notifyCharacteristic == IntPtr.Zero)
            {
                _ready.TrySetException(new InvalidOperationException($"Missing radar notify characteristic {_options.NotifyUuid}."));
                return;
            }

            if (_options.SubscribeHealth && !string.IsNullOrWhiteSpace(_options.HealthUuid))
            {
                _healthCharacteristic = Native.FindCharacteristic(service, _options.HealthUuid);
            }

            Native.Void_objc_msgSend_Bool_IntPtr(peripheral, Native.Sel("setNotifyValue:forCharacteristic:"), true, _notifyCharacteristic);
            if (_healthCharacteristic != IntPtr.Zero)
            {
                Native.Void_objc_msgSend_Bool_IntPtr(peripheral, Native.Sel("setNotifyValue:forCharacteristic:"), true, _healthCharacteristic);
            }

            _ready.TrySetResult(ReadDevice(peripheral, IntPtr.Zero));
        }

        private void OnDidUpdateValueForCharacteristic(IntPtr characteristic, IntPtr error)
        {
            if (error != IntPtr.Zero)
            {
                _ready.TrySetException(new InvalidOperationException($"Characteristic notify failed: {Native.ErrorDescription(error)}"));
                return;
            }

            var payload = Native.ReadCharacteristicValue(characteristic);
            if (characteristic == _notifyCharacteristic)
            {
                _owner.OnNotifyPayload(payload);
            }
            else if (characteristic == _healthCharacteristic)
            {
                _owner.OnHealthPayload(payload);
            }
        }

        private bool MatchesDevice(MacOSBleDevice device)
        {
            if (!RadarProtocol.IsPlaceholderAddress(_options.Address)
                && device.Identifier.Equals(_options.Address, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (_options.MatchByService)
            {
                return true;
            }

            return !string.IsNullOrWhiteSpace(_options.DeviceName)
                && device.Name.Equals(_options.DeviceName, StringComparison.OrdinalIgnoreCase);
        }

        private static MacOSBleDevice ReadDevice(IntPtr peripheral, IntPtr advertisementData)
        {
            var name = Native.ReadStringProperty(peripheral, "name");
            if (string.IsNullOrWhiteSpace(name) && advertisementData != IntPtr.Zero)
            {
                name = Native.ReadAdvertisementLocalName(advertisementData);
            }

            var identifier = Native.IntPtr_objc_msgSend(peripheral, Native.Sel("identifier"));
            var uuidString = Native.ReadStringProperty(identifier, "UUIDString");
            return new MacOSBleDevice(name ?? string.Empty, uuidString ?? string.Empty);
        }

        private static MacOSCoreBluetoothSession? GetSession(IntPtr self)
        {
            Sessions.TryGetValue(self, out var session);
            return session;
        }

        private void Fail(Exception ex)
        {
            _ready.TrySetException(ex);
            _disconnected.TrySetResult();
        }

        private static void SafeInvoke(IntPtr self, Action<MacOSCoreBluetoothSession> action)
        {
            var session = GetSession(self);
            if (session is null)
            {
                return;
            }

            try
            {
                action(session);
            }
            catch (Exception ex)
            {
                session.Fail(ex);
            }
        }

        private static void CentralManagerDidUpdateState(IntPtr self, IntPtr cmd, IntPtr central)
        {
            SafeInvoke(self, session => session.OnCentralManagerDidUpdateState(central));
        }

        private static void DidDiscoverPeripheral(IntPtr self, IntPtr cmd, IntPtr central, IntPtr peripheral, IntPtr advertisementData, IntPtr rssi)
        {
            SafeInvoke(self, session => session.OnDidDiscoverPeripheral(peripheral, advertisementData));
        }

        private static void DidConnectPeripheral(IntPtr self, IntPtr cmd, IntPtr central, IntPtr peripheral)
        {
            SafeInvoke(self, session => session.OnDidConnectPeripheral(peripheral));
        }

        private static void DidFailToConnectPeripheral(IntPtr self, IntPtr cmd, IntPtr central, IntPtr peripheral, IntPtr error)
        {
            SafeInvoke(self, session => session.OnDidFailToConnectPeripheral(error));
        }

        private static void DidDisconnectPeripheral(IntPtr self, IntPtr cmd, IntPtr central, IntPtr peripheral, IntPtr error)
        {
            SafeInvoke(self, session => session.OnDidDisconnectPeripheral(error));
        }

        private static void DidDiscoverServices(IntPtr self, IntPtr cmd, IntPtr peripheral, IntPtr error)
        {
            SafeInvoke(self, session => session.OnDidDiscoverServices(peripheral, error));
        }

        private static void DidDiscoverCharacteristics(IntPtr self, IntPtr cmd, IntPtr peripheral, IntPtr service, IntPtr error)
        {
            SafeInvoke(self, session => session.OnDidDiscoverCharacteristics(peripheral, service, error));
        }

        private static void DidUpdateValueForCharacteristic(IntPtr self, IntPtr cmd, IntPtr peripheral, IntPtr characteristic, IntPtr error)
        {
            SafeInvoke(self, session => session.OnDidUpdateValueForCharacteristic(characteristic, error));
        }
    }

    private static class Native
    {
        private const string ObjCLib = "/usr/lib/libobjc.A.dylib";
        private const string LibSystem = "/usr/lib/libSystem.B.dylib";
        private const string CoreBluetoothPath = "/System/Library/Frameworks/CoreBluetooth.framework/CoreBluetooth";
        private const int RtldLazy = 1;
        private static int _loaded;

        public static void EnsureCoreBluetoothLoaded()
        {
            if (Interlocked.Exchange(ref _loaded, 1) == 0 && dlopen(CoreBluetoothPath, RtldLazy) == IntPtr.Zero)
            {
                throw new InvalidOperationException("Unable to load CoreBluetooth.framework.");
            }
        }

        public static IntPtr Class(string name)
        {
            var cls = objc_getClass(name);
            return cls == IntPtr.Zero ? throw new InvalidOperationException($"Objective-C class {name} was not found.") : cls;
        }

        public static IntPtr Sel(string name)
        {
            return sel_registerName(name);
        }

        public static IntPtr CreateUuidArray(params string?[] uuids)
        {
            var array = IntPtr_objc_msgSend(Class("NSMutableArray"), Sel("array"));
            foreach (var uuid in uuids.Where(value => !string.IsNullOrWhiteSpace(value)))
            {
                var cbUuid = IntPtr_objc_msgSend_IntPtr(Class("CBUUID"), Sel("UUIDWithString:"), CreateNSString(uuid!));
                Void_objc_msgSend_IntPtr(array, Sel("addObject:"), cbUuid);
            }

            return array;
        }

        public static IntPtr FindService(IntPtr peripheral, string uuid)
        {
            var services = IntPtr_objc_msgSend(peripheral, Sel("services"));
            return FindByUuid(services, uuid);
        }

        public static IntPtr FindCharacteristic(IntPtr service, string uuid)
        {
            var characteristics = IntPtr_objc_msgSend(service, Sel("characteristics"));
            return FindByUuid(characteristics, uuid);
        }

        public static byte[] ReadCharacteristicValue(IntPtr characteristic)
        {
            var data = IntPtr_objc_msgSend(characteristic, Sel("value"));
            if (data == IntPtr.Zero)
            {
                return Array.Empty<byte>();
            }

            var length = (int)UIntPtr_objc_msgSend(data, Sel("length"));
            if (length == 0)
            {
                return Array.Empty<byte>();
            }

            var bytes = IntPtr_objc_msgSend(data, Sel("bytes"));
            var payload = new byte[length];
            Marshal.Copy(bytes, payload, 0, length);
            return payload;
        }

        public static string? ReadAdvertisementLocalName(IntPtr advertisementData)
        {
            var key = CreateNSString("kCBAdvDataLocalName");
            var value = IntPtr_objc_msgSend_IntPtr(advertisementData, Sel("objectForKey:"), key);
            return NSStringToString(value);
        }

        public static string? ReadStringProperty(IntPtr obj, string selector)
        {
            return obj == IntPtr.Zero ? null : NSStringToString(IntPtr_objc_msgSend(obj, Sel(selector)));
        }

        public static string ErrorDescription(IntPtr error)
        {
            if (error == IntPtr.Zero)
            {
                return "unknown error";
            }

            return ReadStringProperty(error, "localizedDescription") ?? "unknown error";
        }

        private static IntPtr FindByUuid(IntPtr array, string uuid)
        {
            if (array == IntPtr.Zero)
            {
                return IntPtr.Zero;
            }

            var count = UIntPtr_objc_msgSend(array, Sel("count")).ToUInt64();
            for (ulong i = 0; i < count; i++)
            {
                var item = IntPtr_objc_msgSend_UIntPtr(array, Sel("objectAtIndex:"), (UIntPtr)i);
                var itemUuid = IntPtr_objc_msgSend(item, Sel("UUID"));
                var itemUuidString = ReadStringProperty(itemUuid, "UUIDString");
                if (itemUuidString is not null && itemUuidString.Equals(uuid, StringComparison.OrdinalIgnoreCase))
                {
                    return item;
                }
            }

            return IntPtr.Zero;
        }

        private static IntPtr CreateNSString(string value)
        {
            var utf8 = Marshal.StringToHGlobalAnsi(value);
            try
            {
                return IntPtr_objc_msgSend_IntPtr(Class("NSString"), Sel("stringWithUTF8String:"), utf8);
            }
            finally
            {
                Marshal.FreeHGlobal(utf8);
            }
        }

        private static string? NSStringToString(IntPtr nsString)
        {
            if (nsString == IntPtr.Zero)
            {
                return null;
            }

            var utf8 = IntPtr_objc_msgSend(nsString, Sel("UTF8String"));
            return utf8 == IntPtr.Zero ? null : Marshal.PtrToStringUTF8(utf8);
        }

        [DllImport(LibSystem)]
        public static extern IntPtr dlopen(string path, int mode);

        [DllImport(LibSystem)]
        public static extern IntPtr dispatch_queue_create(string label, IntPtr attr);

        [DllImport(ObjCLib)]
        public static extern IntPtr objc_getClass(string name);

        [DllImport(ObjCLib)]
        public static extern IntPtr objc_allocateClassPair(IntPtr superclass, string name, UIntPtr extraBytes);

        [DllImport(ObjCLib)]
        public static extern void objc_registerClassPair(IntPtr cls);

        [DllImport(ObjCLib)]
        public static extern bool class_addMethod(IntPtr cls, IntPtr name, IntPtr imp, string types);

        [DllImport(ObjCLib)]
        public static extern IntPtr sel_registerName(string name);

        public static void retain(IntPtr obj)
        {
            Void_objc_msgSend(obj, Sel("retain"));
        }

        public static void release(IntPtr obj)
        {
            Void_objc_msgSend(obj, Sel("release"));
        }

        [DllImport(ObjCLib, EntryPoint = "objc_msgSend")]
        public static extern void Void_objc_msgSend(IntPtr receiver, IntPtr selector);

        [DllImport(ObjCLib, EntryPoint = "objc_msgSend")]
        public static extern void Void_objc_msgSend_IntPtr(IntPtr receiver, IntPtr selector, IntPtr arg1);

        [DllImport(ObjCLib, EntryPoint = "objc_msgSend")]
        public static extern void Void_objc_msgSend_IntPtr_IntPtr(IntPtr receiver, IntPtr selector, IntPtr arg1, IntPtr arg2);

        [DllImport(ObjCLib, EntryPoint = "objc_msgSend")]
        public static extern void Void_objc_msgSend_Bool_IntPtr(IntPtr receiver, IntPtr selector, bool arg1, IntPtr arg2);

        [DllImport(ObjCLib, EntryPoint = "objc_msgSend")]
        public static extern IntPtr IntPtr_objc_msgSend(IntPtr receiver, IntPtr selector);

        [DllImport(ObjCLib, EntryPoint = "objc_msgSend")]
        public static extern IntPtr IntPtr_objc_msgSend_IntPtr(IntPtr receiver, IntPtr selector, IntPtr arg1);

        [DllImport(ObjCLib, EntryPoint = "objc_msgSend")]
        public static extern IntPtr IntPtr_objc_msgSend_IntPtr_IntPtr(IntPtr receiver, IntPtr selector, IntPtr arg1, IntPtr arg2);

        [DllImport(ObjCLib, EntryPoint = "objc_msgSend")]
        public static extern IntPtr IntPtr_objc_msgSend_IntPtr_IntPtr_IntPtr(IntPtr receiver, IntPtr selector, IntPtr arg1, IntPtr arg2, IntPtr arg3);

        [DllImport(ObjCLib, EntryPoint = "objc_msgSend")]
        public static extern IntPtr IntPtr_objc_msgSend_UIntPtr(IntPtr receiver, IntPtr selector, UIntPtr arg1);

        [DllImport(ObjCLib, EntryPoint = "objc_msgSend")]
        public static extern long Int64_objc_msgSend(IntPtr receiver, IntPtr selector);

        [DllImport(ObjCLib, EntryPoint = "objc_msgSend")]
        public static extern UIntPtr UIntPtr_objc_msgSend(IntPtr receiver, IntPtr selector);
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void ObjCNoArgDelegate(IntPtr self, IntPtr cmd, IntPtr arg1);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void ObjCTwoObjectDelegate(IntPtr self, IntPtr cmd, IntPtr arg1, IntPtr arg2);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void ObjCThreeObjectDelegate(IntPtr self, IntPtr cmd, IntPtr arg1, IntPtr arg2, IntPtr arg3);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void ObjCFourObjectDelegate(IntPtr self, IntPtr cmd, IntPtr arg1, IntPtr arg2, IntPtr arg3, IntPtr arg4);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void ObjCDidDiscoverPeripheralDelegate(IntPtr self, IntPtr cmd, IntPtr central, IntPtr peripheral, IntPtr advertisementData, IntPtr rssi);
}
