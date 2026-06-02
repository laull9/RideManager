using OpenCvSharp;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using RideManager.Models;

namespace RideManager.Camera;

/// <summary>
/// 串联 YuNet 最大人脸检测、PFLD 关键点和单帧疲劳估计。
/// </summary>
public sealed class FaceCameraAnalyzer : ICameraAnalyzer, IDisposable
{
    private const string FaceDetectorModelName = "face_detection_yunet_2023mar.onnx";
    private const int YuNetInputWidth = 640;
    private const int YuNetInputHeight = 640;
    private const double FaceCropScale = 1.25;
    private const double NmsThreshold = 0.3;
    private static readonly int[] YuNetStrides = { 8, 16, 32 };

    private readonly CameraId _cameraId;
    private readonly IInferenceEngine _landmarkEngine;
    private readonly string _faceDetectorPath;
    private readonly int _landmarkInputWidth;
    private readonly int _landmarkInputHeight;
    private readonly float _faceScoreThreshold;
    private readonly object _faceDetectorGate = new();
    private InferenceSession? _faceDetectorSession;
    private string? _faceDetectorLoadError;

    /// <summary>
    /// 创建面部摄像头分析器。
    /// </summary>
    public FaceCameraAnalyzer(
        CameraId cameraId,
        IInferenceEngine landmarkEngine,
        string modelDirectory,
        int landmarkInputWidth,
        int landmarkInputHeight,
        double faceScoreThreshold)
    {
        _cameraId = cameraId;
        _landmarkEngine = landmarkEngine;
        _faceDetectorPath = Path.Combine(modelDirectory, FaceDetectorModelName);
        _landmarkInputWidth = Math.Max(1, landmarkInputWidth);
        _landmarkInputHeight = Math.Max(1, landmarkInputHeight);
        _faceScoreThreshold = (float)Math.Clamp(faceScoreThreshold, 0.0, 1.0);
    }

    /// <summary>
    /// 对整帧先检测最大人脸，再在人脸 ROI 上运行 PFLD 并输出疲劳结果。
    /// </summary>
    public async Task<IReadOnlyList<CameraFinding>> AnalyzeAsync(ProcessedFrame frame, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!File.Exists(_faceDetectorPath))
        {
            return new[] { CreateStatusFinding($"yunet:{FaceDetectorModelName}:model_missing", frame.CapturedAt) };
        }

        var face = DetectLargestFace(frame.PreviewImage);
        if (face is null)
        {
            return new[] { CreateStatusFinding($"yunet:{FaceDetectorModelName}:{_faceDetectorLoadError ?? "face_missing"}", frame.CapturedAt) };
        }

