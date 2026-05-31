namespace RideManager.Camera;

/// <summary>
/// 表示摄像头采集到的一帧图像数据。
/// </summary>
public sealed record CameraFrame(CameraId CameraId, DateTimeOffset CapturedAt, ReadOnlyMemory<byte> Data);
