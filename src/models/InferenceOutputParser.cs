namespace RideManager.Models;

/// <summary>
/// 将 ONNX/RKNN 共用的原始 float32 输出解析为业务推理结果。
/// </summary>
internal sealed class InferenceOutputParser
{
    private const double NmsIouThreshold = 0.45;
    private const double YuNetNmsIouThreshold = 0.3;
    private const int MaxDetections = 50;
    private static readonly int[] YuNetStrides = { 8, 16, 32 };
    private static readonly int[] YoloPv2RawHeadStrides = { 8, 16, 32 };
    private static readonly IReadOnlyDictionary<int, (double Width, double Height)[]> YoloPv2Anchors =
        new Dictionary<int, (double Width, double Height)[]>
        {
            [8] = new[] { (12.0, 16.0), (19.0, 36.0), (40.0, 28.0) },
            [16] = new[] { (36.0, 75.0), (76.0, 55.0), (72.0, 146.0) },
            [32] = new[] { (142.0, 110.0), (192.0, 243.0), (459.0, 401.0) }
        };
    private static readonly string[] RideAiLabels = { "person", "vehicle", "motorcycle", "bicycle" };
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
    private readonly double _confidenceThreshold;
    private readonly IReadOnlyList<string> _labels;

    /// <summary>
    /// 创建统一输出解析器。
    /// </summary>
    public InferenceOutputParser(double confidenceThreshold, IReadOnlyList<string> labels)
    {
        _confidenceThreshold = Math.Clamp(confidenceThreshold, 0.0, 1.0);
        _labels = labels.Count > 0 ? labels : CocoLabels;
    }

    /// <summary>
    /// 从原始输出张量中解析检测、分割或关键点结果。
    /// </summary>
    public InferenceOutput Parse(IReadOnlyList<InferenceRawTensor> outputs, InferenceInput input, string backendName)
    {
        var best = 0.0;
        var outputName = "output";
        var detections = new List<InferenceDetection>();
        var segmentationMasks = new List<InferenceSegmentationMask>();
        IReadOnlyList<InferenceLandmark>? landmarks = null;
        var recognizedDetectionOutput = false;
        var isYoloPv2OutputSet = IsYoloPv2OutputSet(outputs);
        var isTwinLiteNetOutputSet = IsTwinLiteNetOutputSet(outputs, input);
        var yunetDetections = TryParseYuNetDetections(outputs, input);
        if (yunetDetections is not null)
        {
            recognizedDetectionOutput = true;
            detections.AddRange(yunetDetections);
        }

        var yoloPv2RawHeadDetections = TryParseYoloPv2RawHeadDetections(outputs, input);
        if (yoloPv2RawHeadDetections is not null)
        {
            recognizedDetectionOutput = true;
            detections.AddRange(yoloPv2RawHeadDetections);
        }

        for (var outputIndex = 0; outputIndex < outputs.Count; outputIndex++)
        {
            var output = outputs[outputIndex];
            if (output.Values.Length == 0)
            {
                continue;
            }

            if (IsYuNetOutput(output.Name))
            {
                continue;
            }

            if (IsYoloPv2RawHeadOutput(output) || IsYoloPv2AnchorGridOutput(output))
            {
                continue;
            }

            var values = output.Values.Span;
            var decodedLandmarks = TryParsePfldLandmarks(output.Dimensions, values);
            if (decodedLandmarks is not null)
            {
                landmarks = decodedLandmarks;
                continue;
            }

            var decodedPostProcessed = TryParsePostProcessedDetections(output.Dimensions, values, input);
            if (decodedPostProcessed is not null)
            {
                recognizedDetectionOutput = true;
                detections.AddRange(decodedPostProcessed);
            }
            else
            {
                var twinLiteDetections = ParseTwinLiteNetSegmentation(
                    outputIndex,
                    output.Name,
                    output.Dimensions,
                    values,
                    input,
                    segmentationMasks,
                    _confidenceThreshold,
                    isTwinLiteNetOutputSet);
                if (twinLiteDetections.Count > 0)
                {
                    recognizedDetectionOutput = true;
                    detections.AddRange(twinLiteDetections);
                    continue;
                }

                if (isTwinLiteNetOutputSet && TryGetSegmentationLayout(output.Dimensions, out _))
                {
                    recognizedDetectionOutput = true;
                    continue;
                }

                detections.AddRange(ParseYoloPv2Segmentation(
                    output.Name,
                    output.Dimensions,
                    values,
                    input,
                    segmentationMasks,
                    _confidenceThreshold,
                    isYoloPv2OutputSet));
                detections.AddRange(ParseYoloDetections(output.Dimensions, values, input));
            }

            var max = Max(values);
            if (max > best)
            {
                best = max;
                outputName = output.Name;
            }
        }

        if (landmarks is { Count: > 0 })
        {
            return new InferenceOutput(
                new[] { "face_landmarks_106" },
                1.0,
                new[] { CreatePfldFaceDetection(landmarks) },
                Landmarks: landmarks);
        }

        if (detections.Count > 0)
        {
            var selected = ApplyNms(detections)
                .Take(MaxDetections)
                .ToArray();
            return new InferenceOutput(
                selected.Select(detection => detection.Label).ToArray(),
                selected.Max(detection => detection.Confidence),
                selected,
                segmentationMasks);
        }

        if (recognizedDetectionOutput)
        {
            return new InferenceOutput(Array.Empty<string>(), 0.0, Array.Empty<InferenceDetection>());
        }

        return new InferenceOutput(new[] { $"{backendName}:{outputName}" }, Math.Clamp(best, 0.0, 1.0));
    }

