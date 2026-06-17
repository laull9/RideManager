using System.Text;
using RideManager.Utils;
using Tmds.DBus;

namespace RideManager.Sensors;

/// <summary>
/// 使用 Linux BlueZ 连接 ESP32-C6 雷达 BLE 服务。
/// </summary>
public sealed class RadarBluetoothClient : IRadarClient
{
    private readonly SensorEndpointOptions _options;
    private readonly TimeSpan _discoveryTimeout;
    private readonly TimeSpan _servicesTimeout;
    private readonly TimeSpan _reconnectDelay;
    private readonly CancellationTokenSource _stop = new();
    private readonly object _sync = new();
    private Task? _runTask;
    private TaskCompletionSource<RadarFrame>? _nextFrame;
    private byte[] _lastFramePayload = Array.Empty<byte>();
    private byte[] _lastHealthPayload = Array.Empty<byte>();

    /// <summary>
    /// 创建 BLE 雷达客户端。
    /// </summary>
    public RadarBluetoothClient(SensorEndpointOptions options)
    {
        _options = options;
        ValidateConfiguration(options);
        _discoveryTimeout = TimeSpan.FromSeconds(Math.Max(1.0, options.ScanTimeoutSeconds));
        _servicesTimeout = TimeSpan.FromSeconds(Math.Max(1.0, options.ServicesTimeoutSeconds));
        _reconnectDelay = TimeSpan.FromSeconds(Math.Max(0.2, options.ReconnectDelaySeconds));
    }

    /// <summary>
    /// 校验 BLE 配置是否足够完成扫描和订阅。
    /// </summary>
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

