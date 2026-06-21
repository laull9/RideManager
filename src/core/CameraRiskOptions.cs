using RideManager.Camera;

namespace RideManager.Core;

/// <summary>
/// 表示单路摄像头参与安全决策时的风险算法参数。
/// </summary>
public sealed record CameraRiskOptions(
    double FisheyeFovDegrees,
    double FisheyeStrength,
    double RearCenterDangerAngleDegrees,
    double RearEdgeWarningMinScore)
{
    /// <summary>
    /// 根据摄像头类型创建默认风险参数。
    /// </summary>
    public static CameraRiskOptions ForCamera(CameraId cameraId)
    {
        return cameraId == CameraId.CamBack
            ? new CameraRiskOptions(180.0, 1.0, 45.0, 0.18)
            : new CameraRiskOptions(90.0, 0.0, 45.0, 0.18);
    }
}
