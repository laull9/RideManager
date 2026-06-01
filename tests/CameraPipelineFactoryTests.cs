using RideManager.Camera;
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
}
