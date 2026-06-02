namespace RideManager.Models;

/// <summary>
/// 表示统一推理输入。
/// </summary>
public sealed record InferenceInput(
    string SourceName,
    NativeFloatTensor Tensor,
    IReadOnlyList<int> TensorDimensions,
    int OriginalWidth,
    int OriginalHeight)
{
    /// <summary>
    /// 获取模型输入张量数据的 Memory 视图。
    /// </summary>
    public Memory<float> TensorData => Tensor.Memory;

    /// <summary>
    /// 获取模型输入张量 native 首地址。
    /// </summary>
    public IntPtr TensorDataPointer => Tensor.Pointer;

    /// <summary>
    /// 获取模型输入张量元素数量。
    /// </summary>
    public int TensorElementCount => Tensor.Length;
}