    /// <summary>
    /// 解析 YuNet 的 cls/obj/bbox 多尺度输出，返回归一化人脸框。
    /// </summary>
    private IReadOnlyList<InferenceDetection>? TryParseYuNetDetections(
        IReadOnlyList<InferenceRawTensor> outputs,
        InferenceInput input)
    {
        var tensors = outputs.ToDictionary(output => output.Name, StringComparer.OrdinalIgnoreCase);
        if (!tensors.Keys.Any(IsYuNetOutput))
        {
            return null;
        }

        if (!TryGetNchwImageSize(input.TensorDimensions, out var inputWidth, out var inputHeight))
        {
            return Array.Empty<InferenceDetection>();
        }

        var detections = new List<InferenceDetection>();
        foreach (var stride in YuNetStrides)
        {
            if (!tensors.TryGetValue($"cls_{stride}", out var cls)
                || !tensors.TryGetValue($"obj_{stride}", out var obj)
                || !tensors.TryGetValue($"bbox_{stride}", out var bbox))
            {
                continue;
            }

            DecodeYuNetStride(cls, obj, bbox, stride, inputWidth, inputHeight, detections);
        }

        return ApplyNms(detections, YuNetNmsIouThreshold);
    }

    /// <summary>
    /// 解码 YuNet 单个 stride 输出。
    /// </summary>
    private void DecodeYuNetStride(
        InferenceRawTensor cls,
        InferenceRawTensor obj,
        InferenceRawTensor bbox,
        int stride,
        int inputWidth,
        int inputHeight,
        List<InferenceDetection> detections)
    {
        if (!TryGetYuNetLayout(cls.Dimensions, stride, inputWidth, inputHeight, out var layout))
        {
            return;
        }

        for (var y = 0; y < layout.Height; y++)
        {
            for (var x = 0; x < layout.Width; x++)
            {
                var clsScore = NormalizeScore(ReadYuNetValue(cls.Values.Span, layout, 1, 0, y, x));
                var objScore = NormalizeScore(ReadYuNetValue(obj.Values.Span, layout, 1, 0, y, x));
                var confidence = Math.Sqrt(clsScore * objScore);
                if (confidence < _confidenceThreshold)
                {
                    continue;
                }

                var centerX = (x + ReadYuNetValue(bbox.Values.Span, layout, 4, 0, y, x)) * stride;
                var centerY = (y + ReadYuNetValue(bbox.Values.Span, layout, 4, 1, y, x)) * stride;
                var width = Math.Exp(ReadYuNetValue(bbox.Values.Span, layout, 4, 2, y, x)) * stride;
                var height = Math.Exp(ReadYuNetValue(bbox.Values.Span, layout, 4, 3, y, x)) * stride;
                var left = Math.Clamp((centerX - width / 2.0) / inputWidth, 0.0, 1.0);
                var top = Math.Clamp((centerY - height / 2.0) / inputHeight, 0.0, 1.0);
                var right = Math.Clamp((centerX + width / 2.0) / inputWidth, 0.0, 1.0);
                var bottom = Math.Clamp((centerY + height / 2.0) / inputHeight, 0.0, 1.0);
                if (right <= left || bottom <= top)
                {
                    continue;
                }

                detections.Add(new InferenceDetection("face", confidence, left, top, right - left, bottom - top));
            }
        }
    }

