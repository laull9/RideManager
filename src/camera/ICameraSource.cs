namespace RideManager.Camera;

/// <summary>
/// 定义摄像头采集源。
/// </summary>
public interface ICameraSource : IAsyncDisposable
{
    /// <summary>
    /// 读取最新一帧，实际实现应采用丢帧不缓存策略。
    /// </summary>
    Task<CameraFrame?> ReadLatestAsync(CancellationToken cancellationToken);

    /// <summary>
    /// 获取因下游处理较慢而被丢弃的帧数。
    /// </summary>
    long DroppedFrames { get; }
}
