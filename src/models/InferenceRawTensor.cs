namespace RideManager.Models;

/// <summary>
/// 表示 native 或 ONNX Runtime 返回的一路 float32 原始输出张量。
/// </summary>
internal sealed record InferenceRawTensor(
    string Name,
    IReadOnlyList<int> Dimensions,
    ReadOnlyMemory<float> Values);