        if (RadarProtocol.IsPlaceholderAddress(options.Address) && string.IsNullOrWhiteSpace(options.DeviceName))
        {
            throw new InvalidOperationException("Radar BLE config needs sensors.radar.address or sensors.radar.device_name.");
        }
    }

    /// <summary>
    /// 雷达数据帧到达事件。
    /// </summary>
    public event EventHandler<RadarFrame>? FrameReceived;

    /// <summary>
    /// 雷达固件健康状态到达事件。
    /// </summary>
    public event EventHandler<RadarHealth>? HealthReceived;

    /// <summary>
    /// 连接状态变化事件。
    /// </summary>
    public event EventHandler<RadarConnectionState>? StateChanged;

    /// <summary>
    /// 获取最新数据帧。
    /// </summary>
    public RadarFrame? LatestFrame { get; private set; }

    /// <summary>
    /// 获取最新固件健康状态。
    /// </summary>
    public RadarHealth? LatestHealth { get; private set; }

    /// <summary>
    /// 获取当前连接状态。
    /// </summary>
    public RadarConnectionState State { get; private set; } = RadarConnectionState.Idle();

    /// <summary>
    /// 启动 BLE 后台连接循环。
    /// </summary>
    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (_runTask is not null)
        {
            return Task.CompletedTask;
        }

        _runTask = Task.Run(() => RunAsync(_stop.Token), CancellationToken.None);
        return Task.CompletedTask;
    }

    /// <summary>
    /// 等待下一帧 BLE 雷达数据。
    /// </summary>
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

    /// <summary>
    /// 停止 BLE 后台连接循环。
    /// </summary>
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

    /// <summary>
    /// 保持连接，断开后自动重新扫描。
    /// </summary>
    private async Task RunAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            RadarBluetoothSession? session = null;
            string? lastError = null;
            try
            {
                session = await ConnectSessionAsync(cancellationToken).ConfigureAwait(false);
                await session.WaitForDisconnectAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                lastError = ex.Message;
                PublishState("error", null, null, ex.Message);
            }
            finally
            {
                if (session is not null)
                {
                    await session.DisposeAsync().ConfigureAwait(false);
                }
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

    /// <summary>
    /// 扫描、连接并订阅雷达 GATT 特征。
    /// </summary>
    private async Task<RadarBluetoothSession> ConnectSessionAsync(CancellationToken cancellationToken)
    {
        PublishState("scanning", _options.DeviceName, null, "searching BLE advertisement");
        var adapter = await SelectAdapterAsync(cancellationToken).ConfigureAwait(false);
        var device = await DiscoverDeviceAsync(adapter, cancellationToken).ConfigureAwait(false);
        var name = await ReadDeviceNameAsync(device).ConfigureAwait(false);
        var address = await ReadDeviceAddressAsync(device).ConfigureAwait(false);
        IDisposable? notifyWatcher = null;
        IBlueZGattCharacteristic? notify = null;
        bool notifyStarted = false;

        try
        {
            PublishState("connecting", name, address, "connecting GATT");
            await device.ConnectAsync().WaitAsync(cancellationToken).ConfigureAwait(false);
            await WaitForServicesResolvedAsync(device, cancellationToken).ConfigureAwait(false);

            var service = await GetServiceAsync(device, _options.ServiceUuid, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException($"Missing radar service {_options.ServiceUuid}");
            notify = await GetCharacteristicAsync(service, _options.NotifyUuid, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException($"Missing radar notify characteristic {_options.NotifyUuid}");
            var health = _options.SubscribeHealth
                ? await GetCharacteristicAsync(service, _options.HealthUuid, cancellationToken).ConfigureAwait(false)
                : null;

            notifyWatcher = await notify.WatchPropertiesAsync(OnNotifyProperties).WaitAsync(cancellationToken).ConfigureAwait(false);
            await notify.StartNotifyAsync().WaitAsync(cancellationToken).ConfigureAwait(false);
            notifyStarted = true;
            await TryReadCharacteristicValueAsync(notify, ProcessNotifyPayload, "initial notify read", cancellationToken).ConfigureAwait(false);

            if (health is not null)
            {
                // Some BlueZ/firmware combinations stop the vital-data notifications after a
                // second characteristic is subscribed. Keep the data stream as the only notify
                // subscription and read health periodically from the session fallback loop.
                await TryReadCharacteristicValueAsync(health, ProcessHealthPayload, "initial health read", cancellationToken).ConfigureAwait(false);
            }

            PublishState("connected", name, address, "notifications subscribed");
            var session = new RadarBluetoothSession(
                device,
                notify,
                health,
                notifyWatcher,
                ProcessNotifyPayload,
                ProcessHealthPayload,
                message => PublishState("read_error", State.DeviceName, State.DeviceAddress, message),
                name,
                address);
            notifyWatcher = null;
            notifyStarted = false;
            return session;
        }
        catch
        {
            await CleanupPartialConnectionAsync(device, notify, notifyWatcher, notifyStarted).ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    /// 清理尚未交给会话对象托管的 BlueZ 连接资源。
    /// </summary>
    private static async Task CleanupPartialConnectionAsync(
        IBlueZDevice device,
        IBlueZGattCharacteristic? notify,
        IDisposable? notifyWatcher,
        bool notifyStarted)
    {
        notifyWatcher?.Dispose();
        if (notifyStarted && notify is not null)
        {
            try
            {
                await notify.StopNotifyAsync().WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            }
            catch (Exception)
            {
            }
        }

        try
        {
            if (await device.GetAsync<bool>("Connected").WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false))
            {
                await device.DisconnectAsync().WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
            }
        }
        catch (Exception)
        {
        }
    }

    /// <summary>
    /// 选择第一个可用蓝牙适配器。
    /// </summary>
    private static async Task<IBlueZAdapter> SelectAdapterAsync(CancellationToken cancellationToken)
    {
        var adapters = await GetBlueZProxiesAsync<IBlueZAdapter>("org.bluez.Adapter1", null, cancellationToken).ConfigureAwait(false);
        var adapter = adapters.FirstOrDefault() ?? throw new InvalidOperationException("No BlueZ bluetooth adapter found.");
        if (!await adapter.GetAsync<bool>("Powered").WaitAsync(cancellationToken).ConfigureAwait(false))
        {
            await adapter.SetAsync("Powered", true).WaitAsync(cancellationToken).ConfigureAwait(false);
        }

        return adapter;
    }

    /// <summary>
    /// 按地址、设备名或服务 UUID 发现目标雷达。
    /// </summary>
    private async Task<IBlueZDevice> DiscoverDeviceAsync(IBlueZAdapter adapter, CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow.Add(_discoveryTimeout);
        var device = await FindMatchingDeviceAsync(adapter).ConfigureAwait(false);
        if (device is not null)
        {
            return device;
        }

        var filter = new Dictionary<string, object>
        {
            ["Transport"] = "le",
            ["DuplicateData"] = false
        };

        if (_options.MatchByService)
        {
            filter["UUIDs"] = new[] { _options.ServiceUuid };
        }

        await adapter.SetDiscoveryFilterAsync(filter).WaitAsync(cancellationToken).ConfigureAwait(false);

        await adapter.StartDiscoveryAsync().WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            while (DateTimeOffset.UtcNow < deadline)
            {
                cancellationToken.ThrowIfCancellationRequested();
                device = await FindMatchingDeviceAsync(adapter).ConfigureAwait(false);
                if (device is not null)
                {
                    return device;
                }

                await Task.Delay(400, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            try
            {
                await adapter.StopDiscoveryAsync().WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            }
            catch (Exception)
            {
            }

            try
            {
                await adapter.SetDiscoveryFilterAsync(new Dictionary<string, object>()).WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            }
            catch (Exception)
            {
            }
        }

        throw new TimeoutException($"Radar BLE device {_options.DeviceName} was not found within {_discoveryTimeout.TotalSeconds:F0}s.");
    }

    /// <summary>
    /// 在 BlueZ 当前设备缓存中查找目标设备。
    /// </summary>
    private async Task<IBlueZDevice?> FindMatchingDeviceAsync(IBlueZAdapter adapter)
    {
        foreach (var device in await GetBlueZProxiesAsync<IBlueZDevice>("org.bluez.Device1", adapter.ObjectPath, CancellationToken.None).ConfigureAwait(false))
        {
            if (await IsRadarDeviceAsync(device).ConfigureAwait(false))
            {
                return device;
            }
        }

        return null;
    }

    /// <summary>
    /// 判断设备是否匹配雷达广播。
    /// </summary>
    private async Task<bool> IsRadarDeviceAsync(IBlueZDevice device)
    {
        var address = await ReadDeviceAddressAsync(device).ConfigureAwait(false);
        if (!RadarProtocol.IsPlaceholderAddress(_options.Address)
            && address.Equals(_options.Address, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var name = await ReadDeviceNameAsync(device).ConfigureAwait(false);
        var alias = await ReadDeviceAliasAsync(device).ConfigureAwait(false);
        if (!_options.MatchByService
            && (name.Equals(_options.DeviceName, StringComparison.OrdinalIgnoreCase)
                || alias.Equals(_options.DeviceName, StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        var uuids = await ReadDeviceUuidsAsync(device).ConfigureAwait(false);
        return uuids.Any(uuid => uuid.Equals(_options.ServiceUuid, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// 处理雷达数据通知。
    /// </summary>
    private void OnNotifyProperties(PropertyChanges changes)
    {
        if (!TryReadValue(changes, out var payload))
        {
            return;
        }

        ProcessNotifyPayload(payload, "notify");
    }

    /// <summary>
    /// 处理雷达数据负载。
    /// </summary>
    private void ProcessNotifyPayload(byte[] payload, string source)
    {
        if (payload.Length == 0 || payload.SequenceEqual(_lastFramePayload))
        {
            return;
        }

        _lastFramePayload = payload.ToArray();
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
            PublishState("parse_error", State.DeviceName, State.DeviceAddress, DecodePayloadError(source, payload, ex));
        }
    }

    /// <summary>
    /// 处理雷达健康状态负载。
    /// </summary>
    private void ProcessHealthPayload(byte[] payload, string source)
    {
        if (payload.Length == 0 || payload.SequenceEqual(_lastHealthPayload))
        {
            return;
        }

        _lastHealthPayload = payload.ToArray();
        try
        {
            var health = RadarProtocol.ParseHealth(payload, DateTimeOffset.UtcNow);
            LatestHealth = health;
            HealthReceived?.Invoke(this, health);
        }
        catch (Exception ex)
        {
            PublishState("parse_error", State.DeviceName, State.DeviceAddress, DecodePayloadError(source, payload, ex));
        }
    }

    /// <summary>
    /// 发布连接状态。
    /// </summary>
    private void PublishState(string phase, string? deviceName, string? deviceAddress, string? message)
    {
        State = new RadarConnectionState(phase, deviceName, deviceAddress, message, DateTimeOffset.UtcNow);
        StateChanged?.Invoke(this, State);
    }

    private static async Task<string> ReadDeviceNameAsync(IBlueZDevice device)
    {
        try
        {
            return await device.GetAsync<string>("Name").ConfigureAwait(false);
        }
        catch (Exception)
        {
            return string.Empty;
        }
    }

    private static async Task<string> ReadDeviceAddressAsync(IBlueZDevice device)
    {
        try
        {
            return await device.GetAsync<string>("Address").ConfigureAwait(false);
        }
        catch (Exception)
        {
            return string.Empty;
        }
    }

    private static async Task<string> ReadDeviceAliasAsync(IBlueZDevice device)
    {
        try
        {
            return await device.GetAsync<string>("Alias").ConfigureAwait(false);
        }
        catch (Exception)
        {
            return string.Empty;
        }
    }

    private static async Task<IReadOnlyList<string>> ReadDeviceUuidsAsync(IBlueZDevice device)
    {
        try
        {
            return await device.GetAsync<string[]>("UUIDs").ConfigureAwait(false);
        }
        catch (Exception)
        {
            return Array.Empty<string>();
        }
    }

    private static string DecodePayloadError(string source, byte[] payload, Exception ex)
    {
        var text = Encoding.ASCII.GetString(payload);
        return $"failed to parse {source} payload '{text}': {ex.Message}";
    }

    /// <summary>
    /// 主动读取一次 GATT Value，弥补部分 BlueZ/固件组合不触发 PropertiesChanged 的情况。
    /// </summary>
    private async Task TryReadCharacteristicValueAsync(
        IBlueZGattCharacteristic characteristic,
        Action<byte[], string> handler,
        string source,
        CancellationToken cancellationToken)
    {
        try
        {
            var payload = await characteristic.ReadValueAsync(new Dictionary<string, object>())
                .WaitAsync(cancellationToken)
                .ConfigureAwait(false);
            handler(payload, source);
        }
        catch (Exception ex) when (ex is DBusException or InvalidOperationException or TimeoutException)
        {
            PublishState("read_error", State.DeviceName, State.DeviceAddress, $"{source} failed: {ex.Message}");
        }
    }

    /// <summary>
    /// 从属性变化中读取 GATT Value。
    /// </summary>
    private static bool TryReadValue(PropertyChanges changes, out byte[] payload)
    {
        foreach (var change in changes.Changed)
        {
            if (change.Key != "Value")
            {
                continue;
            }

            if (TryCoerceByteArray(change.Value, out payload))
            {
                return true;
            }
        }

        payload = Array.Empty<byte>();
        return false;
    }

    /// <summary>
    /// BlueZ Value 在不同 D-Bus 读取路径下可能被 Tmds.DBus 解成不同集合类型。
    /// </summary>
    private static bool TryCoerceByteArray(object? value, out byte[] payload)
    {
        switch (value)
        {
            case byte[] bytes:
                payload = bytes;
                return true;
            case IEnumerable<byte> byteEnumerable:
                payload = byteEnumerable.ToArray();
                return true;
            case System.Collections.IEnumerable enumerable when value is not string:
                return TryCoerceEnumerable(enumerable, out payload);
            default:
                payload = Array.Empty<byte>();
                return false;
        }
    }

    private static bool TryCoerceEnumerable(System.Collections.IEnumerable values, out byte[] payload)
    {
        var buffer = new List<byte>();
        foreach (var item in values.Cast<object?>())
        {
            try
            {
                buffer.Add(item switch
                {
                    byte value => value,
                    sbyte value => unchecked((byte)value),
                    IConvertible convertible => Convert.ToByte(convertible),
                    _ => throw new InvalidCastException($"Unsupported byte value type {item?.GetType().FullName ?? "<null>"}.")
                });
            }
            catch (Exception)
            {
                payload = Array.Empty<byte>();
                return false;
            }
        }

        payload = buffer.ToArray();
        return true;
    }

    /// <summary>
    /// 读取 BlueZ 管理对象并创建指定接口代理。
    /// </summary>
    private static async Task<IReadOnlyList<T>> GetBlueZProxiesAsync<T>(
        string interfaceName,
        ObjectPath? root,
        CancellationToken cancellationToken)
        where T : IDBusObject
    {
        var manager = Connection.System.CreateProxy<IBlueZObjectManager>("org.bluez", ObjectPath.Root);
        var objects = await manager.GetManagedObjectsAsync().WaitAsync(cancellationToken).ConfigureAwait(false);
        return objects
            .Where(value => value.Value.ContainsKey(interfaceName) && IsUnderRoot(value.Key, root))
            .Select(value => Connection.System.CreateProxy<T>("org.bluez", value.Key))
            .ToArray();
    }

    /// <summary>
    /// 判断对象路径是否属于指定根对象。
    /// </summary>
    private static bool IsUnderRoot(ObjectPath objectPath, ObjectPath? root)
    {
        if (root is null)
        {
            return true;
        }

        return objectPath.ToString().StartsWith($"{root}/", StringComparison.Ordinal);
    }

    /// <summary>
    /// 按 UUID 查找设备下的 GATT 服务。
    /// </summary>
    private static async Task<IBlueZGattService?> GetServiceAsync(IBlueZDevice device, string uuid, CancellationToken cancellationToken)
    {
        var services = await GetBlueZProxiesAsync<IBlueZGattService>("org.bluez.GattService1", device.ObjectPath, cancellationToken).ConfigureAwait(false);
        foreach (var service in services)
        {
            var serviceUuid = await service.GetAsync<string>("UUID").WaitAsync(cancellationToken).ConfigureAwait(false);
            if (serviceUuid.Equals(uuid, StringComparison.OrdinalIgnoreCase))
            {
                return service;
            }
        }

        return null;
    }

    /// <summary>
    /// 按 UUID 查找服务下的 GATT 特征。
    /// </summary>
    private static async Task<IBlueZGattCharacteristic?> GetCharacteristicAsync(
        IBlueZGattService service,
        string uuid,
        CancellationToken cancellationToken)
    {
        var characteristics = await GetBlueZProxiesAsync<IBlueZGattCharacteristic>("org.bluez.GattCharacteristic1", service.ObjectPath, cancellationToken).ConfigureAwait(false);
        foreach (var characteristic in characteristics)
        {
            var characteristicUuid = await characteristic.GetAsync<string>("UUID").WaitAsync(cancellationToken).ConfigureAwait(false);
            if (characteristicUuid.Equals(uuid, StringComparison.OrdinalIgnoreCase))
            {
                return characteristic;
            }
        }

        return null;
    }

    /// <summary>
    /// 等待设备完成 GATT 服务解析。
    /// </summary>
    private async Task WaitForServicesResolvedAsync(IBlueZDevice device, CancellationToken cancellationToken)
    {
        if (await device.GetAsync<bool>("ServicesResolved").WaitAsync(cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var watcher = await device.WatchPropertiesAsync(changes =>
        {
            foreach (var change in changes.Changed)
            {
                if (change.Key == "ServicesResolved" && true.Equals(change.Value))
                {
                    completion.TrySetResult();
                }
            }
        }).WaitAsync(cancellationToken).ConfigureAwait(false);

        await completion.Task.WaitAsync(_servicesTimeout, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 保存一次 BLE 连接会话资源。
    /// </summary>
    private sealed class RadarBluetoothSession : IAsyncDisposable
    {
        private readonly IBlueZDevice _device;
        private readonly IBlueZGattCharacteristic _notify;
        private readonly IBlueZGattCharacteristic? _health;
        private readonly IDisposable _notifyWatcher;
        private readonly Action<byte[], string> _notifyHandler;
        private readonly Action<byte[], string> _healthHandler;
        private readonly Action<string> _readErrorHandler;
        private readonly CancellationTokenSource _pollStop = new();
        private readonly Task _pollTask;
        private readonly TaskCompletionSource _disconnected = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly IDisposable _disconnectWatcher;

        /// <summary>
        /// 创建 BLE 会话。
        /// </summary>
        public RadarBluetoothSession(
            IBlueZDevice device,
            IBlueZGattCharacteristic notify,
            IBlueZGattCharacteristic? health,
            IDisposable notifyWatcher,
            Action<byte[], string> notifyHandler,
            Action<byte[], string> healthHandler,
            Action<string> readErrorHandler,
            string deviceName,
            string deviceAddress)
        {
            _device = device;
            _notify = notify;
            _health = health;
            _notifyWatcher = notifyWatcher;
            _notifyHandler = notifyHandler;
            _healthHandler = healthHandler;
            _readErrorHandler = readErrorHandler;
            DeviceName = deviceName;
            DeviceAddress = deviceAddress;
            _disconnectWatcher = _device.WatchPropertiesAsync(OnDeviceProperties).GetAwaiter().GetResult();
            _pollTask = Task.Run(() => PollValuesAsync(_pollStop.Token), CancellationToken.None);
        }

        /// <summary>
        /// 获取会话设备名。
        /// </summary>
        public string DeviceName { get; }

        /// <summary>
        /// 获取会话设备地址。
        /// </summary>
        public string DeviceAddress { get; }

        /// <summary>
        /// 等待 BLE 断开。
        /// </summary>
        public Task WaitForDisconnectAsync(CancellationToken cancellationToken)
        {
            return _disconnected.Task.WaitAsync(cancellationToken);
        }

        /// <summary>
        /// 释放 GATT 订阅和连接。
        /// </summary>
        public async ValueTask DisposeAsync()
        {
            _pollStop.Cancel();
            try
            {
                await _pollTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }

            _pollStop.Dispose();
            _disconnectWatcher.Dispose();
            _notifyWatcher.Dispose();
            try
            {
                await _notify.StopNotifyAsync().WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            }
            catch (Exception)
            {
            }

            try
            {
                if (await _device.GetAsync<bool>("Connected").WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false))
                {
                    await _device.DisconnectAsync().WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
                }
            }
            catch (Exception)
            {
            }
        }

        private void OnDeviceProperties(PropertyChanges changes)
        {
            foreach (var change in changes.Changed)
            {
                if (change.Key == "Connected" && false.Equals(change.Value))
                {
                    _disconnected.TrySetResult();
                }
            }
        }

        private async Task PollValuesAsync(CancellationToken cancellationToken)
        {
            var lastHealthRead = DateTimeOffset.MinValue;
            while (!cancellationToken.IsCancellationRequested)
            {
                TryReadCharacteristicPropertyValue(_notify, _notifyHandler, "notify value property poll");
                TryReadCharacteristicValue(_notify, _notifyHandler, "notify read fallback");

                if (_health is not null && DateTimeOffset.UtcNow - lastHealthRead >= TimeSpan.FromSeconds(2))
                {
                    TryReadCharacteristicValue(_health, _healthHandler, "health read fallback");
                    lastHealthRead = DateTimeOffset.UtcNow;
                }

                await Task.Delay(200, cancellationToken).ConfigureAwait(false);
            }
        }

        private void TryReadCharacteristicValue(
            IBlueZGattCharacteristic characteristic,
            Action<byte[], string> handler,
            string source)
        {
            try
            {
                var payload = characteristic.ReadValueAsync(new Dictionary<string, object>())
                    .WaitAsync(TimeSpan.FromSeconds(2))
                    .GetAwaiter()
                    .GetResult();
                handler(payload, source);
            }
            catch (Exception ex)
            {
                _readErrorHandler($"{source} failed: {ex.Message}");
            }
        }

        private void TryReadCharacteristicPropertyValue(
            IBlueZGattCharacteristic characteristic,
            Action<byte[], string> handler,
            string source)
        {
            try
            {
                var properties = characteristic.GetAllAsync()
                    .WaitAsync(TimeSpan.FromSeconds(2))
                    .GetAwaiter()
                    .GetResult();
                if (properties.TryGetValue("Value", out var value)
                    && TryCoerceByteArray(value, out var payload))
                {
                    handler(payload, source);
                }
            }
            catch (Exception ex)
            {
                _readErrorHandler($"{source} failed: {ex.Message}");
            }
        }
    }
}
