using OpenCvSharp;
using RideManager.Utils;

namespace RideManager.Camera;

/// <summary>
/// 提供可运行的合成摄像头源。
/// </summary>
public sealed class SimulatedCameraSource : ICameraSource
{
    private readonly CameraOptions _options;
    private long _frameIndex;

    /// <summary>
    /// 创建模拟摄像头源。
    /// </summary>
    public SimulatedCameraSource(CameraOptions options)
    {
        _options = options;
    }

    /// <summary>
    /// 获取合成源丢帧数。
    /// </summary>
    public long DroppedFrames => 0;

    /// <summary>
    /// 读取一帧带时间与摄像头标识的合成图像。
    /// </summary>
    public Task<CameraFrame?> ReadLatestAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var width = Math.Max(320, _options.Width);
        var height = Math.Max(240, _options.Height);
        var frameIndex = Interlocked.Increment(ref _frameIndex);
        var image = new Mat(height, width, MatType.CV_8UC3, CameraColor(_options.Id, frameIndex));

        Cv2.PutText(
            image,
            $"{_options.Id} synthetic #{frameIndex}",
            new Point(24, 48),
            HersheyFonts.HersheySimplex,
            1.0,
            Scalar.White,
            2);

        Cv2.Circle(
            image,
            new Point((int)(width * ((frameIndex % 120) / 120.0)), height / 2),
            40,
            Scalar.White,
            -1);

        return Task.FromResult<CameraFrame?>(
            new CameraFrame(_options.Id, DateTimeOffset.UtcNow, image));
    }

    /// <summary>
    /// 释放模拟源。
    /// </summary>
    public ValueTask DisposeAsync()
    {
        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// 按摄像头类型生成不同背景色。
    /// </summary>
    private static Scalar CameraColor(CameraId cameraId, long frameIndex)
    {
        var pulse = 40 + frameIndex % 80;
        return cameraId switch
        {
            CameraId.CamFace => new Scalar(60, pulse, 130),
            CameraId.CamBack => new Scalar(pulse, 90, 60),
            _ => new Scalar(80, 80, pulse)
        };
    }
}
