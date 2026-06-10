using RideManager.Camera;
using RideManager.Sensors;

namespace RideManager.Core;

/// <summary>
/// 表示一次主控综合决策结果。
/// </summary>
public sealed record SafetyDecision(
    SafetyRiskLevel RiskLevel,
    DateTimeOffset DecidedAt,
    IReadOnlyList<CameraFinding> CameraFindings,
    IReadOnlyList<SensorSnapshot> SensorSnapshots,
    IReadOnlyList<CameraRiskAssessment> CameraRiskAssessments,
    IReadOnlyList<CameraFrameState> CameraFrames);
