using RideManager.Utils;

namespace RideManager.Camera;

/// <summary>
/// 提供 OpenCV 图像预处理封装。
/// </summary>
public sealed class OpenCvFramePreprocessor : IFramePreprocessor
{
    private readonly CameraId _cameraId;
    private readonly int _targetWidth;
    private readonly int _targetHeight;

    /// <summary>
    /// 创建指定摄像头的预处理器。
    /// </summary>
    public OpenCvFramePreprocessor(CameraOptions options)
    {
        _cameraId = options.Id;
        _targetWidth = Math.Max(1, options.InputWidth);
        _targetHeight = Math.Max(1, options.InputHeight);
    }

    /// <summary>
    /// 将 BGR 图像 letterbox、转换为 RGB，并归一化为 NCHW float32 张量。
    /// </summary>
    public ValueTask<ProcessedFrame> ProcessAsync(CameraFrame frame, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var tensor = ModelInputTensorFactory.CreateRgbNchwTensor(frame.Image, _targetWidth, _targetHeight);
        return ValueTask.FromResult(new ProcessedFrame(
            _cameraId,
            frame.CapturedAt,
            tensor,
            new[] { 1, 3, _targetHeight, _targetWidth },
            frame.Width,
            frame.Height,
            frame.Image.Clone()));
    }
}
