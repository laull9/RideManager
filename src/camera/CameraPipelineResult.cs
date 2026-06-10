using OpenCvSharp;

namespace RideManager.Camera;

/// <summary>
/// 表示单路摄像头一次完整处理的结果。
/// </summary>
public sealed class CameraPipelineResult : IDisposable
{
    /// <summary>
    /// 创建摄像头管线结果。
    /// </summary>
    public CameraPipelineResult(
        CameraId cameraId,
        DateTimeOffset capturedAt,
        int? width,
        int? height,
        IReadOnlyList<CameraFinding> findings,
        CameraPipelineMetrics metrics,
        Mat previewImage)
    {
        CameraId = cameraId;
        CapturedAt = capturedAt;
        Width = width;
        Height = height;
        Findings = findings;
        Metrics = metrics;
        PreviewImage = previewImage;
    }

    /// <summary>
    /// 获取当前结果所属摄像头。
    /// </summary>
    public CameraId CameraId { get; }

    /// <summary>
    /// 获取采集时间。
    /// </summary>
    public DateTimeOffset CapturedAt { get; }

    /// <summary>
    /// 获取原始帧宽度。
    /// </summary>
    public int? Width { get; }

    /// <summary>
    /// 获取原始帧高度。
    /// </summary>
    public int? Height { get; }

    /// <summary>
    /// 获取检测结果。
    /// </summary>
    public IReadOnlyList<CameraFinding> Findings { get; }

    /// <summary>
    /// 获取单帧性能指标。
    /// </summary>
    public CameraPipelineMetrics Metrics { get; }

    /// <summary>
    /// 获取 live 显示用图像，所有权归当前结果。
    /// </summary>
    public Mat PreviewImage { get; }

    /// <summary>
    /// 转换为不包含图像数据的可持久化帧状态。
    /// </summary>
    public CameraFrameState ToFrameState()
    {
        return new CameraFrameState(CameraId, CapturedAt, Width, Height, Metrics);
    }

    /// <summary>
    /// 释放 live 显示图像。
    /// </summary>
    public void Dispose()
    {
        PreviewImage.Dispose();
    }
}
