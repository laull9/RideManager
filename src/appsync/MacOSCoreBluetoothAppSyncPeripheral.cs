using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Text;
using RideManager.Utils;

namespace RideManager.AppSync;

/// <summary>
/// 使用 macOS CoreBluetooth 暴露手机 App 同步 BLE 外设服务。
/// </summary>
public sealed class MacOSCoreBluetoothAppSyncPeripheral : IAppSyncPeripheral
{
    private readonly AppSyncOptions _options;
    private MacOSCoreBluetoothPeripheralSession? _session;

    /// <summary>
    /// 创建 macOS CoreBluetooth 外设宿主。
    /// </summary>
    public MacOSCoreBluetoothAppSyncPeripheral(AppSyncOptions options)
    {
        _options = options;
    }

    /// <summary>
    /// 启动 macOS CoreBluetooth 外设广播和 GATT 服务。
    /// </summary>
    public async Task StartAsync(Func<string, CancellationToken, Task<string>> requestHandler, CancellationToken cancellationToken)
    {
        if (_session is not null)
        {
            return;
        }

        ValidateConfiguration(_options);
        var session = new MacOSCoreBluetoothPeripheralSession(_options, requestHandler);
        _session = session;
        await session.StartAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 释放 CoreBluetooth 外设资源。
    /// </summary>
    public ValueTask DisposeAsync()
    {
        _session?.Dispose();
        _session = null;
        return ValueTask.CompletedTask;
    }

    private static void ValidateConfiguration(AppSyncOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.DeviceName))
        {
            throw new InvalidOperationException("App sync config missing app_sync.device_name.");
        }

        if (string.IsNullOrWhiteSpace(options.ServiceUuid))
        {
            throw new InvalidOperationException("App sync config missing app_sync.service_uuid.");
        }

        if (string.IsNullOrWhiteSpace(options.RxUuid))
        {
            throw new InvalidOperationException("App sync config missing app_sync.rx_uuid.");
        }

