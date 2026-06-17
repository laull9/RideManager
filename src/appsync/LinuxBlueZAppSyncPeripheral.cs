using System.Text;
using RideManager.Sensors;
using RideManager.Utils;
using Tmds.DBus;

namespace RideManager.AppSync;

/// <summary>
/// 使用 Linux BlueZ 暴露 App 同步蓝牙外设服务。
/// </summary>
public sealed class LinuxBlueZAppSyncPeripheral : IAppSyncPeripheral
{
    private static readonly ObjectPath AppPath = new("/com/ridemanager/appsync");
    private static readonly ObjectPath ServicePath = new("/com/ridemanager/appsync/service0");
    private static readonly ObjectPath RxPath = new("/com/ridemanager/appsync/service0/rx");
    private static readonly ObjectPath TxPath = new("/com/ridemanager/appsync/service0/tx");
    private static readonly ObjectPath AdvertisementPath = new("/com/ridemanager/appsync/advertisement0");

    private readonly AppSyncOptions _options;
    private Connection? _connection;
    private IBlueZGattManager? _gattManager;
    private IBlueZLeAdvertisingManager? _advertisingManager;
    private AppSyncGattApplication? _application;
    private AppSyncAdvertisement? _advertisement;
    private bool _gattRegistered;
    private bool _advertisementRegistered;

    /// <summary>
    /// 创建 BlueZ App 同步外设宿主。
    /// </summary>
    public LinuxBlueZAppSyncPeripheral(AppSyncOptions options)
    {
        _options = options;
    }

    /// <summary>
    /// 启动 BlueZ 外设并注册 GATT 服务与 LE 广播。
    /// </summary>
    public async Task StartAsync(Func<string, CancellationToken, Task<string>> requestHandler, CancellationToken cancellationToken)
    {
        try
        {
            _connection = new Connection(Address.System);
            await _connection.ConnectAsync().WaitAsync(cancellationToken).ConfigureAwait(false);

            var adapter = await SelectAdapterAsync(_connection, cancellationToken).ConfigureAwait(false);
            await adapter.Proxy.SetAsync("Powered", true).WaitAsync(cancellationToken).ConfigureAwait(false);
            await adapter.Proxy.SetAsync("Pairable", true).WaitAsync(cancellationToken).ConfigureAwait(false);
            await adapter.Proxy.SetAsync("Alias", _options.DeviceName).WaitAsync(cancellationToken).ConfigureAwait(false);

            _application = new AppSyncGattApplication(_options, requestHandler, cancellationToken);
            _advertisement = new AppSyncAdvertisement(_options);
            await _connection.RegisterObjectsAsync(_application.Objects.Concat(new IDBusObject[] { _advertisement }))
                .WaitAsync(cancellationToken)
                .ConfigureAwait(false);

            _gattManager = _connection.CreateProxy<IBlueZGattManager>("org.bluez", adapter.Path);
            _advertisingManager = _connection.CreateProxy<IBlueZLeAdvertisingManager>("org.bluez", adapter.Path);
            await _advertisingManager.RegisterAdvertisementAsync(AdvertisementPath, new Dictionary<string, object>())
                .WaitAsync(cancellationToken)
                .ConfigureAwait(false);
            _advertisementRegistered = true;

            await _gattManager.RegisterApplicationAsync(AppPath, new Dictionary<string, object>())
                .WaitAsync(cancellationToken)
                .ConfigureAwait(false);
            _gattRegistered = true;

            var activeAdvertisements = await _advertisingManager.GetAsync<byte>("ActiveInstances")
                .WaitAsync(cancellationToken)
                .ConfigureAwait(false);

            Console.WriteLine(
                $"App sync bluetooth GATT registered on BlueZ as {_options.DeviceName}; service={_options.ServiceUuid}, rx={_options.RxUuid}, tx={_options.TxUuid}.");
            Console.WriteLine($"App sync bluetooth advertisement registered; active advertisements={activeAdvertisements}. Scan with nRF Connect and subscribe to TX before writing RX.");
        }
        catch (Exception ex) when (ex is DBusException or InvalidOperationException or TimeoutException)
        {
            await DisposeAsync().ConfigureAwait(false);
            Console.WriteLine($"App sync bluetooth BlueZ startup failed: {ex.Message}");
        }
    }

