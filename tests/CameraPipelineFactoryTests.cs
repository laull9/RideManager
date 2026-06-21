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
    public async Task CreateCameraSources_WhenOnePhysicalCameraFails_DisablesOnlyThatCamera()
    {
        var cameras = new[]
        {
            CreateCamera(CameraId.CamFront, enabled: true) with { Device = "/dev/video20" },
            CreateCamera(CameraId.CamFace, enabled: true) with { Device = "/dev/video21" }
        };

        var sources = CameraPipelineFactory.CreateCameraSources(
            cameras,
            camera =>
            {
                if (camera.Id == CameraId.CamFace)
                {
                    throw new InvalidOperationException("camera open failed");
                }

                return new EmptyCameraSource();
            });

        var source = Assert.Single(sources);
        Assert.Equal(CameraId.CamFront, source.Key);
        await source.Value.DisposeAsync();
    }

    [Fact]
    public void CreateCameraSources_WhenSharedPhysicalDeviceFails_DisablesAllReadersOnThatDevice()
    {
        var cameras = new[]
        {
            CreateCamera(CameraId.CamFront, enabled: true) with { Device = "/dev/video22" },
            CreateCamera(CameraId.CamFace, enabled: true) with { Device = "/dev/video22" },
            CreateCamera(CameraId.CamBack, enabled: true) with { Device = "synthetic" }
        };

        var sources = CameraPipelineFactory.CreateCameraSources(
            cameras,
            camera =>
            {
                if (camera.Device == "/dev/video22")
                {
                    throw new InvalidOperationException("shared camera open failed");
                }

                return new EmptyCameraSource();
            });

        var source = Assert.Single(sources);
        Assert.Equal(CameraId.CamBack, source.Key);
    }

    [Fact]
    public void CreateFramePreprocessor_UsesOpenCvPreprocessorForFrontObjectDetectionOnly()
    {
        var options = CreateCamera(CameraId.CamFront, enabled: true) with
        {
            ModelName = "yolo26n.onnx",
            Models = new[]
            {
                new CameraModelOptions("yolo26n.onnx", 640, 640, 0.35)
            }
        };

        var preprocessor = CameraPipelineFactory.CreateFramePreprocessor(options);

        Assert.IsType<OpenCvFramePreprocessor>(preprocessor);
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
    public void CreateFramePreprocessor_UsesFullFramePreprocessorForMultiModelCamera()
    {
        var options = CreateCamera(CameraId.CamFront, enabled: true) with
        {
            Models = new[]
            {
                new CameraModelOptions("twinlitenet.onnx", 640, 360, 0.35),
                new CameraModelOptions("yolo26n.onnx", 640, 640, 0.35)
            }
        };

        var preprocessor = CameraPipelineFactory.CreateFramePreprocessor(options);

        Assert.IsType<FullFramePreprocessor>(preprocessor);
    }

    [Fact]
    public void ConfigLoader_ParsesCameraModelList()
    {
        var configPath = Path.Combine(Path.GetTempPath(), $"ridemanager-config-{Guid.NewGuid():N}.toml");
        File.WriteAllText(
            configPath,
            """
            [models]
            backend = "onnx"
            directory = "models"

            [[cameras]]
            id = "CAM_FRONT"
            enabled = true
            device = "synthetic"
            model = "twinlitenet.onnx"
            width = 1280
            height = 720
            input_width = 640
            input_height = 360
            fps = 30
            confidence_threshold = 0.35

            [[cameras.models]]
            model = "twinlitenet.onnx"
            input_width = 640
            input_height = 360
            confidence_threshold = 0.35
            max_fps = 3
            crop_x = 0.0
            crop_y = 0.333333
            crop_width = 1.0
            crop_height = 0.666667

            [[cameras.models]]
            model = "yolo26n.onnx"
            input_width = 640
            input_height = 640
            confidence_threshold = 0.40
            """);
        try
        {
            var config = ConfigLoader.Load(configPath);

            var camera = Assert.Single(config.Cameras);
            Assert.Equal("twinlitenet.onnx", camera.ModelName);
            Assert.Equal(2, camera.EffectiveModels.Count);
            Assert.Equal("twinlitenet.onnx", camera.EffectiveModels[0].ModelName);
            Assert.Equal(360, camera.EffectiveModels[0].InputHeight);
            Assert.Equal(3.0, camera.EffectiveModels[0].MaxFps, 6);
            Assert.Equal(0.333333, camera.EffectiveModels[0].CropY, 6);
            Assert.Equal(0.666667, camera.EffectiveModels[0].CropHeight, 6);
            Assert.Equal("yolo26n.onnx", camera.EffectiveModels[1].ModelName);
            Assert.Equal(640, camera.EffectiveModels[1].InputHeight);
            Assert.Equal(0.40, camera.EffectiveModels[1].ConfidenceThreshold, 6);
        }
        finally
        {
            File.Delete(configPath);
        }
    }

    [Fact]
    public void ConfigLoader_ParsesRearFisheyeRiskOptions()
    {
        var configPath = Path.Combine(Path.GetTempPath(), $"ridemanager-config-{Guid.NewGuid():N}.toml");
        File.WriteAllText(
            configPath,
            """
            [models]
            backend = "onnx"
            directory = "models"

            [[cameras]]
            id = "CAM_BACK"
            enabled = true
            device = "synthetic"
            model = "yolo26n.onnx"
            width = 1280
            height = 720
            input_width = 640
            input_height = 640
            fps = 30
            confidence_threshold = 0.35
            fisheye_fov_degrees = 180
            fisheye_strength = 0.75
            rear_center_danger_angle_degrees = 50
            rear_edge_warning_min_score = 0.22
            """);
        try
        {
            var config = ConfigLoader.Load(configPath);

            var camera = Assert.Single(config.Cameras);
            Assert.Equal(CameraId.CamBack, camera.Id);
            Assert.Equal(180.0, camera.Risk.FisheyeFovDegrees, 6);
            Assert.Equal(0.75, camera.Risk.FisheyeStrength, 6);
            Assert.Equal(50.0, camera.Risk.RearCenterDangerAngleDegrees, 6);
            Assert.Equal(0.22, camera.Risk.RearEdgeWarningMinScore, 6);
        }
        finally
        {
            File.Delete(configPath);
        }
    }

    [Fact]
    public void ConfigLoader_DefaultsBackCameraFisheyeFovTo180Degrees()
    {
        var configPath = Path.Combine(Path.GetTempPath(), $"ridemanager-config-{Guid.NewGuid():N}.toml");
        File.WriteAllText(
            configPath,
            """
            [models]
            backend = "onnx"
            directory = "models"

            [[cameras]]
            id = "CAM_BACK"
            enabled = true
            device = "synthetic"
            model = "yolo26n.onnx"
            width = 1280
            height = 720
            input_width = 640
            input_height = 640
            fps = 30
            confidence_threshold = 0.35
            """);
        try
        {
            var config = ConfigLoader.Load(configPath);

            var camera = Assert.Single(config.Cameras);
            Assert.Equal(CameraId.CamBack, camera.Id);
            Assert.Equal(180.0, camera.Risk.FisheyeFovDegrees, 6);
            Assert.Equal(1.0, camera.Risk.FisheyeStrength, 6);
        }
        finally
        {
            File.Delete(configPath);
        }
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
