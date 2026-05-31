namespace RideManager.Camera;

/// <summary>
/// 表示归一化目标框，坐标范围为 0 到 1。
/// </summary>
public sealed record CameraBoundingBox(double X, double Y, double Width, double Height);
