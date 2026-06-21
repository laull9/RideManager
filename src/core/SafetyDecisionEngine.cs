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
    private readonly IReadOnlyDictionary<CameraId, CameraRiskOptions> _cameraRiskOptions;
    private readonly Dictionary<CameraId, List<CameraRiskSample>> _cameraRiskSamples = new();

    /// <summary>
    /// 创建安全决策引擎。
    /// </summary>
    public SafetyDecisionEngine(
        TimeProvider? timeProvider = null,
        IReadOnlyDictionary<CameraId, CameraRiskOptions>? cameraRiskOptions = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
        _cameraRiskOptions = cameraRiskOptions ?? new Dictionary<CameraId, CameraRiskOptions>();
    }

    /// <summary>
    /// 汇总各模块数据并输出当前风险等级。
    /// </summary>
    public SafetyDecision Decide(
        IReadOnlyCollection<CameraId> activeCameraIds,
        IReadOnlyList<CameraFinding> cameraFindings,
        IReadOnlyList<SensorSnapshot> sensorSnapshots,
        IReadOnlyList<CameraFrameState>? cameraFrames = null)
    {
        var decidedAt = _timeProvider.GetUtcNow();
        var cameraRiskAssessments = BuildCameraRiskAssessments(activeCameraIds, cameraFindings, decidedAt);
        var riskLevel = DetermineOverallRisk(cameraRiskAssessments, cameraFindings);

        return new SafetyDecision(
            riskLevel,
            decidedAt,
            cameraFindings,
            sensorSnapshots,
            cameraRiskAssessments,
            cameraFrames ?? Array.Empty<CameraFrameState>());
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
            var riskOptions = GetCameraRiskOptions(cameraId);
            samples.Add(CreateRiskSample(cameraId, riskOptions, currentFindings, decidedAt));
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
        var recentAverageScore = recentSamples.Length == 0 ? 0.0 : recentSamples.Average(sample => sample.Score);
        var previousAverageScore = previousSamples.Length == 0 ? recentAverageScore : previousSamples.Average(sample => sample.Score);
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
            DetermineTrendRiskLevel(cameraId, samples.Count, currentScore, recentAverageScore, trendScoreDelta, peakScore),
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
    /// 生成当前帧在前后向风险窗口中的主风险采样。
    /// </summary>
    private static CameraRiskSample CreateRiskSample(
        CameraId cameraId,
        CameraRiskOptions riskOptions,
        IReadOnlyList<CameraFinding> cameraFindings,
        DateTimeOffset observedAt)
    {
        var features = cameraFindings
            .Select(finding => CalculateFindingRisk(cameraId, riskOptions, finding))
            .Where(feature => feature.Score > 0.0)
            .OrderByDescending(feature => feature.Score)
            .ToArray();
        var primary = features.FirstOrDefault();
        var labels = features
            .Where(feature => feature.Score >= 0.05)
            .Select(feature => feature.Label)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(3)
            .ToArray();

        return new CameraRiskSample(observedAt, primary?.Score ?? 0.0, labels);
    }

    /// <summary>
    /// 为单个检测结果计算风险特征。
    /// </summary>
    private static FindingRiskFeature CalculateFindingRisk(
        CameraId cameraId,
        CameraRiskOptions riskOptions,
        CameraFinding finding)
    {
        var labelWeight = GetLabelWeight(finding.Label);
        if (labelWeight <= 0.0)
        {
            return FindingRiskFeature.Empty(finding.Label);
        }

        return cameraId == CameraId.CamFront
            ? CalculateFrontFindingRisk(finding, labelWeight)
            : CalculateRearFindingRisk(finding, labelWeight, riskOptions);
    }

    /// <summary>
    /// 根据近 10 秒半窗趋势划分风险等级。
    /// </summary>
    private static SafetyRiskLevel DetermineTrendRiskLevel(
        CameraId cameraId,
        int sampleCount,
        double currentScore,
        double recentAverageScore,
        double trendScoreDelta,
        double peakScore)
    {
        if (cameraId == CameraId.CamFront)
        {
            return DetermineFrontRiskLevel(sampleCount, currentScore, recentAverageScore, trendScoreDelta, peakScore);
        }

        if ((currentScore >= 0.82 && recentAverageScore >= 0.55)
            || (sampleCount >= 3 && recentAverageScore >= 0.50 && trendScoreDelta >= 0.10)
            || (sampleCount >= 3 && currentScore >= 0.72 && trendScoreDelta >= 0.16))
        {
            return SafetyRiskLevel.Danger;
        }

        return currentScore >= 0.12
            || recentAverageScore >= 0.10
            || trendScoreDelta >= 0.06
            || peakScore >= 0.18
            ? SafetyRiskLevel.Warning
            : SafetyRiskLevel.Normal;
    }

    /// <summary>
    /// 前向摄像头只在碰撞走廊内并持续接近时升级到 Danger。
    /// </summary>
    private static SafetyRiskLevel DetermineFrontRiskLevel(
        int sampleCount,
        double currentScore,
        double recentAverageScore,
        double trendScoreDelta,
        double peakScore)
    {
        if ((currentScore >= 0.92 && recentAverageScore >= 0.70)
            || (sampleCount >= 3 && recentAverageScore >= 0.62 && trendScoreDelta >= 0.12)
            || (sampleCount >= 3 && currentScore >= 0.82 && trendScoreDelta >= 0.18))
        {
            return SafetyRiskLevel.Danger;
        }

        if (currentScore >= 0.12
            || recentAverageScore >= 0.10
            || trendScoreDelta >= 0.06
            || peakScore >= 0.18)
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
    /// 计算前向摄像头单目标在碰撞走廊中的风险。
    /// </summary>
    private static FindingRiskFeature CalculateFrontFindingRisk(CameraFinding finding, double labelWeight)
    {
        if (finding.BoundingBox is not { } box)
        {
            return FindingRiskFeature.Empty(finding.Label);
        }

        var centerX = Math.Clamp(box.X + box.Width / 2.0, 0.0, 1.0);
        var bottomY = Math.Clamp(box.Y + box.Height, 0.0, 1.0);
        var lateralOffset = Math.Abs(centerX - 0.5);
        var bottomGate = SmoothStep(0.50, 0.88, bottomY);
        var corridorHalfWidth = Lerp(0.08, 0.23, bottomGate);
        var lateralWeight = 1.0 - SmoothStep(corridorHalfWidth * 0.65, corridorHalfWidth, lateralOffset);
        var collisionWeight = bottomGate * Math.Clamp(lateralWeight, 0.0, 1.0);
        if (collisionWeight <= 0.0)
        {
            return FindingRiskFeature.Empty(finding.Label);
        }

        var normalizedArea = Math.Clamp(box.Width, 0.0, 1.0) * Math.Clamp(box.Height, 0.0, 1.0);
        var areaProximity = SmoothStep(0.12, 0.40, Math.Sqrt(normalizedArea));
        var bottomProximity = SmoothStep(0.56, 0.96, bottomY);
        var proximity = Math.Clamp(areaProximity * 0.55 + bottomProximity * 0.45, 0.0, 1.0);
        var score = finding.Confidence * labelWeight * collisionWeight * proximity;

        return new FindingRiskFeature(finding.Label, Math.Clamp(score, 0.0, 1.0));
    }

    /// <summary>
    /// 计算后向鱼眼摄像头单目标风险，中心区域可升危险，边缘区域只提供警告分数。
    /// </summary>
    private static FindingRiskFeature CalculateRearFindingRisk(
        CameraFinding finding,
        double labelWeight,
        CameraRiskOptions riskOptions)
    {
        if (finding.BoundingBox is not { } box)
        {
            return new FindingRiskFeature(finding.Label, finding.Confidence * labelWeight * 0.25);
        }

        var centerX = Math.Clamp(box.X + box.Width / 2.0, 0.0, 1.0);
        var bottomY = Math.Clamp(box.Y + box.Height, 0.0, 1.0);
        var absoluteAngle = Math.Abs(centerX - 0.5) * riskOptions.FisheyeFovDegrees;
        var centerHalfAngle = riskOptions.RearCenterDangerAngleDegrees / 2.0;
        var centerWeight = 1.0 - SmoothStep(centerHalfAngle * 0.65, centerHalfAngle, absoluteAngle);
        var edgeScore = CalculateRearEdgeWarningScore(finding, labelWeight, riskOptions, box);
        if (centerWeight <= 0.0)
        {
            return new FindingRiskFeature(finding.Label, edgeScore);
        }

        var bottomGate = SmoothStep(0.50, 0.88, bottomY);
        var normalizedArea = Math.Clamp(box.Width, 0.0, 1.0) * Math.Clamp(box.Height, 0.0, 1.0);
        var areaProximity = SmoothStep(0.12, 0.40, Math.Sqrt(normalizedArea));
        var bottomProximity = SmoothStep(0.56, 0.96, bottomY);
        var proximity = Math.Clamp(areaProximity * 0.55 + bottomProximity * 0.45, 0.0, 1.0);
        var centerScore = finding.Confidence * labelWeight * bottomGate * Math.Clamp(centerWeight, 0.0, 1.0) * proximity;

        return new FindingRiskFeature(finding.Label, Math.Clamp(Math.Max(centerScore, edgeScore), 0.0, 1.0));
    }

    /// <summary>
    /// 计算后向鱼眼边缘区域的警告分数，并限制其不能单帧触发危险。
    /// </summary>
    private static double CalculateRearEdgeWarningScore(
        CameraFinding finding,
        double labelWeight,
        CameraRiskOptions riskOptions,
        CameraBoundingBox box)
    {
        var normalizedArea = Math.Clamp(box.Width, 0.0, 1.0) * Math.Clamp(box.Height, 0.0, 1.0);
        var sizeWeight = Math.Clamp(Math.Sqrt(normalizedArea) * 2.5, 0.25, 1.0);
        var edgeAttenuation = Lerp(1.0, 0.35, riskOptions.FisheyeStrength);
        var rawScore = finding.Confidence * labelWeight * sizeWeight * edgeAttenuation;
        if (rawScore < riskOptions.RearEdgeWarningMinScore)
        {
            return rawScore;
        }

        return Math.Min(rawScore, 0.50);
    }

    /// <summary>
    /// 线性插值。
    /// </summary>
    private static double Lerp(double start, double end, double value)
    {
        return start + (end - start) * Math.Clamp(value, 0.0, 1.0);
    }

    /// <summary>
    /// 在归一化透视区间内生成平滑权重。
    /// </summary>
    private static double SmoothStep(double edge0, double edge1, double value)
    {
        var t = Math.Clamp((value - edge0) / (edge1 - edge0), 0.0, 1.0);
        return t * t * (3.0 - 2.0 * t);
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
    /// 获取单路摄像头的风险算法参数。
    /// </summary>
    private CameraRiskOptions GetCameraRiskOptions(CameraId cameraId)
    {
        return _cameraRiskOptions.TryGetValue(cameraId, out var options)
            ? options
            : CameraRiskOptions.ForCamera(cameraId);
    }

    /// <summary>
    /// 表示风险窗口中的一次采样结果。
    /// </summary>
    private sealed record CameraRiskSample(DateTimeOffset ObservedAt, double Score, IReadOnlyList<string> Labels);

    /// <summary>
    /// 表示单个 finding 对当前帧风险的贡献。
    /// </summary>
    private sealed record FindingRiskFeature(string Label, double Score)
    {
        public static FindingRiskFeature Empty(string label)
        {
            return new FindingRiskFeature(label, 0.0);
        }
    }
}
