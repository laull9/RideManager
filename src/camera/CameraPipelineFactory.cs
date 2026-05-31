using RideManager.Models;
using RideManager.Utils;

namespace RideManager.Camera;

/// <summary>
/// 负责创建系统固定的三路摄像头处理链路。
/// </summary>
public static class CameraPipelineFactory
{
    private static readonly CameraId[] RequiredCameras =
    {
        CameraId.CamFront,
        CameraId.CamFace,
        CameraId.CamBack
    };

    /// <summary>
    /// 创建前向、面部、后向三路摄像头链路。
    /// </summary>
    public static IReadOnlyList<CameraPipeline> CreateThreeCameraPipelines(
        IEnumerable<CameraOptions> cameraOptions,
        ModelRuntimeSelector runtimeSelector)
    {
        var enabledCameras = cameraOptions
            .Where(camera => camera.Enabled)
            .ToDictionary(camera => camera.Id);

        return RequiredCameras
            .Select(cameraId => CreatePipeline(GetRequiredCamera(enabledCameras, cameraId), runtimeSelector))
            .ToArray();
    }

    /// <summary>
    /// 创建单路摄像头的采集、预处理、推理分析链路。
    /// </summary>
    private static CameraPipeline CreatePipeline(CameraOptions options, ModelRuntimeSelector runtimeSelector)
    {
        var inferenceEngine = runtimeSelector.Create(options.ModelName);
        return new CameraPipeline(
            options.Id,
            new SimulatedCameraSource(options),
            new OpenCvFramePreprocessor(options.Id),
            new CameraAnalyzer(options.Id, inferenceEngine));
    }

    /// <summary>
    /// 获取必需的摄像头配置，缺失时直接失败以暴露配置问题。
    /// </summary>
    private static CameraOptions GetRequiredCamera(
        IReadOnlyDictionary<CameraId, CameraOptions> enabledCameras,
        CameraId cameraId)
    {
        if (enabledCameras.TryGetValue(cameraId, out var options))
        {
            return options;
        }

        throw new InvalidOperationException($"Missing enabled camera pipeline config: {cameraId}");
    }
}
