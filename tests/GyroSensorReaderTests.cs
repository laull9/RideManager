using RideManager.Sensors;
using Xunit;

namespace RideManager.Tests;

public sealed class GyroSensorReaderTests
{
    [Fact]
    public void TryParseSample_ParsesCsvSixAxisSample()
    {
        var observedAt = DateTimeOffset.Parse("2026-07-08T10:00:00Z");

        var parsed = GyroSensorReader.TryParseSample("1.1,-2.2,3.3,0.01,0.02,9.81", observedAt, out var snapshot);

        Assert.True(parsed);
        Assert.Equal("GYRO", snapshot.SensorName);
        Assert.Equal(1.1, snapshot.Values["roll"], 6);
        Assert.Equal(-2.2, snapshot.Values["pitch"], 6);
        Assert.Equal(3.3, snapshot.Values["yaw"], 6);
        Assert.Equal(0.01, snapshot.Values["accel_x"], 6);
        Assert.Equal(0.02, snapshot.Values["accel_y"], 6);
        Assert.Equal(9.81, snapshot.Values["accel_z"], 6);
    }

    [Fact]
    public void TryParseSample_ParsesKeyValueAliases()
    {
        var observedAt = DateTimeOffset.Parse("2026-07-08T10:00:00Z");

        var parsed = GyroSensorReader.TryParseSample(
            "gx=1 gy=2 gz=3 ax=4 ay=5 az=6",
            observedAt,
            out var snapshot);

        Assert.True(parsed);
        Assert.Equal(1.0, snapshot.Values["roll"], 6);
        Assert.Equal(2.0, snapshot.Values["pitch"], 6);
        Assert.Equal(3.0, snapshot.Values["yaw"], 6);
        Assert.Equal(4.0, snapshot.Values["accel_x"], 6);
        Assert.Equal(5.0, snapshot.Values["accel_y"], 6);
        Assert.Equal(6.0, snapshot.Values["accel_z"], 6);
    }

    [Fact]
    public void TryParseSample_RejectsIncompleteSample()
    {
        var parsed = GyroSensorReader.TryParseSample(
            "roll=1 pitch=2 yaw=3",
            DateTimeOffset.UtcNow,
            out _);

        Assert.False(parsed);
    }
}
