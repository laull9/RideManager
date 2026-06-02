using OpenCvSharp;
using RideManager.Models;
using RideManager.Utils;

namespace RideManager.Camera;

/// <summary>
/// 提供 OpenCV 图像预处理封装。
/// </summary>
public sealed class OpenCvFramePreprocessor : IFramePreprocessor
{
    private const double PadValue = 114.0;

    private readonly CameraId _cameraId;
    private readonly int _targetWidth;
    private readonly int _targetHeight;

    /// <summary>
    /// 创建指定摄像头的预处理器。
    /// </summary>
    public OpenCvFramePreprocessor(CameraOptions options)
    {
        _cameraId = options.Id;
        _targetWidth = Math.Max(1, options.InputWidth);
        _targetHeight = Math.Max(1, options.InputHeight);
    }

    /// <summary>
    /// 将 BGR 图像 letterbox、转换为 RGB，并归一化为 NCHW float32 张量。
    /// </summary>
    public ValueTask<ProcessedFrame> ProcessAsync(CameraFrame frame, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        using var letterboxed = CreateLetterboxedImage(frame.Image);

        using var rgb = new Mat();
        Cv2.CvtColor(letterboxed, rgb, ColorConversionCodes.BGR2RGB);

        var tensor = new NativeFloatTensor(3 * _targetWidth * _targetHeight);
        FillNchwTensor(rgb, tensor.Span);

        return ValueTask.FromResult(new ProcessedFrame(
            _cameraId,
            frame.CapturedAt,
            tensor,
            new[] { 1, 3, _targetHeight, _targetWidth },
            frame.Width,
            frame.Height,
            frame.Image.Clone()));
    }

    /// <summary>
    /// 按 YOLO 常用 letterbox 方式缩放并填充，保持检测框坐标可逆。
    /// </summary>
    private Mat CreateLetterboxedImage(Mat source)
    {
        var scale = Math.Min((double)_targetWidth / source.Width, (double)_targetHeight / source.Height);
        var resizedWidth = Math.Max(1, (int)(source.Width * scale));
        var resizedHeight = Math.Max(1, (int)(source.Height * scale));
        var padX = (_targetWidth - resizedWidth) / 2;
        var padY = (_targetHeight - resizedHeight) / 2;

        var output = new Mat(_targetHeight, _targetWidth, MatType.CV_8UC3, new Scalar(PadValue, PadValue, PadValue));
        using var resized = new Mat();
        Cv2.Resize(source, resized, new Size(resizedWidth, resizedHeight), 0, 0, InterpolationFlags.Linear);

        using var roi = new Mat(output, new Rect(padX, padY, resizedWidth, resizedHeight));
        resized.CopyTo(roi);
        return output;
    }

    /// <summary>
    /// 将 RGB uint8 图像转换为 NCHW float32。
    /// </summary>
    private static unsafe void FillNchwTensor(Mat rgb, Span<float> tensor)
    {
        var height = rgb.Rows;
        var width = rgb.Cols;
        var channelSize = height * width;

        for (var y = 0; y < height; y++)
        {
            var row = (byte*)rgb.Ptr(y);
            for (var x = 0; x < width; x++)
            {
                var pixelIndex = y * width + x;
                var sourceIndex = x * 3;
                tensor[pixelIndex] = row[sourceIndex] / 255f;
                tensor[channelSize + pixelIndex] = row[sourceIndex + 1] / 255f;
                tensor[channelSize * 2 + pixelIndex] = row[sourceIndex + 2] / 255f;
            }
        }
    }
}
