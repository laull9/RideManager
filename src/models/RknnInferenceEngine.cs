namespace RideManager.Models;

/// <summary>
/// 通过 C++ 桥接层提供 RKNN Runtime 推理实现。
/// </summary>
public sealed class RknnInferenceEngine : IInferenceEngine, IDisposable
{
    private readonly string _modelPath;
    private readonly double _confidenceThreshold;
    private readonly object _gate = new();
    private IntPtr _context;
    private string? _loadError;
    private bool _disposed;

    /// <summary>
    /// 创建 RKNN 推理引擎。
    /// </summary>
    public RknnInferenceEngine(string modelPath, double confidenceThreshold)
    {
        _modelPath = modelPath;
        _confidenceThreshold = Math.Clamp(confidenceThreshold, 0.0, 1.0);
    }

    /// <summary>
    /// 使用 native RKNN Runtime 运行一次推理，模型或桥接库缺失时返回可诊断结果。
    /// </summary>
    public Task<InferenceOutput> RunAsync(InferenceInput input, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(_disposed, this);

        var context = GetContext();
        if (context == IntPtr.Zero)
        {
            var reason = File.Exists(_modelPath) ? _loadError ?? "load_failed" : "model_missing";
            return Task.FromResult(new InferenceOutput(new[] { $"rknn:{Path.GetFileName(_modelPath)}:{reason}" }, 0.0));
        }

        lock (_gate)
        {
            int runStatus;
            unsafe
            {
                var nativeInputs = stackalloc RknnNative.RknnInputTensor[1];
                nativeInputs[0] = new RknnNative.RknnInputTensor
                {
                    Index = 0,
                    Data = input.TensorDataPointer,
                    ElementCount = input.TensorElementCount,
                    Type = RknnNative.RknnTensorType.Float32,
                    Format = RknnNative.RknnTensorFormat.Nchw
                };
                runStatus = RknnNative.Run(context, nativeInputs, 1);
            }

            if (runStatus != 0)
            {
                return Task.FromResult(new InferenceOutput(new[] { $"rknn:{RknnNative.GetLastError(context)}" }, 0.0));
            }

            var outputs = ReadOutputs(context);
            var labels = ReadLabels();
            var result = new InferenceOutputParser(_confidenceThreshold, labels).Parse(outputs, input, "rknn");
            return Task.FromResult(result);
        }
    }

    /// <summary>
    /// 释放 RKNN native 上下文。
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_context != IntPtr.Zero)
        {
            RknnNative.Destroy(_context);
            _context = IntPtr.Zero;
        }
    }

    /// <summary>
    /// 懒加载 RKNN native 上下文。
    /// </summary>
    private IntPtr GetContext()
    {
        if (_context != IntPtr.Zero || _loadError is not null || !File.Exists(_modelPath))
        {
            return _context;
        }

        lock (_gate)
        {
            if (_context != IntPtr.Zero || _loadError is not null)
            {
                return _context;
            }

            try
            {
                var status = RknnNative.Create(_modelPath, out _context);
                if (status != 0 || _context == IntPtr.Zero)
                {
                    _loadError = RknnNative.GetLastError(IntPtr.Zero);
                    _context = IntPtr.Zero;
                }
            }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
            {
                _loadError = ex.GetType().Name;
            }

            return _context;
        }
    }

    /// <summary>
    /// 读取 native 桥接层保留的当前推理输出。
    /// </summary>
    private static unsafe IReadOnlyList<InferenceRawTensor> ReadOutputs(IntPtr context)
    {
        var count = RknnNative.GetOutputCount(context);
        if (count <= 0)
        {
            return Array.Empty<InferenceRawTensor>();
        }

        var outputs = new List<InferenceRawTensor>(count);
        for (var outputIndex = 0; outputIndex < count; outputIndex++)
        {
            var metadata = new RknnNative.RknnTensorMetadata();
            var metadataStatus = RknnNative.GetOutputMetadata(context, outputIndex, &metadata);
            if (metadataStatus != 0)
            {
                continue;
            }

            var dataStatus = RknnNative.GetOutputData(context, outputIndex, out var dataPointer, out var elementCount);
            if (dataStatus != 0 || dataPointer == IntPtr.Zero || elementCount <= 0)
            {
                continue;
            }

            var values = new float[elementCount];
            System.Runtime.InteropServices.Marshal.Copy(dataPointer, values, 0, elementCount);
            outputs.Add(new InferenceRawTensor(metadata.GetName(), metadata.GetDimensions(), values));
        }

        return outputs;
    }

    /// <summary>
    /// 从 RKNN 模型同名 sidecar 文件读取类别名。
    /// </summary>
    private IReadOnlyList<string> ReadLabels()
    {
        var sidecarPath = Path.ChangeExtension(_modelPath, ".labels.txt");
        if (!File.Exists(sidecarPath))
        {
            return Array.Empty<string>();
        }

        return File.ReadAllLines(sidecarPath)
            .Select(label => label.Trim())
            .Where(label => !string.IsNullOrWhiteSpace(label))
            .ToArray();
    }
}
