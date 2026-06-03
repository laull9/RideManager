using OpenCvSharp;
using RideManager.Models;

namespace RideManager.Camera;

/// <summary>
/// 串联 YuNet 最大人脸检测、PFLD 关键点和单帧疲劳估计。
/// </summary>
public sealed class FaceCameraAnalyzer : ICameraAnalyzer, IDisposable
{
    internal const string FaceDetectorModelName = "face_detection_yunet_2023mar.onnx";
    private const int YuNetInputWidth = 640;
    private const int YuNetInputHeight = 640;
    private const double FaceCropScale = 1.25;

    private readonly CameraId _cameraId;
    private readonly IInferenceEngine _faceDetectorEngine;
    private readonly IInferenceEngine _landmarkEngine;
    private readonly int _landmarkInputWidth;
    private readonly int _landmarkInputHeight;

    /// <summary>
    /// 获取 YuNet 统一推理引擎，供链路测试确认后端选择。
    /// </summary>
    internal IInferenceEngine FaceDetectorEngine => _faceDetectorEngine;

    /// <summary>
    /// 获取 PFLD 统一推理引擎，供链路测试确认后端选择。
    /// </summary>
    internal IInferenceEngine LandmarkEngine => _landmarkEngine;

    /// <summary>
    /// 创建面部摄像头分析器。
    /// </summary>
    public FaceCameraAnalyzer(
        CameraId cameraId,
        IInferenceEngine faceDetectorEngine,
        IInferenceEngine landmarkEngine,
        int landmarkInputWidth,
        int landmarkInputHeight)
    {
        _cameraId = cameraId;
        _faceDetectorEngine = faceDetectorEngine;
        _landmarkEngine = landmarkEngine;
        _landmarkInputWidth = Math.Max(1, landmarkInputWidth);
        _landmarkInputHeight = Math.Max(1, landmarkInputHeight);
    }

    /// <summary>
    /// 对整帧先检测最大人脸，再在人脸 ROI 上运行 PFLD 并输出疲劳结果。
    /// </summary>
    public async Task<IReadOnlyList<CameraFinding>> AnalyzeAsync(ProcessedFrame frame, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var faceResult = await DetectLargestFaceAsync(frame, cancellationToken);
        if (faceResult.Face is null)
        {
            return new[] { CreateStatusFinding(faceResult.StatusLabel, frame.CapturedAt) };
        }

        var face = faceResult.Face.Value;
        using var faceCrop = CropFace(frame.PreviewImage, face.Crop);
        if (faceCrop.Empty())
        {
            return new[] { CreateStatusFinding("face_crop_empty", frame.CapturedAt) };
        }

        using var landmarkTensor = CreateLandmarkTensor(faceCrop);
        var landmarkOutput = await _landmarkEngine.RunAsync(
            new InferenceInput(
                _cameraId.ToString(),
                landmarkTensor,
                new[] { 1, 3, _landmarkInputHeight, _landmarkInputWidth },
                (int)Math.Round(face.Crop.Size),
                (int)Math.Round(face.Crop.Size)),
            cancellationToken);
        var landmarks = (landmarkOutput.Landmarks ?? Array.Empty<InferenceLandmark>())
            .Select(landmark => MapLandmarkToFrame(landmark, face.Crop, frame.OriginalWidth, frame.OriginalHeight))
            .ToArray();
        if (landmarks.Length == 0)
        {
            return new[] { CreateStatusFinding("pfld:landmarks_missing", frame.CapturedAt) };
        }

        var fatigue = FaceFatigueEstimator.Estimate(landmarks);
        return new[]
        {
            new CameraFinding(
                _cameraId,
                "face_landmarks_106",
                face.Confidence,
                frame.CapturedAt,
                face.Box,
                Landmarks: landmarks),
            new CameraFinding(
                _cameraId,
                fatigue.Label,
                fatigue.Confidence,
                frame.CapturedAt,
                face.Box)
        };
    }

    /// <summary>
    /// 释放 YuNet 和 PFLD 底层资源。
    /// </summary>
    public void Dispose()
    {
        if (_faceDetectorEngine is IDisposable faceDetectorDisposable)
        {
            faceDetectorDisposable.Dispose();
        }

        if (_landmarkEngine is IDisposable disposable)
        {
            disposable.Dispose();
        }
    }

