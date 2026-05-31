namespace RideManager.Sensors;

/// <summary>
/// 配置雷达 live 测试运行参数。
/// </summary>
public sealed record RadarLiveTestOptions(
    TimeSpan? Duration,
    bool Headless,
    bool Simulated,
    int Port);
