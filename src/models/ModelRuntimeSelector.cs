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
    /// 获取模型目录。
    /// </summary>
    public string ModelDirectory => _options.Directory;

    /// <summary>
    /// 为指定模型创建推理引擎。
    /// </summary>
    public IInferenceEngine Create(string modelName, double confidenceThreshold)
    {
        var modelPath = Path.Combine(_options.Directory, modelName);
        return _options.Backend switch
        {
            ModelBackend.Rknn => new RknnInferenceEngine(modelPath),
            _ => new OnnxInferenceEngine(modelPath, confidenceThreshold)
        };
    }
}
