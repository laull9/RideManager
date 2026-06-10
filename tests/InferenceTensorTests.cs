using OpenCvSharp;
using RideManager.Camera;
using RideManager.Models;
using RideManager.Utils;
using Xunit;

namespace RideManager.Tests;

public sealed class InferenceTensorTests
{
    [Fact]
    public void NativeFloatTensor_ExposesStableNativePointerAndMemoryView()
    {
        using var tensor = new NativeFloatTensor(3);

        tensor.Span[0] = 1.0f;
        tensor.Span[1] = 2.0f;
        tensor.Span[2] = 3.0f;

        Assert.NotEqual(IntPtr.Zero, tensor.Pointer);
        Assert.Equal(3, tensor.Memory.Length);
        Assert.Equal(new[] { 1.0f, 2.0f, 3.0f }, tensor.Memory.ToArray());
    }

    [Fact]
    public async Task OpenCvFramePreprocessor_WritesNchwTensorIntoNativeMemory()
    {
        var options = new CameraOptions(
            CameraId.CamFront,
            true,
            "synthetic",
            "model.onnx",
            1,
            1,
            1,
            1,
            30,
            0.35);
        using var frame = new CameraFrame(
            CameraId.CamFront,
            DateTimeOffset.UtcNow,
            new Mat(1, 1, MatType.CV_8UC3, new Scalar(10, 20, 30)));
        var preprocessor = new OpenCvFramePreprocessor(options);

        using var processed = await preprocessor.ProcessAsync(frame, CancellationToken.None);

        Assert.NotEqual(IntPtr.Zero, processed.TensorDataPointer);
        Assert.Equal(new[] { 1, 3, 1, 1 }, processed.TensorDimensions);
        Assert.Equal(30f / 255f, processed.TensorData.Span[0], 6);
        Assert.Equal(20f / 255f, processed.TensorData.Span[1], 6);
        Assert.Equal(10f / 255f, processed.TensorData.Span[2], 6);
    }

    [Fact]
    public async Task RknnInferenceEngine_ReturnsDiagnosticWhenModelIsMissing()
    {
        using var tensor = new NativeFloatTensor(1);
        var input = new InferenceInput("test", tensor, new[] { 1, 1, 1, 1 }, 1, 1);
        using var engine = new RknnInferenceEngine("missing-model.rknn", 0.5);

        var output = await engine.RunAsync(input, CancellationToken.None);

        Assert.Contains("model_missing", output.Labels.Single());
    }

    [Fact]
    public async Task ModelRuntimeSelector_UsesSiblingRknnModelWhenBackendIsRknn()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"ridemanager-rknn-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            File.WriteAllBytes(Path.Combine(directory, "model.rknn"), Array.Empty<byte>());
            var options = new ModelOptions(ModelBackend.Rknn, directory);
            var selector = new ModelRuntimeSelector(options);

            using var engine = Assert.IsType<RknnInferenceEngine>(selector.Create("model.onnx", 0.5));
            using var tensor = new NativeFloatTensor(1);
            var input = new InferenceInput("test", tensor, new[] { 1, 1, 1, 1 }, 1, 1);
            var output = await engine.RunAsync(input, CancellationToken.None);

