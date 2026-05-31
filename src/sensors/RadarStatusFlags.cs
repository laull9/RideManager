namespace RideManager.Sensors;

/// <summary>
/// 雷达数据帧状态位。
/// </summary>
public static class RadarStatusFlags
{
    /// <summary>
    /// 呼吸率有效。
    /// </summary>
    public const byte Breath = 0x01;

    /// <summary>
    /// 心率有效。
    /// </summary>
    public const byte Heart = 0x02;

    /// <summary>
    /// 距离有效。
    /// </summary>
    public const byte Distance = 0x04;

    /// <summary>
    /// 检测到人体。
    /// </summary>
    public const byte Presence = 0x08;
}