        if (string.IsNullOrWhiteSpace(options.TxUuid))
        {
            throw new InvalidOperationException("App sync config missing app_sync.tx_uuid.");
        }
    }

    private sealed class MacOSCoreBluetoothPeripheralSession : IDisposable
    {
        private const long PeripheralManagerStateUnsupported = 2;
        private const long PeripheralManagerStateUnauthorized = 3;
        private const long PeripheralManagerStatePoweredOff = 4;
        private const long PeripheralManagerStatePoweredOn = 5;
        private const ulong CharacteristicPropertyWrite = 0x08;
        private const ulong CharacteristicPropertyWriteWithoutResponse = 0x04;
        private const ulong CharacteristicPropertyNotify = 0x10;
        private const ulong AttributePermissionWriteable = 0x02;
        private const long AttErrorSuccess = 0;
        private const long AttErrorInvalidHandle = 1;

        private static readonly ConcurrentDictionary<IntPtr, MacOSCoreBluetoothPeripheralSession> Sessions = new();
        private static readonly object ClassSync = new();
        private static IntPtr _delegateClass;

        private static readonly ObjCNoArgDelegate PeripheralManagerDidUpdateStateDelegate = PeripheralManagerDidUpdateState;
        private static readonly ObjCThreeObjectDelegate DidAddServiceDelegate = DidAddService;
        private static readonly ObjCTwoObjectDelegate DidStartAdvertisingDelegate = DidStartAdvertising;
        private static readonly ObjCThreeObjectDelegate DidSubscribeDelegate = DidSubscribeToCharacteristic;
        private static readonly ObjCThreeObjectDelegate DidUnsubscribeDelegate = DidUnsubscribeFromCharacteristic;
        private static readonly ObjCTwoObjectDelegate DidReceiveWriteRequestsDelegate = DidReceiveWriteRequests;
        private static readonly ObjCNoArgDelegate IsReadyToUpdateSubscribersDelegate = PeripheralManagerIsReadyToUpdateSubscribers;

        private readonly AppSyncOptions _options;
        private readonly Func<string, CancellationToken, Task<string>> _requestHandler;
        private readonly CancellationTokenSource _stop = new();
        private readonly TaskCompletionSource _advertising = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly object _notifySync = new();
        private readonly Queue<byte[]> _pendingNotifications = new();
        private readonly IntPtr _delegate;
        private readonly IntPtr _queue;
        private readonly IntPtr _peripheralManager;
        private IntPtr _service;
        private IntPtr _rxCharacteristic;
        private IntPtr _txCharacteristic;
        private int _subscribedCentrals;
        private bool _disposed;

        public MacOSCoreBluetoothPeripheralSession(
            AppSyncOptions options,
            Func<string, CancellationToken, Task<string>> requestHandler)
        {
            _options = options;
            _requestHandler = requestHandler;

            Native.EnsureCoreBluetoothLoaded();
            _delegate = Native.IntPtr_objc_msgSend(EnsureDelegateClass(), Native.Sel("new"));
            Sessions[_delegate] = this;
            _queue = Native.dispatch_queue_create("RideManager.AppSync.CoreBluetooth", IntPtr.Zero);

            var managerClass = Native.Class("CBPeripheralManager");
            var allocated = Native.IntPtr_objc_msgSend(managerClass, Native.Sel("alloc"));
            _peripheralManager = Native.IntPtr_objc_msgSend_IntPtr_IntPtr_IntPtr(
                allocated,
                Native.Sel("initWithDelegate:queue:options:"),
                _delegate,
                _queue,
                IntPtr.Zero);
        }

        public async Task StartAsync(CancellationToken cancellationToken)
        {
            await _advertising.Task.WaitAsync(TimeSpan.FromSeconds(12), cancellationToken).ConfigureAwait(false);
            Console.WriteLine(
                $"App sync bluetooth advertising on CoreBluetooth as {_options.DeviceName}; service={_options.ServiceUuid}, rx={_options.RxUuid}, tx={_options.TxUuid}.");
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _stop.Cancel();
            try
            {
                Native.Void_objc_msgSend(_peripheralManager, Native.Sel("stopAdvertising"));
                Native.Void_objc_msgSend(_peripheralManager, Native.Sel("removeAllServices"));
            }
            catch (Exception)
            {
            }

            Sessions.TryRemove(_delegate, out _);
            ReleaseIfNeeded(_peripheralManager);
            ReleaseIfNeeded(_txCharacteristic);
            ReleaseIfNeeded(_rxCharacteristic);
            ReleaseIfNeeded(_service);
            ReleaseIfNeeded(_delegate);
            _stop.Dispose();
        }

        private static void ReleaseIfNeeded(IntPtr obj)
        {
            if (obj != IntPtr.Zero)
            {
                Native.release(obj);
            }
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
                var cls = Native.objc_allocateClassPair(nsObject, "RideManagerAppSyncPeripheralDelegate", 0);
                AddMethod(cls, "peripheralManagerDidUpdateState:", PeripheralManagerDidUpdateStateDelegate, "v@:@");
                AddMethod(cls, "peripheralManager:didAddService:error:", DidAddServiceDelegate, "v@:@@@");
                AddMethod(cls, "peripheralManagerDidStartAdvertising:error:", DidStartAdvertisingDelegate, "v@:@@");
                AddMethod(cls, "peripheralManager:central:didSubscribeToCharacteristic:", DidSubscribeDelegate, "v@:@@@");
                AddMethod(cls, "peripheralManager:central:didUnsubscribeFromCharacteristic:", DidUnsubscribeDelegate, "v@:@@@");
                AddMethod(cls, "peripheralManager:didReceiveWriteRequests:", DidReceiveWriteRequestsDelegate, "v@:@@");
                AddMethod(cls, "peripheralManagerIsReadyToUpdateSubscribers:", IsReadyToUpdateSubscribersDelegate, "v@:@");
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

        private void OnPeripheralManagerDidUpdateState(IntPtr manager)
        {
            var state = Native.Int64_objc_msgSend(manager, Native.Sel("state"));
            switch (state)
            {
                case PeripheralManagerStatePoweredOn:
                    RegisterService();
                    break;
                case PeripheralManagerStateUnsupported:
                    Fail(new InvalidOperationException("CoreBluetooth peripheral role is unsupported on this Mac."));
                    break;
                case PeripheralManagerStateUnauthorized:
                    Fail(new InvalidOperationException("CoreBluetooth is unauthorized. Allow Bluetooth access for the RideManager process in macOS Privacy settings."));
                    break;
                case PeripheralManagerStatePoweredOff:
                    Fail(new InvalidOperationException("Bluetooth is powered off."));
                    break;
            }
        }

        private void RegisterService()
        {
            var serviceUuid = Native.CreateCBUuid(_options.ServiceUuid);
            var allocatedService = Native.IntPtr_objc_msgSend(Native.Class("CBMutableService"), Native.Sel("alloc"));
            _service = Native.IntPtr_objc_msgSend_IntPtr_Byte(
                allocatedService,
                Native.Sel("initWithType:primary:"),
                serviceUuid,
                1);

            _rxCharacteristic = CreateCharacteristic(
                _options.RxUuid,
                CharacteristicPropertyWrite | CharacteristicPropertyWriteWithoutResponse,
                AttributePermissionWriteable);
            _txCharacteristic = CreateCharacteristic(_options.TxUuid, CharacteristicPropertyNotify, 0);

            var characteristics = Native.CreateArray(_rxCharacteristic, _txCharacteristic);
            Native.Void_objc_msgSend_IntPtr(_service, Native.Sel("setCharacteristics:"), characteristics);
            Native.Void_objc_msgSend_IntPtr(_peripheralManager, Native.Sel("addService:"), _service);
        }

        private static IntPtr CreateCharacteristic(string uuid, ulong properties, ulong permissions)
        {
            var allocated = Native.IntPtr_objc_msgSend(Native.Class("CBMutableCharacteristic"), Native.Sel("alloc"));
            var characteristic = Native.IntPtr_objc_msgSend_IntPtr_UInt64_IntPtr_UInt64(
                allocated,
                Native.Sel("initWithType:properties:value:permissions:"),
                Native.CreateCBUuid(uuid),
                properties,
                IntPtr.Zero,
                permissions);
            return characteristic;
        }

        private void OnDidAddService(IntPtr error)
        {
            if (error != IntPtr.Zero)
            {
                Fail(new InvalidOperationException($"CoreBluetooth add service failed: {Native.ErrorDescription(error)}"));
                return;
            }

            Native.Void_objc_msgSend_IntPtr(
                _peripheralManager,
                Native.Sel("startAdvertising:"),
                Native.CreateAdvertisement(_options.DeviceName, _options.ServiceUuid));
        }

        private void OnDidStartAdvertising(IntPtr error)
        {
            if (error != IntPtr.Zero)
            {
                Fail(new InvalidOperationException($"CoreBluetooth advertising failed: {Native.ErrorDescription(error)}"));
                return;
            }

            _advertising.TrySetResult();
        }

        private void OnDidSubscribeToCharacteristic()
        {
            lock (_notifySync)
            {
                _subscribedCentrals++;
                TryFlushNotificationsLocked();
            }
        }

        private void OnDidUnsubscribeFromCharacteristic()
        {
            lock (_notifySync)
            {
                _subscribedCentrals = Math.Max(0, _subscribedCentrals - 1);
            }
        }

        private void OnDidReceiveWriteRequests(IntPtr requests)
        {
            var count = Native.UIntPtr_objc_msgSend(requests, Native.Sel("count")).ToUInt64();
            for (ulong i = 0; i < count; i++)
            {
                var request = Native.IntPtr_objc_msgSend_UIntPtr(requests, Native.Sel("objectAtIndex:"), (UIntPtr)i);
                if (!RequestTargetsRx(request))
                {
                    Native.Void_objc_msgSend_IntPtr_Int64(
                        _peripheralManager,
                        Native.Sel("respondToRequest:withResult:"),
                        request,
                        AttErrorInvalidHandle);
                    continue;
                }

                Native.Void_objc_msgSend_IntPtr_Int64(
                    _peripheralManager,
                    Native.Sel("respondToRequest:withResult:"),
                    request,
                    AttErrorSuccess);
                HandleRequestAsync(Native.ReadRequestValue(request));
            }
        }

        private bool RequestTargetsRx(IntPtr request)
        {
            var characteristic = Native.IntPtr_objc_msgSend(request, Native.Sel("characteristic"));
            var uuid = Native.ReadUuidString(characteristic);
            return uuid is not null && uuid.Equals(_options.RxUuid, StringComparison.OrdinalIgnoreCase);
        }

        private void HandleRequestAsync(byte[] payload)
        {
            _ = Task.Run(async () =>
            {
                var response = string.Empty;
                try
                {
                    var frame = Encoding.UTF8.GetString(payload);
                    response = await _requestHandler(frame, _stop.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (_stop.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex)
                {
                    response = "{\"v\":1,\"id\":\"\",\"type\":\"error\",\"status\":\"failed\",\"payload\":{\"message\":\""
                        + EscapeJsonString(ex.Message)
                        + "\"}}";
                }

                EnqueueResponse(response);
            }, CancellationToken.None);
        }

        private void EnqueueResponse(string response)
        {
            lock (_notifySync)
            {
                foreach (var chunk in AppSyncNotificationFramer.CreateChunks(response, _options.NotifyChunkBytes))
                {
                    _pendingNotifications.Enqueue(chunk);
                }

                TryFlushNotificationsLocked();
            }
        }

        private void TryFlushNotificationsLocked()
        {
            while (_subscribedCentrals > 0 && _pendingNotifications.Count > 0)
            {
                var chunk = _pendingNotifications.Peek();
                var data = Native.CreateNSData(chunk);
                var didSend = Native.Bool_objc_msgSend_IntPtr_IntPtr_IntPtr(
                    _peripheralManager,
                    Native.Sel("updateValue:forCharacteristic:onSubscribedCentrals:"),
                    data,
                    _txCharacteristic,
                    IntPtr.Zero);
                if (!didSend)
                {
                    return;
                }

                _pendingNotifications.Dequeue();
            }
        }

        private void Fail(Exception ex)
        {
            _advertising.TrySetException(ex);
        }

        private static string EscapeJsonString(string value)
        {
            return value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal);
        }

        private static MacOSCoreBluetoothPeripheralSession? GetSession(IntPtr self)
        {
            Sessions.TryGetValue(self, out var session);
            return session;
        }

        private static void SafeInvoke(IntPtr self, Action<MacOSCoreBluetoothPeripheralSession> action)
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

        private static void PeripheralManagerDidUpdateState(IntPtr self, IntPtr cmd, IntPtr peripheral)
        {
            SafeInvoke(self, session => session.OnPeripheralManagerDidUpdateState(peripheral));
        }

        private static void DidAddService(IntPtr self, IntPtr cmd, IntPtr peripheral, IntPtr service, IntPtr error)
        {
            SafeInvoke(self, session => session.OnDidAddService(error));
        }

        private static void DidStartAdvertising(IntPtr self, IntPtr cmd, IntPtr peripheral, IntPtr error)
        {
            SafeInvoke(self, session => session.OnDidStartAdvertising(error));
        }

        private static void DidSubscribeToCharacteristic(IntPtr self, IntPtr cmd, IntPtr peripheral, IntPtr central, IntPtr characteristic)
        {
            SafeInvoke(self, session => session.OnDidSubscribeToCharacteristic());
        }

        private static void DidUnsubscribeFromCharacteristic(IntPtr self, IntPtr cmd, IntPtr peripheral, IntPtr central, IntPtr characteristic)
        {
            SafeInvoke(self, session => session.OnDidUnsubscribeFromCharacteristic());
        }

        private static void DidReceiveWriteRequests(IntPtr self, IntPtr cmd, IntPtr peripheral, IntPtr requests)
        {
            SafeInvoke(self, session => session.OnDidReceiveWriteRequests(requests));
        }

        private static void PeripheralManagerIsReadyToUpdateSubscribers(IntPtr self, IntPtr cmd, IntPtr peripheral)
        {
            SafeInvoke(self, session =>
            {
                lock (session._notifySync)
                {
                    session.TryFlushNotificationsLocked();
                }
            });
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

        public static IntPtr CreateCBUuid(string uuid)
        {
            return IntPtr_objc_msgSend_IntPtr(Class("CBUUID"), Sel("UUIDWithString:"), CreateNSString(uuid));
        }

        public static IntPtr CreateArray(params IntPtr[] objects)
        {
            var array = IntPtr_objc_msgSend(Class("NSMutableArray"), Sel("array"));
            foreach (var obj in objects.Where(value => value != IntPtr.Zero))
            {
                Void_objc_msgSend_IntPtr(array, Sel("addObject:"), obj);
            }

            return array;
        }

        public static IntPtr CreateAdvertisement(string localName, string serviceUuid)
        {
            var dictionary = IntPtr_objc_msgSend(Class("NSMutableDictionary"), Sel("dictionary"));
            Void_objc_msgSend_IntPtr_IntPtr(
                dictionary,
                Sel("setObject:forKey:"),
                CreateArray(CreateCBUuid(serviceUuid)),
                CreateNSString("kCBAdvDataServiceUUIDs"));
            Void_objc_msgSend_IntPtr_IntPtr(
                dictionary,
                Sel("setObject:forKey:"),
                CreateNSString(localName),
                CreateNSString("kCBAdvDataLocalName"));
            return dictionary;
        }

        public static IntPtr CreateNSData(byte[] bytes)
        {
            if (bytes.Length == 0)
            {
                return IntPtr_objc_msgSend(Class("NSData"), Sel("data"));
            }

            var handle = GCHandle.Alloc(bytes, GCHandleType.Pinned);
            try
            {
                return IntPtr_objc_msgSend_IntPtr_UIntPtr(
                    Class("NSData"),
                    Sel("dataWithBytes:length:"),
                    handle.AddrOfPinnedObject(),
                    (UIntPtr)bytes.Length);
            }
            finally
            {
                handle.Free();
            }
        }

        public static byte[] ReadRequestValue(IntPtr request)
        {
            var data = IntPtr_objc_msgSend(request, Sel("value"));
            return ReadNSData(data);
        }

        public static string? ReadUuidString(IntPtr characteristic)
        {
            if (characteristic == IntPtr.Zero)
            {
                return null;
            }

            var uuid = IntPtr_objc_msgSend(characteristic, Sel("UUID"));
            return ReadStringProperty(uuid, "UUIDString");
        }

        public static string ErrorDescription(IntPtr error)
        {
            if (error == IntPtr.Zero)
            {
                return "unknown error";
            }

            return ReadStringProperty(error, "localizedDescription") ?? "unknown error";
        }

        private static byte[] ReadNSData(IntPtr data)
        {
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

        private static string? ReadStringProperty(IntPtr obj, string selector)
        {
            return obj == IntPtr.Zero ? null : NSStringToString(IntPtr_objc_msgSend(obj, Sel(selector)));
        }

        private static IntPtr CreateNSString(string value)
        {
            var bytes = Encoding.UTF8.GetBytes(value + '\0');
            var utf8 = Marshal.AllocHGlobal(bytes.Length);
            try
            {
                Marshal.Copy(bytes, 0, utf8, bytes.Length);
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
        public static extern void Void_objc_msgSend_IntPtr_Int64(IntPtr receiver, IntPtr selector, IntPtr arg1, long arg2);

        [DllImport(ObjCLib, EntryPoint = "objc_msgSend")]
        public static extern void Void_objc_msgSend_IntPtr_IntPtr(IntPtr receiver, IntPtr selector, IntPtr arg1, IntPtr arg2);

        [DllImport(ObjCLib, EntryPoint = "objc_msgSend")]
        public static extern IntPtr IntPtr_objc_msgSend(IntPtr receiver, IntPtr selector);

        [DllImport(ObjCLib, EntryPoint = "objc_msgSend")]
        public static extern IntPtr IntPtr_objc_msgSend_IntPtr(IntPtr receiver, IntPtr selector, IntPtr arg1);

        [DllImport(ObjCLib, EntryPoint = "objc_msgSend")]
        public static extern IntPtr IntPtr_objc_msgSend_IntPtr_IntPtr_IntPtr(
            IntPtr receiver,
            IntPtr selector,
            IntPtr arg1,
            IntPtr arg2,
            IntPtr arg3);

        [DllImport(ObjCLib, EntryPoint = "objc_msgSend")]
        public static extern IntPtr IntPtr_objc_msgSend_IntPtr_Byte(
            IntPtr receiver,
            IntPtr selector,
            IntPtr arg1,
            byte arg2);

        [DllImport(ObjCLib, EntryPoint = "objc_msgSend")]
        public static extern IntPtr IntPtr_objc_msgSend_IntPtr_UInt64_IntPtr_UInt64(
            IntPtr receiver,
            IntPtr selector,
            IntPtr arg1,
            ulong arg2,
            IntPtr arg3,
            ulong arg4);

        [DllImport(ObjCLib, EntryPoint = "objc_msgSend")]
        public static extern IntPtr IntPtr_objc_msgSend_IntPtr_UIntPtr(
            IntPtr receiver,
            IntPtr selector,
            IntPtr arg1,
            UIntPtr arg2);

        [DllImport(ObjCLib, EntryPoint = "objc_msgSend")]
        public static extern IntPtr IntPtr_objc_msgSend_UIntPtr(IntPtr receiver, IntPtr selector, UIntPtr arg1);

        [DllImport(ObjCLib, EntryPoint = "objc_msgSend")]
        public static extern long Int64_objc_msgSend(IntPtr receiver, IntPtr selector);

        [DllImport(ObjCLib, EntryPoint = "objc_msgSend")]
        public static extern UIntPtr UIntPtr_objc_msgSend(IntPtr receiver, IntPtr selector);

        [DllImport(ObjCLib, EntryPoint = "objc_msgSend")]
        [return: MarshalAs(UnmanagedType.I1)]
        public static extern bool Bool_objc_msgSend_IntPtr_IntPtr_IntPtr(
            IntPtr receiver,
            IntPtr selector,
            IntPtr arg1,
            IntPtr arg2,
            IntPtr arg3);
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void ObjCNoArgDelegate(IntPtr self, IntPtr cmd, IntPtr arg);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void ObjCTwoObjectDelegate(IntPtr self, IntPtr cmd, IntPtr arg1, IntPtr arg2);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void ObjCThreeObjectDelegate(IntPtr self, IntPtr cmd, IntPtr arg1, IntPtr arg2, IntPtr arg3);
}
