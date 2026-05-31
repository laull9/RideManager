namespace RideManager.Sensors;

/// <summary>
/// 定义雷达实时数据源。
/// </summary>
public interface IRadarClient : IAsyncDisposable
{
    /// <summary>
    /// 雷达数据帧到达事件。
    /// </summary>
    event EventHandler<RadarFrame>? FrameReceived;

    /// <summary>
    /// 雷达固件健康状态到达事件。
    /// </summary>
    event EventHandler<RadarHealth>? HealthReceived;

    /// <summary>
    /// 连接状态变化事件。
    /// </summary>
    event EventHandler<RadarConnectionState>? StateChanged;

    /// <summary>
    /// 获取最新数据帧。
    /// </summary>
    RadarFrame? LatestFrame { get; }

    /// <summary>
    /// 获取最新固件健康状态。
    /// </summary>
    RadarHealth? LatestHealth { get; }

    /// <summary>
    /// 获取当前连接状态。
    /// </summary>
    RadarConnectionState State { get; }

    /// <summary>
    /// 启动数据源。
    /// </summary>
    Task StartAsync(CancellationToken cancellationToken);

    /// <summary>
    /// 等待下一帧雷达数据。
    /// </summary>
    Task<RadarFrame?> WaitForFrameAsync(TimeSpan timeout, CancellationToken cancellationToken);
}
