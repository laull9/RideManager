using OpenCvSharp;
using RideManager.Camera;
using Xunit;

namespace RideManager.Tests;

public sealed class OpenCvCameraSourceTests
{
    [Theory]
    [InlineData("MJPG")]
    [InlineData("yuyv")]
    public void TryGetPixelFormat_ParsesFourCharacterCode(string value)
    {
        var parsed = OpenCvCameraSource.TryGetPixelFormat(value, out var pixelFormat);

        Assert.True(parsed);
        Assert.Equal(value.ToUpperInvariant(), OpenCvCameraSource.FormatFourCc(pixelFormat));
    }

    [Theory]
    [InlineData("")]
    [InlineData("auto")]
    [InlineData("AUTO")]
    public void TryGetPixelFormat_AllowsDriverNegotiation(string value)
    {
        var parsed = OpenCvCameraSource.TryGetPixelFormat(value, out var pixelFormat);

        Assert.False(parsed);
        Assert.Equal(0, pixelFormat);
    }

    [Fact]
    public void TryGetPixelFormat_RejectsInvalidCode()
    {
        var exception = Assert.Throws<InvalidOperationException>(
            () => OpenCvCameraSource.TryGetPixelFormat("mjpeg", out _));

        Assert.Contains("four-character code", exception.Message);
    }

    [Fact]
    public void FormatFourCc_FormatsOpenCvCode()
    {
        Assert.Equal("MJPG", OpenCvCameraSource.FormatFourCc(FourCC.FromString("MJPG")));
    }
}
