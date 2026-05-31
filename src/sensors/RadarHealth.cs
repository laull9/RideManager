namespace RideManager.Sensors;

/// <summary>
/// 表示雷达固件健康状态。
/// </summary>
public sealed record RadarHealth(
    int Version,
    uint UptimeMs,
    uint NotifyCount,
    uint DroppedNotifyCount,
    uint RadarStaleMs,
    bool ClientConnected,
    string? FirmwareVersion,
    DateTimeOffset ObservedAt);
