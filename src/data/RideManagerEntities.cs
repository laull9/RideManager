using RideManager.Core;

namespace RideManager.Data;

/// <summary>
/// 表示系统接入的摄像头、传感器或执行器设备。
/// </summary>
public sealed class DeviceEntity
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public string Code { get; set; } = string.Empty;

    public string DeviceType { get; set; } = string.Empty;

    public string? Transport { get; set; }

    public string? Address { get; set; }

    public bool Enabled { get; set; }

    public string ConfigJson { get; set; } = "{}";

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// 表示可部署的算法模型资产。
/// </summary>
public sealed class ModelArtifactEntity
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public string Name { get; set; } = string.Empty;

    public string Backend { get; set; } = string.Empty;

    public string RelativePath { get; set; } = string.Empty;

    public string? Version { get; set; }

    public int? InputWidth { get; set; }

    public int? InputHeight { get; set; }

    public string LabelsJson { get; set; } = "[]";

    public string ConfigJson { get; set; } = "{}";

    public bool IsActive { get; set; } = true;

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// 表示一次上位机运行会话。
/// </summary>
public sealed class RunSessionEntity
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public DateTimeOffset StartedAt { get; set; } = DateTimeOffset.UtcNow;

    public DateTimeOffset? StoppedAt { get; set; }

    public string? HostName { get; set; }

    public string ConfigJson { get; set; } = "{}";

    public string? Note { get; set; }
}

/// <summary>
/// 表示主控模块输出的一次安全决策。
/// </summary>
public sealed class SafetyDecisionEntity
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid? RunSessionId { get; set; }

    public SafetyRiskLevel RiskLevel { get; set; }

    public DateTimeOffset DecidedAt { get; set; }

    public string PayloadJson { get; set; } = "{}";

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public RunSessionEntity? RunSession { get; set; }

    public List<CameraFrameEntity> CameraFrames { get; } = new();

    public List<CameraFindingEntity> CameraFindings { get; } = new();

    public List<SensorSnapshotEntity> SensorSnapshots { get; } = new();

    public List<ActuatorCommandEntity> ActuatorCommands { get; } = new();
}

/// <summary>
/// 表示单路摄像头一次帧处理结果。
/// </summary>
public sealed class CameraFrameEntity
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid? SafetyDecisionId { get; set; }

    public Guid? DeviceId { get; set; }

    public string CameraId { get; set; } = string.Empty;

    public DateTimeOffset CapturedAt { get; set; }

    public int? Width { get; set; }

    public int? Height { get; set; }

    public double? CaptureLatencyMs { get; set; }

    public double? PreprocessLatencyMs { get; set; }

    public double? InferenceLatencyMs { get; set; }

    public double? TotalLatencyMs { get; set; }

    public double? Fps { get; set; }

    public long? DroppedFrames { get; set; }

    public string MetadataJson { get; set; } = "{}";

    public SafetyDecisionEntity? SafetyDecision { get; set; }

    public DeviceEntity? Device { get; set; }

    public List<CameraFindingEntity> Findings { get; } = new();
}

/// <summary>
/// 表示摄像头算法输出的一条检测结果。
/// </summary>
public sealed class CameraFindingEntity
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid SafetyDecisionId { get; set; }

    public Guid? CameraFrameId { get; set; }

    public string CameraId { get; set; } = string.Empty;

    public string Label { get; set; } = string.Empty;

    public double Confidence { get; set; }

    public DateTimeOffset ObservedAt { get; set; }

    public double? BoxX { get; set; }

    public double? BoxY { get; set; }

    public double? BoxWidth { get; set; }

    public double? BoxHeight { get; set; }

    public string PayloadJson { get; set; } = "{}";

    public SafetyDecisionEntity? SafetyDecision { get; set; }

    public CameraFrameEntity? CameraFrame { get; set; }
}

/// <summary>
/// 表示传感器上报的一次状态快照。
/// </summary>
public sealed class SensorSnapshotEntity
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid SafetyDecisionId { get; set; }

    public Guid? DeviceId { get; set; }

    public string SensorName { get; set; } = string.Empty;

    public DateTimeOffset ObservedAt { get; set; }

    public string ValuesJson { get; set; } = "{}";

    public SafetyDecisionEntity? SafetyDecision { get; set; }

    public DeviceEntity? Device { get; set; }

    public List<SensorReadingEntity> Readings { get; } = new();
}

/// <summary>
/// 表示传感器快照里的单个指标值。
/// </summary>
public sealed class SensorReadingEntity
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid SensorSnapshotId { get; set; }

    public string Metric { get; set; } = string.Empty;

    public double Value { get; set; }

    public string? Unit { get; set; }

    public SensorSnapshotEntity? SensorSnapshot { get; set; }
}

/// <summary>
/// 表示系统向执行器发出的一条命令。
/// </summary>
public sealed class ActuatorCommandEntity
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid? SafetyDecisionId { get; set; }

    public Guid? DeviceId { get; set; }

    public string ActuatorName { get; set; } = string.Empty;

    public string CommandType { get; set; } = string.Empty;

    public DateTimeOffset RequestedAt { get; set; } = DateTimeOffset.UtcNow;

    public DateTimeOffset? CompletedAt { get; set; }

    public string Status { get; set; } = "pending";

    public string PayloadJson { get; set; } = "{}";

    public string? ErrorMessage { get; set; }

    public SafetyDecisionEntity? SafetyDecision { get; set; }

    public DeviceEntity? Device { get; set; }
}

/// <summary>
/// 表示预留给诊断、异常和生命周期事件的系统事件。
/// </summary>
public sealed class SystemEventEntity
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public DateTimeOffset OccurredAt { get; set; } = DateTimeOffset.UtcNow;

    public string Source { get; set; } = string.Empty;

    public string Level { get; set; } = "info";

    public string Message { get; set; } = string.Empty;

    public string PayloadJson { get; set; } = "{}";
}
