using Tmds.DBus;

namespace RideManager.Sensors;

/// <summary>
/// BlueZ ObjectManager 代理。
/// </summary>
[DBusInterface("org.freedesktop.DBus.ObjectManager")]
internal interface IBlueZObjectManager : IDBusObject
{
    /// <summary>
    /// 读取 BlueZ 当前管理对象。
    /// </summary>
    Task<IDictionary<ObjectPath, IDictionary<string, IDictionary<string, object>>>> GetManagedObjectsAsync();
}

/// <summary>
/// BlueZ Adapter1 代理。
/// </summary>
[DBusInterface("org.bluez.Adapter1")]
internal interface IBlueZAdapter : IDBusObject
{
    /// <summary>
    /// 开始扫描。
    /// </summary>
    Task StartDiscoveryAsync();

    /// <summary>
    /// 设置扫描过滤器。
    /// </summary>
    Task SetDiscoveryFilterAsync(IDictionary<string, object> properties);

    /// <summary>
    /// 停止扫描。
    /// </summary>
    Task StopDiscoveryAsync();

    /// <summary>
    /// 读取属性。
    /// </summary>
    Task<T> GetAsync<T>(string prop);

    /// <summary>
    /// 写入属性。
    /// </summary>
    Task SetAsync(string prop, object val);

    /// <summary>
    /// 监听属性变化。
    /// </summary>
    Task<IDisposable> WatchPropertiesAsync(Action<PropertyChanges> handler);
}

/// <summary>
/// BlueZ Device1 代理。
/// </summary>
[DBusInterface("org.bluez.Device1")]
internal interface IBlueZDevice : IDBusObject
{
    /// <summary>
    /// 连接设备。
    /// </summary>
    Task ConnectAsync();

    /// <summary>
    /// 断开设备。
    /// </summary>
    Task DisconnectAsync();

    /// <summary>
    /// 读取属性。
    /// </summary>
    Task<T> GetAsync<T>(string prop);

    /// <summary>
    /// 监听属性变化。
    /// </summary>
    Task<IDisposable> WatchPropertiesAsync(Action<PropertyChanges> handler);
}

/// <summary>
/// BlueZ GattService1 代理。
/// </summary>
[DBusInterface("org.bluez.GattService1")]
internal interface IBlueZGattService : IDBusObject
{
    /// <summary>
    /// 读取属性。
    /// </summary>
    Task<T> GetAsync<T>(string prop);
}

/// <summary>
/// BlueZ GattCharacteristic1 代理。
/// </summary>
[DBusInterface("org.bluez.GattCharacteristic1")]
internal interface IBlueZGattCharacteristic : IDBusObject
{
    /// <summary>
    /// 开始通知。
    /// </summary>
    Task StartNotifyAsync();

    /// <summary>
    /// 停止通知。
    /// </summary>
    Task StopNotifyAsync();

    /// <summary>
    /// 读取特征值。
    /// </summary>
    Task<byte[]> ReadValueAsync(IDictionary<string, object> options);

    /// <summary>
    /// 写入特征值。
    /// </summary>
    Task WriteValueAsync(byte[] value, IDictionary<string, object> options);

    /// <summary>
    /// 读取属性。
    /// </summary>
    Task<T> GetAsync<T>(string prop);

    /// <summary>
    /// 监听属性变化。
    /// </summary>
    Task<IDisposable> WatchPropertiesAsync(Action<PropertyChanges> handler);
}
