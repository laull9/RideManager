namespace RideManager.Sensors;

/// <summary>
/// 定义外部传感器读取接口。
/// </summary>
public interface ISensorReader
{
    /// <summary>
    /// 读取当前传感器状态。
    /// </summary>
    Task<SensorSnapshot?> ReadAsync(CancellationToken cancellationToken);
}
