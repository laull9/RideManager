using RideManager.Actuators;
using RideManager.Core;
using RideManager.Utils;
using Xunit;

namespace RideManager.Tests;

public sealed class SystemSpeakerNotifierTests
{
    [Fact]
    public void SpeakerNotificationPlan_UsesDangerAssetForDanger()
    {
        var options = new ActuatorEndpointOptions(
            true,
            "assests",
            "warning.wav",
            "danger.wav",
            string.Empty,
            3.0);

        var plan = SpeakerNotificationPlan.Create(options, SafetyRiskLevel.Danger);

        Assert.NotNull(plan);
        Assert.Equal(Path.Combine("assests", "danger.wav"), plan.AssetPath);
    }

    [Fact]
    public void SpeakerNotificationPlan_UsesWarningAssetForWarning()
    {
        var options = new ActuatorEndpointOptions(
            true,
            "/tmp/audio",
            "warning.wav",
            "danger.wav",
            string.Empty,
            3.0);

        var plan = SpeakerNotificationPlan.Create(options, SafetyRiskLevel.Warning);

        Assert.NotNull(plan);
        Assert.Equal(Path.Combine("/tmp/audio", "warning.wav"), plan.AssetPath);
    }

    [Fact]
    public void SpeakerNotificationPlan_SkipsNormalRisk()
    {
        var options = new ActuatorEndpointOptions(true);

        var plan = SpeakerNotificationPlan.Create(options, SafetyRiskLevel.Normal);

        Assert.Null(plan);
    }
}
