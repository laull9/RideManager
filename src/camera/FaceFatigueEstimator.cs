namespace RideManager.Camera;

/// <summary>
/// 基于 PFLD 106 点关键点估算单帧闭眼疲劳状态。
/// </summary>
internal static class FaceFatigueEstimator
{
    private const double ClosedEyeThreshold = 0.16;
    private const double OpenEyeThreshold = 0.24;

    /// <summary>
    /// 根据左右眼开合度返回疲劳标签和置信度。
    /// </summary>
    public static FaceFatigueResult Estimate(IReadOnlyList<CameraLandmark> landmarks)
    {
        if (landmarks.Count < 106)
        {
            return new FaceFatigueResult("fatigue_unknown", 0.0, 0.0);
        }

        var leftEye = CalculateLeftEyeOpenness(landmarks);
        var rightEye = CalculateRightEyeOpenness(landmarks);
        var eyeOpenness = Math.Min(leftEye, rightEye);
        var fatigueScore = Math.Clamp((OpenEyeThreshold - eyeOpenness) / (OpenEyeThreshold - ClosedEyeThreshold), 0.0, 1.0);

        return fatigueScore >= 0.8
            ? new FaceFatigueResult("fatigue", fatigueScore, eyeOpenness)
            : new FaceFatigueResult("fatigue_normal", 1.0 - fatigueScore, eyeOpenness);
    }

    /// <summary>
    /// 计算左眼开合度，使用 106 点标注中的眼角和上下眼睑点。
    /// </summary>
    private static double CalculateLeftEyeOpenness(IReadOnlyList<CameraLandmark> landmarks)
    {
        var horizontal = Distance(landmarks[35], landmarks[75]);
        var vertical = (Distance(landmarks[41], landmarks[36])
            + Distance(landmarks[40], landmarks[33])
            + Distance(landmarks[42], landmarks[37])) / 3.0;
        return horizontal <= 0 ? 0.0 : vertical / horizontal;
    }

    /// <summary>
    /// 计算右眼开合度，使用 106 点标注中的眼角和上下眼睑点。
    /// </summary>
    private static double CalculateRightEyeOpenness(IReadOnlyList<CameraLandmark> landmarks)
    {
        var horizontal = Distance(landmarks[89], landmarks[93]);
        var vertical = (Distance(landmarks[95], landmarks[90])
            + Distance(landmarks[94], landmarks[88])
            + Distance(landmarks[96], landmarks[91])) / 3.0;
        return horizontal <= 0 ? 0.0 : vertical / horizontal;
    }

    /// <summary>
    /// 计算两个归一化关键点之间的欧式距离。
    /// </summary>
    private static double Distance(CameraLandmark first, CameraLandmark second)
    {
        var dx = first.X - second.X;
        var dy = first.Y - second.Y;
        return Math.Sqrt(dx * dx + dy * dy);
    }
}

/// <summary>
/// 表示单帧面部疲劳估计结果。
/// </summary>
internal sealed record FaceFatigueResult(string Label, double Confidence, double EyeOpenness);