        using var faceCrop = CropFace(frame.PreviewImage, face.Value.Crop);
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
                (int)Math.Round(face.Value.Crop.Size),
                (int)Math.Round(face.Value.Crop.Size)),
            cancellationToken);
        var landmarks = (landmarkOutput.Landmarks ?? Array.Empty<InferenceLandmark>())
            .Select(landmark => MapLandmarkToFrame(landmark, face.Value.Crop, frame.OriginalWidth, frame.OriginalHeight))
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
                face.Value.Confidence,
                frame.CapturedAt,
                face.Value.Box,
                Landmarks: landmarks),
            new CameraFinding(
                _cameraId,
                fatigue.Label,
                fatigue.Confidence,
                frame.CapturedAt,
                face.Value.Box)
        };
    }

    /// <summary>
    /// 释放 YuNet 和 PFLD 底层资源。
    /// </summary>
    public void Dispose()
    {
        _faceDetectorSession?.Dispose();
        if (_landmarkEngine is IDisposable disposable)
        {
            disposable.Dispose();
        }
    }

    /// <summary>
    /// 使用 YuNet 检测当前帧并选择面积最大的单张人脸。
    /// </summary>
    private FaceDetection? DetectLargestFace(Mat image)
    {
        var session = GetFaceDetectorSession();
        if (session is null)
        {
            return null;
        }

        using var resized = new Mat();
        Cv2.Resize(image, resized, new Size(YuNetInputWidth, YuNetInputHeight), 0, 0, InterpolationFlags.Linear);
        using var inputTensor = CreateYuNetInput(resized);
        using var inputValue = FixedBufferOnnxValue.CreateFromMemory(
            OrtMemoryInfo.DefaultInstance,
            inputTensor.Memory,
            TensorElementType.Float,
            new long[] { 1, 3, YuNetInputHeight, YuNetInputWidth },
            checked((long)inputTensor.Length * sizeof(float)));
        using var results = session.Run(new[] { session.InputMetadata.Keys.First() }, new[] { inputValue });
        var candidates = DecodeYuNetFaces(results, YuNetInputWidth, YuNetInputHeight, image.Width, image.Height);
        var selected = ApplyNms(candidates).OrderByDescending(candidate => candidate.Area).ToArray();
        return selected.Length == 0 ? null : selected[0];
    }

    /// <summary>
    /// 获取 YuNet ONNX Runtime 会话。
    /// </summary>
    private InferenceSession? GetFaceDetectorSession()
    {
        if (_faceDetectorSession is not null || _faceDetectorLoadError is not null)
        {
            return _faceDetectorSession;
        }

        lock (_faceDetectorGate)
        {
            if (_faceDetectorSession is not null || _faceDetectorLoadError is not null)
            {
                return _faceDetectorSession;
            }

            try
            {
                var sessionOptions = new SessionOptions
                {
                    GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
                    ExecutionMode = ExecutionMode.ORT_SEQUENTIAL,
                    LogSeverityLevel = OrtLoggingLevel.ORT_LOGGING_LEVEL_ERROR,
                    InterOpNumThreads = 1,
                    IntraOpNumThreads = Math.Clamp(Environment.ProcessorCount / 2, 1, 4)
                };
                _faceDetectorSession = new InferenceSession(_faceDetectorPath, sessionOptions);
            }
            catch (Exception ex) when (ex is OnnxRuntimeException or DllNotFoundException or BadImageFormatException)
            {
                _faceDetectorLoadError = ex.GetType().Name;
            }

            return _faceDetectorSession;
        }
    }

    /// <summary>
    /// 从 YuNet 原始输出中解码人脸候选框。
    /// </summary>
    private IReadOnlyList<FaceDetection> DecodeYuNetFaces(
        IDisposableReadOnlyCollection<DisposableNamedOnnxValue> results,
        int inputWidth,
        int inputHeight,
        int frameWidth,
        int frameHeight)
    {
        var tensors = results
            .Where(result => result.Value is Tensor<float>)
            .ToDictionary(result => result.Name, result => (Tensor<float>)result.Value, StringComparer.OrdinalIgnoreCase);
        var detections = new List<FaceDetection>();

        foreach (var stride in YuNetStrides)
        {
            if (!tensors.TryGetValue($"cls_{stride}", out var cls)
                || !tensors.TryGetValue($"obj_{stride}", out var obj)
                || !tensors.TryGetValue($"bbox_{stride}", out var bbox))
            {
                continue;
            }

            DecodeYuNetStride(cls, obj, bbox, stride, inputWidth, inputHeight, frameWidth, frameHeight, detections);
        }

        return detections;
    }

    /// <summary>
    /// 解码 YuNet 单个 stride 层的候选框。
    /// </summary>
    private void DecodeYuNetStride(
        Tensor<float> cls,
        Tensor<float> obj,
        Tensor<float> bbox,
        int stride,
        int inputWidth,
        int inputHeight,
        int frameWidth,
        int frameHeight,
        List<FaceDetection> detections)
    {
        if (!TryGetYuNetLayout(cls, stride, inputWidth, inputHeight, out var layout))
        {
            return;
        }

        var clsValues = cls.ToArray();
        var objValues = obj.ToArray();
        var bboxValues = bbox.ToArray();

        for (var y = 0; y < layout.Height; y++)
        {
            for (var x = 0; x < layout.Width; x++)
            {
                var clsScore = NormalizeScore(ReadYuNetValue(clsValues, layout, 1, 0, y, x));
                var objScore = NormalizeScore(ReadYuNetValue(objValues, layout, 1, 0, y, x));
                var confidence = Math.Sqrt(clsScore * objScore);
                if (confidence < _faceScoreThreshold)
                {
                    continue;
                }

                var centerX = (x + ReadYuNetValue(bboxValues, layout, 4, 0, y, x)) * stride;
                var centerY = (y + ReadYuNetValue(bboxValues, layout, 4, 1, y, x)) * stride;
                var width = Math.Exp(ReadYuNetValue(bboxValues, layout, 4, 2, y, x)) * stride;
                var height = Math.Exp(ReadYuNetValue(bboxValues, layout, 4, 3, y, x)) * stride;
                var left = centerX - width / 2.0;
                var top = centerY - height / 2.0;
                var right = centerX + width / 2.0;
                var bottom = centerY + height / 2.0;
                AddYuNetDetection(left, top, right, bottom, confidence, inputWidth, inputHeight, frameWidth, frameHeight, detections);
            }
        }
    }

    /// <summary>
    /// 加入一个裁剪到图像范围内的 YuNet 检测框。
    /// </summary>
    private static void AddYuNetDetection(
        double x1,
        double y1,
        double x2,
        double y2,
        double confidence,
        int inputWidth,
        int inputHeight,
        int frameWidth,
        int frameHeight,
        List<FaceDetection> detections)
    {
        var scaleX = (double)frameWidth / inputWidth;
        var scaleY = (double)frameHeight / inputHeight;
        var left = Math.Clamp(x1 * scaleX, 0, Math.Max(0, frameWidth - 1));
        var top = Math.Clamp(y1 * scaleY, 0, Math.Max(0, frameHeight - 1));
        var right = Math.Clamp(x2 * scaleX, 0, frameWidth);
        var bottom = Math.Clamp(y2 * scaleY, 0, frameHeight);
        if (right <= left || bottom <= top)
        {
            return;
        }

        var boxWidth = right - left;
        var boxHeight = bottom - top;
        detections.Add(new FaceDetection(
            new CameraBoundingBox(left / frameWidth, top / frameHeight, boxWidth / frameWidth, boxHeight / frameHeight),
            CreateSquareCrop(left, top, boxWidth, boxHeight),
            confidence,
            boxWidth * boxHeight));
    }

    /// <summary>
    /// 对 YuNet 候选框执行 NMS。
    /// </summary>
    private static IReadOnlyList<FaceDetection> ApplyNms(IReadOnlyList<FaceDetection> detections)
    {
        var selected = new List<FaceDetection>();
        foreach (var detection in detections.OrderByDescending(detection => detection.Confidence))
        {
            if (selected.Any(existing => IoU(existing.Box, detection.Box) > NmsThreshold))
            {
                continue;
            }

            selected.Add(detection);
        }

        return selected;
    }

    /// <summary>
    /// 计算两个归一化框的交并比。
    /// </summary>
    private static double IoU(CameraBoundingBox first, CameraBoundingBox second)
    {
        var left = Math.Max(first.X, second.X);
        var top = Math.Max(first.Y, second.Y);
        var right = Math.Min(first.X + first.Width, second.X + second.Width);
        var bottom = Math.Min(first.Y + first.Height, second.Y + second.Height);
        var intersection = Math.Max(0, right - left) * Math.Max(0, bottom - top);
        var union = first.Width * first.Height + second.Width * second.Height - intersection;
        return union <= 0 ? 0 : intersection / union;
    }

    /// <summary>
    /// 读取 YuNet 输出张量中的单个值，兼容 [1,N,C]、[1,C,N]、NCHW 和 NHWC。
    /// </summary>
    private static float ReadYuNetValue(
        float[] values,
        YuNetLayout layout,
        int channels,
        int channel,
        int y,
        int x)
    {
        var index = y * layout.Width + x;
        return layout.Kind switch
        {
            YuNetLayoutKind.ChannelFirst3D => values[channel * layout.AnchorCount + index],
            YuNetLayoutKind.ChannelLast4D => values[(y * layout.Width + x) * channels + channel],
            YuNetLayoutKind.ChannelFirst4D => values[channel * layout.AnchorCount + index],
            _ => values[index * channels + channel]
        };
    }

    /// <summary>
    /// 根据输出张量维度识别 YuNet 单层布局。
    /// </summary>
    private static bool TryGetYuNetLayout(
        Tensor<float> tensor,
        int stride,
        int inputWidth,
        int inputHeight,
        out YuNetLayout layout)
    {
        var expectedWidth = inputWidth / stride;
        var expectedHeight = inputHeight / stride;
        var expectedAnchors = expectedWidth * expectedHeight;
        var dims = tensor.Dimensions.ToArray();

        if (dims.Length == 3 && dims[0] == 1)
        {
            if (dims[1] == expectedAnchors)
            {
                layout = new YuNetLayout(YuNetLayoutKind.ChannelLast3D, expectedWidth, expectedHeight, expectedAnchors);
                return true;
            }

            if (dims[2] == expectedAnchors)
            {
                layout = new YuNetLayout(YuNetLayoutKind.ChannelFirst3D, expectedWidth, expectedHeight, expectedAnchors);
                return true;
            }
        }

        if (dims.Length == 4 && dims[0] == 1)
        {
            if (dims[2] == expectedHeight && dims[3] == expectedWidth)
            {
                layout = new YuNetLayout(YuNetLayoutKind.ChannelFirst4D, expectedWidth, expectedHeight, expectedAnchors);
                return true;
            }

            if (dims[1] == expectedHeight && dims[2] == expectedWidth)
            {
                layout = new YuNetLayout(YuNetLayoutKind.ChannelLast4D, expectedWidth, expectedHeight, expectedAnchors);
                return true;
            }
        }

        layout = default;
        return false;
    }

    /// <summary>
    /// 兼容概率输出和少数未 Sigmoid 的分数输出。
    /// </summary>
    private static double NormalizeScore(float value)
    {
        return value is >= 0.0f and <= 1.0f
            ? value
            : 1.0 / (1.0 + Math.Exp(-value));
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
    private readonly record struct FaceDetection(CameraBoundingBox Box, FaceCrop Crop, double Confidence, double Area);

    /// <summary>
    /// 表示可越界的正方形人脸裁剪区域。
    /// </summary>
    private readonly record struct FaceCrop(double Left, double Top, double Size);

    /// <summary>
    /// 表示 YuNet 输出张量排布。
    /// </summary>
    private readonly record struct YuNetLayout(YuNetLayoutKind Kind, int Width, int Height, int AnchorCount);

    /// <summary>
    /// 表示 YuNet 输出张量排布类型。
    /// </summary>
    private enum YuNetLayoutKind
    {
        ChannelLast3D,
        ChannelFirst3D,
        ChannelLast4D,
        ChannelFirst4D
    }
}
