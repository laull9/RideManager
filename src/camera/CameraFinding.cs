namespace RideManager.Camera;

/// <summary>
/// 表示单路摄像头算法输出的检测结果。
/// </summary>
public sealed record CameraFinding(
    CameraId CameraId,
    string Label,
    double Confidence,
    DateTimeOffset ObservedAt,
    CameraBoundingBox? BoundingBox = null,
    CameraSegmentationMask? SegmentationMask = null,
    IReadOnlyList<CameraLandmark>? Landmarks = null);

/// <summary>
/// 表示映射到模型输入 letterbox 空间的二值分割 mask。
/// </summary>
public sealed record CameraSegmentationMask(
    string Label,
    int Width,
    int Height,
    byte[] Data,
    double RegionX = 0.0,
    double RegionY = 0.0,
    double RegionWidth = 1.0,
    double RegionHeight = 1.0);

/// <summary>
/// 表示映射到预览原图空间的归一化二维关键点坐标。
/// </summary>
public sealed record CameraLandmark(double X, double Y);
