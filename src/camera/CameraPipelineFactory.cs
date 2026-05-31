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
        var inferenceEngine = runtimeSelector.Create(options.ModelName, options.ConfidenceThreshold);
        return new CameraPipeline(
            options.Id,
            CreateCameraSource(options),
            new OpenCvFramePreprocessor(options),
            new CameraAnalyzer(options.Id, inferenceEngine));
    }

    /// <summary>
    /// 创建真实摄像头源；配置为 synthetic 或真实设备不可用时回退到合成源。
    /// </summary>
    private static ICameraSource CreateCameraSource(CameraOptions options)
    {
        if (IsSyntheticDevice(options.Device))
        {
            return new SimulatedCameraSource(options);
        }

        try
        {
            return new OpenCvCameraSource(options);
        }
        catch (Exception ex) when (ex is InvalidOperationException or OpenCvSharp.OpenCVException)
        {
            Console.WriteLine($"Camera {options.Id} fallback to synthetic source: {ex.Message}");
            return new SimulatedCameraSource(options);
        }
    }

    /// <summary>
    /// 判断摄像头配置是否显式要求使用合成源。
    /// </summary>
    private static bool IsSyntheticDevice(string device)
    {
        return device.Equals("synthetic", StringComparison.OrdinalIgnoreCase)
            || device.Equals("simulated", StringComparison.OrdinalIgnoreCase)
            || device.StartsWith("synthetic://", StringComparison.OrdinalIgnoreCase)
            || device.StartsWith("simulated://", StringComparison.OrdinalIgnoreCase);
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
