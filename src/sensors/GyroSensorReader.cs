using RideManager.Utils;

namespace RideManager.Sensors;

/// <summary>
/// 提供陀螺仪通讯占位实现。
/// </summary>
public sealed class GyroSensorReader : ISensorReader
{
    private readonly SensorEndpointOptions _options;

    /// <summary>
    /// 创建陀螺仪读取器。
    /// </summary>
    public GyroSensorReader(SensorEndpointOptions options)
    {
        _options = options;
    }

    /// <summary>
    /// 读取姿态数据，后续根据下位机协议实现。
    /// </summary>
    public Task<SensorSnapshot?> ReadAsync(CancellationToken cancellationToken)
    {
        if (!_options.Enabled)
        {
            return Task.FromResult<SensorSnapshot?>(null);
        }

        return Task.FromResult<SensorSnapshot?>(
            new SensorSnapshot(
                "GYRO",
                DateTimeOffset.UtcNow,
                new Dictionary<string, double>
                {
                    ["roll"] = 0,
                    ["pitch"] = 0,
                    ["yaw"] = 0
                }));
    }
}
