namespace RideManager.Models;

/// <summary>
/// 提供 RKNN 推理占位实现。
/// </summary>
public sealed class RknnInferenceEngine : IInferenceEngine
{
    private readonly string _modelPath;

    /// <summary>
    /// 创建 RKNN 推理引擎。
    /// </summary>
    public RknnInferenceEngine(string modelPath)
    {
        _modelPath = modelPath;
    }

    /// <summary>
    /// 返回占位推理结果，后续接入 RKNN Runtime。
    /// </summary>
    public Task<InferenceOutput> RunAsync(InferenceInput input, CancellationToken cancellationToken)
    {
        return Task.FromResult(new InferenceOutput(new[] { $"rknn:{Path.GetFileName(_modelPath)}" }, 0.0));
    }
}
