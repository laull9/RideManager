using RideManager.Models;
using RideManager.Utils;

namespace RideManager.Camera;

/// <summary>
/// 保留整帧图像，供后续分析器自行按模型需求完成预处理。
/// </summary>
public sealed class FullFramePreprocessor : IFramePreprocessor
{
    private readonly CameraId _cameraId;

    /// <summary>
    /// 创建整帧透传预处理器。
    /// </summary>
    public FullFramePreprocessor(CameraOptions options)
    {
        _cameraId = options.Id;
    }

    /// <summary>
    /// 返回整帧预览图和空张量占位。
    /// </summary>
    public ValueTask<ProcessedFrame> ProcessAsync(CameraFrame frame, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        return ValueTask.FromResult(new ProcessedFrame(
            _cameraId,
            frame.CapturedAt,
            new NativeFloatTensor(0),
            Array.Empty<int>(),
            frame.Width,
            frame.Height,
            frame.Image,
            ownsPreviewImage: false));
    }
}
