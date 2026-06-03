using RideManager.Camera;
using RideManager.Models;
using RideManager.Utils;
using Xunit;

namespace RideManager.Tests;

public sealed class CameraPipelineFactoryTests
{
    [Fact]
    public void GetEnabledCameraOptionsInPreferredOrder_ReturnsOnlyEnabledCameras()
    {
        var cameras = new[]
        {
            CreateCamera(CameraId.CamFront, enabled: false),
            CreateCamera(CameraId.CamFace, enabled: true),
            CreateCamera(CameraId.CamBack, enabled: false)
        };

        var ordered = CameraPipelineFactory.GetEnabledCameraOptionsInPreferredOrder(cameras);

        var camera = Assert.Single(ordered);
        Assert.Equal(CameraId.CamFace, camera.Id);
    }

    [Fact]
    public void GetEnabledCameraOptionsInPreferredOrder_UsesStableFrontFaceBackOrder()
    {
        var cameras = new[]
        {
            CreateCamera(CameraId.CamBack, enabled: true),
            CreateCamera(CameraId.CamFront, enabled: true),
            CreateCamera(CameraId.CamFace, enabled: false)
        };

        var ordered = CameraPipelineFactory.GetEnabledCameraOptionsInPreferredOrder(cameras);

        Assert.Collection(
            ordered,
            camera => Assert.Equal(CameraId.CamFront, camera.Id),
            camera => Assert.Equal(CameraId.CamBack, camera.Id));
    }

    [Fact]
    public void PrepareLiveTestCameraOptions_EnablesOnlySelectedCameraAndOverridesItsSource()
    {
        var cameras = new[]
        {
            CreateCamera(CameraId.CamFront, enabled: true),
            CreateCamera(CameraId.CamFace, enabled: true),
            CreateCamera(CameraId.CamBack, enabled: false)
        };

        var prepared = CameraPipelineFactory.PrepareLiveTestCameraOptions(
            cameras,
            CameraId.CamFront,
            "videos/test1.mp4");

        Assert.Collection(
            prepared,
            camera =>
            {
                Assert.Equal(CameraId.CamFront, camera.Id);
                Assert.True(camera.Enabled);
                Assert.Equal("videos/test1.mp4", camera.Device);
            },
            camera =>
            {
                Assert.Equal(CameraId.CamFace, camera.Id);
                Assert.False(camera.Enabled);
            },
            camera =>
            {
                Assert.Equal(CameraId.CamBack, camera.Id);
                Assert.False(camera.Enabled);
            });
    }

    [Fact]
    public async Task CreateCameraSources_OpensSharedPhysicalDeviceOnlyOnce()
    {
        var cameras = new[]
        {
            CreateCamera(CameraId.CamFront, enabled: true) with { Device = "/dev/video23" },
            CreateCamera(CameraId.CamFace, enabled: true) with { Device = "/dev/video23" }
        };
        var createdSources = 0;

        var sources = CameraPipelineFactory.CreateCameraSources(
            cameras,
            _ =>
            {
                createdSources++;
                return new EmptyCameraSource();
            });

        Assert.Equal(1, createdSources);
        Assert.Equal(2, sources.Count);

        foreach (var source in sources.Values)
        {
            await source.DisposeAsync();
        }
    }

    [Fact]
    public void CreateFramePreprocessor_UsesFacePipelinePreprocessorForFaceLandmarkModel()
    {
        var options = CreateCamera(CameraId.CamFace, enabled: true) with
        {
            ModelName = "pfld_lite.onnx",
            InputWidth = 112,
            InputHeight = 112
        };

        var preprocessor = CameraPipelineFactory.CreateFramePreprocessor(options);

        Assert.IsType<FacePipelineFramePreprocessor>(preprocessor);
    }

    [Fact]
    public void CreateAnalyzer_UsesRknnWrapperForYuNetAndPfldWhenTomlBackendIsRknn()
    {
        var configPath = Path.Combine(Path.GetTempPath(), $"ridemanager-config-{Guid.NewGuid():N}.toml");
        File.WriteAllText(
            configPath,
            """
            [models]
            backend = "rknn"
            directory = "models"

            [[cameras]]
            id = "CAM_FACE"
            enabled = true
            device = "synthetic"
            model = "pfld_lite.onnx"
            width = 640
            height = 480
            input_width = 112
            input_height = 112
            fps = 10
            confidence_threshold = 0.60
            """);
        try
        {
            var config = ConfigLoader.Load(configPath);
            var options = Assert.Single(config.Cameras);
            var selector = new ModelRuntimeSelector(config.Models);
            var landmarkEngine = selector.Create(options.ModelName, options.ConfidenceThreshold);

            using var analyzer = Assert.IsType<FaceCameraAnalyzer>(
                CameraPipelineFactory.CreateAnalyzer(options, selector, landmarkEngine));

            Assert.IsType<RknnInferenceEngine>(analyzer.FaceDetectorEngine);
            Assert.IsType<RknnInferenceEngine>(analyzer.LandmarkEngine);
        }
        finally
        {
            File.Delete(configPath);
        }
    }

    private static CameraOptions CreateCamera(CameraId id, bool enabled)
    {
        return new CameraOptions(
            id,
            enabled,
            "synthetic",
            "model.onnx",
            1280,
            720,
            640,
            640,
            30,
            0.35);
    }

    private sealed class EmptyCameraSource : ICameraSource
    {
        public long DroppedFrames => 0;

        public Task<CameraFrame?> ReadLatestAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult<CameraFrame?>(null);
        }

        public ValueTask DisposeAsync()
        {
            return ValueTask.CompletedTask;
        }
    }
}
