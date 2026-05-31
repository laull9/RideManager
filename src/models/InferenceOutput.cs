namespace RideManager.Models;

/// <summary>
/// 表示统一推理输出。
/// </summary>
public sealed record InferenceOutput(IReadOnlyList<string> Labels, double Confidence);
