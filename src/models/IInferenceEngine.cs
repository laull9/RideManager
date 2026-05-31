namespace RideManager.Models;

/// <summary>
/// 定义 ONNX 与 RKNN 共用的推理接口。
/// </summary>
public interface IInferenceEngine
{
    /// <summary>
    /// 运行一次模型推理。
    /// </summary>
    Task<InferenceOutput> RunAsync(InferenceInput input, CancellationToken cancellationToken);
}
