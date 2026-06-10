using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using System.Text.RegularExpressions;

namespace RideManager.Models;

/// <summary>
/// 提供 ONNX Runtime 推理实现。
/// </summary>
public sealed class OnnxInferenceEngine : IInferenceEngine, IDisposable
{
    private const double NmsIouThreshold = 0.45;
    private const int MaxDetections = 50;
    private static readonly Regex NamesMetadataRegex = new("(\\d+)\\s*:\\s*['\"]([^'\"]+)['\"]", RegexOptions.Compiled);
    private static readonly string[] CocoLabels =
    {
        "person", "bicycle", "car", "motorcycle", "airplane", "bus", "train", "truck",
        "boat", "traffic light", "fire hydrant", "stop sign", "parking meter", "bench",
        "bird", "cat", "dog", "horse", "sheep", "cow", "elephant", "bear", "zebra",
        "giraffe", "backpack", "umbrella", "handbag", "tie", "suitcase", "frisbee",
        "skis", "snowboard", "sports ball", "kite", "baseball bat", "baseball glove",
        "skateboard", "surfboard", "tennis racket", "bottle", "wine glass", "cup",
        "fork", "knife", "spoon", "bowl", "banana", "apple", "sandwich", "orange",
        "broccoli", "carrot", "hot dog", "pizza", "donut", "cake", "chair", "couch",
        "potted plant", "bed", "dining table", "toilet", "tv", "laptop", "mouse",
        "remote", "keyboard", "cell phone", "microwave", "oven", "toaster", "sink",
        "refrigerator", "book", "clock", "vase", "scissors", "teddy bear",
        "hair drier", "toothbrush"
    };
    private readonly string _modelPath;
    private readonly double _confidenceThreshold;
    private readonly object _gate = new();
    private InferenceSession? _session;
    private string[] _labels = CocoLabels;
    private string? _loadError;

    /// <summary>
    /// 创建 ONNX 推理引擎。
    /// </summary>
    public OnnxInferenceEngine(string modelPath, double confidenceThreshold)
    {
        _modelPath = modelPath;
        _confidenceThreshold = Math.Clamp(confidenceThreshold, 0.0, 1.0);
    }

    /// <summary>
    /// 使用 ONNX Runtime 运行一次推理，模型缺失时返回可诊断结果。
    /// </summary>
    public Task<InferenceOutput> RunAsync(InferenceInput input, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var session = GetSession();
        if (session is null)
        {
            var reason = File.Exists(_modelPath) ? _loadError ?? "load_failed" : "model_missing";
            return Task.FromResult(new InferenceOutput(new[] { $"onnx:{Path.GetFileName(_modelPath)}:{reason}" }, 0.0));
        }

        var inputName = session.InputMetadata.Keys.First();
        var dimensions = input.TensorDimensions.Select(Convert.ToInt64).ToArray();
        var byteCount = checked((long)input.TensorElementCount * sizeof(float));
        using var value = FixedBufferOnnxValue.CreateFromMemory(
            OrtMemoryInfo.DefaultInstance,
            input.TensorData,
            TensorElementType.Float,
            dimensions,
            byteCount);
        using var results = session.Run(new[] { inputName }, new[] { value });

        return Task.FromResult(ParseOutput(results, input));
    }

    /// <summary>
    /// 释放 ONNX Runtime 会话。
    /// </summary>
    public void Dispose()
    {
        _session?.Dispose();
    }

    /// <summary>
    /// 懒加载 ONNX 会话。
    /// </summary>
    private InferenceSession? GetSession()
    {
        if (_session is not null || _loadError is not null || !File.Exists(_modelPath))
        {
            return _session;
        }

        lock (_gate)
        {
            if (_session is not null || _loadError is not null)
            {
                return _session;
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
                _session = new InferenceSession(_modelPath, sessionOptions);
                _labels = GetLabels(_session);
            }
            catch (Exception ex) when (ex is OnnxRuntimeException or DllNotFoundException or BadImageFormatException)
            {
                _loadError = ex.GetType().Name;
            }

            return _session;
        }
    }

    /// <summary>
    /// 从通用数值输出中提取最高置信度，作为 live 链路的基础后处理。
    /// </summary>
    private InferenceOutput ParseOutput(
        IDisposableReadOnlyCollection<DisposableNamedOnnxValue> results,
        InferenceInput input)
    {
        var outputs = new List<InferenceRawTensor>();

        foreach (var result in results)
        {
            if (result.Value is not Tensor<float> tensor)
            {
                continue;
            }

            outputs.Add(new InferenceRawTensor(result.Name, tensor.Dimensions.ToArray(), GetTensorMemory(tensor)));
        }

        return new InferenceOutputParser(_confidenceThreshold, _labels).Parse(outputs, input, "onnx");
    }

