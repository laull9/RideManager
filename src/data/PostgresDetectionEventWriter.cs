using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
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
            PayloadJson = SerializePayload(decision)
        };

        var frameEntitiesByCamera = new Dictionary<string, CameraFrameEntity>(StringComparer.OrdinalIgnoreCase);
        foreach (var frame in decision.CameraFrames)
        {
            var cameraId = FormatCameraId(frame.CameraId);
            var frameEntity = new CameraFrameEntity
            {
                CameraId = cameraId,
                CapturedAt = frame.CapturedAt,
                Width = frame.Width,
                Height = frame.Height,
                CaptureLatencyMs = frame.Metrics.CaptureLatencyMs,
                PreprocessLatencyMs = frame.Metrics.PreprocessLatencyMs,
                InferenceLatencyMs = frame.Metrics.InferenceLatencyMs,
                TotalLatencyMs = frame.Metrics.TotalLatencyMs,
                Fps = frame.Metrics.Fps,
                DroppedFrames = frame.Metrics.DroppedFrames,
                MetadataJson = SerializePayload(frame)
            };
            decisionEntity.CameraFrames.Add(frameEntity);
            frameEntitiesByCamera[cameraId] = frameEntity;
        }

        foreach (var finding in decision.CameraFindings)
        {
            var cameraId = FormatCameraId(finding.CameraId);
            decisionEntity.CameraFindings.Add(new CameraFindingEntity
            {
                CameraFrame = frameEntitiesByCamera.GetValueOrDefault(cameraId),
                CameraId = cameraId,
                Label = finding.Label,
                Confidence = finding.Confidence,
                ObservedAt = finding.ObservedAt,
                BoxX = finding.BoundingBox?.X,
                BoxY = finding.BoundingBox?.Y,
                BoxWidth = finding.BoundingBox?.Width,
                BoxHeight = finding.BoundingBox?.Height,
                PayloadJson = SerializePayload(finding)
            });
        }

        foreach (var snapshot in decision.SensorSnapshots)
        {
            var snapshotEntity = new SensorSnapshotEntity
            {
                SensorName = snapshot.SensorName,
                ObservedAt = snapshot.ObservedAt,
                ValuesJson = SerializePayload(snapshot.Values)
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
    /// 使用 source-generated JSON 上下文序列化 payload，兼容 trimmed/self-contained 运行。
    /// </summary>
    private static string SerializePayload<T>(T value)
    {
        var typeInfo = RideManagerJsonContext.Default.GetTypeInfo(typeof(T));
        return typeInfo is JsonTypeInfo<T> typed
            ? JsonSerializer.Serialize(value, typed)
            : throw new InvalidOperationException($"No JSON source generation metadata registered for {typeof(T)}.");
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
