using RideManager.Camera;
using RideManager.Sensors;

namespace RideManager.Core;

/// <summary>
/// 根据摄像头与传感器数据生成安全决策。
/// </summary>
public sealed class SafetyDecisionEngine
{
    /// <summary>
    /// 汇总各模块数据并输出当前风险等级。
    /// </summary>
    public SafetyDecision Decide(
        IReadOnlyList<CameraFinding> cameraFindings,
        IReadOnlyList<SensorSnapshot> sensorSnapshots)
    {
        var riskLevel = cameraFindings.Any(finding => finding.Confidence >= 0.8)
            ? SafetyRiskLevel.Warning
            : SafetyRiskLevel.Normal;

        return new SafetyDecision(riskLevel, DateTimeOffset.UtcNow, cameraFindings, sensorSnapshots);
    }
}
