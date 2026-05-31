namespace RideManager.Sensors;

/// <summary>
/// 把雷达帧转换为主控统一传感器指标。
/// </summary>
public static class RadarSnapshotValues
{
    /// <summary>
    /// 创建指标字典。
    /// </summary>
    public static IReadOnlyDictionary<string, double> Create(RadarFrame frame)
    {
        var values = new Dictionary<string, double>
        {
            ["sequence"] = frame.Sequence,
            ["device_timestamp_ms"] = frame.DeviceTimestampMs,
            ["status"] = frame.Status,
            ["presence"] = frame.HasPresence ? 1 : 0,
            ["breathing_rate_valid"] = frame.HasBreathingRate ? 1 : 0,
            ["heart_rate_valid"] = frame.HasHeartRate ? 1 : 0,
            ["distance_valid"] = frame.HasDistance ? 1 : 0,
            ["stale_ms"] = Math.Max(0, (DateTimeOffset.UtcNow - frame.ObservedAt).TotalMilliseconds)
        };

        if (frame.HasBreathingRate && frame.BreathingRateBpm is { } breathingRate)
        {
            values["breathing_rate"] = breathingRate;
        }

        if (frame.HasHeartRate && frame.HeartRateBpm is { } heartRate)
        {
            values["heart_rate"] = heartRate;
        }

        if (frame.HasDistance && frame.DistanceCm is { } distance)
        {
            values["distance_cm"] = distance;
        }

        return values;
    }
}
