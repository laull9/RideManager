namespace RideManager.Sensors;

/// <summary>
/// 描述雷达连接的当前状态。
/// </summary>
public sealed record RadarConnectionState(
    string Phase,
    string? DeviceName,
    string? DeviceAddress,
    string? Message,
    DateTimeOffset UpdatedAt)
{
    /// <summary>
    /// 创建初始空闲状态。
    /// </summary>
    public static RadarConnectionState Idle()
    {
        return new RadarConnectionState("idle", null, null, null, DateTimeOffset.UtcNow);
    }
}
