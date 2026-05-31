namespace RideManager.Camera;

/// <summary>
/// 表示预处理后可送入模型推理的图像帧。
/// </summary>
public sealed record ProcessedFrame(CameraId CameraId, DateTimeOffset CapturedAt, ReadOnlyMemory<byte> TensorData);
