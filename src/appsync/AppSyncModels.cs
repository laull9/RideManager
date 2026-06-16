using System.Text.Json;

namespace RideManager.AppSync;

/// <summary>
/// 表示手机 App 同步协议请求。
/// </summary>
public sealed record AppSyncRequest(int V, string Id, string Type, JsonElement Payload);

/// <summary>
/// 表示手机 App 同步协议响应。
/// </summary>
public sealed record AppSyncResponse(int V, string Id, string Type, string Status, JsonElement Payload);

/// <summary>
/// 表示一次同步页。
/// </summary>
public sealed record AppSyncPage(IReadOnlyList<AppSyncDecisionRecord> Items, string? NextCursor, bool HasMore);

/// <summary>
/// 表示 App 侧展示的一条安全决策记录。
/// </summary>
public sealed record AppSyncDecisionRecord(
    Guid Id,
    DateTimeOffset DecidedAt,
    string RiskLevel,
    JsonElement Payload,
    IReadOnlyList<AppSyncCameraFindingRecord> CameraFindings,
    IReadOnlyList<AppSyncSensorSnapshotRecord> SensorSnapshots);

/// <summary>
/// 表示 App 侧展示的一条摄像头检测结果。
/// </summary>
public sealed record AppSyncCameraFindingRecord(
    Guid Id,
    string CameraId,
    string Label,
    double Confidence,
    DateTimeOffset ObservedAt,
    double? BoxX,
    double? BoxY,
    double? BoxWidth,
    double? BoxHeight,
    JsonElement Payload);

/// <summary>
/// 表示 App 侧展示的一条传感器快照。
/// </summary>
public sealed record AppSyncSensorSnapshotRecord(
    Guid Id,
    string SensorName,
    DateTimeOffset ObservedAt,
    JsonElement Values);

/// <summary>
/// 表示 App 设置变更写入结果。
/// </summary>
public sealed record AppSyncSettingsUpdateResult(
    Guid EventId,
    DateTimeOffset AcceptedAt,
    bool RequiresRestart,
    string Message);

/// <summary>
/// 表示 App 设置变更系统事件负载。
/// </summary>
public sealed record AppSyncSettingsUpdateEvent(string? ClientId, JsonElement Patch);

/// <summary>
/// 表示 App ping 响应负载。
/// </summary>
public sealed record AppSyncPing(DateTimeOffset Pong);

/// <summary>
/// 表示 App 协议错误响应负载。
/// </summary>
public sealed record AppSyncError(string Message);

/// <summary>
/// 表示 App 连接握手信息。
/// </summary>
public sealed record AppSyncHello(
    string DeviceName,
    string Protocol,
    int Version,
    double DefaultSyncWindowHours,
    int MaxPageSize,
    IReadOnlyList<string> Capabilities);
