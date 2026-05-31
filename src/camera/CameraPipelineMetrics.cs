namespace RideManager.Camera;

/// <summary>
/// 表示单帧摄像头链路的性能指标。
/// </summary>
public sealed record CameraPipelineMetrics(
    double CaptureLatencyMs,
    double PreprocessLatencyMs,
    double InferenceLatencyMs,
    double TotalLatencyMs,
    double Fps,
    long DroppedFrames);