    /// <summary>
    /// 优先复用 ONNX Runtime 映射出的 tensor buffer，避免大分割输出 ToArray 拷贝。
    /// </summary>
    private static ReadOnlyMemory<float> GetTensorMemory(Tensor<float> tensor)
    {
        return tensor is DenseTensor<float> denseTensor
            ? denseTensor.Buffer
            : tensor.ToArray();
    }

    /// <summary>
    /// 解析 PFLD 106 点模型输出，布局为 [1, 212] 或 [212] 的归一化 x/y 坐标。
    /// </summary>
    private static IReadOnlyList<InferenceLandmark>? TryParsePfldLandmarks(Tensor<float> tensor, float[] values)
    {
        var dims = tensor.Dimensions.ToArray();
        var isPfldShape = values.Length == 106 * 2
            && (dims.Length == 1
                || (dims.Length == 2 && dims[0] == 1)
                || (dims.Length == 2 && dims[1] == 1));
        if (!isPfldShape)
        {
            return null;
        }

        var landmarks = new InferenceLandmark[106];
        for (var index = 0; index < landmarks.Length; index++)
        {
            var x = values[index * 2];
            var y = values[index * 2 + 1];
            if (!float.IsFinite(x) || !float.IsFinite(y))
            {
                return null;
            }

            landmarks[index] = new InferenceLandmark(
                Math.Clamp(x, 0.0, 1.0),
                Math.Clamp(y, 0.0, 1.0));
        }

        return landmarks;
    }

    /// <summary>
    /// 根据 PFLD 关键点外接范围生成一条面部 finding，便于统一链路显示和存储。
    /// </summary>
    private static InferenceDetection CreatePfldFaceDetection(IReadOnlyList<InferenceLandmark> landmarks)
    {
        var left = landmarks.Min(landmark => landmark.X);
        var top = landmarks.Min(landmark => landmark.Y);
        var right = landmarks.Max(landmark => landmark.X);
        var bottom = landmarks.Max(landmark => landmark.Y);
        const double padding = 0.02;

        left = Math.Clamp(left - padding, 0.0, 1.0);
        top = Math.Clamp(top - padding, 0.0, 1.0);
        right = Math.Clamp(right + padding, 0.0, 1.0);
        bottom = Math.Clamp(bottom + padding, 0.0, 1.0);

        return new InferenceDetection(
            "face_landmarks_106",
            1.0,
            left,
            top,
            Math.Max(0.001, right - left),
            Math.Max(0.001, bottom - top));
    }

    /// <summary>
    /// 解析 YOLOPv2 的可行驶区域与车道线分割输出，并以区域 finding 形式交给 live 链路显示。
    /// </summary>
    private IReadOnlyList<InferenceDetection> ParseYoloPv2Segmentation(
        string outputName,
        Tensor<float> tensor,
        float[] values,
        InferenceInput input,
        List<InferenceSegmentationMask> segmentationMasks)
    {
        var dims = tensor.Dimensions.ToArray();
        if (dims.Length != 4 || dims[0] != 1)
        {
            return Array.Empty<InferenceDetection>();
        }

        if (outputName.Contains("lane", StringComparison.OrdinalIgnoreCase) && dims[1] == 1)
        {
            return TryCreateMaskDetection("lane_line", values, dims[2], dims[3], input, segmentationMasks);
        }

        if (outputName.Contains("drivable", StringComparison.OrdinalIgnoreCase) && dims[1] == 2)
        {
            return TryCreateTwoClassMaskDetection("drivable_area", values, dims[2], dims[3], input, segmentationMasks);
        }

        return Array.Empty<InferenceDetection>();
    }

    /// <summary>
    /// 从单通道概率图中提取正样本区域。
    /// </summary>
    private static IReadOnlyList<InferenceDetection> TryCreateMaskDetection(
        string label,
        float[] values,
        int height,
        int width,
        InferenceInput input,
        List<InferenceSegmentationMask> segmentationMasks)
    {
        var bounds = new MaskBounds();
        var maskData = new byte[height * width];
        for (var y = 0; y < height; y++)
        {
            var rowOffset = y * width;
            for (var x = 0; x < width; x++)
            {
                var value = values[rowOffset + x];
                if (value >= 0.5f)
                {
                    maskData[rowOffset + x] = 255;
                    bounds.Include(x, y, value);
                }
            }
        }

        var detections = bounds.ToDetection(label, width, height, input);
        if (detections.Count > 0)
        {
            segmentationMasks.Add(new InferenceSegmentationMask(label, width, height, maskData));
        }

        return detections;
    }

