namespace RideManager.Camera;

/// <summary>
/// 定义摄像头算法分析器。
/// </summary>
public interface ICameraAnalyzer
{
    /// <summary>
    /// 对预处理后的图像帧执行算法分析。
    /// </summary>
    Task<IReadOnlyList<CameraFinding>> AnalyzeAsync(ProcessedFrame frame, CancellationToken cancellationToken);
}
