using RideManager.Camera;
using RideManager.Core;
using RideManager.Sensors;
using Xunit;

namespace RideManager.Tests;

public sealed class SafetyDecisionEngineTests
{
    [Fact]
    public void Decide_WhenFrontCameraShowsStrongObstacle_ReturnsDangerAssessment()
    {
        var timeProvider = new ManualTimeProvider(new DateTimeOffset(2026, 6, 1, 10, 0, 0, TimeSpan.Zero));
        var engine = new SafetyDecisionEngine(timeProvider);

        var decision = engine.Decide(
            new[] { CameraId.CamFront },
            new[] { CreateFinding(CameraId.CamFront, "person", 0.95, timeProvider.GetUtcNow(), 0.55, 0.55) },
            Array.Empty<SensorSnapshot>());

        Assert.Equal(SafetyRiskLevel.Danger, decision.RiskLevel);

        var assessment = Assert.Single(decision.CameraRiskAssessments);
        Assert.Equal(CameraId.CamFront, assessment.CameraId);
        Assert.Equal(SafetyRiskLevel.Danger, assessment.RiskLevel);
        Assert.Contains(assessment.LeadingLabels, label => label.Equals("person", StringComparison.OrdinalIgnoreCase));
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
    public void Decide_WhenBackCameraRiskTrendsUp_ReturnsWarning()
    {
        var timeProvider = new ManualTimeProvider(new DateTimeOffset(2026, 6, 1, 10, 0, 0, TimeSpan.Zero));
        var engine = new SafetyDecisionEngine(timeProvider);

        engine.Decide(
            new[] { CameraId.CamBack },
            new[] { CreateFinding(CameraId.CamBack, "car", 0.65, timeProvider.GetUtcNow(), 0.18, 0.18) },
            Array.Empty<SensorSnapshot>());

        timeProvider.Advance(TimeSpan.FromSeconds(1));
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

    private static CameraFinding CreateFinding(
        CameraId cameraId,
        string label,
        double confidence,
        DateTimeOffset observedAt,
        double? boxWidth = null,
        double? boxHeight = null)
    {
        var boundingBox = boxWidth is null || boxHeight is null
            ? null
            : new CameraBoundingBox(0.2, 0.2, boxWidth.Value, boxHeight.Value);

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