using System.Runtime.InteropServices;
using RideManager.Utils;

namespace RideManager.AppSync;

/// <summary>
/// 管理手机 App 蓝牙同步服务生命周期。
/// </summary>
public sealed class AppSyncServer : IAsyncDisposable
{
    private readonly AppSyncOptions _options;
    private readonly AppSyncProtocolHandler _handler;
    private readonly IAppSyncPeripheral _peripheral;

    /// <summary>
    /// 创建手机 App 同步服务。
    /// </summary>
    public AppSyncServer(
        AppSyncOptions options,
        DatabaseOptions databaseOptions,
        IAppSyncPeripheral? peripheral = null)
    {
        _options = options;
        _handler = new AppSyncProtocolHandler(options, new PostgresAppSyncRepository(databaseOptions));
        _peripheral = peripheral ?? CreatePlatformPeripheral(options);
    }

    /// <summary>
    /// 启动同步服务。
    /// </summary>
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (!_options.Enabled)
        {
            Console.WriteLine("App sync bluetooth is disabled.");
            return;
        }

        await _peripheral.StartAsync(HandleRequestAsync, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 释放底层蓝牙外设宿主。
    /// </summary>
    public ValueTask DisposeAsync()
    {
        return _peripheral.DisposeAsync();
    }

    /// <summary>
    /// 处理外设收到的一条协议帧。
    /// </summary>
    private Task<string> HandleRequestAsync(string frame, CancellationToken cancellationToken)
    {
        if (frame.Length > _options.MaxRequestBytes)
        {
            return Task.FromResult("{\"v\":1,\"id\":\"\",\"type\":\"error\",\"status\":\"too_large\",\"payload\":{\"message\":\"request too large\"}}");
        }

        return _handler.HandleAsync(frame, cancellationToken);
    }

    /// <summary>
    /// 根据当前平台创建蓝牙外设宿主。
    /// </summary>
    private static IAppSyncPeripheral CreatePlatformPeripheral(AppSyncOptions options)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            return new LinuxBlueZAppSyncPeripheral(options);
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            return new MacOSCoreBluetoothAppSyncPeripheral(options);
        }

        return new UnsupportedAppSyncPeripheral("unsupported operating system");
    }
}
