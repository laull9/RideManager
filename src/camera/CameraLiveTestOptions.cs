namespace RideManager.Camera;

/// <summary>
/// 表示摄像头 live 测试运行选项。
/// </summary>
public sealed record CameraLiveTestOptions(
    CameraId? InitialCamera,
    TimeSpan? Duration,
    bool Headless);
