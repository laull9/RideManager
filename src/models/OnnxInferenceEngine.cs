namespace RideManager.Models;

/// <summary>
/// 提供 ONNX Runtime 推理占位实现。
/// </summary>
public sealed class OnnxInferenceEngine : IInferenceEngine
{
    private readonly string _modelPath;

    /// <summary>
    /// 创建 ONNX 推理引擎。
    /// </summary>
    public OnnxInferenceEngine(string modelPath)
    {
        _modelPath = modelPath;
    }

    /// <summary>
    /// 返回占位推理结果，后续接入 Microsoft.ML.OnnxRuntime。
    /// </summary>
    public Task<InferenceOutput> RunAsync(InferenceInput input, CancellationToken cancellationToken)
    {
        return Task.FromResult(new InferenceOutput(new[] { $"onnx:{Path.GetFileName(_modelPath)}" }, 0.0));
    }
}
