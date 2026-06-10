using RideManager.Models;
using RideManager.Utils;

namespace RideManager.Camera;

/// <summary>
/// 负责按配置创建摄像头处理链路。
/// </summary>
public static class CameraPipelineFactory
{
    private static readonly CameraId[] PreferredCameraOrder =
    {
        CameraId.CamFront,
        CameraId.CamFace,
        CameraId.CamBack
    };

    /// <summary>
    /// 按启用配置创建摄像头链路，并保持固定的前、面、后顺序。
    /// </summary>
    public static IReadOnlyList<CameraPipeline> CreateCameraPipelines(
        IEnumerable<CameraOptions> cameraOptions,
        ModelRuntimeSelector runtimeSelector)
    {
        var enabledOptions = GetEnabledCameraOptionsInPreferredOrder(cameraOptions);
        var sources = CreateCameraSources(enabledOptions, CreateCameraSource);

        return enabledOptions
            .Where(options => sources.ContainsKey(options.Id))
            .Select(options => CreatePipeline(options, sources[options.Id], runtimeSelector))
            .ToArray();
    }

    /// <summary>
    /// 提取已启用的摄像头配置，并保持固定的前、面、后顺序。
    /// </summary>
    internal static IReadOnlyList<CameraOptions> GetEnabledCameraOptionsInPreferredOrder(
        IEnumerable<CameraOptions> cameraOptions)
    {
        var enabledCameras = cameraOptions
            .Where(camera => camera.Enabled)
            .ToDictionary(camera => camera.Id);

        return PreferredCameraOrder
            .Where(enabledCameras.ContainsKey)
            .Select(cameraId => enabledCameras[cameraId])
            .ToArray();
    }

    /// <summary>
    /// 为 live test 应用单摄像头筛选和可选输入源覆盖。
    /// </summary>
    internal static IReadOnlyList<CameraOptions> PrepareLiveTestCameraOptions(
        IEnumerable<CameraOptions> cameraOptions,
        CameraId? cameraId,
        string? source)
    {
        var hasSource = !string.IsNullOrWhiteSpace(source);
        var targetCamera = cameraId ?? (hasSource ? CameraId.CamFront : (CameraId?)null);
        if (targetCamera is null)
        {
            return cameraOptions.ToArray();
        }

        return cameraOptions
            .Select(camera =>
            {
                if (camera.Id == targetCamera.Value)
                {
                    return camera with
                    {
                        Device = hasSource ? source! : camera.Device,
                        Enabled = true
                    };
                }

                return cameraId is not null
                    ? camera with { Enabled = false }
                    : camera;
            })
            .ToArray();
    }

    /// <summary>
    /// 创建单路摄像头的采集、预处理、推理分析链路。
    /// </summary>
    private static CameraPipeline CreatePipeline(
        CameraOptions options,
        ICameraSource source,
        ModelRuntimeSelector runtimeSelector)
    {
        return new CameraPipeline(
            options.Id,
            source,
            CreateFramePreprocessor(options),
            CreateAnalyzer(options, runtimeSelector));
    }

    /// <summary>
    /// 为相同真实输入设备创建单个底层采集源，并向多条管线广播最新帧。
    /// </summary>
    internal static IReadOnlyDictionary<CameraId, ICameraSource> CreateCameraSources(
        IReadOnlyList<CameraOptions> cameraOptions,
        Func<CameraOptions, ICameraSource> sourceFactory)
    {
        var sources = new Dictionary<CameraId, ICameraSource>();
        foreach (var group in cameraOptions.GroupBy(camera => camera.Device, StringComparer.OrdinalIgnoreCase))
        {
            var groupedOptions = group.ToArray();
            if (groupedOptions.Length == 1 || IsSyntheticDevice(group.Key))
            {
                foreach (var options in groupedOptions)
                {
                    var source = TryCreateCameraSource(options, sourceFactory);
                    if (source is not null)
                    {
                        sources.Add(options.Id, source);
                    }
                }

                continue;
            }

            var sharedSource = TryCreateCameraSource(groupedOptions[0], sourceFactory);
            if (sharedSource is null)
            {
                foreach (var options in groupedOptions.Skip(1))
                {
                    Console.WriteLine($"Warning: camera {options.Id} disabled because shared device {group.Key} is unavailable.");
                }

                continue;
            }

            var broadcastSource = new BroadcastCameraSource(sharedSource);
            foreach (var options in groupedOptions)
            {
                sources.Add(options.Id, broadcastSource.CreateReader(options.Id));
            }
        }

        return sources;
    }

