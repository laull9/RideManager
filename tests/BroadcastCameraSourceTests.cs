using System.Threading.Channels;
using OpenCvSharp;
using RideManager.Camera;
using Xunit;

namespace RideManager.Tests;

public sealed class BroadcastCameraSourceTests
{
    [Fact]
    public async Task ReadersReceiveIndependentFramesWithTheirOwnCameraIds()
    {
        var source = new ChannelCameraSource();
        var broadcast = new BroadcastCameraSource(source);
        await using var frontReader = broadcast.CreateReader(CameraId.CamFront);
        await using var faceReader = broadcast.CreateReader(CameraId.CamFace);

        source.Publish(new CameraFrame(
            CameraId.CamFront,
            DateTimeOffset.UtcNow,
            new Mat(24, 32, MatType.CV_8UC3, Scalar.White)));

        using var frontFrame = await ReadFrameAsync(frontReader);
        using var faceFrame = await ReadFrameAsync(faceReader);

        Assert.Equal(CameraId.CamFront, frontFrame.CameraId);
        Assert.Equal(CameraId.CamFace, faceFrame.CameraId);
        Assert.Equal(frontFrame.CapturedAt, faceFrame.CapturedAt);
        Assert.NotSame(frontFrame.Image, faceFrame.Image);
    }

    private static async Task<CameraFrame> ReadFrameAsync(ICameraSource source)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        while (true)
        {
            var frame = await source.ReadLatestAsync(timeout.Token);
            if (frame is not null)
            {
                return frame;
            }

            await Task.Delay(5, timeout.Token);
        }
    }

    private sealed class ChannelCameraSource : ICameraSource
    {
        private readonly Channel<CameraFrame> _frames = Channel.CreateUnbounded<CameraFrame>();

        public long DroppedFrames => 0;

        public async Task<CameraFrame?> ReadLatestAsync(CancellationToken cancellationToken)
        {
            return await _frames.Reader.ReadAsync(cancellationToken);
        }

        public ValueTask DisposeAsync()
        {
            _frames.Writer.TryComplete();
            return ValueTask.CompletedTask;
        }

        public void Publish(CameraFrame frame)
        {
            Assert.True(_frames.Writer.TryWrite(frame));
        }
    }
}
