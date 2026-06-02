using System.Buffers;
using System.Runtime.InteropServices;

namespace RideManager.Models;

/// <summary>
/// 表示可直接传给 native 推理运行时的 float32 张量缓冲区。
/// </summary>
public sealed unsafe class NativeFloatTensor : MemoryManager<float>
{
    private const nuint Alignment = 64;
    private void* _pointer;
    private bool _disposed;

    /// <summary>
    /// 创建指定元素数量的 native float32 缓冲区。
    /// </summary>
    public NativeFloatTensor(int length)
    {
        if (length < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(length), "Tensor length must be non-negative.");
        }

        Length = length;
        if (length == 0)
        {
            return;
        }

        var byteCount = checked((nuint)length * (nuint)sizeof(float));
        _pointer = NativeMemory.AlignedAlloc(AlignByteCount(byteCount), Alignment);
        if (_pointer is null)
        {
            throw new OutOfMemoryException($"Failed to allocate {byteCount} bytes for inference tensor.");
        }
    }

    /// <summary>
    /// 获取张量元素数量。
    /// </summary>
    public int Length { get; }

    /// <summary>
    /// 获取 native 缓冲区首地址，可传递给 RKNN/C++ 桥接层。
    /// </summary>
    public IntPtr Pointer
    {
        get
        {
            ThrowIfDisposed();
            return (IntPtr)_pointer;
        }
    }

    /// <summary>
    /// 获取可写 Span，用于预处理阶段直接填充 native 内存。
    /// </summary>
    public Span<float> Span => GetSpan();

    /// <summary>
    /// 获取当前 native 缓冲区上的托管 Memory 视图。
    /// </summary>
    public override Span<float> GetSpan()
    {
        ThrowIfDisposed();
        return new Span<float>(_pointer, Length);
    }

    /// <summary>
    /// 返回已固定的 native 内存句柄。
    /// </summary>
    public override MemoryHandle Pin(int elementIndex = 0)
    {
        ThrowIfDisposed();
        if ((uint)elementIndex > (uint)Length)
        {
            throw new ArgumentOutOfRangeException(nameof(elementIndex));
        }

        return new MemoryHandle((float*)_pointer + elementIndex, default, this);
    }

    /// <summary>
    /// native 内存无需解除 GC 固定。
    /// </summary>
    public override void Unpin()
    {
    }

    /// <summary>
    /// 释放 native 缓冲区。
    /// </summary>
    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// 释放 native 缓冲区。
    /// </summary>
    protected override void Dispose(bool disposing)
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_pointer is not null)
        {
            NativeMemory.AlignedFree(_pointer);
            _pointer = null;
        }
    }

    /// <summary>
    /// 将分配大小补齐到 native 对齐要求。
    /// </summary>
    private static nuint AlignByteCount(nuint byteCount)
    {
        return ((byteCount + Alignment - 1) / Alignment) * Alignment;
    }

    /// <summary>
    /// 确保缓冲区仍处于可用状态。
    /// </summary>
    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }
}
