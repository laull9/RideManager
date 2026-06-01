using RideManager.Camera;
using RideManager.Sensors;

namespace RideManager.Core;

/// <summary>
/// 根据摄像头与传感器数据生成安全决策。
/// </summary>
public sealed class SafetyDecisionEngine
{
    private static readonly TimeSpan TrendWindow = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan TrendSplitWindow = TimeSpan.FromSeconds(5);

    private readonly TimeProvider _timeProvider;
    private readonly Dictionary<CameraId, List<CameraRiskSample>> _cameraRiskSamples = new();

    /// <summary>
    /// 创建安全决策引擎。
    /// </summary>
    public SafetyDecisionEngine(TimeProvider? timeProvider = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>
    /// 汇总各模块数据并输出当前风险等级。
    /// </summary>
    public SafetyDecision Decide(
        IReadOnlyCollection<CameraId> activeCameraIds,
        IReadOnlyList<CameraFinding> cameraFindings,
        IReadOnlyList<SensorSnapshot> sensorSnapshots)
    {
        var decidedAt = _timeProvider.GetUtcNow();
        var cameraRiskAssessments = BuildCameraRiskAssessments(activeCameraIds, cameraFindings, decidedAt);
        var riskLevel = DetermineOverallRisk(cameraRiskAssessments, cameraFindings);

        return new SafetyDecision(riskLevel, decidedAt, cameraFindings, sensorSnapshots, cameraRiskAssessments);
    }

    /// <summary>
    /// 计算前后摄像头 10 秒窗口内的趋势风险。
    /// </summary>
    private IReadOnlyList<CameraRiskAssessment> BuildCameraRiskAssessments(
        IReadOnlyCollection<CameraId> activeCameraIds,
        IReadOnlyList<CameraFinding> cameraFindings,
        DateTimeOffset decidedAt)
    {
        var trackedCameraIds = activeCameraIds
            .Where(IsTrendCamera)
            .Distinct()
            .ToArray();

        TrimInactiveSamples(trackedCameraIds);

        if (trackedCameraIds.Length == 0)
        {
            return Array.Empty<CameraRiskAssessment>();
        }

        var assessments = new List<CameraRiskAssessment>(trackedCameraIds.Length);
        foreach (var cameraId in trackedCameraIds)
        {
            var currentFindings = cameraFindings
                .Where(finding => finding.CameraId == cameraId)
                .ToArray();

            var samples = GetOrCreateSamples(cameraId);
            samples.Add(new CameraRiskSample(
                decidedAt,
                CalculateRiskScore(currentFindings),
                currentFindings.Select(finding => finding.Label).Distinct(StringComparer.OrdinalIgnoreCase).ToArray()));
            samples.RemoveAll(sample => sample.ObservedAt < decidedAt - TrendWindow);

            assessments.Add(CreateAssessment(cameraId, samples, decidedAt));
        }

        return assessments;
    }

    /// <summary>
    /// 汇总前后摄像头趋势风险，并保留其它摄像头的高置信度告警。
    /// </summary>
    private static SafetyRiskLevel DetermineOverallRisk(
        IReadOnlyList<CameraRiskAssessment> cameraRiskAssessments,
        IReadOnlyList<CameraFinding> cameraFindings)
    {
        if (cameraRiskAssessments.Any(assessment => assessment.RiskLevel == SafetyRiskLevel.Danger))
        {
            return SafetyRiskLevel.Danger;
        }

        if (cameraRiskAssessments.Any(assessment => assessment.RiskLevel == SafetyRiskLevel.Warning)
            || cameraFindings.Any(finding => !IsTrendCamera(finding.CameraId) && IsNonTrendAlert(finding)))
        {
            return SafetyRiskLevel.Warning;
        }

        return SafetyRiskLevel.Normal;
    }

    /// <summary>
    /// 根据窗口中的历史样本生成单路摄像头风险评估。
    /// </summary>
    private static CameraRiskAssessment CreateAssessment(
        CameraId cameraId,
        IReadOnlyList<CameraRiskSample> samples,
        DateTimeOffset decidedAt)
    {
        var splitAt = decidedAt - TrendSplitWindow;
        var previousSamples = samples.Where(sample => sample.ObservedAt < splitAt).ToArray();
        var recentSamples = samples.Where(sample => sample.ObservedAt >= splitAt).ToArray();

        var currentScore = samples.Count == 0 ? 0.0 : samples[^1].Score;
        var previousAverageScore = previousSamples.Length == 0 ? 0.0 : previousSamples.Average(sample => sample.Score);
        var recentAverageScore = recentSamples.Length == 0 ? 0.0 : recentSamples.Average(sample => sample.Score);
        var trendScoreDelta = recentAverageScore - previousAverageScore;
        var peakScore = samples.Count == 0 ? 0.0 : samples.Max(sample => sample.Score);
        var leadingLabels = samples
            .SelectMany(sample => sample.Labels)
            .GroupBy(label => label, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(group => group.Count())
            .ThenBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
            .Take(3)
            .Select(group => group.First())
            .ToArray();

        return new CameraRiskAssessment(
            cameraId,
            DetermineTrendRiskLevel(currentScore, recentAverageScore, trendScoreDelta, peakScore),
            TrendWindow.TotalSeconds,
            samples.Count,
            currentScore,
            recentAverageScore,
            previousAverageScore,
            trendScoreDelta,
            peakScore,
            leadingLabels);
    }

    /// <summary>
    /// 计算当前帧在前后向风险窗口中的贡献分数。
    /// </summary>
    private static double CalculateRiskScore(IReadOnlyList<CameraFinding> cameraFindings)
    {
        return Math.Clamp(cameraFindings.Sum(CalculateFindingScore), 0.0, 1.0);
    }

    /// <summary>
    /// 为单个检测结果计算风险权重，综合标签、框面积与置信度。
    /// </summary>
    private static double CalculateFindingScore(CameraFinding finding)
    {
        var labelWeight = GetLabelWeight(finding.Label);
        var sizeWeight = GetSizeWeight(finding.BoundingBox);
        return finding.Confidence * labelWeight * sizeWeight;
    }

    /// <summary>
    /// 根据近 10 秒的峰值和半窗趋势划分风险等级。
    /// </summary>
    private static SafetyRiskLevel DetermineTrendRiskLevel(
        double currentScore,
        double recentAverageScore,
        double trendScoreDelta,
        double peakScore)
    {
        if (currentScore >= 0.85
            || peakScore >= 0.9
            || (recentAverageScore >= 0.55 && trendScoreDelta >= 0.08))
        {
            return SafetyRiskLevel.Danger;
        }

        if (currentScore >= 0.45
            || peakScore >= 0.6
            || recentAverageScore >= 0.28
            || trendScoreDelta >= 0.05)
        {
            return SafetyRiskLevel.Warning;
        }

        return SafetyRiskLevel.Normal;
    }

    /// <summary>
    /// 为道路相关目标分配标签权重。
    /// </summary>
    private static double GetLabelWeight(string label)
    {
        return label.Trim().ToLowerInvariant() switch
        {
            "lane_line" or "drivable_area" or "face_landmarks_106" or "fatigue_normal" or "fatigue_unknown" => 0.0,
            "fatigue" => 0.9,
            "person" => 1.0,
            "bicycle" or "motorcycle" => 0.95,
            "car" or "bus" or "truck" or "train" => 0.9,
            "dog" or "cat" or "horse" or "sheep" or "cow" => 0.75,
            "traffic light" or "stop sign" => 0.45,
            _ => 0.35
        };
    }

    /// <summary>
    /// 判断非趋势摄像头 finding 是否代表可直接提示的风险。
    /// </summary>
    private static bool IsNonTrendAlert(CameraFinding finding)
    {
        return finding.Confidence >= 0.8 && GetLabelWeight(finding.Label) > 0.0;
    }

    /// <summary>
    /// 使用目标框面积近似目标距离，放大逼近风险。
    /// </summary>
    private static double GetSizeWeight(CameraBoundingBox? boundingBox)
    {
        if (boundingBox is null)
        {
            return 0.5;
        }

        var normalizedArea = Math.Clamp(boundingBox.Width, 0.0, 1.0) * Math.Clamp(boundingBox.Height, 0.0, 1.0);
        return Math.Clamp(Math.Sqrt(normalizedArea) * 2.5, 0.25, 1.0);
    }

    /// <summary>
    /// 判断当前摄像头是否参与前后向趋势风险计算。
    /// </summary>
    private static bool IsTrendCamera(CameraId cameraId)
    {
        return cameraId is CameraId.CamFront or CameraId.CamBack;
    }

    /// <summary>
    /// 清理不再活跃的前后向摄像头历史窗口。
    /// </summary>
    private void TrimInactiveSamples(IReadOnlyCollection<CameraId> trackedCameraIds)
    {
        foreach (var cameraId in _cameraRiskSamples.Keys.Where(cameraId => !trackedCameraIds.Contains(cameraId)).ToArray())
        {
            _cameraRiskSamples.Remove(cameraId);
        }
    }

    /// <summary>
    /// 获取单路摄像头的风险样本窗口。
    /// </summary>
    private List<CameraRiskSample> GetOrCreateSamples(CameraId cameraId)
    {
        if (_cameraRiskSamples.TryGetValue(cameraId, out var samples))
        {
            return samples;
        }

        samples = new List<CameraRiskSample>();
        _cameraRiskSamples[cameraId] = samples;
        return samples;
    }

    /// <summary>
    /// 表示风险窗口中的一次采样结果。
    /// </summary>
    private sealed record CameraRiskSample(DateTimeOffset ObservedAt, double Score, IReadOnlyList<string> Labels);
}
