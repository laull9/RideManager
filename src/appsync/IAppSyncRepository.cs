using System.Text.Json;

namespace RideManager.AppSync;

/// <summary>
/// 定义手机 App 同步所需的数据访问接口。
/// </summary>
public interface IAppSyncRepository
{
    /// <summary>
    /// 读取最近一段时间内的安全决策页。
    /// </summary>
    Task<AppSyncPage> GetRecentDecisionsAsync(
        DateTimeOffset since,
        int limit,
        AppSyncCursor? cursor,
        CancellationToken cancellationToken);

    /// <summary>
    /// 读取指定游标之前的更早安全决策页。
    /// </summary>
    Task<AppSyncPage> GetMoreDecisionsAsync(
        AppSyncCursor cursor,
        int limit,
        CancellationToken cancellationToken);

    /// <summary>
    /// 记录 App 请求的设置变更。
    /// </summary>
    Task<AppSyncSettingsUpdateResult> RecordSettingsUpdateAsync(
        JsonElement patch,
        string? clientId,
        CancellationToken cancellationToken);
}
