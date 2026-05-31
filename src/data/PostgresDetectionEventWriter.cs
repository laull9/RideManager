using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using RideManager.Core;
using RideManager.Utils;

namespace RideManager.Data;

/// <summary>
/// 提供 PostgreSQL 检测事件写入实现。
/// </summary>
public sealed class PostgresDetectionEventWriter : IDetectionEventWriter
{
    private readonly DatabaseOptions _options;
    private bool _migrationApplied;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    /// <summary>
    /// 创建 PostgreSQL 事件写入器。
    /// </summary>
    public PostgresDetectionEventWriter(DatabaseOptions options)
    {
        _options = options;
    }

    /// <summary>
    /// 使用 EF Core 写入一次主控决策及其当前可用的明细数据。
    /// </summary>
    public async Task WriteAsync(SafetyDecision decision, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_options.ConnectionString))
        {
            return;
        }

        await using var dbContext = RideManagerDbContext.Create(_options);
        await EnsureMigratedAsync(dbContext, cancellationToken);

        var decisionEntity = new SafetyDecisionEntity
        {
            RiskLevel = decision.RiskLevel,
            DecidedAt = decision.DecidedAt,
            PayloadJson = JsonSerializer.Serialize(decision, JsonOptions)
        };

        foreach (var finding in decision.CameraFindings)
        {
            decisionEntity.CameraFindings.Add(new CameraFindingEntity
            {
                CameraId = FormatCameraId(finding.CameraId),
                Label = finding.Label,
                Confidence = finding.Confidence,
                ObservedAt = finding.ObservedAt,
                BoxX = finding.BoundingBox?.X,
                BoxY = finding.BoundingBox?.Y,
                BoxWidth = finding.BoundingBox?.Width,
                BoxHeight = finding.BoundingBox?.Height,
                PayloadJson = JsonSerializer.Serialize(finding, JsonOptions)
            });
        }

        foreach (var snapshot in decision.SensorSnapshots)
        {
            var snapshotEntity = new SensorSnapshotEntity
            {
                SensorName = snapshot.SensorName,
                ObservedAt = snapshot.ObservedAt,
                ValuesJson = JsonSerializer.Serialize(snapshot.Values, JsonOptions)
            };

            foreach (var (metric, value) in snapshot.Values)
            {
                snapshotEntity.Readings.Add(new SensorReadingEntity
                {
                    Metric = metric,
                    Value = value
                });
            }

            decisionEntity.SensorSnapshots.Add(snapshotEntity);
        }

        dbContext.SafetyDecisions.Add(decisionEntity);
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    /// <summary>
    /// 确保数据库迁移在当前写入器实例内只执行一次。
    /// </summary>
    private async Task EnsureMigratedAsync(RideManagerDbContext dbContext, CancellationToken cancellationToken)
    {
        if (_migrationApplied)
        {
            return;
        }

        await dbContext.Database.MigrateAsync(cancellationToken);
        _migrationApplied = true;
    }

    /// <summary>
    /// 输出数据库中使用的摄像头编码。
    /// </summary>
    private static string FormatCameraId(Camera.CameraId cameraId)
    {
        return cameraId switch
        {
            Camera.CameraId.CamFace => "CAM_FACE",
            Camera.CameraId.CamBack => "CAM_BACK",
            _ => "CAM_FRONT"
        };
    }
}