    /// <summary>
    /// 使用 YuNet 检测当前帧并选择面积最大的单张人脸。
    /// </summary>
    private async Task<FaceDetectionResult> DetectLargestFaceAsync(ProcessedFrame frame, CancellationToken cancellationToken)
    {
        using var resized = new Mat();
        Cv2.Resize(frame.PreviewImage, resized, new Size(YuNetInputWidth, YuNetInputHeight), 0, 0, InterpolationFlags.Linear);
        using var inputTensor = CreateYuNetInput(resized);
        var output = await _faceDetectorEngine.RunAsync(
            new InferenceInput(
                _cameraId.ToString(),
                inputTensor,
                new[] { 1, 3, YuNetInputHeight, YuNetInputWidth },
                frame.OriginalWidth,
                frame.OriginalHeight),
            cancellationToken);
        var selected = (output.Detections ?? Array.Empty<InferenceDetection>())
            .Where(detection => detection.Label.Equals("face", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(detection => detection.Width * detection.Height)
            .FirstOrDefault();
        if (selected is null)
        {
            var statusLabel = output.Labels.FirstOrDefault()
                ?? $"yunet:{FaceDetectorModelName}:face_missing";
            return new FaceDetectionResult(null, statusLabel);
        }

        var left = selected.X * frame.OriginalWidth;
        var top = selected.Y * frame.OriginalHeight;
        var width = selected.Width * frame.OriginalWidth;
        var height = selected.Height * frame.OriginalHeight;
        return new FaceDetectionResult(
            new FaceDetection(
                new CameraBoundingBox(selected.X, selected.Y, selected.Width, selected.Height),
                CreateSquareCrop(left, top, width, height),
                selected.Confidence),
            string.Empty);
    }

    /// <summary>
    /// 生成围绕人脸框的正方形扩张裁剪区域。
    /// </summary>
    private static FaceCrop CreateSquareCrop(double x, double y, double width, double height)
    {
        var size = Math.Max(width, height) * FaceCropScale;
        var centerX = x + width / 2.0;
        var centerY = y + height / 2.0;
        return new FaceCrop(centerX - size / 2.0, centerY - size / 2.0, size);
    }

    /// <summary>
    /// 从原图裁剪人脸正方形区域，越界部分用黑边补齐。
    /// </summary>
    private static Mat CropFace(Mat image, FaceCrop crop)
    {
        var left = (int)Math.Floor(crop.Left);
        var top = (int)Math.Floor(crop.Top);
        var size = Math.Max(1, (int)Math.Ceiling(crop.Size));
        var right = left + size;
        var bottom = top + size;

        var sourceLeft = Math.Clamp(left, 0, image.Width);
        var sourceTop = Math.Clamp(top, 0, image.Height);
        var sourceRight = Math.Clamp(right, 0, image.Width);
        var sourceBottom = Math.Clamp(bottom, 0, image.Height);
        if (sourceRight <= sourceLeft || sourceBottom <= sourceTop)
        {
            return new Mat();
        }

        using var roi = new Mat(image, new Rect(sourceLeft, sourceTop, sourceRight - sourceLeft, sourceBottom - sourceTop));
        var padLeft = Math.Max(0, sourceLeft - left);
        var padTop = Math.Max(0, sourceTop - top);
        var padRight = Math.Max(0, right - sourceRight);
        var padBottom = Math.Max(0, bottom - sourceBottom);

        if (padLeft == 0 && padTop == 0 && padRight == 0 && padBottom == 0)
        {
            return roi.Clone();
        }

        var output = new Mat();
        Cv2.CopyMakeBorder(roi, output, padTop, padBottom, padLeft, padRight, BorderTypes.Constant, Scalar.Black);
        return output;
    }

    /// <summary>
    /// 创建 PFLD 人脸 ROI native 输入张量。
    /// </summary>
    private NativeFloatTensor CreateLandmarkTensor(Mat faceCrop)
    {
        using var resized = new Mat();
        Cv2.Resize(faceCrop, resized, new Size(_landmarkInputWidth, _landmarkInputHeight), 0, 0, InterpolationFlags.Linear);
        var tensor = new NativeFloatTensor(3 * _landmarkInputWidth * _landmarkInputHeight);
        FillBgrNchwTensor(resized, tensor.Span);
        return tensor;
    }

    /// <summary>
    /// 将 PFLD ROI 关键点映射回整帧归一化坐标。
    /// </summary>
    private CameraLandmark MapLandmarkToFrame(
        InferenceLandmark landmark,
        FaceCrop crop,
        int frameWidth,
        int frameHeight)
    {
        var x = (crop.Left + landmark.X * crop.Size) / frameWidth;
        var y = (crop.Top + landmark.Y * crop.Size) / frameHeight;
        return new CameraLandmark(Math.Clamp(x, 0.0, 1.0), Math.Clamp(y, 0.0, 1.0));
    }

    /// <summary>
    /// 将 BGR uint8 图像转换为 NCHW float32 / 255。
    /// </summary>
    private static unsafe void FillBgrNchwTensor(Mat bgr, Span<float> tensor)
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
                tensor[pixelIndex] = row[sourceIndex] / 255f;
                tensor[channelSize + pixelIndex] = row[sourceIndex + 1] / 255f;
                tensor[channelSize * 2 + pixelIndex] = row[sourceIndex + 2] / 255f;
            }
        }
    }

    /// <summary>
    /// 创建 YuNet 整帧输入，布局为 BGR NCHW float32。
    /// </summary>
    private static unsafe NativeFloatTensor CreateYuNetInput(Mat bgr)
    {
        var tensor = new NativeFloatTensor(3 * bgr.Width * bgr.Height);
        var span = tensor.Span;
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
                span[pixelIndex] = row[sourceIndex];
                span[channelSize + pixelIndex] = row[sourceIndex + 1];
                span[channelSize * 2 + pixelIndex] = row[sourceIndex + 2];
            }
        }

        return tensor;
    }

    /// <summary>
    /// 创建诊断状态 finding。
    /// </summary>
    private CameraFinding CreateStatusFinding(string label, DateTimeOffset capturedAt)
    {
        return new CameraFinding(_cameraId, label, 0.0, capturedAt);
    }

    /// <summary>
    /// 表示 YuNet 最大人脸检测结果。
    /// </summary>
    private readonly record struct FaceDetection(CameraBoundingBox Box, FaceCrop Crop, double Confidence);

    /// <summary>
    /// 表示 YuNet 检测结果或可展示的诊断状态。
    /// </summary>
    private readonly record struct FaceDetectionResult(FaceDetection? Face, string StatusLabel);

    /// <summary>
    /// 表示可越界的正方形人脸裁剪区域。
    /// </summary>
    private readonly record struct FaceCrop(double Left, double Top, double Size);

}
