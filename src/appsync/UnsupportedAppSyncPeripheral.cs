namespace RideManager.AppSync;

/// <summary>
/// 表示当前平台暂不支持蓝牙外设模式。
/// </summary>
public sealed class UnsupportedAppSyncPeripheral : IAppSyncPeripheral
{
    private readonly string _reason;

    /// <summary>
    /// 创建不支持平台的外设宿主。
    /// </summary>
    public UnsupportedAppSyncPeripheral(string reason)
    {
        _reason = reason;
    }

    /// <summary>
    /// 输出提示并保持主程序继续运行。
    /// </summary>
    public Task StartAsync(Func<string, CancellationToken, Task<string>> requestHandler, CancellationToken cancellationToken)
    {
        Console.WriteLine($"App sync bluetooth peripheral unavailable: {_reason}.");
        return Task.CompletedTask;
    }

    /// <summary>
    /// 释放资源。
    /// </summary>
    public ValueTask DisposeAsync()
    {
        return ValueTask.CompletedTask;
    }
}
