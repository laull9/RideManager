namespace RideManager.Models;

/// <summary>
/// 表示统一推理输出。
/// </summary>
public sealed record InferenceOutput(
    IReadOnlyList<string> Labels,
    double Confidence,
    IReadOnlyList<InferenceDetection>? Detections = null,
    IReadOnlyList<InferenceSegmentationMask>? SegmentationMasks = null);

/// <summary>
/// 表示推理输出中的单个目标检测结果。
/// </summary>
public sealed record InferenceDetection(
    string Label,
    double Confidence,
    double X,
    double Y,
    double Width,
    double Height);

/// <summary>
/// 表示推理输出中的二值分割 mask，数据为行优先 0/255。
/// </summary>
public sealed record InferenceSegmentationMask(
    string Label,
    int Width,
    int Height,
    byte[] Data);