    /// <summary>
    /// 判断输出名是否属于 YuNet 的多尺度检测头。
    /// </summary>
    private static bool IsYuNetOutput(string outputName)
    {
        return YuNetStrides.Any(stride =>
            outputName.Equals($"cls_{stride}", StringComparison.OrdinalIgnoreCase)
            || outputName.Equals($"obj_{stride}", StringComparison.OrdinalIgnoreCase)
            || outputName.Equals($"bbox_{stride}", StringComparison.OrdinalIgnoreCase)
            || outputName.Equals($"kps_{stride}", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// 从 NCHW 输入维度读取图像尺寸。
    /// </summary>
    private static bool TryGetNchwImageSize(IReadOnlyList<int> dimensions, out int width, out int height)
    {
        if (dimensions.Count == 4 && dimensions[2] > 0 && dimensions[3] > 0)
        {
            width = dimensions[3];
            height = dimensions[2];
            return true;
        }

        width = 0;
        height = 0;
        return false;
    }

    /// <summary>
    /// 读取 YuNet 输出张量中的单个值。
    /// </summary>
    private static float ReadYuNetValue(
        ReadOnlySpan<float> values,
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
        IReadOnlyList<int> dimensions,
        int stride,
        int inputWidth,
        int inputHeight,
        out YuNetLayout layout)
    {
        var expectedWidth = inputWidth / stride;
        var expectedHeight = inputHeight / stride;
        var expectedAnchors = expectedWidth * expectedHeight;

        if (dimensions.Count == 3 && dimensions[0] == 1)
        {
            if (dimensions[1] == expectedAnchors)
            {
                layout = new YuNetLayout(YuNetLayoutKind.ChannelLast3D, expectedWidth, expectedHeight, expectedAnchors);
                return true;
            }

            if (dimensions[2] == expectedAnchors)
            {
                layout = new YuNetLayout(YuNetLayoutKind.ChannelFirst3D, expectedWidth, expectedHeight, expectedAnchors);
                return true;
            }
        }

        if (dimensions.Count == 4 && dimensions[0] == 1)
        {
            if (dimensions[2] == expectedHeight && dimensions[3] == expectedWidth)
            {
                layout = new YuNetLayout(YuNetLayoutKind.ChannelFirst4D, expectedWidth, expectedHeight, expectedAnchors);
                return true;
            }

            if (dimensions[1] == expectedHeight && dimensions[2] == expectedWidth)
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
    /// 计算 sigmoid，兼容 raw logits。
    /// </summary>
    private static double Sigmoid(float value)
    {
        if (value >= 0)
        {
            var z = Math.Exp(-value);
            return 1.0 / (1.0 + z);
        }

        var negativeZ = Math.Exp(value);
        return negativeZ / (1.0 + negativeZ);
    }

    /// <summary>
    /// 在不分配临时数组的情况下读取输出张量最大值。
    /// </summary>
    private static float Max(ReadOnlySpan<float> values)
    {
        if (values.IsEmpty)
        {
            return 0.0f;
        }

        var max = values[0];
        for (var index = 1; index < values.Length; index++)
        {
            if (values[index] > max)
            {
                max = values[index];
            }
        }

        return max;
    }

    /// <summary>
    /// 解析 PFLD 106 点模型输出，布局为 [1, 212] 或 [212] 的归一化 x/y 坐标。
    /// </summary>
    private static IReadOnlyList<InferenceLandmark>? TryParsePfldLandmarks(IReadOnlyList<int> dims, ReadOnlySpan<float> values)
    {
        var isPfldShape = values.Length == 106 * 2
            && (dims.Count == 1
                || (dims.Count == 2 && dims[0] == 1)
                || (dims.Count == 2 && dims[1] == 1));
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
    private static IReadOnlyList<InferenceDetection> ParseYoloPv2Segmentation(
        string outputName,
        IReadOnlyList<int> dims,
        ReadOnlySpan<float> values,
        InferenceInput input,
        List<InferenceSegmentationMask> segmentationMasks,
        double threshold,
        bool allowShapeInference)
    {
        if (!TryGetSegmentationLayout(dims, out var layout))
        {
            return Array.Empty<InferenceDetection>();
        }

        var isLaneLine = outputName.Contains("lane", StringComparison.OrdinalIgnoreCase)
            || allowShapeInference && layout.Channels == 1;
        if (isLaneLine && layout.Channels == 1)
        {
            return TryCreateMaskDetection(
                "lane_line",
                values,
                layout.Height,
                layout.Width,
                input,
                segmentationMasks,
                threshold);
        }

        var isDrivableArea = outputName.Contains("drivable", StringComparison.OrdinalIgnoreCase)
            || allowShapeInference && layout.Channels == 2;
        if (isDrivableArea && layout.Channels == 2)
        {
            return TryCreateTwoClassMaskDetection(
                "drivable_area",
                values,
                layout,
                input,
                segmentationMasks);
        }

        return Array.Empty<InferenceDetection>();
    }

    /// <summary>
    /// 判断输出集合是否符合 YOLOPv2 的检测、可行驶区域和车道线三输出特征。
    /// RKNN 转换后输出名可能不再保留 ONNX 名称，因此需要按形状识别。
    /// </summary>
    private static bool IsYoloPv2OutputSet(IReadOnlyList<InferenceRawTensor> outputs)
    {
        var hasDetection = outputs.Any(output =>
            IsYoloDetectionShape(output.Dimensions)
            || TryGetYoloPv2RawHeadLayout(output.Dimensions, out _));
        var segmentationChannels = outputs
            .Select(output => TryGetSegmentationLayout(output.Dimensions, out var layout) ? layout.Channels : 0)
            .ToHashSet();
        return hasDetection && segmentationChannels.Contains(1) && segmentationChannels.Contains(2);
    }

    /// <summary>
    /// 解析 YOLOPv2/ride_ai 导出的三尺度 raw head。
    /// </summary>
    private IReadOnlyList<InferenceDetection>? TryParseYoloPv2RawHeadDetections(
        IReadOnlyList<InferenceRawTensor> outputs,
        InferenceInput input)
    {
        var rawHeads = outputs
            .Select(output => TryGetYoloPv2RawHeadLayout(output.Dimensions, out var layout)
                ? (Output: output, Layout: layout)
                : ((InferenceRawTensor Output, YoloPv2RawHeadLayout Layout)?)null)
            .Where(item => item is not null)
            .Select(item => item!.Value)
            .ToArray();
        if (rawHeads.Length == 0)
        {
            return null;
        }

        var detections = new List<InferenceDetection>();
        foreach (var (output, layout) in rawHeads)
        {
            var anchors = GetYoloPv2Anchors(outputs, layout.Stride);
            DecodeYoloPv2RawHead(output.Values.Span, layout, anchors, input, detections);
        }

        return detections;
    }

    /// <summary>
    /// 解码单个 stride 的 YOLO raw head，输出原图归一化检测框。
    /// </summary>
    private void DecodeYoloPv2RawHead(
        ReadOnlySpan<float> values,
        YoloPv2RawHeadLayout layout,
        IReadOnlyList<(double Width, double Height)> anchors,
        InferenceInput input,
        List<InferenceDetection> detections)
    {
        var expectedValues = layout.AnchorCount * layout.Attributes * layout.CellCount;
        if (values.Length < expectedValues)
        {
            return;
        }

        for (var anchor = 0; anchor < layout.AnchorCount; anchor++)
        {
            var anchorSize = anchors[Math.Min(anchor, anchors.Count - 1)];
            for (var y = 0; y < layout.Height; y++)
            {
                for (var x = 0; x < layout.Width; x++)
                {
                    var objectness = Sigmoid(ReadYoloPv2RawHeadValue(values, layout, anchor, 4, y, x));
                    if (objectness <= _confidenceThreshold)
                    {
                        continue;
                    }

                    var bestClass = 0;
                    var bestClassScore = 0.0;
                    for (var classIndex = 0; classIndex < layout.ClassCount; classIndex++)
                    {
                        var classScore = Sigmoid(ReadYoloPv2RawHeadValue(
                            values,
                            layout,
                            anchor,
                            5 + classIndex,
                            y,
                            x));
                        if (classScore > bestClassScore)
                        {
                            bestClassScore = classScore;
                            bestClass = classIndex;
                        }
                    }

                    var confidence = objectness * bestClassScore;
                    if (confidence <= _confidenceThreshold)
                    {
                        continue;
                    }

                    var centerX = (Sigmoid(ReadYoloPv2RawHeadValue(values, layout, anchor, 0, y, x)) * 2.0
                        - 0.5
                        + x) * layout.Stride;
                    var centerY = (Sigmoid(ReadYoloPv2RawHeadValue(values, layout, anchor, 1, y, x)) * 2.0
                        - 0.5
                        + y) * layout.Stride;
                    var width = Math.Pow(Sigmoid(ReadYoloPv2RawHeadValue(values, layout, anchor, 2, y, x)) * 2.0, 2)
                        * anchorSize.Width;
                    var height = Math.Pow(Sigmoid(ReadYoloPv2RawHeadValue(values, layout, anchor, 3, y, x)) * 2.0, 2)
                        * anchorSize.Height;

                    if (!double.IsFinite(centerX)
                        || !double.IsFinite(centerY)
                        || !double.IsFinite(width)
                        || !double.IsFinite(height)
                        || width <= 0
                        || height <= 0)
                    {
                        continue;
                    }

                    var box = NormalizeLetterboxedBox(
                        centerX - width / 2.0,
                        centerY - height / 2.0,
                        centerX + width / 2.0,
                        centerY + height / 2.0,
                        input);
                    if (box.Width <= 0 || box.Height <= 0)
                    {
                        continue;
                    }

                    detections.Add(new InferenceDetection(
                        ResolveRideAiLabel(bestClass),
                        confidence,
                        box.X,
                        box.Y,
                        box.Width,
                        box.Height));
                }
            }
        }
    }

    /// <summary>
    /// 读取 raw head 中的单个 NCHW 值。
    /// </summary>
    private static float ReadYoloPv2RawHeadValue(
        ReadOnlySpan<float> values,
        YoloPv2RawHeadLayout layout,
        int anchor,
        int attribute,
        int y,
        int x)
    {
        var channel = anchor * layout.Attributes + attribute;
        return values[channel * layout.CellCount + y * layout.Width + x];
    }

    /// <summary>
    /// 识别 YOLOPv2 三尺度 raw head 的 NCHW 输出布局。
    /// </summary>
    private static bool TryGetYoloPv2RawHeadLayout(
        IReadOnlyList<int> dims,
        out YoloPv2RawHeadLayout layout)
    {
        if (dims.Count != 4 || dims[0] != 1 || dims[1] % 3 != 0 || dims[2] <= 1 || dims[3] <= 1)
        {
            layout = default;
            return false;
        }

        var attributes = dims[1] / 3;
        if (attributes < 6)
        {
            layout = default;
            return false;
        }

        var stride = YoloPv2RawHeadStrides.FirstOrDefault(candidate => dims[2] * candidate == 640 || dims[3] * candidate == 640);
        if (stride == 0)
        {
            stride = YoloPv2RawHeadStrides.FirstOrDefault(candidate => dims[2] is > 0 && dims[3] is > 0 && 640 / candidate == dims[2]);
        }

        if (stride == 0 && dims[2] == dims[3])
        {
            stride = dims[2] switch
            {
                80 => 8,
                40 => 16,
                20 => 32,
                _ => 0
            };
        }

        if (stride == 0)
        {
            layout = default;
            return false;
        }

        layout = new YoloPv2RawHeadLayout(3, attributes, dims[3], dims[2], stride);
        return true;
    }

    /// <summary>
    /// 判断输出是否是 YOLOPv2 raw head。
    /// </summary>
    private static bool IsYoloPv2RawHeadOutput(InferenceRawTensor output)
    {
        return output.Name.StartsWith("pred_s", StringComparison.OrdinalIgnoreCase)
            || TryGetYoloPv2RawHeadLayout(output.Dimensions, out _);
    }

    /// <summary>
    /// 判断输出是否是伴随 raw head 导出的 anchor grid 常量。
    /// </summary>
    private static bool IsYoloPv2AnchorGridOutput(InferenceRawTensor output)
    {
        return output.Name.StartsWith("anchor_grid", StringComparison.OrdinalIgnoreCase)
            || output.Dimensions.Count == 5
                && output.Dimensions[0] == 1
                && output.Dimensions[1] == 3
                && output.Dimensions[4] == 2;
    }

    /// <summary>
    /// 从输出中的 anchor_grid 读取 anchor，缺失时使用 YOLOPv2 默认值。
    /// </summary>
    private static IReadOnlyList<(double Width, double Height)> GetYoloPv2Anchors(
        IReadOnlyList<InferenceRawTensor> outputs,
        int stride)
    {
        var anchorGrid = outputs.FirstOrDefault(output =>
            output.Name.Equals($"anchor_grid_s{stride}", StringComparison.OrdinalIgnoreCase)
            && output.Values.Length >= 6);
        if (anchorGrid is not null)
        {
            return new[]
            {
                ((double)anchorGrid.Values.Span[0], (double)anchorGrid.Values.Span[1]),
                ((double)anchorGrid.Values.Span[2], (double)anchorGrid.Values.Span[3]),
                ((double)anchorGrid.Values.Span[4], (double)anchorGrid.Values.Span[5])
            };
        }

        return YoloPv2Anchors[stride];
    }

    /// <summary>
    /// 解析 TwinLiteNet 的可行驶区域和车道线二分类分割输出。
    /// </summary>
    private static IReadOnlyList<InferenceDetection> ParseTwinLiteNetSegmentation(
        int outputIndex,
        string outputName,
        IReadOnlyList<int> dims,
        ReadOnlySpan<float> values,
        InferenceInput input,
        List<InferenceSegmentationMask> segmentationMasks,
        double threshold,
        bool allowOrderInference)
    {
        if (!TryGetSegmentationLayout(dims, out var layout))
        {
            return Array.Empty<InferenceDetection>();
        }

        var normalizedName = outputName.Trim().ToLowerInvariant();
        var isDrivableArea = normalizedName is "da" or "drivable" or "drivable_area"
            || normalizedName.Contains("drive", StringComparison.OrdinalIgnoreCase)
            || allowOrderInference && outputIndex == 0;
        var isLaneLine = normalizedName is "ll" or "lane" or "lane_line"
            || normalizedName.Contains("lane", StringComparison.OrdinalIgnoreCase)
            || allowOrderInference && outputIndex == 1;

        if (layout.Channels == 2 && isDrivableArea)
        {
            return TryCreateTwoClassMaskDetection(
                "drivable_area",
                values,
                layout,
                input,
                segmentationMasks);
        }

        if (layout.Channels == 2 && isLaneLine)
        {
            return TryCreateTwoClassMaskDetection(
                "lane_line",
                values,
                layout,
                input,
                segmentationMasks);
        }

        if (layout.Channels == 1 && isLaneLine)
        {
            return TryCreateMaskDetection(
                "lane_line",
                values,
                layout.Height,
                layout.Width,
                input,
                segmentationMasks,
                threshold);
        }

        if (layout.Channels == 1 && isDrivableArea)
        {
            return TryCreateMaskDetection(
                "drivable_area",
                values,
                layout.Height,
                layout.Width,
                input,
                segmentationMasks,
                threshold);
        }

        return Array.Empty<InferenceDetection>();
    }

    /// <summary>
    /// 判断输出集合是否来自 TwinLiteNet，RKNN 重命名输出时按 DA/LL 顺序兜底。
    /// </summary>
    private static bool IsTwinLiteNetOutputSet(IReadOnlyList<InferenceRawTensor> outputs, InferenceInput input)
    {
        if (input.SourceName.Contains("twinlitenet", StringComparison.OrdinalIgnoreCase)
            || input.SourceName.Contains("twinlite", StringComparison.OrdinalIgnoreCase))
        {
            return outputs.Count(output =>
                TryGetSegmentationLayout(output.Dimensions, out var layout)
                && layout.Channels == 2) >= 2;
        }

        var names = outputs
            .Select(output => output.Name.Trim().ToLowerInvariant())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return names.Contains("da") && names.Contains("ll");
    }

    /// <summary>
    /// 判断张量是否为常见 YOLO 检测头布局。
    /// </summary>
    private static bool IsYoloDetectionShape(IReadOnlyList<int> dims)
    {
        return dims.Count == 3
            && dims[0] == 1
            && (dims[1] >= 6 || dims[2] >= 6);
    }

    /// <summary>
    /// 识别 NCHW 或 NHWC 的单批次语义分割输出。
    /// </summary>
    private static bool TryGetSegmentationLayout(IReadOnlyList<int> dims, out SegmentationLayout layout)
    {
        if (dims.Count == 4 && dims[0] == 1)
        {
            if (dims[1] is 1 or 2 && dims[2] > 1 && dims[3] > 1)
            {
                layout = new SegmentationLayout(dims[1], dims[3], dims[2], ChannelFirst: true);
                return true;
            }

            if (dims[3] is 1 or 2 && dims[1] > 1 && dims[2] > 1)
            {
                layout = new SegmentationLayout(dims[3], dims[2], dims[1], ChannelFirst: false);
                return true;
            }
        }

        layout = default;
        return false;
    }

    /// <summary>
    /// 从单通道概率图中提取正样本区域。
    /// </summary>
    private static IReadOnlyList<InferenceDetection> TryCreateMaskDetection(
        string label,
        ReadOnlySpan<float> values,
        int height,
        int width,
        InferenceInput input,
        List<InferenceSegmentationMask> segmentationMasks,
        double threshold)
    {
        var pixelCount = height * width;
        if (values.Length < pixelCount)
        {
            return Array.Empty<InferenceDetection>();
        }

        var valuesAreLogits = ContainsLogitRange(values, pixelCount);
        var normalizedThreshold = Math.Clamp(threshold, 0.0, 1.0);
        var bounds = new MaskBounds();
        var maskData = new byte[pixelCount];
        for (var y = 0; y < height; y++)
        {
            var rowOffset = y * width;
            for (var x = 0; x < width; x++)
            {
                var value = valuesAreLogits
                    ? Sigmoid(values[rowOffset + x])
                    : values[rowOffset + x];
                if (value >= normalizedThreshold)
                {
                    maskData[rowOffset + x] = 255;
                    bounds.Include(x, y, (float)value);
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
    /// 判断单通道 mask 输出是否是 logits，而不是已经归一化的概率。
    /// </summary>
    private static bool ContainsLogitRange(ReadOnlySpan<float> values, int length)
    {
        for (var index = 0; index < length; index++)
        {
            var value = values[index];
            if (value < 0.0f || value > 1.0f)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// 从两通道语义分割 logits/probability 图中提取类别 1 的区域。
    /// </summary>
    private static IReadOnlyList<InferenceDetection> TryCreateTwoClassMaskDetection(
        string label,
        ReadOnlySpan<float> values,
        SegmentationLayout layout,
        InferenceInput input,
        List<InferenceSegmentationMask> segmentationMasks)
    {
        var height = layout.Height;
        var width = layout.Width;
        var channelSize = height * width;
        if (values.Length < channelSize * 2)
        {
            return Array.Empty<InferenceDetection>();
        }

        var bounds = new MaskBounds();
        var maskData = new byte[height * width];
        for (var y = 0; y < height; y++)
        {
            var rowOffset = y * width;
            for (var x = 0; x < width; x++)
            {
                var index = rowOffset + x;
                var background = layout.ChannelFirst ? values[index] : values[index * 2];
                var foreground = layout.ChannelFirst ? values[channelSize + index] : values[index * 2 + 1];
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
        IReadOnlyList<int> dims,
        ReadOnlySpan<float> values,
        InferenceInput input)
    {
        if (dims.Count == 2 && dims[1] == 6)
        {
            return ParsePostProcessedRows(values, dims[0], false, input);
        }

        if (dims.Count != 3 || dims[0] != 1)
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
        ReadOnlySpan<float> values,
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
    private static float ReadPostProcessedValue(ReadOnlySpan<float> values, int rows, int row, int attribute, bool transposed)
    {
        return transposed
            ? values[attribute * rows + row]
            : values[row * 6 + attribute];
    }

    /// <summary>
    /// 解析常见 YOLO 输出布局：[1, 84, 8400] 或 [1, 8400, 84]。
    /// </summary>
    private IReadOnlyList<InferenceDetection> ParseYoloDetections(
        IReadOnlyList<int> dims,
        ReadOnlySpan<float> values,
        InferenceInput input)
    {
        if (dims.Count != 3)
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
        return classIndex >= 0 && classIndex < _labels.Count
            ? _labels[classIndex]
            : $"class_{classIndex}";
    }

    /// <summary>
    /// ride_ai 学生模型把前 4 个类别通道映射为道路安全业务类别。
    /// </summary>
    private string ResolveRideAiLabel(int classIndex)
    {
        return classIndex >= 0 && classIndex < RideAiLabels.Length
            ? RideAiLabels[classIndex]
            : ResolveLabel(classIndex);
    }

    /// <summary>
    /// 读取 YOLO 输出中的单个属性值。
    /// </summary>
    private static float ReadYoloValue(
        ReadOnlySpan<float> values,
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
    private static IReadOnlyList<InferenceDetection> ApplyNms(
        IReadOnlyList<InferenceDetection> detections,
        double iouThreshold = NmsIouThreshold)
    {
        var selected = new List<InferenceDetection>();
        foreach (var detection in detections.OrderByDescending(detection => detection.Confidence))
        {
            if (selected.Any(existing => existing.Label == detection.Label && IoU(existing, detection) > iouThreshold))
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
    /// 表示 YuNet 输出张量排布。
    /// </summary>
    private readonly record struct YuNetLayout(YuNetLayoutKind Kind, int Width, int Height, int AnchorCount);

    /// <summary>
    /// 表示语义分割输出的通道数、尺寸和内存布局。
    /// </summary>
    private readonly record struct SegmentationLayout(int Channels, int Width, int Height, bool ChannelFirst);

    /// <summary>
    /// 表示 YOLOPv2 raw head 输出张量排布。
    /// </summary>
    private readonly record struct YoloPv2RawHeadLayout(
        int AnchorCount,
        int Attributes,
        int Width,
        int Height,
        int Stride)
    {
        /// <summary>
        /// 获取每个 anchor 的类别数。
        /// </summary>
        public int ClassCount => Attributes - 5;

        /// <summary>
        /// 获取单个通道的空间元素数量。
        /// </summary>
        public int CellCount => Width * Height;
    }

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
}
