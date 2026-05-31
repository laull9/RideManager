using RideManager.Core;

namespace RideManager.Data;

/// <summary>
/// 表示可写入数据库的一次检测事件。
/// </summary>
public sealed record DetectionEvent(Guid Id, DateTimeOffset CreatedAt, SafetyRiskLevel RiskLevel, string Payload);
