namespace RideManager.Sensors;

/// <summary>
/// 表示一帧 BLE 雷达生命体征数据。
/// </summary>
public sealed record RadarFrame(
    int Version,
    uint Sequence,
    uint DeviceTimestampMs,
    double? BreathingRateBpm,
    double? HeartRateBpm,
    double? DistanceCm,
    byte Status,
    DateTimeOffset ObservedAt,
    bool HasBreathingRate,
    bool HasHeartRate,
    bool HasDistance,
    bool HasPresence);
