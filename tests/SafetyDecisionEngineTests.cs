using RideManager.Camera;
using RideManager.Core;
using RideManager.Sensors;
using Xunit;

namespace RideManager.Tests;

public sealed class SafetyDecisionEngineTests
{
    [Fact]
    public void Decide_WhenFrontCameraShowsStrongObstacleOnce_ReturnsWarningAssessment()
    {
        var timeProvider = new ManualTimeProvider(new DateTimeOffset(2026, 6, 1, 10, 0, 0, TimeSpan.Zero));
        var engine = new SafetyDecisionEngine(timeProvider);

        var decision = engine.Decide(
            new[] { CameraId.CamFront },
            new[] { CreateFinding(CameraId.CamFront, "person", 0.95, timeProvider.GetUtcNow(), 0.55, 0.55) },
            Array.Empty<SensorSnapshot>());

        Assert.Equal(SafetyRiskLevel.Warning, decision.RiskLevel);

        var assessment = Assert.Single(decision.CameraRiskAssessments);
        Assert.Equal(CameraId.CamFront, assessment.CameraId);
        Assert.Equal(SafetyRiskLevel.Warning, assessment.RiskLevel);
        Assert.Contains(assessment.LeadingLabels, label => label.Equals("person", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Decide_WhenFrontObstacleApproachesInCollisionCorridor_ReturnsDangerAssessment()
    {
        var timeProvider = new ManualTimeProvider(new DateTimeOffset(2026, 6, 1, 10, 0, 0, TimeSpan.Zero));
        var engine = new SafetyDecisionEngine(timeProvider);

        engine.Decide(
            new[] { CameraId.CamFront },
            new[] { CreateFinding(CameraId.CamFront, "motorcycle", 0.88, timeProvider.GetUtcNow(), 0.16, 0.16, 0.42, 0.56) },
            Array.Empty<SensorSnapshot>());

        timeProvider.Advance(TimeSpan.FromSeconds(6));
        engine.Decide(
            new[] { CameraId.CamFront },
            new[] { CreateFinding(CameraId.CamFront, "motorcycle", 0.92, timeProvider.GetUtcNow(), 0.26, 0.26, 0.37, 0.62) },
            Array.Empty<SensorSnapshot>());

        timeProvider.Advance(TimeSpan.FromSeconds(1));
        var decision = engine.Decide(
            new[] { CameraId.CamFront },
            new[] { CreateFinding(CameraId.CamFront, "motorcycle", 0.95, timeProvider.GetUtcNow(), 0.34, 0.34, 0.33, 0.58) },
            Array.Empty<SensorSnapshot>());

        Assert.Equal(SafetyRiskLevel.Danger, decision.RiskLevel);

        var assessment = Assert.Single(decision.CameraRiskAssessments);
        Assert.Equal(SafetyRiskLevel.Danger, assessment.RiskLevel);
        Assert.True(assessment.TrendScoreDelta > 0.12);
    }

    [Fact]
    public void Decide_WhenFrontCameraRiskExpiresBeyondWindow_ReturnsNormal()
    {
        var timeProvider = new ManualTimeProvider(new DateTimeOffset(2026, 6, 1, 10, 0, 0, TimeSpan.Zero));
        var engine = new SafetyDecisionEngine(timeProvider);

        engine.Decide(
            new[] { CameraId.CamFront },
            new[] { CreateFinding(CameraId.CamFront, "person", 0.95, timeProvider.GetUtcNow(), 0.55, 0.55) },
            Array.Empty<SensorSnapshot>());

        timeProvider.Advance(TimeSpan.FromSeconds(11));

        var decision = engine.Decide(
            new[] { CameraId.CamFront },
            Array.Empty<CameraFinding>(),
            Array.Empty<SensorSnapshot>());

        Assert.Equal(SafetyRiskLevel.Normal, decision.RiskLevel);

        var assessment = Assert.Single(decision.CameraRiskAssessments);
        Assert.Equal(SafetyRiskLevel.Normal, assessment.RiskLevel);
        Assert.Equal(0.0, assessment.CurrentScore, 6);
    }

    [Fact]
    public void Decide_WhenFaceCameraConfidenceIsHigh_ReturnsWarningWithoutTrendAssessment()
    {
        var timeProvider = new ManualTimeProvider(new DateTimeOffset(2026, 6, 1, 10, 0, 0, TimeSpan.Zero));
        var engine = new SafetyDecisionEngine(timeProvider);

        var decision = engine.Decide(
            new[] { CameraId.CamFace },
            new[] { CreateFinding(CameraId.CamFace, "fatigue", 0.92, timeProvider.GetUtcNow()) },
            Array.Empty<SensorSnapshot>());

        Assert.Equal(SafetyRiskLevel.Warning, decision.RiskLevel);
        Assert.Empty(decision.CameraRiskAssessments);
    }

    [Fact]
    public void Decide_WhenFaceCameraOnlyHasLandmarks_StaysNormal()
    {
        var timeProvider = new ManualTimeProvider(new DateTimeOffset(2026, 6, 1, 10, 0, 0, TimeSpan.Zero));
        var engine = new SafetyDecisionEngine(timeProvider);

        var decision = engine.Decide(
            new[] { CameraId.CamFace },
            new[] { CreateFinding(CameraId.CamFace, "face_landmarks_106", 1.0, timeProvider.GetUtcNow()) },
            Array.Empty<SensorSnapshot>());

        Assert.Equal(SafetyRiskLevel.Normal, decision.RiskLevel);
        Assert.Empty(decision.CameraRiskAssessments);
    }

    [Fact]
    public void Decide_WhenBackCameraRiskTrendsUp_ReturnsWarning()
    {
        var timeProvider = new ManualTimeProvider(new DateTimeOffset(2026, 6, 1, 10, 0, 0, TimeSpan.Zero));
        var engine = new SafetyDecisionEngine(timeProvider);

        engine.Decide(
            new[] { CameraId.CamBack },
            new[] { CreateFinding(CameraId.CamBack, "car", 0.65, timeProvider.GetUtcNow(), 0.18, 0.18) },
            Array.Empty<SensorSnapshot>());

        timeProvider.Advance(TimeSpan.FromSeconds(6));
        engine.Decide(
            new[] { CameraId.CamBack },
            new[] { CreateFinding(CameraId.CamBack, "car", 0.75, timeProvider.GetUtcNow(), 0.22, 0.22) },
            Array.Empty<SensorSnapshot>());

        timeProvider.Advance(TimeSpan.FromSeconds(1));
        var decision = engine.Decide(
            new[] { CameraId.CamBack },
            new[] { CreateFinding(CameraId.CamBack, "car", 0.82, timeProvider.GetUtcNow(), 0.28, 0.28) },
            Array.Empty<SensorSnapshot>());

        Assert.Equal(SafetyRiskLevel.Warning, decision.RiskLevel);

        var assessment = Assert.Single(decision.CameraRiskAssessments);
        Assert.Equal(CameraId.CamBack, assessment.CameraId);
        Assert.Equal(SafetyRiskLevel.Warning, assessment.RiskLevel);
        Assert.True(assessment.TrendScoreDelta > 0);
    }

    [Fact]
    public void Decide_WhenBackFisheyeCenterObjectApproaches_ReturnsDanger()
    {
        var timeProvider = new ManualTimeProvider(new DateTimeOffset(2026, 6, 1, 10, 0, 0, TimeSpan.Zero));
        var engine = new SafetyDecisionEngine(
            timeProvider,
            new Dictionary<CameraId, CameraRiskOptions>
            {
                [CameraId.CamBack] = new CameraRiskOptions(180.0, 1.0, 45.0, 0.18)
            });

        engine.Decide(
            new[] { CameraId.CamBack },
            new[] { CreateFinding(CameraId.CamBack, "motorcycle", 0.88, timeProvider.GetUtcNow(), 0.18, 0.18, 0.41, 0.56) },
            Array.Empty<SensorSnapshot>());

        timeProvider.Advance(TimeSpan.FromSeconds(6));
        engine.Decide(
            new[] { CameraId.CamBack },
            new[] { CreateFinding(CameraId.CamBack, "motorcycle", 0.92, timeProvider.GetUtcNow(), 0.28, 0.28, 0.36, 0.62) },
            Array.Empty<SensorSnapshot>());

        timeProvider.Advance(TimeSpan.FromSeconds(1));
        var decision = engine.Decide(
            new[] { CameraId.CamBack },
            new[] { CreateFinding(CameraId.CamBack, "motorcycle", 0.95, timeProvider.GetUtcNow(), 0.38, 0.34, 0.31, 0.60) },
            Array.Empty<SensorSnapshot>());

        Assert.Equal(SafetyRiskLevel.Danger, decision.RiskLevel);

        var assessment = Assert.Single(decision.CameraRiskAssessments);
        Assert.Equal(CameraId.CamBack, assessment.CameraId);
        Assert.Equal(SafetyRiskLevel.Danger, assessment.RiskLevel);
    }

    [Fact]
    public void Decide_WhenBackFisheyeEdgeObjectIsLarge_ReturnsWarningOnly()
    {
        var timeProvider = new ManualTimeProvider(new DateTimeOffset(2026, 6, 1, 10, 0, 0, TimeSpan.Zero));
        var engine = new SafetyDecisionEngine(
            timeProvider,
            new Dictionary<CameraId, CameraRiskOptions>
            {
                [CameraId.CamBack] = new CameraRiskOptions(180.0, 1.0, 45.0, 0.18)
            });

        var decision = engine.Decide(
            new[] { CameraId.CamBack },
            new[] { CreateFinding(CameraId.CamBack, "car", 0.96, timeProvider.GetUtcNow(), 0.36, 0.36, 0.00, 0.58) },
            Array.Empty<SensorSnapshot>());

        Assert.Equal(SafetyRiskLevel.Warning, decision.RiskLevel);

        var assessment = Assert.Single(decision.CameraRiskAssessments);
        Assert.Equal(CameraId.CamBack, assessment.CameraId);
        Assert.Equal(SafetyRiskLevel.Warning, assessment.RiskLevel);
        Assert.True(assessment.CurrentScore < 0.82);
    }

    [Fact]
    public void Decide_WhenOnlyTinyLowRiskObjectExists_StaysNormal()
    {
        var timeProvider = new ManualTimeProvider(new DateTimeOffset(2026, 6, 1, 10, 0, 0, TimeSpan.Zero));
        var engine = new SafetyDecisionEngine(timeProvider);

        var decision = engine.Decide(
            new[] { CameraId.CamFront },
            new[] { CreateFinding(CameraId.CamFront, "traffic light", 0.40, timeProvider.GetUtcNow(), 0.05, 0.05) },
            Array.Empty<SensorSnapshot>());

        Assert.Equal(SafetyRiskLevel.Normal, decision.RiskLevel);

        var assessment = Assert.Single(decision.CameraRiskAssessments);
        Assert.Equal(SafetyRiskLevel.Normal, assessment.RiskLevel);
        Assert.True(assessment.PeakScore < 0.1);
    }

    [Fact]
    public void Decide_WhenFrontCameraSeesManyObjectsOutsideCollisionCorridor_StaysNormal()
    {
        var timeProvider = new ManualTimeProvider(new DateTimeOffset(2026, 6, 1, 10, 0, 0, TimeSpan.Zero));
        var engine = new SafetyDecisionEngine(timeProvider);

        var decision = engine.Decide(
            new[] { CameraId.CamFront },
            new[]
            {
                CreateFinding(CameraId.CamFront, "person", 0.92, timeProvider.GetUtcNow(), 0.22, 0.26, 0.02, 0.64),
                CreateFinding(CameraId.CamFront, "motorcycle", 0.88, timeProvider.GetUtcNow(), 0.20, 0.24, 0.76, 0.62),
                CreateFinding(CameraId.CamFront, "car", 0.90, timeProvider.GetUtcNow(), 0.18, 0.18, 0.35, 0.20)
            },
            Array.Empty<SensorSnapshot>());

        Assert.Equal(SafetyRiskLevel.Normal, decision.RiskLevel);

        var assessment = Assert.Single(decision.CameraRiskAssessments);
        Assert.Equal(SafetyRiskLevel.Normal, assessment.RiskLevel);
        Assert.True(assessment.CurrentScore < 0.1);
    }

    [Fact]
    public void Decide_WhenFrontObjectMovesToLowerCenter_IncreasesPerspectiveRisk()
    {
        var timeProvider = new ManualTimeProvider(new DateTimeOffset(2026, 6, 1, 10, 0, 0, TimeSpan.Zero));
        var edgeEngine = new SafetyDecisionEngine(timeProvider);
        var centerEngine = new SafetyDecisionEngine(timeProvider);

        var edgeDecision = edgeEngine.Decide(
            new[] { CameraId.CamFront },
            new[] { CreateFinding(CameraId.CamFront, "person", 0.75, timeProvider.GetUtcNow(), 0.22, 0.22, 0.02, 0.20) },
            Array.Empty<SensorSnapshot>());
        var centerDecision = centerEngine.Decide(
            new[] { CameraId.CamFront },
            new[] { CreateFinding(CameraId.CamFront, "person", 0.75, timeProvider.GetUtcNow(), 0.22, 0.22, 0.39, 0.62) },
            Array.Empty<SensorSnapshot>());

        Assert.True(
            centerDecision.CameraRiskAssessments.Single().CurrentScore
                > edgeDecision.CameraRiskAssessments.Single().CurrentScore);
    }

    private static CameraFinding CreateFinding(
        CameraId cameraId,
        string label,
        double confidence,
        DateTimeOffset observedAt,
        double? boxWidth = null,
        double? boxHeight = null,
        double boxX = 0.2,
        double boxY = 0.2)
    {
        var boundingBox = boxWidth is null || boxHeight is null
            ? null
            : new CameraBoundingBox(boxX, boxY, boxWidth.Value, boxHeight.Value);

        return new CameraFinding(cameraId, label, confidence, observedAt, boundingBox);
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        private DateTimeOffset _utcNow;

        public ManualTimeProvider(DateTimeOffset utcNow)
        {
            _utcNow = utcNow;
        }

        public override DateTimeOffset GetUtcNow()
        {
            return _utcNow;
        }

        public void Advance(TimeSpan value)
        {
            _utcNow = _utcNow.Add(value);
        }
    }
}
