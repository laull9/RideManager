using OpenCvSharp;
using RideManager.Models;

namespace RideManager.Camera;

/// <summary>
/// 为道路/目标检测模型创建 letterbox RGB NCHW 输入张量。
/// </summary>
internal static class ModelInputTensorFactory
{
    private const double PadValue = 114.0;

    /// <summary>
    /// 将 BGR 图像 letterbox、转换为 RGB，并归一化为 NCHW float32 张量。
    /// </summary>
    public static NativeFloatTensor CreateRgbNchwTensor(Mat source, int targetWidth, int targetHeight)
    {
        var tensor = new NativeFloatTensor(3 * targetWidth * targetHeight);
        FillRgbNchwTensor(source, targetWidth, targetHeight, tensor.Span);
        return tensor;
    }

    /// <summary>
    /// 将 BGR 图像写入复用的 RGB NCHW float32 张量。
    /// </summary>
    public static void FillRgbNchwTensor(Mat source, int targetWidth, int targetHeight, Span<float> tensor)
    {
        if (tensor.Length < 3 * targetWidth * targetHeight)
        {
            throw new ArgumentException("Tensor buffer is smaller than the requested image shape.", nameof(tensor));
        }

        if (source.Width == targetWidth && source.Height == targetHeight)
        {
            FillRgbNchwTensorFromBgr(source, tensor);
            return;
        }

        using var letterboxed = CreateLetterboxedImage(source, targetWidth, targetHeight);
        FillRgbNchwTensorFromBgr(letterboxed, tensor);
    }

    /// <summary>
    /// 按 YOLO 常用 letterbox 方式缩放并填充，保持检测框坐标可逆。
    /// </summary>
    private static Mat CreateLetterboxedImage(Mat source, int targetWidth, int targetHeight)
    {
        var scale = Math.Min((double)targetWidth / source.Width, (double)targetHeight / source.Height);
        var resizedWidth = Math.Max(1, (int)(source.Width * scale));
        var resizedHeight = Math.Max(1, (int)(source.Height * scale));
        var padX = (targetWidth - resizedWidth) / 2;
        var padY = (targetHeight - resizedHeight) / 2;

        if (resizedWidth == targetWidth && resizedHeight == targetHeight)
        {
            var resized = new Mat();
            Cv2.Resize(source, resized, new Size(targetWidth, targetHeight), 0, 0, InterpolationFlags.Linear);
            return resized;
        }

        var output = new Mat(targetHeight, targetWidth, MatType.CV_8UC3, new Scalar(PadValue, PadValue, PadValue));
        using var roi = new Mat(output, new Rect(padX, padY, resizedWidth, resizedHeight));
        Cv2.Resize(source, roi, new Size(resizedWidth, resizedHeight), 0, 0, InterpolationFlags.Linear);
        return output;
    }

    /// <summary>
    /// 将 BGR uint8 图像直接转换为 RGB NCHW float32，避免额外 RGB Mat。
    /// </summary>
    private static unsafe void FillRgbNchwTensorFromBgr(Mat bgr, Span<float> tensor)
    {
        var height = bgr.Rows;
        var width = bgr.Cols;
        var channelSize = height * width;

        for (var y = 0; y < height; y++)
        {
            var row = (byte*)bgr.Ptr(y);
            for (var x = 0; x < width; x++)
            {
                var pixelIndex = y * width + x;
                var sourceIndex = x * 3;
                tensor[pixelIndex] = row[sourceIndex + 2] / 255f;
                tensor[channelSize + pixelIndex] = row[sourceIndex + 1] / 255f;
                tensor[channelSize * 2 + pixelIndex] = row[sourceIndex] / 255f;
            }
        }
    }
}
