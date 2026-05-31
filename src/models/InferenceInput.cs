namespace RideManager.Models;

/// <summary>
/// 表示统一推理输入。
/// </summary>
public sealed record InferenceInput(string SourceName, ReadOnlyMemory<byte> TensorData);
