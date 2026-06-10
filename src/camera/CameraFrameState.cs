namespace RideManager.Camera;

/// <summary>
/// 表示单路摄像头一次处理周期的可持久化帧状态。
/// </summary>
public sealed record CameraFrameState(
    CameraId CameraId,
    DateTimeOffset CapturedAt,
    int? Width,
    int? Height,
    CameraPipelineMetrics Metrics);
