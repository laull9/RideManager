using OpenCvSharp;
using RideManager.Models;
using RideManager.Utils;

namespace RideManager.Camera;

/// <summary>
/// 为面部两阶段链路保留整帧图像，实际模型预处理在分析器中按人脸 ROI 完成。
/// </summary>
public sealed class FacePipelineFramePreprocessor : IFramePreprocessor
{
    private readonly CameraId _cameraId;

    /// <summary>
    /// 创建面部链路整帧预处理器。
    /// </summary>
    public FacePipelineFramePreprocessor(CameraOptions options)
    {
        _cameraId = options.Id;
    }

    /// <summary>
    /// 返回整帧预览图和空张量占位，供后续 YuNet 检测与 PFLD 裁剪使用。
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
            frame.Image.Clone()));
    }
}
