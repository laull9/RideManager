namespace RideManager.Camera;

/// <summary>
/// 定义摄像头采集源。
/// </summary>
public interface ICameraSource
{
    /// <summary>
    /// 读取最新一帧，实际实现应采用丢帧不缓存策略。
    /// </summary>
    Task<CameraFrame?> ReadLatestAsync(CancellationToken cancellationToken);
}
