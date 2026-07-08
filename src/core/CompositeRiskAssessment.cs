using RideManager.Camera;

namespace RideManager.Core;

/// <summary>
/// Represents the fused risk result across all active camera and sensor channels.
/// </summary>
public sealed record CompositeRiskAssessment(
    SafetyRiskLevel RiskLevel,
    string PrimarySource,
    IReadOnlyList<string> Reasons,
    IReadOnlyList<CompositeRiskContribution> Contributions);

/// <summary>
/// Represents one channel's contribution to the fused safety decision.
/// </summary>
public sealed record CompositeRiskContribution(
    string Source,
    CameraId? CameraId,
    SafetyRiskLevel RiskLevel,
    double Score,
    IReadOnlyList<string> Labels);
