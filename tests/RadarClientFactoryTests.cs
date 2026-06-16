using RideManager.Sensors;
using RideManager.Utils;
using Xunit;

namespace RideManager.Tests;

public sealed class RadarClientFactoryTests
{
    [Fact]
    public void Create_UsesPythonPrimaryForNativeBleWhenLinuxPolicyIsEnabled()
    {
        var client = RadarClientFactory.Create(CreateRadarOptions(), forceSimulated: false, usePythonAsPrimary: true);

        Assert.IsType<PythonRadarClient>(client);
    }

    [Fact]
    public void Create_UsesPythonPrimaryOnLinuxEvenWhenFallbackIsDisabled()
    {
        var client = RadarClientFactory.Create(
            CreateRadarOptions() with { PythonFallbackEnabled = false },
            forceSimulated: false,
            usePythonAsPrimary: true);

        Assert.IsType<PythonRadarClient>(client);
    }

    [Fact]
    public void Create_UsesFallbackWrapperForNativeBleWhenLinuxPolicyIsDisabled()
    {
        var client = RadarClientFactory.Create(CreateRadarOptions(), forceSimulated: false, usePythonAsPrimary: false);

        Assert.IsType<FallbackRadarClient>(client);
    }

    private static SensorEndpointOptions CreateRadarOptions()
    {
        return new SensorEndpointOptions(
            true,
            "bluetooth",
            string.Empty,
            "EVADAR-C6",
            "0000ad01-0000-1000-8000-00805f9b34fb",
            "0000ad02-0000-1000-8000-00805f9b34fb",
            "0000ad03-0000-1000-8000-00805f9b34fb",
            "0000ad04-0000-1000-8000-00805f9b34fb",
            true,
            true,
            12.0,
            10.0,
            2.0,
            true,
            "python3",
            "scripts/ble_radar_stream.py",
            8.0,
            2.0);
    }
}
