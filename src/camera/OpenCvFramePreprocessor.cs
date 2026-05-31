namespace RideManager.Camera;

/// <summary>
/// 提供 OpenCV 图像预处理的占位封装。
/// </summary>
public sealed class OpenCvFramePreprocessor : IFramePreprocessor
{
    private readonly CameraId _cameraId;

    /// <summary>
    /// 创建指定摄像头的预处理器。
    /// </summary>
    public OpenCvFramePreprocessor(CameraId cameraId)
    {
        _cameraId = cameraId;
    }

    /// <summary>
    /// 当前直接透传图像数据，后续接入 resize、normalize 和色彩空间转换。
    /// </summary>
    public ValueTask<ProcessedFrame> ProcessAsync(CameraFrame frame, CancellationToken cancellationToken)
    {
        return ValueTask.FromResult(new ProcessedFrame(_cameraId, frame.CapturedAt, frame.Data));
    }
}
