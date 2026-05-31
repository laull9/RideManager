using System.Text.Json;
using RideManager.Core;
using RideManager.Utils;

namespace RideManager.Data;

/// <summary>
/// 提供 PostgreSQL 检测事件写入占位实现。
/// </summary>
public sealed class PostgresDetectionEventWriter : IDetectionEventWriter
{
    private readonly DatabaseOptions _options;

    /// <summary>
    /// 创建 PostgreSQL 事件写入器。
    /// </summary>
    public PostgresDetectionEventWriter(DatabaseOptions options)
    {
        _options = options;
    }

    /// <summary>
    /// 当前仅序列化事件，后续接入 EF Core DbContext 写库。
    /// </summary>
    public Task WriteAsync(SafetyDecision decision, CancellationToken cancellationToken)
    {
        var detectionEvent = new DetectionEvent(
            Guid.NewGuid(),
            DateTimeOffset.UtcNow,
            decision.RiskLevel,
            JsonSerializer.Serialize(decision));

        _ = detectionEvent;
        _ = _options;
        return Task.CompletedTask;
    }
}
