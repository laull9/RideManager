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

    [Fact]
    public void SpeakerPlayerResolver_UsesAssetPlaceholderForFfmpegCommand()
    {
        var player = SpeakerPlayerResolver.Resolve("ffmpeg -re -i {asset} -ac 2 -ar 48000 -sample_fmt s16 -f alsa plughw:1,0");

        Assert.NotNull(player);
        Assert.Equal("ffmpeg", player.Command);
        Assert.Equal(new[] { "-re", "-i" }, player.ArgumentsBeforePath);
        Assert.Equal(new[] { "-ac", "2", "-ar", "48000", "-sample_fmt", "s16", "-f", "alsa", "plughw:1,0" }, player.ArgumentsAfterPath);
    }

    [Fact]
    public void SpeakerPlayerResolver_KeepsLegacyConfiguredCommandAsExecutable()
    {
        var player = SpeakerPlayerResolver.Resolve("/usr/bin/aplay");

        Assert.NotNull(player);
        Assert.Equal("/usr/bin/aplay", player.Command);
        Assert.Empty(player.ArgumentsBeforePath);
        Assert.Empty(player.ArgumentsAfterPath);
    }
}
