namespace RideManager.Camera;

/// <summary>
/// 定义摄像头图像预处理器。
/// </summary>
public interface IFramePreprocessor
{
    /// <summary>
    /// 将原始图像帧转换为模型输入张量。
    /// </summary>
    ValueTask<ProcessedFrame> ProcessAsync(CameraFrame frame, CancellationToken cancellationToken);
}