    /// <summary>
    /// 从两通道语义分割 logits/probability 图中提取类别 1 的区域。
    /// </summary>
    private static IReadOnlyList<InferenceDetection> TryCreateTwoClassMaskDetection(
        string label,
        float[] values,
        int height,
        int width,
        InferenceInput input,
        List<InferenceSegmentationMask> segmentationMasks)
    {
        var channelSize = height * width;
        var bounds = new MaskBounds();
        var maskData = new byte[height * width];
        for (var y = 0; y < height; y++)
        {
            var rowOffset = y * width;
            for (var x = 0; x < width; x++)
            {
                var index = rowOffset + x;
                var background = values[index];
                var foreground = values[channelSize + index];
                if (foreground >= background)
                {
                    maskData[index] = 255;
                    bounds.Include(x, y, foreground);
                }
            }
        }

        var detections = bounds.ToDetection(label, width, height, input);
        if (detections.Count > 0)
        {
            segmentationMasks.Add(new InferenceSegmentationMask(label, width, height, maskData));
        }

        return detections;
    }

    /// <summary>
    /// 解析已在模型内完成后处理的 YOLO 输出：[1, N, 6]，每行 x1,y1,x2,y2,confidence,classId。
    /// </summary>
    private IReadOnlyList<InferenceDetection>? TryParsePostProcessedDetections(
        Tensor<float> tensor,
        float[] values,
        InferenceInput input)
    {
        var dims = tensor.Dimensions.ToArray();
        if (dims.Length == 2 && dims[1] == 6)
        {
            return ParsePostProcessedRows(values, dims[0], false, input);
        }

        if (dims.Length != 3 || dims[0] != 1)
        {
            return null;
        }

        if (dims[2] == 6)
        {
            return ParsePostProcessedRows(values, dims[1], false, input);
        }

        return dims[1] == 6
            ? ParsePostProcessedRows(values, dims[2], true, input)
            : null;
    }

    /// <summary>
    /// 解析后处理输出行，并把 letterbox 输入坐标还原为原图归一化坐标。
    /// </summary>
    private IReadOnlyList<InferenceDetection> ParsePostProcessedRows(
        float[] values,
        int rows,
        bool transposed,
        InferenceInput input)
    {
        var detections = new List<InferenceDetection>();
        for (var row = 0; row < rows; row++)
        {
            var confidence = ReadPostProcessedValue(values, rows, row, 4, transposed);
            if (confidence < _confidenceThreshold)
            {
                continue;
            }

            var classId = Math.Max(0, (int)ReadPostProcessedValue(values, rows, row, 5, transposed));
            var x1 = ReadPostProcessedValue(values, rows, row, 0, transposed);
            var y1 = ReadPostProcessedValue(values, rows, row, 1, transposed);
            var x2 = ReadPostProcessedValue(values, rows, row, 2, transposed);
            var y2 = ReadPostProcessedValue(values, rows, row, 3, transposed);
            var box = NormalizeLetterboxedBox(x1, y1, x2, y2, input);
            if (box.Width <= 0 || box.Height <= 0)
            {
                continue;
            }

            detections.Add(new InferenceDetection(
                ResolveLabel(classId),
                confidence,
                box.X,
                box.Y,
                box.Width,
                box.Height));
        }

        return detections;
    }

    /// <summary>
    /// 读取后处理输出中的单个属性值。
    /// </summary>
    private static float ReadPostProcessedValue(float[] values, int rows, int row, int attribute, bool transposed)
    {
        return transposed
            ? values[attribute * rows + row]
            : values[row * 6 + attribute];
    }

