namespace RideManager.AppSync;

/// <summary>
/// 定义手机 App 同步蓝牙外设宿主。
/// </summary>
public interface IAppSyncPeripheral : IAsyncDisposable
{
    /// <summary>
    /// 启动蓝牙外设宿主。
    /// </summary>
    Task StartAsync(Func<string, CancellationToken, Task<string>> requestHandler, CancellationToken cancellationToken);
}
