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
}
