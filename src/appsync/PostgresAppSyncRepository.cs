using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using RideManager.Data;
using RideManager.Utils;

namespace RideManager.AppSync;

/// <summary>
/// 使用 PostgreSQL 为手机 App 同步提供数据。
/// </summary>
public sealed class PostgresAppSyncRepository : IAppSyncRepository
{
    private readonly DatabaseOptions _options;

    /// <summary>
    /// 创建 PostgreSQL App 同步仓储。
    /// </summary>
    public PostgresAppSyncRepository(DatabaseOptions options)
    {
        _options = options;
    }

    /// <summary>
    /// 读取最近一段时间内的安全决策页。
    /// </summary>
    public async Task<AppSyncPage> GetRecentDecisionsAsync(
        DateTimeOffset since,
        int limit,
        AppSyncCursor? cursor,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_options.ConnectionString))
        {
            return new AppSyncPage(Array.Empty<AppSyncDecisionRecord>(), null, false);
        }

        await using var dbContext = RideManagerDbContext.Create(_options);
        IQueryable<SafetyDecisionEntity> query = dbContext.SafetyDecisions
            .AsNoTracking()
            .Include(value => value.CameraFindings)
            .Include(value => value.SensorSnapshots)
            .Where(value => value.DecidedAt >= since);

        if (cursor is not null)
        {
            query = query.Where(value => value.DecidedAt < cursor.DecidedAt);
        }

        return await ReadPageAsync(query, limit, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 读取指定游标之前的更早安全决策页。
    /// </summary>
    public async Task<AppSyncPage> GetMoreDecisionsAsync(
        AppSyncCursor cursor,
        int limit,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_options.ConnectionString))
        {
            return new AppSyncPage(Array.Empty<AppSyncDecisionRecord>(), null, false);
        }

        await using var dbContext = RideManagerDbContext.Create(_options);
        var query = dbContext.SafetyDecisions
            .AsNoTracking()
            .Include(value => value.CameraFindings)
            .Include(value => value.SensorSnapshots)
            .Where(value => value.DecidedAt < cursor.DecidedAt);

        return await ReadPageAsync(query, limit, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 记录 App 请求的设置变更。
    /// </summary>
    public async Task<AppSyncSettingsUpdateResult> RecordSettingsUpdateAsync(
        JsonElement patch,
        string? clientId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_options.ConnectionString))
        {
            throw new InvalidOperationException("database connection is required for settings update.");
        }

        await using var dbContext = RideManagerDbContext.Create(_options);
        var now = DateTimeOffset.UtcNow;
        var payload = AppSyncJson.ToElement(new AppSyncSettingsUpdateEvent(clientId, patch));
        var entity = new SystemEventEntity
        {
            OccurredAt = now,
            Source = "app_sync",
            Level = "info",
            Message = "settings_update_requested",
            PayloadJson = payload.GetRawText()
        };

        dbContext.SystemEvents.Add(entity);
        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return new AppSyncSettingsUpdateResult(
            entity.Id,
            now,
            true,
            "settings update accepted and stored as a pending system event");
    }

    /// <summary>
    /// 读取一页安全决策，并生成下一页游标。
    /// </summary>
    private static async Task<AppSyncPage> ReadPageAsync(
        IQueryable<SafetyDecisionEntity> query,
        int limit,
        CancellationToken cancellationToken)
    {
        var entities = await query
            .OrderByDescending(value => value.DecidedAt)
            .Take(limit + 1)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        var hasMore = entities.Count > limit;
        var pageEntities = entities.Take(limit).ToArray();
        var items = pageEntities.Select(MapDecision).ToArray();
        var last = pageEntities.LastOrDefault();
        var nextCursor = hasMore && last is not null
            ? new AppSyncCursor(last.DecidedAt, last.Id).Encode()
            : null;

        return new AppSyncPage(items, nextCursor, hasMore);
    }

    /// <summary>
    /// 映射数据库安全决策到 App 协议记录。
    /// </summary>
    private static AppSyncDecisionRecord MapDecision(SafetyDecisionEntity entity)
    {
        return new AppSyncDecisionRecord(
            entity.Id,
            entity.DecidedAt,
            entity.RiskLevel.ToString(),
            ParseJson(entity.PayloadJson),
            entity.CameraFindings
                .OrderBy(value => value.ObservedAt)
                .Select(MapFinding)
                .ToArray(),
            entity.SensorSnapshots
                .OrderBy(value => value.ObservedAt)
                .Select(MapSensorSnapshot)
                .ToArray());
    }

    /// <summary>
    /// 映射摄像头检测结果。
    /// </summary>
    private static AppSyncCameraFindingRecord MapFinding(CameraFindingEntity entity)
    {
        return new AppSyncCameraFindingRecord(
            entity.Id,
            entity.CameraId,
            entity.Label,
            entity.Confidence,
            entity.ObservedAt,
            entity.BoxX,
            entity.BoxY,
            entity.BoxWidth,
            entity.BoxHeight,
            ParseJson(entity.PayloadJson));
    }

    /// <summary>
    /// 映射传感器快照。
    /// </summary>
    private static AppSyncSensorSnapshotRecord MapSensorSnapshot(SensorSnapshotEntity entity)
    {
        return new AppSyncSensorSnapshotRecord(
            entity.Id,
            entity.SensorName,
            entity.ObservedAt,
            ParseJson(entity.ValuesJson));
    }

    /// <summary>
    /// 解析数据库 JSON 字段，空值或非法值返回空对象。
    /// </summary>
    private static JsonElement ParseJson(string? json)
    {
        try
        {
            using var document = JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json);
            return document.RootElement.Clone();
        }
        catch (JsonException)
        {
            using var document = JsonDocument.Parse("{}");
            return document.RootElement.Clone();
        }
    }
}
