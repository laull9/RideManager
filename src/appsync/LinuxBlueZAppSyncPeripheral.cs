using RideManager.Sensors;
using RideManager.Utils;
using Tmds.DBus;

namespace RideManager.AppSync;

/// <summary>
/// 使用 Linux BlueZ 暴露 App 同步蓝牙外设服务。
/// </summary>
public sealed class LinuxBlueZAppSyncPeripheral : IAppSyncPeripheral
{
    private readonly AppSyncOptions _options;

    /// <summary>
    /// 创建 BlueZ App 同步外设宿主。
    /// </summary>
    public LinuxBlueZAppSyncPeripheral(AppSyncOptions options)
    {
        _options = options;
    }

    /// <summary>
    /// 启动 BlueZ 外设准备流程。
    /// </summary>
    public async Task StartAsync(Func<string, CancellationToken, Task<string>> requestHandler, CancellationToken cancellationToken)
    {
        try
        {
            var adapter = await SelectAdapterAsync(cancellationToken).ConfigureAwait(false);
            await adapter.SetAsync("Powered", true).WaitAsync(cancellationToken).ConfigureAwait(false);
            await adapter.SetAsync("Discoverable", true).WaitAsync(cancellationToken).ConfigureAwait(false);
            await adapter.SetAsync("Pairable", true).WaitAsync(cancellationToken).ConfigureAwait(false);
            Console.WriteLine(
                $"App sync bluetooth prepared on BlueZ as {_options.DeviceName}; service={_options.ServiceUuid}, rx={_options.RxUuid}, tx={_options.TxUuid}.");
            Console.WriteLine("BlueZ GATT peripheral registration is reserved for the platform host; protocol handler is ready.");
        }
        catch (Exception ex) when (ex is DBusException or InvalidOperationException or TimeoutException)
        {
            Console.WriteLine($"App sync bluetooth BlueZ startup failed: {ex.Message}");
        }
    }

    /// <summary>
    /// 释放 BlueZ 外设资源。
    /// </summary>
    public ValueTask DisposeAsync()
    {
        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// 选择第一个可用 BlueZ 适配器。
    /// </summary>
    private static async Task<IBlueZAdapter> SelectAdapterAsync(CancellationToken cancellationToken)
    {
        var objectManager = Connection.System.CreateProxy<IBlueZObjectManager>("org.bluez", "/");
        var managedObjects = await objectManager.GetManagedObjectsAsync().WaitAsync(cancellationToken).ConfigureAwait(false);
        foreach (var (path, interfaces) in managedObjects)
        {
            if (interfaces.ContainsKey("org.bluez.Adapter1"))
            {
                return Connection.System.CreateProxy<IBlueZAdapter>("org.bluez", path);
            }
        }

        throw new InvalidOperationException("No BlueZ bluetooth adapter found.");
    }
}
