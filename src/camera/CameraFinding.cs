namespace RideManager.Camera;

/// <summary>
/// 表示单路摄像头算法输出的检测结果。
/// </summary>
public sealed record CameraFinding(CameraId CameraId, string Label, double Confidence, DateTimeOffset ObservedAt);