    /// <summary>
    /// 解析常见 YOLO 输出布局：[1, 84, 8400] 或 [1, 8400, 84]。
    /// </summary>
    private IReadOnlyList<InferenceDetection> ParseYoloDetections(
        Tensor<float> tensor,
        float[] values,
        InferenceInput input)
    {
        var dims = tensor.Dimensions.ToArray();
        if (dims.Length != 3)
        {
            return Array.Empty<InferenceDetection>();
        }

        var detections = new List<InferenceDetection>();

        var channelFirst = dims[1] < dims[2];
        var attributes = channelFirst ? dims[1] : dims[2];
        var anchors = channelFirst ? dims[2] : dims[1];
        if (attributes < 6)
        {
            return Array.Empty<InferenceDetection>();
        }

        var hasObjectness = attributes == 85;
        var classStart = hasObjectness ? 5 : 4;
        var classCount = attributes - classStart;

        for (var anchor = 0; anchor < anchors; anchor++)
        {
            var centerX = ReadYoloValue(values, channelFirst, attributes, anchors, anchor, 0);
            var centerY = ReadYoloValue(values, channelFirst, attributes, anchors, anchor, 1);
            var width = ReadYoloValue(values, channelFirst, attributes, anchors, anchor, 2);
            var height = ReadYoloValue(values, channelFirst, attributes, anchors, anchor, 3);
            var objectness = hasObjectness
                ? ReadYoloValue(values, channelFirst, attributes, anchors, anchor, 4)
                : 1f;

            var bestClass = 0;
            var bestScore = 0f;
            for (var classIndex = 0; classIndex < classCount; classIndex++)
            {
                var score = ReadYoloValue(values, channelFirst, attributes, anchors, anchor, classStart + classIndex);
                if (score > bestScore)
                {
                    bestScore = score;
                    bestClass = classIndex;
                }
            }

            var confidence = Math.Clamp(objectness * bestScore, 0f, 1f);
            if (confidence < _confidenceThreshold)
            {
                continue;
            }

            var normalized = NormalizeLetterboxedBox(
                centerX - width / 2,
                centerY - height / 2,
                centerX + width / 2,
                centerY + height / 2,
                input);
            detections.Add(new InferenceDetection(
                ResolveLabel(bestClass),
                confidence,
                normalized.X,
                normalized.Y,
                normalized.Width,
                normalized.Height));
        }

        return detections;
    }

    /// <summary>
    /// 将类别索引转换为可读标签。
    /// </summary>
    private string ResolveLabel(int classIndex)
    {
        return classIndex >= 0 && classIndex < _labels.Length
            ? _labels[classIndex]
            : $"class_{classIndex}";
    }

    /// <summary>
    /// 读取 YOLO 输出中的单个属性值。
    /// </summary>
    private static float ReadYoloValue(
        float[] values,
        bool channelFirst,
        int attributes,
        int anchors,
        int anchor,
        int attribute)
    {
        return channelFirst
            ? values[attribute * anchors + anchor]
            : values[anchor * attributes + attribute];
    }

    /// <summary>
    /// 将 letterbox 坐标转换为原图归一化左上角格式。
    /// </summary>
    private static InferenceDetection NormalizeLetterboxedBox(
        double x1,
        double y1,
        double x2,
        double y2,
        InferenceInput input)
    {
        var inputHeight = input.TensorDimensions.Count >= 4 ? input.TensorDimensions[2] : 640;
        var inputWidth = input.TensorDimensions.Count >= 4 ? input.TensorDimensions[3] : 640;
        var originalWidth = input.OriginalWidth > 0 ? input.OriginalWidth : inputWidth;
        var originalHeight = input.OriginalHeight > 0 ? input.OriginalHeight : inputHeight;

        if (Math.Max(Math.Max(x1, x2), Math.Max(y1, y2)) <= 1.5)
        {
            return new InferenceDetection(
                string.Empty,
                0,
                Math.Clamp(x1, 0, 1),
                Math.Clamp(y1, 0, 1),
                Math.Clamp(x2 - x1, 0, 1),
                Math.Clamp(y2 - y1, 0, 1));
        }

        var scale = Math.Min((double)inputWidth / originalWidth, (double)inputHeight / originalHeight);
        var paddedWidth = originalWidth * scale;
        var paddedHeight = originalHeight * scale;
        var padX = (inputWidth - paddedWidth) / 2;
        var padY = (inputHeight - paddedHeight) / 2;

        var left = Math.Clamp((x1 - padX) / scale, 0, Math.Max(0, originalWidth - 1));
        var top = Math.Clamp((y1 - padY) / scale, 0, Math.Max(0, originalHeight - 1));
        var right = Math.Clamp((x2 - padX) / scale, 0, Math.Max(0, originalWidth - 1));
        var bottom = Math.Clamp((y2 - padY) / scale, 0, Math.Max(0, originalHeight - 1));

        return new InferenceDetection(
            string.Empty,
            0,
            Math.Clamp(left / originalWidth, 0, 1),
            Math.Clamp(top / originalHeight, 0, 1),
            Math.Clamp((right - left) / originalWidth, 0, 1),
            Math.Clamp((bottom - top) / originalHeight, 0, 1));
    }

