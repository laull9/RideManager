using System.Text.Json;
using RideManager.Camera;
using RideManager.Core;
using RideManager.Sensors;
using RideManager.Utils;
using Xunit;

namespace RideManager.Tests;

public sealed class RideManagerJsonContextTests
{
    [Fact]
    public void DatabasePayloadTypes_SerializeWithoutReflection()
    {
        var observedAt = DateTimeOffset.Parse("2026-06-16T10:20:30Z");
        var finding = new CameraFinding(
            CameraId.CamFront,
            "vehicle",
            0.92,
            observedAt,
            new CameraBoundingBox(0.1, 0.2, 0.3, 0.4),
            new CameraSegmentationMask("lane_line", 2, 2, new byte[] { 0, 1, 1, 0 }),
            new[] { new CameraLandmark(0.45, 0.55) });
        var snapshot = new SensorSnapshot(
            "radar",
            observedAt,
            new Dictionary<string, double> { ["heart_rate"] = 72.5 });
        var frame = new CameraFrameState(
            CameraId.CamFront,
            observedAt,
            1280,
            720,
            new CameraPipelineMetrics(1.0, 2.0, 3.0, 6.0, 30.0, 0));
        var decision = new SafetyDecision(
            SafetyRiskLevel.Warning,
            observedAt,
            new[] { finding },
            new[] { snapshot },
            new[] { new CameraRiskAssessment(CameraId.CamFront, SafetyRiskLevel.Warning, 10.0, 1, 0.8, 0.7, 0.4, 0.3, 0.8, new[] { "vehicle" }) },
            new[] { frame });

        var decisionJson = JsonSerializer.Serialize(decision, RideManagerJsonContext.Default.SafetyDecision);
        var frameJson = JsonSerializer.Serialize(frame, RideManagerJsonContext.Default.CameraFrameState);
        var findingJson = JsonSerializer.Serialize(finding, RideManagerJsonContext.Default.CameraFinding);
        var valuesJson = JsonSerializer.Serialize(snapshot.Values, RideManagerJsonContext.Default.IReadOnlyDictionaryStringDouble);

        Assert.Contains("Warning", decisionJson);
        Assert.Contains("captureLatencyMs", frameJson);
        Assert.Contains("vehicle", findingJson);
        Assert.Contains("heart_rate", valuesJson);
    }
}