    /// <summary>
    /// 释放 BlueZ 外设资源。
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (_advertisementRegistered && _advertisingManager is not null)
        {
            try
            {
                await _advertisingManager.UnregisterAdvertisementAsync(AdvertisementPath)
                    .WaitAsync(TimeSpan.FromSeconds(3))
                    .ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is DBusException or InvalidOperationException or DisconnectedException or TimeoutException)
            {
                Console.WriteLine($"App sync bluetooth advertisement unregister warning: {ex.Message}");
            }
            finally
            {
                _advertisementRegistered = false;
            }
        }

        if (_gattRegistered && _gattManager is not null)
        {
            try
            {
                await _gattManager.UnregisterApplicationAsync(AppPath)
                    .WaitAsync(TimeSpan.FromSeconds(3))
                    .ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is DBusException or InvalidOperationException or DisconnectedException or TimeoutException)
            {
                Console.WriteLine($"App sync bluetooth GATT unregister warning: {ex.Message}");
            }
            finally
            {
                _gattRegistered = false;
            }
        }

        if (_connection is not null)
        {
            if (_application is not null)
            {
                TryUnregisterObjects(_connection, _application.Objects);
            }

            if (_advertisement is not null)
            {
                TryUnregisterObjects(_connection, new IDBusObject[] { _advertisement });
            }

            _connection.Dispose();
            _connection = null;
        }