    /// <summary>
    /// 对检测框执行基础非极大值抑制。
    /// </summary>
    private static IReadOnlyList<InferenceDetection> ApplyNms(IReadOnlyList<InferenceDetection> detections)
    {
        var selected = new List<InferenceDetection>();
        foreach (var detection in detections.OrderByDescending(detection => detection.Confidence))
        {
            if (selected.Any(existing => existing.Label == detection.Label && IoU(existing, detection) > NmsIouThreshold))
            {
                continue;
            }

            selected.Add(detection);
            if (selected.Count >= MaxDetections)
            {
                break;
            }
        }

        return selected;
    }

    /// <summary>
    /// 计算两个归一化检测框的交并比。
    /// </summary>
    private static double IoU(InferenceDetection first, InferenceDetection second)
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
    /// 从模型 metadata 或 sidecar 文件读取类别名。
    /// </summary>
    private string[] GetLabels(InferenceSession session)
    {
        if (session.ModelMetadata.CustomMetadataMap.TryGetValue("names", out var namesMetadata))
        {
            var metadataLabels = ParseNamesMetadata(namesMetadata);
            if (metadataLabels.Length > 0)
            {
                return metadataLabels;
            }
        }

        var sidecarPath = Path.ChangeExtension(_modelPath, ".labels.txt");
        if (File.Exists(sidecarPath))
        {
            var sidecarLabels = File.ReadAllLines(sidecarPath)
                .Select(label => label.Trim())
                .Where(label => !string.IsNullOrWhiteSpace(label))
                .ToArray();
            if (sidecarLabels.Length > 0)
            {
                return sidecarLabels;
            }
        }

        return CocoLabels;
    }

    /// <summary>
    /// 记录分割 mask 的外接矩形与置信度近似值。
    /// </summary>
    private sealed class MaskBounds
    {
        private int _left = int.MaxValue;
        private int _top = int.MaxValue;
        private int _right = int.MinValue;
        private int _bottom = int.MinValue;
        private float _maxValue;
        private int _count;

        /// <summary>
        /// 纳入一个正样本像素。
        /// </summary>
        public void Include(int x, int y, float value)
        {
            _left = Math.Min(_left, x);
            _top = Math.Min(_top, y);
            _right = Math.Max(_right, x);
            _bottom = Math.Max(_bottom, y);
            _maxValue = Math.Max(_maxValue, value);
            _count++;
        }

        /// <summary>
        /// 转换为归一化检测区域。
        /// </summary>
        public IReadOnlyList<InferenceDetection> ToDetection(
            string label,
            int width,
            int height,
            InferenceInput input)
        {
            if (_count == 0)
            {
                return Array.Empty<InferenceDetection>();
            }

            var box = NormalizeLetterboxedBox(_left, _top, _right + 1, _bottom + 1, input);
            if (box.Width <= 0 || box.Height <= 0)
            {
                return Array.Empty<InferenceDetection>();
            }

            var coverage = _count / Math.Max(1.0, width * height);
            var confidence = Math.Clamp(Math.Max(_maxValue, coverage), 0.0, 1.0);
            return new[]
            {
                new InferenceDetection(label, confidence, box.X, box.Y, box.Width, box.Height)
            };
        }
    }

    /// <summary>
    /// 解析 YOLO 导出模型中的 names metadata。
    /// </summary>
    private static string[] ParseNamesMetadata(string namesMetadata)
    {
        var labelsById = new Dictionary<int, string>();

        foreach (Match match in NamesMetadataRegex.Matches(namesMetadata))
        {
            if (!int.TryParse(match.Groups[1].Value, out var classId) || classId < 0)
            {
                continue;
            }

            labelsById[classId] = match.Groups[2].Value.Trim();
        }

        if (labelsById.Count == 0)
        {
            return Array.Empty<string>();
        }

        var labels = new string[labelsById.Keys.Max() + 1];
        for (var index = 0; index < labels.Length; index++)
        {
            labels[index] = labelsById.TryGetValue(index, out var label) && !string.IsNullOrWhiteSpace(label)
                ? label
                : $"class_{index}";
        }

        return labels;
    }
}
