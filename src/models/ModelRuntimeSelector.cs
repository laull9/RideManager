using RideManager.Utils;

namespace RideManager.Models;

/// <summary>
/// 根据配置创建对应的推理运行时。
/// </summary>
public sealed class ModelRuntimeSelector
{
    private readonly ModelOptions _options;

    /// <summary>
    /// 创建推理运行时选择器。
    /// </summary>
    public ModelRuntimeSelector(ModelOptions options)
    {
        _options = options;
    }

    /// <summary>
    /// 为指定模型创建推理引擎。
    /// </summary>
    public IInferenceEngine Create(string modelName, double confidenceThreshold)
    {
        var modelPath = Path.Combine(_options.Directory, modelName);
        ValidateConfiguredModelPath(modelPath);
        var resolvedPath = _options.Backend == ModelBackend.Rknn
            ? ResolveRknnModelPath(modelPath)
            : modelPath;
        Console.WriteLine(
            $"Model runtime backend={_options.Backend.ToString().ToLowerInvariant()} model={Path.GetFullPath(resolvedPath)}");

        return _options.Backend switch
        {
            ModelBackend.Rknn => new RknnInferenceEngine(resolvedPath, confidenceThreshold),
            _ => new OnnxInferenceEngine(resolvedPath, confidenceThreshold)
        };
    }

    /// <summary>
    /// 阻止将 RKNN 文件交给 ONNX Runtime，并给出可直接修复的配置提示。
    /// </summary>
    private void ValidateConfiguredModelPath(string configuredPath)
    {
        if (_options.Backend == ModelBackend.Onnx
            && string.Equals(Path.GetExtension(configuredPath), ".rknn", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Model backend is onnx but model is an RKNN file: {Path.GetFullPath(configuredPath)}. "
                + "Set [models] backend = \"rknn\".");
        }
    }

    /// <summary>
    /// 后端选择 RKNN 时始终使用同名 .rknn 模型，便于配置沿用 ONNX 模型名。
    /// </summary>
    private static string ResolveRknnModelPath(string configuredPath)
    {
        if (string.Equals(Path.GetExtension(configuredPath), ".rknn", StringComparison.OrdinalIgnoreCase))
        {
            return configuredPath;
        }

        return Path.ChangeExtension(configuredPath, ".rknn");
    }
}
