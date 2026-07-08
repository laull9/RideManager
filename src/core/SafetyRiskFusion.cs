using RideManager.Camera;

namespace RideManager.Core;

/// <summary>
/// Fuses per-camera trend assessments and direct camera alerts into one safety result.
/// </summary>
public sealed class SafetyRiskFusion
{
    private const double FatigueWarningConfidence = 0.8;
    private const double FatigueStrongConfidence = 0.9;

    /// <summary>
    /// Creates the composite risk assessment for the current supervisor cycle.
    /// </summary>
    public CompositeRiskAssessment Fuse(
        IReadOnlyList<CameraRiskAssessment> cameraRiskAssessments,
        IReadOnlyList<CameraFinding> cameraFindings)
    {
        var contributions = new List<CompositeRiskContribution>();
        foreach (var assessment in cameraRiskAssessments)
        {
            contributions.Add(new CompositeRiskContribution(
                FormatTrendSource(assessment.CameraId),
                assessment.CameraId,
                assessment.RiskLevel,
                assessment.CurrentScore,
                assessment.LeadingLabels));
        }

        var directAlerts = cameraFindings
            .Where(IsDirectAlert)
            .GroupBy(finding => finding.CameraId)
            .Select(group => CreateDirectContribution(group.Key, group))
            .ToArray();
        contributions.AddRange(directAlerts);

        var reasons = new List<string>();
        var trendDanger = cameraRiskAssessments
            .Where(assessment => assessment.RiskLevel == SafetyRiskLevel.Danger)
            .OrderByDescending(assessment => assessment.CurrentScore)
            .FirstOrDefault();
        if (trendDanger is not null)
        {
            reasons.Add($"{FormatCameraId(trendDanger.CameraId)} trend risk reached danger");
            return CreateResult(SafetyRiskLevel.Danger, FormatTrendSource(trendDanger.CameraId), reasons, contributions);
        }

        var hasTrendWarning = cameraRiskAssessments.Any(assessment => assessment.RiskLevel == SafetyRiskLevel.Warning);
        var strongestFatigue = cameraFindings
            .Where(IsFatigueFinding)
            .OrderByDescending(finding => finding.Confidence)
            .FirstOrDefault();
        if (strongestFatigue is not null
            && strongestFatigue.Confidence >= FatigueStrongConfidence
            && hasTrendWarning)
        {
            reasons.Add("fatigue combined with front or rear camera warning");
            return CreateResult(SafetyRiskLevel.Danger, "composite.camera_fusion", reasons, contributions);
        }

        if (hasTrendWarning)
        {
            reasons.Add("front or rear camera trend risk reached warning");
        }

        if (strongestFatigue is not null && strongestFatigue.Confidence >= FatigueWarningConfidence)
        {
            reasons.Add("face camera reported fatigue");
        }

        var hasOtherDirectAlert = cameraFindings.Any(finding => IsDirectAlert(finding) && !IsFatigueFinding(finding));
        if (hasOtherDirectAlert)
        {
            reasons.Add("non-trend camera reported high-confidence alert");
        }

        return reasons.Count > 0
            ? CreateResult(SafetyRiskLevel.Warning, PickWarningSource(cameraRiskAssessments, strongestFatigue, hasOtherDirectAlert), reasons, contributions)
            : CreateResult(SafetyRiskLevel.Normal, "none", new[] { "no active risk source" }, contributions);
    }

    /// <summary>
    /// Determines whether a finding can directly contribute warning risk outside trend scoring.
    /// </summary>
    internal static bool IsDirectAlert(CameraFinding finding)
    {
        return !IsTrendCamera(finding.CameraId)
            && finding.Confidence >= FatigueWarningConfidence
            && LabelRiskWeight(finding.Label) > 0.0;
    }

    /// <summary>
    /// Returns the risk weight for labels used by the fusion layer.
    /// </summary>
    internal static double LabelRiskWeight(string label)
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
    /// Determines whether the finding represents fatigue from the face camera.
    /// </summary>
    private static bool IsFatigueFinding(CameraFinding finding)
    {
        return finding.CameraId == CameraId.CamFace
            && finding.Label.Equals("fatigue", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Determines whether the camera is scored by the trend window model.
    /// </summary>
    private static bool IsTrendCamera(CameraId cameraId)
    {
        return cameraId is CameraId.CamFront or CameraId.CamBack;
    }

    /// <summary>
    /// Creates a direct-alert contribution for a non-trend camera.
    /// </summary>
    private static CompositeRiskContribution CreateDirectContribution(
        CameraId cameraId,
        IEnumerable<CameraFinding> findings)
    {
        var ordered = findings
            .OrderByDescending(finding => finding.Confidence * LabelRiskWeight(finding.Label))
            .ToArray();
        var primary = ordered.First();
        var labels = ordered
            .Select(finding => finding.Label)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(3)
            .ToArray();

        return new CompositeRiskContribution(
            $"{FormatCameraId(cameraId)}.direct_alert",
            cameraId,
            SafetyRiskLevel.Warning,
            Math.Clamp(primary.Confidence * LabelRiskWeight(primary.Label), 0.0, 1.0),
            labels);
    }

    /// <summary>
    /// Picks the primary source for a warning result.
    /// </summary>
    private static string PickWarningSource(
        IReadOnlyList<CameraRiskAssessment> cameraRiskAssessments,
        CameraFinding? strongestFatigue,
        bool hasOtherDirectAlert)
    {
        var trendWarning = cameraRiskAssessments
            .Where(assessment => assessment.RiskLevel == SafetyRiskLevel.Warning)
            .OrderByDescending(assessment => assessment.CurrentScore)
            .FirstOrDefault();

        if (trendWarning is not null)
        {
            return FormatTrendSource(trendWarning.CameraId);
        }

        if (strongestFatigue is not null)
        {
            return "CAM_FACE.direct_alert";
        }

        return hasOtherDirectAlert ? "camera.direct_alert" : "none";
    }

    /// <summary>
    /// Creates the immutable fused assessment.
    /// </summary>
    private static CompositeRiskAssessment CreateResult(
        SafetyRiskLevel riskLevel,
        string primarySource,
        IReadOnlyList<string> reasons,
        IReadOnlyList<CompositeRiskContribution> contributions)
    {
        return new CompositeRiskAssessment(
            riskLevel,
            primarySource,
            reasons.ToArray(),
            contributions
                .OrderByDescending(contribution => contribution.RiskLevel)
                .ThenByDescending(contribution => contribution.Score)
                .ToArray());
    }

    /// <summary>
    /// Formats a trend source identifier.
    /// </summary>
    private static string FormatTrendSource(CameraId cameraId)
    {
        return $"{FormatCameraId(cameraId)}.trend";
    }

    /// <summary>
    /// Formats a camera identifier for payload diagnostics.
    /// </summary>
    private static string FormatCameraId(CameraId cameraId)
    {
        return cameraId switch
        {
            CameraId.CamFace => "CAM_FACE",
            CameraId.CamBack => "CAM_BACK",
            _ => "CAM_FRONT"
        };
    }
}
