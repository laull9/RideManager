using Tmds.DBus;

namespace RideManager.Sensors;

/// <summary>
/// BlueZ ObjectManager 代理。
/// </summary>
[DBusInterface("org.freedesktop.DBus.ObjectManager")]
public interface IBlueZObjectManager : IDBusObject
{
    /// <summary>
    /// 读取 BlueZ 当前管理对象。
    /// </summary>
    Task<IDictionary<ObjectPath, IDictionary<string, IDictionary<string, object>>>> GetManagedObjectsAsync();
}

/// <summary>
/// BlueZ GATT 管理器代理。
/// </summary>
[DBusInterface("org.bluez.GattManager1")]
public interface IBlueZGattManager : IDBusObject
{
    /// <summary>
    /// 注册本地 GATT 应用。
    /// </summary>
    Task RegisterApplicationAsync(ObjectPath application, IDictionary<string, object> options);

    /// <summary>
    /// 取消注册本地 GATT 应用。
    /// </summary>
    Task UnregisterApplicationAsync(ObjectPath application);
}

/// <summary>
/// BlueZ LE 广播管理器代理。
/// </summary>
[DBusInterface("org.bluez.LEAdvertisingManager1")]
public interface IBlueZLeAdvertisingManager : IDBusObject
{
    /// <summary>
    /// 注册本地 LE 广播。
    /// </summary>
    Task RegisterAdvertisementAsync(ObjectPath advertisement, IDictionary<string, object> options);

    /// <summary>
    /// 取消注册本地 LE 广播。
    /// </summary>
    Task UnregisterAdvertisementAsync(ObjectPath advertisement);

    /// <summary>
    /// 读取属性。
    /// </summary>
    Task<T> GetAsync<T>(string prop);
}

/// <summary>
/// BlueZ Adapter1 代理。
/// </summary>
[DBusInterface("org.bluez.Adapter1")]
public interface IBlueZAdapter : IDBusObject
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
public interface IBlueZDevice : IDBusObject
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
    /// 读取全部属性。
    /// </summary>
    Task<IDictionary<string, object>> GetAllAsync();

    /// <summary>
    /// 监听属性变化。
    /// </summary>
    Task<IDisposable> WatchPropertiesAsync(Action<PropertyChanges> handler);
}

/// <summary>
/// BlueZ GattService1 代理。
/// </summary>
[DBusInterface("org.bluez.GattService1")]
public interface IBlueZGattService : IDBusObject
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
public interface IBlueZGattCharacteristic : IDBusObject
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
    /// 读取全部属性。
    /// </summary>
    Task<IDictionary<string, object>> GetAllAsync();

    /// <summary>
    /// 监听属性变化。
    /// </summary>
    Task<IDisposable> WatchPropertiesAsync(Action<PropertyChanges> handler);
}

/// <summary>
/// 本地 BlueZ LEAdvertisement1 对象。
/// </summary>
[DBusInterface("org.bluez.LEAdvertisement1")]
public interface IBlueZLocalAdvertisement : IDBusObject
{
    /// <summary>
    /// BlueZ 释放广播对象。
    /// </summary>
    Task ReleaseAsync();

    /// <summary>
    /// 读取属性。
    /// </summary>
    Task<object> GetAsync(string prop);

    /// <summary>
    /// 读取全部属性。
    /// </summary>
    Task<IDictionary<string, object>> GetAllAsync();

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
/// 本地 BlueZ GattService1 对象。
/// </summary>
[DBusInterface("org.bluez.GattService1")]
public interface IBlueZLocalGattService : IDBusObject
{
    /// <summary>
    /// 读取属性。
    /// </summary>
    Task<object> GetAsync(string prop);

    /// <summary>
    /// 读取全部属性。
    /// </summary>
    Task<IDictionary<string, object>> GetAllAsync();

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
/// 本地 BlueZ GattCharacteristic1 对象。
/// </summary>
[DBusInterface("org.bluez.GattCharacteristic1")]
public interface IBlueZLocalGattCharacteristic : IDBusObject
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
    Task<object> GetAsync(string prop);

    /// <summary>
    /// 读取全部属性。
    /// </summary>
    Task<IDictionary<string, object>> GetAllAsync();

    /// <summary>
    /// 写入属性。
    /// </summary>
    Task SetAsync(string prop, object val);

    /// <summary>
    /// 监听属性变化。
    /// </summary>
    Task<IDisposable> WatchPropertiesAsync(Action<PropertyChanges> handler);
}
