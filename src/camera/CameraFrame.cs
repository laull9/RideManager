using OpenCvSharp;

namespace RideManager.Camera;

/// <summary>
/// 表示摄像头采集到的一帧图像数据。
/// </summary>
public sealed class CameraFrame : IDisposable
{
    /// <summary>
    /// 创建一帧 BGR 图像。
    /// </summary>
    public CameraFrame(CameraId cameraId, DateTimeOffset capturedAt, Mat image)
    {
        CameraId = cameraId;
        CapturedAt = capturedAt;
        Image = image;
    }

    /// <summary>
    /// 获取当前帧所属摄像头。
    /// </summary>
    public CameraId CameraId { get; }

    /// <summary>
    /// 获取采集时间。
    /// </summary>
    public DateTimeOffset CapturedAt { get; }

    /// <summary>
    /// 获取 OpenCV BGR 图像，所有权归当前帧。
    /// </summary>
    public Mat Image { get; }

    /// <summary>
    /// 获取图像宽度。
    /// </summary>
    public int Width => Image.Width;

    /// <summary>
    /// 获取图像高度。
    /// </summary>
    public int Height => Image.Height;

    /// <summary>
    /// 释放底层 OpenCV 图像。
    /// </summary>
    public void Dispose()
    {
        Image.Dispose();
    }
}