    /// <summary>
    /// 按模型类型创建图像预处理器。
    /// </summary>
    internal static IFramePreprocessor CreateFramePreprocessor(CameraOptions options)
    {
        if (options.EffectiveModels.Count > 1)
        {
            return new FullFramePreprocessor(options);
        }

        return IsPfldModel(options.ModelName)
            ? new FacePipelineFramePreprocessor(options)
            : new OpenCvFramePreprocessor(options);
    }

    /// <summary>
    /// 按模型类型创建图像分析器。
    /// </summary>
    internal static ICameraAnalyzer CreateAnalyzer(
        CameraOptions options,
        ModelRuntimeSelector runtimeSelector,
        IInferenceEngine inferenceEngine)
    {
        return IsPfldModel(options.ModelName)
            ? new FaceCameraAnalyzer(
                options.Id,
                runtimeSelector.Create(FaceCameraAnalyzer.FaceDetectorModelName, options.ConfidenceThreshold),
                inferenceEngine,
                options.InputWidth,
                options.InputHeight)
            : new CameraAnalyzer(options.Id, inferenceEngine);
    }

    /// <summary>
    /// 按模型类型创建图像分析器。
    /// </summary>
    private static ICameraAnalyzer CreateAnalyzer(
        CameraOptions options,
        ModelRuntimeSelector runtimeSelector)
    {
        var models = options.EffectiveModels;
        if (models.Count > 1)
        {
            var runners = models
                .Select(model => new MultiModelCameraAnalyzer.ModelRunner(
                    model.ModelName,
                    Math.Max(1, model.InputWidth),
                    Math.Max(1, model.InputHeight),
                    model.MaxFps,
                    model.CropX,
                    model.CropY,
                    model.CropWidth,
                    model.CropHeight,
                    runtimeSelector.Create(model.ModelName, model.ConfidenceThreshold)))
                .ToArray();
            return new MultiModelCameraAnalyzer(options.Id, runners);
        }

        var modelOptions = models[0];
        var inferenceEngine = runtimeSelector.Create(modelOptions.ModelName, modelOptions.ConfidenceThreshold);
        return CreateAnalyzer(options, runtimeSelector, inferenceEngine);
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

        return new OpenCvCameraSource(options);
    }

    /// <summary>
    /// 尝试创建摄像头源，真实设备不可用时禁用对应链路。
    /// </summary>
    private static ICameraSource? TryCreateCameraSource(CameraOptions options, Func<CameraOptions, ICameraSource> sourceFactory)
    {
        try
        {
            return sourceFactory(options);
        }
        catch (Exception ex) when (IsOpenCvCaptureUnavailable(ex))
        {
            Console.WriteLine($"Warning: camera {options.Id} disabled because source '{options.Device}' is unavailable: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// 判断 OpenCV 采集模块是否因为 native runtime 不完整或设备不可用而无法启动。
    /// </summary>
    private static bool IsOpenCvCaptureUnavailable(Exception ex)
    {
        return ex is InvalidOperationException
            or OpenCvSharp.OpenCVException
            or DllNotFoundException
            or EntryPointNotFoundException
            || ex is TypeInitializationException { InnerException: DllNotFoundException or EntryPointNotFoundException };
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
    /// 判断模型是否为 PFLD 人脸关键点模型。
    /// </summary>
    private static bool IsPfldModel(string modelName)
    {
        return Path.GetFileName(modelName).Contains("pfld", StringComparison.OrdinalIgnoreCase);
    }
}