        _gattManager = null;
        _advertisingManager = null;
        _application = null;
        _advertisement = null;
    }

    /// <summary>
    /// 选择第一个同时支持 GATT 和 LE 广播管理器的 BlueZ 适配器。
    /// </summary>
    private static async Task<BlueZAdapterSelection> SelectAdapterAsync(Connection connection, CancellationToken cancellationToken)
    {
        var objectManager = connection.CreateProxy<IBlueZObjectManager>("org.bluez", ObjectPath.Root);
        var managedObjects = await objectManager.GetManagedObjectsAsync().WaitAsync(cancellationToken).ConfigureAwait(false);
        foreach (var (path, interfaces) in managedObjects)
        {
            if (interfaces.ContainsKey("org.bluez.Adapter1")
                && interfaces.ContainsKey("org.bluez.GattManager1")
                && interfaces.ContainsKey("org.bluez.LEAdvertisingManager1"))
            {
                return new BlueZAdapterSelection(path, connection.CreateProxy<IBlueZAdapter>("org.bluez", path));
            }
        }

        throw new InvalidOperationException("No BlueZ bluetooth adapter with GATT and LE advertising support found.");
    }

    /// <summary>
    /// 尝试取消导出本地 D-Bus 对象。
    /// </summary>
    private static void TryUnregisterObjects(Connection connection, IEnumerable<IDBusObject> objects)
    {
        try
        {
            connection.UnregisterObjects(objects);
        }
        catch (Exception ex) when (ex is InvalidOperationException or DisconnectedException)
        {
            Console.WriteLine($"App sync bluetooth local object unregister warning: {ex.Message}");
        }
    }

    private sealed record BlueZAdapterSelection(ObjectPath Path, IBlueZAdapter Proxy);

    private sealed class AppSyncGattApplication : IBlueZObjectManager
    {
        private readonly AppSyncGattService _service;
        private readonly AppSyncRxCharacteristic _rx;
        private readonly AppSyncTxCharacteristic _tx;

        public AppSyncGattApplication(
            AppSyncOptions options,
            Func<string, CancellationToken, Task<string>> requestHandler,
            CancellationToken cancellationToken)
        {
            _service = new AppSyncGattService(options.ServiceUuid);
            _tx = new AppSyncTxCharacteristic(options.TxUuid, options.NotifyChunkBytes);
            _rx = new AppSyncRxCharacteristic(options.RxUuid, requestHandler, _tx.NotifyAsync, cancellationToken);
        }

        public ObjectPath ObjectPath => AppPath;

        public IReadOnlyList<IDBusObject> Objects => new IDBusObject[] { this, _service, _rx, _tx };

        public Task<IDictionary<ObjectPath, IDictionary<string, IDictionary<string, object>>>> GetManagedObjectsAsync()
        {
            IDictionary<ObjectPath, IDictionary<string, IDictionary<string, object>>> objects =
                new Dictionary<ObjectPath, IDictionary<string, IDictionary<string, object>>>
                {
                    [_service.ObjectPath] = _service.GetInterfaceProperties(),
                    [_rx.ObjectPath] = _rx.GetInterfaceProperties(),
                    [_tx.ObjectPath] = _tx.GetInterfaceProperties()
                };
            return Task.FromResult(objects);
        }
    }

    private sealed class AppSyncAdvertisement : IBlueZLocalAdvertisement
    {
        private readonly AppSyncOptions _options;

        public AppSyncAdvertisement(AppSyncOptions options)
        {
            _options = options;
        }

        public ObjectPath ObjectPath => AdvertisementPath;

        public event Action<PropertyChanges>? PropertiesChanged;

        public Task ReleaseAsync()
        {
            Console.WriteLine("App sync bluetooth advertisement released by BlueZ.");
            return Task.CompletedTask;
        }

        public Task<object> GetAsync(string prop)
        {
            return Task.FromResult(GetProperties()[prop]);
        }

        public Task<IDictionary<string, object>> GetAllAsync()
        {
            return Task.FromResult(GetProperties());
        }

        public Task SetAsync(string prop, object val)
        {
            throw new DBusException("org.freedesktop.DBus.Error.PropertyReadOnly", $"Property {prop} is read-only.");
        }

        public Task<IDisposable> WatchPropertiesAsync(Action<PropertyChanges> handler)
        {
            return SignalWatcher.AddAsync(this, nameof(PropertiesChanged), handler);
        }

        public IDictionary<string, IDictionary<string, object>> GetInterfaceProperties()
        {
            return new Dictionary<string, IDictionary<string, object>>
            {
                ["org.bluez.LEAdvertisement1"] = GetProperties()
            };
        }

        private IDictionary<string, object> GetProperties()
        {
            return new Dictionary<string, object>
            {
                ["Type"] = "peripheral",
                ["ServiceUUIDs"] = new[] { _options.ServiceUuid },
                ["LocalName"] = _options.DeviceName,
                ["Discoverable"] = true,
                ["Includes"] = new[] { "tx-power" }
            };
        }
    }

    private sealed class AppSyncGattService : IBlueZLocalGattService
    {
        private readonly string _uuid;

        public AppSyncGattService(string uuid)
        {
            _uuid = uuid;
        }

        public ObjectPath ObjectPath => ServicePath;

        public event Action<PropertyChanges>? PropertiesChanged;

        public Task<object> GetAsync(string prop)
        {
            return Task.FromResult(GetProperties()[prop]);
        }

        public Task<IDictionary<string, object>> GetAllAsync()
        {
            return Task.FromResult(GetProperties());
        }

        public Task SetAsync(string prop, object val)
        {
            throw new DBusException("org.freedesktop.DBus.Error.PropertyReadOnly", $"Property {prop} is read-only.");
        }

        public Task<IDisposable> WatchPropertiesAsync(Action<PropertyChanges> handler)
        {
            return SignalWatcher.AddAsync(this, nameof(PropertiesChanged), handler);
        }

        public IDictionary<string, IDictionary<string, object>> GetInterfaceProperties()
        {
            return new Dictionary<string, IDictionary<string, object>>
            {
                ["org.bluez.GattService1"] = GetProperties()
            };
        }

        private IDictionary<string, object> GetProperties()
        {
            return new Dictionary<string, object>
            {
                ["UUID"] = _uuid,
                ["Primary"] = true,
                ["Characteristics"] = new[] { RxPath, TxPath }
            };
        }
    }

    private sealed class AppSyncRxCharacteristic : IBlueZLocalGattCharacteristic
    {
        private readonly string _uuid;
        private readonly Func<string, CancellationToken, Task<string>> _requestHandler;
        private readonly Func<string, Task> _responseWriter;
        private readonly CancellationToken _cancellationToken;

        public AppSyncRxCharacteristic(
            string uuid,
            Func<string, CancellationToken, Task<string>> requestHandler,
            Func<string, Task> responseWriter,
            CancellationToken cancellationToken)
        {
            _uuid = uuid;
            _requestHandler = requestHandler;
            _responseWriter = responseWriter;
            _cancellationToken = cancellationToken;
        }

        public ObjectPath ObjectPath => RxPath;

        public event Action<PropertyChanges>? PropertiesChanged;

        public Task StartNotifyAsync()
        {
            return Task.CompletedTask;
        }

        public Task StopNotifyAsync()
        {
            return Task.CompletedTask;
        }

        public Task<byte[]> ReadValueAsync(IDictionary<string, object> options)
        {
            return Task.FromResult(Array.Empty<byte>());
        }

        public async Task WriteValueAsync(byte[] value, IDictionary<string, object> options)
        {
            var frame = Encoding.UTF8.GetString(value);
            Console.WriteLine($"App sync bluetooth RX {value.Length} bytes: {frame}");
            var response = await _requestHandler(frame, _cancellationToken).ConfigureAwait(false);
            await _responseWriter(response).ConfigureAwait(false);
        }

        public Task<object> GetAsync(string prop)
        {
            return Task.FromResult(GetProperties()[prop]);
        }

        public Task<IDictionary<string, object>> GetAllAsync()
        {
            return Task.FromResult(GetProperties());
        }

        public Task SetAsync(string prop, object val)
        {
            throw new DBusException("org.freedesktop.DBus.Error.PropertyReadOnly", $"Property {prop} is read-only.");
        }

        public Task<IDisposable> WatchPropertiesAsync(Action<PropertyChanges> handler)
        {
            return SignalWatcher.AddAsync(this, nameof(PropertiesChanged), handler);
        }

        public IDictionary<string, IDictionary<string, object>> GetInterfaceProperties()
        {
            return new Dictionary<string, IDictionary<string, object>>
            {
                ["org.bluez.GattCharacteristic1"] = GetProperties()
            };
        }

        private IDictionary<string, object> GetProperties()
        {
            return new Dictionary<string, object>
            {
                ["UUID"] = _uuid,
                ["Service"] = ServicePath,
                ["Flags"] = new[] { "write", "write-without-response" }
            };
        }
    }

    private sealed class AppSyncTxCharacteristic : IBlueZLocalGattCharacteristic
    {
        private readonly string _uuid;
        private readonly int _notifyChunkBytes;
        private byte[] _value = Array.Empty<byte>();
        private bool _notifying;

        public AppSyncTxCharacteristic(string uuid, int notifyChunkBytes)
        {
            _uuid = uuid;
            _notifyChunkBytes = Math.Max(20, notifyChunkBytes);
        }

        public ObjectPath ObjectPath => TxPath;

        public event Action<PropertyChanges>? PropertiesChanged;

        public Task StartNotifyAsync()
        {
            if (!_notifying)
            {
                _notifying = true;
                Console.WriteLine("App sync bluetooth TX notifications enabled.");
                PropertiesChanged?.Invoke(PropertyChanges.ForProperty("Notifying", true));
            }

            return Task.CompletedTask;
        }

        public Task StopNotifyAsync()
        {
            if (_notifying)
            {
                _notifying = false;
                Console.WriteLine("App sync bluetooth TX notifications disabled.");
                PropertiesChanged?.Invoke(PropertyChanges.ForProperty("Notifying", false));
            }

            return Task.CompletedTask;
        }

        public Task<byte[]> ReadValueAsync(IDictionary<string, object> options)
        {
            return Task.FromResult(_value);
        }

        public Task WriteValueAsync(byte[] value, IDictionary<string, object> options)
        {
            return Task.CompletedTask;
        }

        public async Task NotifyAsync(string response)
        {
            var data = Encoding.UTF8.GetBytes(response);
            Console.WriteLine($"App sync bluetooth TX {data.Length} bytes: {response}");
            if (!_notifying)
            {
                Console.WriteLine("App sync bluetooth TX skipped because client has not subscribed.");
                return;
            }

            foreach (var chunk in AppSyncNotificationFramer.CreateChunks(response, _notifyChunkBytes))
            {
                _value = chunk;
                PropertiesChanged?.Invoke(PropertyChanges.ForProperty("Value", _value));
                await Task.Yield();
            }
        }

        public Task<object> GetAsync(string prop)
        {
            return Task.FromResult(GetProperties()[prop]);
        }

        public Task<IDictionary<string, object>> GetAllAsync()
        {
            return Task.FromResult(GetProperties());
        }

        public Task SetAsync(string prop, object val)
        {
            throw new DBusException("org.freedesktop.DBus.Error.PropertyReadOnly", $"Property {prop} is read-only.");
        }

        public Task<IDisposable> WatchPropertiesAsync(Action<PropertyChanges> handler)
        {
            return SignalWatcher.AddAsync(this, nameof(PropertiesChanged), handler);
        }

        public IDictionary<string, IDictionary<string, object>> GetInterfaceProperties()
        {
            return new Dictionary<string, IDictionary<string, object>>
            {
                ["org.bluez.GattCharacteristic1"] = GetProperties()
            };
        }

        private IDictionary<string, object> GetProperties()
        {
            return new Dictionary<string, object>
            {
                ["UUID"] = _uuid,
                ["Service"] = ServicePath,
                ["Flags"] = new[] { "notify" },
                ["Notifying"] = _notifying,
                ["Value"] = _value
            };
        }
    }
}
