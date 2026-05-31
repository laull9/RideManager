namespace RideManager.Sensors;

/// <summary>
/// 表示传感器上报的一次状态快照。
/// </summary>
public sealed record SensorSnapshot(string SensorName, DateTimeOffset ObservedAt, IReadOnlyDictionary<string, double> Values);
