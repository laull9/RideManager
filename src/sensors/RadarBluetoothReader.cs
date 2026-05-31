using RideManager.Utils;

namespace RideManager.Sensors;

/// <summary>
/// 提供雷达蓝牙通信占位实现。
/// </summary>
public sealed class RadarBluetoothReader : ISensorReader
{
    private readonly SensorEndpointOptions _options;

    /// <summary>
    /// 创建雷达读取器。
    /// </summary>
    public RadarBluetoothReader(SensorEndpointOptions options)
    {
        _options = options;
    }

    /// <summary>
    /// 读取雷达心率与呼吸数据，后续替换为蓝牙协议实现。
    /// </summary>
    public Task<SensorSnapshot?> ReadAsync(CancellationToken cancellationToken)
    {
        if (!_options.Enabled)
        {
            return Task.FromResult<SensorSnapshot?>(null);
        }

        return Task.FromResult<SensorSnapshot?>(
            new SensorSnapshot(
                "RADAR",
                DateTimeOffset.UtcNow,
                new Dictionary<string, double>
                {
                    ["heart_rate"] = 0,
                    ["breathing_rate"] = 0
                }));
    }
}
