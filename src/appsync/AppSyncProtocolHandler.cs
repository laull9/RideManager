using System.Text.Json;
using RideManager.Utils;

namespace RideManager.AppSync;

/// <summary>
/// 处理手机 App 同步协议请求。
/// </summary>
public sealed class AppSyncProtocolHandler
{
    private const int ProtocolVersion = 1;
    private readonly AppSyncOptions _options;
    private readonly IAppSyncRepository _repository;

    /// <summary>
    /// 创建协议处理器。
    /// </summary>
    public AppSyncProtocolHandler(AppSyncOptions options, IAppSyncRepository repository)
    {
        _options = options;
        _repository = repository;
    }

    /// <summary>
    /// 处理一条 JSON 协议帧。
    /// </summary>
    public async Task<string> HandleAsync(string frame, CancellationToken cancellationToken)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(frame))
            {
                return SerializeError(string.Empty, "bad_request", "empty request");
            }

            var request = JsonSerializer.Deserialize(frame, RideManagerJsonContext.Default.AppSyncRequest);
            if (request is null || request.V != ProtocolVersion || string.IsNullOrWhiteSpace(request.Type))
            {
                return SerializeError(request?.Id ?? string.Empty, "bad_request", "unsupported request");
            }

            return request.Type.Trim().ToLowerInvariant() switch
            {
                "hello" => SerializeOk(request, CreateHello()),
                "sync_recent" => SerializeOk(request, await SyncRecentAsync(request.Payload, cancellationToken).ConfigureAwait(false)),
                "load_more" => SerializeOk(request, await LoadMoreAsync(request.Payload, cancellationToken).ConfigureAwait(false)),
                "update_settings" => SerializeOk(request, await UpdateSettingsAsync(request.Payload, cancellationToken).ConfigureAwait(false)),
                "ping" => SerializeOk(request, new AppSyncPing(DateTimeOffset.UtcNow)),
                _ => SerializeError(request.Id, "unknown_type", $"unknown request type: {request.Type}")
            };
        }
        catch (JsonException ex)
        {
            return SerializeError(string.Empty, "bad_json", ex.Message);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            return SerializeError(string.Empty, "failed", ex.Message);
        }
    }

    /// <summary>
    /// 创建握手响应。
    /// </summary>
    private AppSyncHello CreateHello()
    {
        return new AppSyncHello(
            _options.DeviceName,
            "RideManager.AppSync",
            ProtocolVersion,
            _options.DefaultSyncWindowHours,
            _options.MaxPageSize,
            new[] { "sync_recent", "load_more", "update_settings", "ping" });
    }

    /// <summary>
    /// 读取默认最近数据页。
    /// </summary>
    private Task<AppSyncPage> SyncRecentAsync(JsonElement payload, CancellationToken cancellationToken)
    {
        var hours = Math.Clamp(
            AppSyncJson.GetDouble(payload, "hours", _options.DefaultSyncWindowHours),
            1.0,
            168.0);
        var limit = ClampLimit(AppSyncJson.GetInt(payload, "limit", _options.MaxPageSize));
        var since = DateTimeOffset.UtcNow.Subtract(TimeSpan.FromHours(hours));
        var cursor = AppSyncCursor.TryDecode(AppSyncJson.GetString(payload, "cursor"), out var decoded)
            ? decoded
            : null;

        return _repository.GetRecentDecisionsAsync(since, limit, cursor, cancellationToken);
    }

    /// <summary>
    /// 读取更早数据页。
    /// </summary>
    private Task<AppSyncPage> LoadMoreAsync(JsonElement payload, CancellationToken cancellationToken)
    {
        if (!AppSyncCursor.TryDecode(AppSyncJson.GetString(payload, "cursor"), out var cursor))
        {
            throw new ArgumentException("load_more requires a valid cursor.");
        }

        return _repository.GetMoreDecisionsAsync(
            cursor,
            ClampLimit(AppSyncJson.GetInt(payload, "limit", _options.MaxPageSize)),
            cancellationToken);
    }

    /// <summary>
    /// 记录设置变更请求。
    /// </summary>
    private Task<AppSyncSettingsUpdateResult> UpdateSettingsAsync(JsonElement payload, CancellationToken cancellationToken)
    {
        if (payload.ValueKind != JsonValueKind.Object || !payload.TryGetProperty("patch", out var patch))
        {
            throw new ArgumentException("update_settings requires payload.patch.");
        }

        return _repository.RecordSettingsUpdateAsync(
            patch.Clone(),
            AppSyncJson.GetString(payload, "client_id"),
            cancellationToken);
    }

    /// <summary>
    /// 约束分页大小。
    /// </summary>
    private int ClampLimit(int limit)
    {
        return Math.Clamp(limit <= 0 ? _options.MaxPageSize : limit, 1, _options.MaxPageSize);
    }

    /// <summary>
    /// 序列化成功响应。
    /// </summary>
    private static string SerializeOk<T>(AppSyncRequest request, T payload)
    {
        return JsonSerializer.Serialize(
            new AppSyncResponse(ProtocolVersion, request.Id, request.Type, "ok", AppSyncJson.ToElement(payload)),
            RideManagerJsonContext.Default.AppSyncResponse);
    }

    /// <summary>
    /// 序列化错误响应。
    /// </summary>
    private static string SerializeError(string id, string code, string message)
    {
        return JsonSerializer.Serialize(
            new AppSyncResponse(ProtocolVersion, id, "error", code, AppSyncJson.ToElement(new AppSyncError(message))),
            RideManagerJsonContext.Default.AppSyncResponse);
    }
}
