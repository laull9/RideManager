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
}
