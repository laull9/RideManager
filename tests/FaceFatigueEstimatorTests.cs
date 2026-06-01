using RideManager.Camera;
using Xunit;

namespace RideManager.Tests;

public sealed class FaceFatigueEstimatorTests
{
    [Fact]
    public void Estimate_WhenBothEyesAreOpen_ReturnsNormal()
    {
        var landmarks = CreateLandmarks(eyeVerticalOffset: 0.06);

        var result = FaceFatigueEstimator.Estimate(landmarks);

        Assert.Equal("fatigue_normal", result.Label);
        Assert.True(result.Confidence > 0.7);
    }

    [Fact]
    public void Estimate_WhenBothEyesAreClosed_ReturnsFatigue()
    {
        var landmarks = CreateLandmarks(eyeVerticalOffset: 0.01);

        var result = FaceFatigueEstimator.Estimate(landmarks);

        Assert.Equal("fatigue", result.Label);
        Assert.True(result.Confidence >= 0.8);
    }

    private static IReadOnlyList<CameraLandmark> CreateLandmarks(double eyeVerticalOffset)
    {
        var landmarks = Enumerable.Range(0, 106)
            .Select(_ => new CameraLandmark(0.5, 0.5))
            .ToArray();

        SetEye(
            landmarks,
            leftCorner: 35,
            rightCorner: 75,
            top: new[] { 41, 40, 42 },
            bottom: new[] { 36, 33, 37 },
            centerX: 0.35,
            centerY: 0.38,
            width: 0.2,
            verticalOffset: eyeVerticalOffset);
        SetEye(
            landmarks,
            leftCorner: 89,
            rightCorner: 93,
            top: new[] { 95, 94, 96 },
            bottom: new[] { 90, 88, 91 },
            centerX: 0.65,
            centerY: 0.38,
            width: 0.2,
            verticalOffset: eyeVerticalOffset);

        return landmarks;
    }

    private static void SetEye(
        CameraLandmark[] landmarks,
        int leftCorner,
        int rightCorner,
        IReadOnlyList<int> top,
        IReadOnlyList<int> bottom,
        double centerX,
        double centerY,
        double width,
        double verticalOffset)
    {
        landmarks[leftCorner] = new CameraLandmark(centerX - width / 2, centerY);
        landmarks[rightCorner] = new CameraLandmark(centerX + width / 2, centerY);

        for (var index = 0; index < top.Count; index++)
        {
            var x = centerX - width / 4 + index * width / 4;
            landmarks[top[index]] = new CameraLandmark(x, centerY - verticalOffset / 2);
            landmarks[bottom[index]] = new CameraLandmark(x, centerY + verticalOffset / 2);
        }
    }
}
