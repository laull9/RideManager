using OpenCvSharp;
using RideManager.Models;

namespace RideManager.Camera;

/// <summary>
/// 表示预处理后可送入模型推理的图像帧。
/// </summary>
public sealed class ProcessedFrame : IDisposable
{
    /// <summary>
    /// 创建预处理结果。
    /// </summary>
    public ProcessedFrame(
        CameraId cameraId,
        DateTimeOffset capturedAt,
        NativeFloatTensor tensor,
        IReadOnlyList<int> tensorDimensions,
        int originalWidth,
        int originalHeight,
        Mat previewImage,
        bool ownsPreviewImage = true)
    {
        CameraId = cameraId;
        CapturedAt = capturedAt;
        Tensor = tensor;
        TensorDimensions = tensorDimensions;
        OriginalWidth = originalWidth;
        OriginalHeight = originalHeight;
        PreviewImage = previewImage;
        OwnsPreviewImage = ownsPreviewImage;
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
    /// 获取模型输入张量 native 缓冲区。
    /// </summary>
    public NativeFloatTensor Tensor { get; }

    /// <summary>
    /// 获取模型输入张量数据，布局为 NCHW float32。
    /// </summary>
    public Memory<float> TensorData => Tensor.Memory;

    /// <summary>
    /// 获取模型输入张量 native 首地址。
    /// </summary>
    public IntPtr TensorDataPointer => Tensor.Pointer;

    /// <summary>
    /// 获取模型输入张量维度。
    /// </summary>
    public IReadOnlyList<int> TensorDimensions { get; }

    /// <summary>
    /// 获取模型输入对应的原始图像宽度。
    /// </summary>
    public int OriginalWidth { get; }

    /// <summary>
    /// 获取模型输入对应的原始图像高度。
    /// </summary>
    public int OriginalHeight { get; }

    /// <summary>
    /// 获取用于 live 显示的 BGR 图像，所有权归当前预处理结果。
    /// </summary>
    public Mat PreviewImage { get; }

    /// <summary>
    /// 获取当前对象是否负责释放预览图。
    /// </summary>
    private bool OwnsPreviewImage { get; }

    /// <summary>
    /// 释放底层 OpenCV 图像。
    /// </summary>
    public void Dispose()
    {
        Tensor.Dispose();
        if (OwnsPreviewImage)
        {
            PreviewImage.Dispose();
        }
    }
}
