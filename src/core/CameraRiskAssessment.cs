using RideManager.Camera;

namespace RideManager.Core;

/// <summary>
/// 表示前后摄像头在 10 秒窗口内的风险评估结果。
/// </summary>
public sealed record CameraRiskAssessment(
    CameraId CameraId,
    SafetyRiskLevel RiskLevel,
    double WindowSeconds,
    int SampleCount,
    double CurrentScore,
    double RecentAverageScore,
    double PreviousAverageScore,
    double TrendScoreDelta,
    double PeakScore,
    IReadOnlyList<string> LeadingLabels);