            Assert.StartsWith("rknn:model.rknn:", output.Labels.Single(), StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task ModelRuntimeSelector_DoesNotPassOnnxFileToRknnRuntime()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"ridemanager-rknn-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            File.WriteAllBytes(Path.Combine(directory, "model.onnx"), Array.Empty<byte>());
            var selector = new ModelRuntimeSelector(new ModelOptions(ModelBackend.Rknn, directory));

            using var engine = Assert.IsType<RknnInferenceEngine>(selector.Create("model.onnx", 0.5));
            using var tensor = new NativeFloatTensor(1);
            var output = await engine.RunAsync(
                new InferenceInput("test", tensor, new[] { 1, 1, 1, 1 }, 1, 1),
                CancellationToken.None);

            Assert.Equal("rknn:model.rknn:model_missing", output.Labels.Single());
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void ModelRuntimeSelector_RejectsRknnFileWhenBackendIsOnnx()
    {
        var selector = new ModelRuntimeSelector(new ModelOptions(ModelBackend.Onnx, "models"));

        var exception = Assert.Throws<InvalidOperationException>(() => selector.Create("model.rknn", 0.5));

        Assert.Contains("Set [models] backend = \"rknn\"", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void InferenceOutputParser_DecodesYuNetOutputsFromUnifiedEngine()
    {
        const int anchors = 80 * 80;
        const int anchorX = 20;
        const int anchorY = 10;
        var anchorIndex = anchorY * 80 + anchorX;
        var cls = new float[anchors];
        var obj = new float[anchors];
        var bbox = new float[anchors * 4];
        cls[anchorIndex] = 1.0f;
        obj[anchorIndex] = 1.0f;
        bbox[anchorIndex * 4] = 0.5f;
        bbox[anchorIndex * 4 + 1] = 0.5f;
        bbox[anchorIndex * 4 + 2] = MathF.Log(4.0f);
        bbox[anchorIndex * 4 + 3] = MathF.Log(4.0f);

        using var tensor = new NativeFloatTensor(1);
        var input = new InferenceInput("face", tensor, new[] { 1, 3, 640, 640 }, 1280, 720);
        var outputs = new[]
        {
            new InferenceRawTensor("cls_8", new[] { 1, anchors, 1 }, cls),
            new InferenceRawTensor("obj_8", new[] { 1, anchors, 1 }, obj),
            new InferenceRawTensor("bbox_8", new[] { 1, anchors, 4 }, bbox)
        };

        var output = new InferenceOutputParser(0.6, Array.Empty<string>()).Parse(outputs, input, "rknn");

        var detection = Assert.Single(output.Detections!);
        Assert.Equal("face", detection.Label);
        Assert.Equal(1.0, detection.Confidence, 6);
        Assert.Equal(0.23125, detection.X, 6);
        Assert.Equal(0.10625, detection.Y, 6);
        Assert.Equal(0.05, detection.Width, 6);
        Assert.Equal(0.05, detection.Height, 6);
    }

    [Fact]
    public void InferenceOutputParser_DoesNotParseYuNetKeypointsAsYoloDetections()
    {
        const int anchors = 20 * 20;
        const int anchorIndex = 10 * 20 + 10;
        var cls = new float[anchors];
        var obj = new float[anchors];
        var bbox = new float[anchors * 4];
        var keypoints = Enumerable.Repeat(2.0f, anchors * 10).ToArray();
        cls[anchorIndex] = 1.0f;
        obj[anchorIndex] = 1.0f;

        using var tensor = new NativeFloatTensor(1);
        var input = new InferenceInput("face", tensor, new[] { 1, 3, 640, 640 }, 640, 480);
        var outputs = new[]
        {
            new InferenceRawTensor("cls_32", new[] { 1, anchors, 1 }, cls),
            new InferenceRawTensor("obj_32", new[] { 1, anchors, 1 }, obj),
            new InferenceRawTensor("bbox_32", new[] { 1, anchors, 4 }, bbox),
            new InferenceRawTensor("kps_32", new[] { 1, anchors, 10 }, keypoints)
        };

        var output = new InferenceOutputParser(0.6, Array.Empty<string>()).Parse(outputs, input, "onnx");

        var detection = Assert.Single(output.Detections!);
        Assert.Equal("face", detection.Label);
        Assert.DoesNotContain(output.Labels, label => label.Equals("bus", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void InferenceOutputParser_DecodesRenamedNhwcYoloPv2SegmentationOutputs()
    {
        using var tensor = new NativeFloatTensor(1);
        var input = new InferenceInput("front", tensor, new[] { 1, 3, 640, 640 }, 640, 640);
        var detections = new float[85 * 100];
        var drivableArea = Enumerable.Range(0, 12)
            .SelectMany(index => index is 5 or 6 ? new[] { 0.0f, 1.0f } : new[] { 1.0f, 0.0f })
            .ToArray();
        var laneLine = Enumerable.Range(0, 12)
            .Select(index => index is 5 or 6 ? 1.0f : 0.0f)
            .ToArray();
        var outputs = new[]
        {
            new InferenceRawTensor("output0", new[] { 1, 85, 100 }, detections),
            new InferenceRawTensor("output1", new[] { 1, 3, 4, 2 }, drivableArea),
            new InferenceRawTensor("output2", new[] { 1, 3, 4, 1 }, laneLine)
        };

        var output = new InferenceOutputParser(0.35, Array.Empty<string>()).Parse(outputs, input, "rknn");

        Assert.Contains(output.Detections!, detection => detection.Label == "drivable_area");
        Assert.Contains(output.Detections!, detection => detection.Label == "lane_line");
        Assert.Contains(output.SegmentationMasks!, mask => mask.Label == "drivable_area");
        Assert.Contains(output.SegmentationMasks!, mask => mask.Label == "lane_line");
    }

    [Fact]
    public void InferenceOutputParser_DecodesRideAiRawHeadsAndSegmentationOutputs()
    {
        using var tensor = new NativeFloatTensor(1);
        var input = new InferenceInput("CamFront:ride_ai.onnx", tensor, new[] { 1, 3, 640, 640 }, 640, 640);
        var predS8 = Enumerable.Repeat(-10.0f, 255 * 80 * 80).ToArray();
        WriteRawHeadValue(predS8, 85, 80, 80, 0, 0, 40, 40, 0.0f);
        WriteRawHeadValue(predS8, 85, 80, 80, 0, 1, 40, 40, 0.0f);
        WriteRawHeadValue(predS8, 85, 80, 80, 0, 2, 40, 40, 0.0f);
        WriteRawHeadValue(predS8, 85, 80, 80, 0, 3, 40, 40, 0.0f);
        WriteRawHeadValue(predS8, 85, 80, 80, 0, 4, 40, 40, 8.0f);
        WriteRawHeadValue(predS8, 85, 80, 80, 0, 6, 40, 40, 8.0f);
        var predS16 = Enumerable.Repeat(-10.0f, 255 * 40 * 40).ToArray();
        var predS32 = Enumerable.Repeat(-10.0f, 255 * 20 * 20).ToArray();
        var drivableArea = new float[2 * 4 * 4];
        var laneLine = new float[4 * 4];
        Array.Fill(drivableArea, 1.0f, 0, 4 * 4);
        drivableArea[4 * 4 + 5] = 2.0f;
        laneLine[10] = 1.0f;
        var outputs = new[]
        {
            new InferenceRawTensor("pred_s8", new[] { 1, 255, 80, 80 }, predS8),
            new InferenceRawTensor("pred_s16", new[] { 1, 255, 40, 40 }, predS16),
            new InferenceRawTensor("pred_s32", new[] { 1, 255, 20, 20 }, predS32),
            new InferenceRawTensor("drivable_logits", new[] { 1, 2, 4, 4 }, drivableArea),
            new InferenceRawTensor("lane_logits", new[] { 1, 1, 4, 4 }, laneLine)
        };

        var output = new InferenceOutputParser(0.35, Array.Empty<string>()).Parse(outputs, input, "onnx");

        var vehicle = Assert.Single(output.Detections!, detection => detection.Label == "vehicle");
        Assert.Equal(0.496875, vehicle.X, 6);
        Assert.Equal(0.49375, vehicle.Y, 6);
        Assert.Equal(0.01875, vehicle.Width, 6);
        Assert.Equal(0.025, vehicle.Height, 6);
        Assert.Contains(output.Detections!, detection => detection.Label == "drivable_area");
        Assert.Contains(output.Detections!, detection => detection.Label == "lane_line");
        Assert.Contains(output.SegmentationMasks!, mask => mask.Label == "drivable_area");
        Assert.Contains(output.SegmentationMasks!, mask => mask.Label == "lane_line");
    }

    [Fact]
    public void InferenceOutputParser_DecodesSingleChannelLaneLogitsWithConfiguredThreshold()
    {
        using var tensor = new NativeFloatTensor(1);
        var input = new InferenceInput("CamFront:ride_ai.onnx", tensor, new[] { 1, 3, 640, 640 }, 640, 640);
        var laneLogits = new[] { -2.0f, -1.0f, -0.7f, 0.1f, -0.8f, -2.0f };
        var outputs = new[]
        {
            new InferenceRawTensor("lane_logits", new[] { 1, 1, 2, 3 }, laneLogits)
        };

        var output = new InferenceOutputParser(0.35, Array.Empty<string>()).Parse(outputs, input, "onnx");

        var lane = Assert.Single(output.Detections!);
        Assert.Equal("lane_line", lane.Label);
        Assert.True(lane.Confidence > 0.5);
        var mask = Assert.Single(output.SegmentationMasks!);
        Assert.Equal("lane_line", mask.Label);
        Assert.Equal(255, mask.Data[3]);
    }

    private static void WriteRawHeadValue(
        float[] values,
        int attributes,
        int width,
        int height,
        int anchor,
        int attribute,
        int y,
        int x,
        float value)
    {
        var channel = anchor * attributes + attribute;
        values[channel * width * height + y * width + x] = value;
    }

    [Fact]
    public void InferenceOutputParser_DecodesRenamedTwinLiteNetSegmentationOutputsByOrder()
    {
        using var tensor = new NativeFloatTensor(1);
        var input = new InferenceInput("CamFront:twinlitenet.onnx", tensor, new[] { 1, 3, 360, 640 }, 640, 360);
        var da = new float[2 * 6];
        var ll = new float[2 * 6];
        Array.Fill(da, 1.0f, 0, 6);
        Array.Fill(ll, 1.0f, 0, 6);
        da[1] = 0.0f;
        da[7] = 1.0f;
        ll[3] = 0.0f;
        ll[9] = 1.0f;
        var outputs = new[]
        {
            new InferenceRawTensor("output0", new[] { 1, 2, 2, 3 }, da),
            new InferenceRawTensor("output1", new[] { 1, 2, 2, 3 }, ll)
        };

        var output = new InferenceOutputParser(0.35, Array.Empty<string>()).Parse(outputs, input, "rknn");

        Assert.Contains(output.Detections!, detection => detection.Label == "drivable_area");
        Assert.Contains(output.Detections!, detection => detection.Label == "lane_line");
        Assert.Contains(output.SegmentationMasks!, mask => mask.Label == "drivable_area");
        Assert.Contains(output.SegmentationMasks!, mask => mask.Label == "lane_line");
    }
}
