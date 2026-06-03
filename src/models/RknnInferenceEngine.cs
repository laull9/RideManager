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
    private string? _loadDiagnostic;
    private string? _lastReportedDiagnostic;
    private bool _reportedOutputMetadata;
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
            ReportDiagnostic(_loadDiagnostic ?? reason);
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
                var error = RknnNative.GetLastError(context);
                ReportDiagnostic(error);
                return Task.FromResult(new InferenceOutput(new[] { $"rknn:{error}" }, 0.0));
            }

            ReportOutputMetadata(context);
            if (!TryReadOutputs(context, out var outputs, out var outputError))
            {
                ReportDiagnostic(outputError);
                return Task.FromResult(new InferenceOutput(new[] { $"rknn:{outputError}" }, 0.0));
            }

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
                else
                {
                    ReportModelMetadata(_context);
                }
            }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
            {
                _loadError = ex.GetType().Name;
                _loadDiagnostic = CreateNativeLoadDiagnostic(ex);
            }

            return _context;
        }
    }

    /// <summary>
    /// 读取 native 桥接层保留的当前推理输出。
    /// </summary>
    private static unsafe bool TryReadOutputs(
        IntPtr context,
        out IReadOnlyList<InferenceRawTensor> outputs,
        out string error)
    {
        var count = RknnNative.GetOutputCount(context);
        if (count <= 0)
        {
            outputs = Array.Empty<InferenceRawTensor>();
            error = "output_count_zero";
            return false;
        }

        var result = new List<InferenceRawTensor>(count);
        for (var outputIndex = 0; outputIndex < count; outputIndex++)
        {
            var metadata = new RknnNative.RknnTensorMetadata();
            var metadataStatus = RknnNative.GetOutputMetadata(context, outputIndex, &metadata);
            if (metadataStatus != 0)
            {
                outputs = Array.Empty<InferenceRawTensor>();
                error = $"output_{outputIndex}_metadata_failed:{RknnNative.GetLastError(context)}";
                return false;
            }

            var dataStatus = RknnNative.GetOutputData(context, outputIndex, out var dataPointer, out var elementCount);
            if (dataStatus != 0 || dataPointer == IntPtr.Zero || elementCount <= 0)
            {
                outputs = Array.Empty<InferenceRawTensor>();
                error = $"output_{outputIndex}_data_failed:{RknnNative.GetLastError(context)}";
                return false;
            }

            var dimensions = metadata.GetDimensions();
            var metadataElementCount = GetElementCount(dimensions);
            if (metadataElementCount > 0 && metadataElementCount != elementCount)
            {
                outputs = Array.Empty<InferenceRawTensor>();
                error = $"output_{outputIndex}_size_mismatch:metadata={metadataElementCount},data={elementCount}";
                return false;
            }

            var values = new float[elementCount];
            System.Runtime.InteropServices.Marshal.Copy(dataPointer, values, 0, elementCount);
            result.Add(new InferenceRawTensor(metadata.GetName(), dimensions, values));
        }

        outputs = result;
        error = string.Empty;
        return true;
    }

    /// <summary>
    /// 首次加载模型时打印 RKNN Runtime 实际暴露的输入张量。
    /// </summary>
    private void ReportModelMetadata(IntPtr context)
    {
        var inputs = ReadTensorMetadata(context, RknnNative.GetInputCount(context), input: true);
        Console.WriteLine($"RKNN loaded model={_modelPath} inputs=[{string.Join(", ", inputs)}]");
    }

    /// <summary>
    /// 首次成功推理后打印 RKNN Runtime 实际暴露的输出张量。
    /// </summary>
    private void ReportOutputMetadata(IntPtr context)
    {
        if (_reportedOutputMetadata)
        {
            return;
        }

        var outputs = ReadTensorMetadata(context, RknnNative.GetOutputCount(context), input: false);
        Console.WriteLine($"RKNN outputs model={_modelPath} outputs=[{string.Join(", ", outputs)}]");
        _reportedOutputMetadata = true;
    }

    /// <summary>
    /// 读取一组输入或输出张量描述，供启动诊断显示。
    /// </summary>
    private static unsafe IReadOnlyList<string> ReadTensorMetadata(IntPtr context, int count, bool input)
    {
        var descriptions = new List<string>(Math.Max(0, count));
        for (var index = 0; index < count; index++)
        {
            var metadata = new RknnNative.RknnTensorMetadata();
            var status = input
                ? RknnNative.GetInputMetadata(context, index, &metadata)
                : RknnNative.GetOutputMetadata(context, index, &metadata);
            if (status != 0)
            {
                descriptions.Add($"{index}:metadata_failed");
                continue;
            }

            descriptions.Add(
                $"{metadata.GetName()}:{string.Join('x', metadata.GetDimensions())}:{metadata.Type}:{metadata.Format}");
        }

        return descriptions;
    }

    /// <summary>
    /// 计算张量维度声明的元素数量。
    /// </summary>
    private static int GetElementCount(IReadOnlyList<int> dimensions)
    {
        if (dimensions.Count == 0 || dimensions.Any(dimension => dimension <= 0))
        {
            return 0;
        }

        var count = 1L;
        foreach (var dimension in dimensions)
        {
            count *= dimension;
            if (count > int.MaxValue)
            {
                return 0;
            }
        }

        return (int)count;
    }

    /// <summary>
    /// 将 P/Invoke 加载异常扩展为包含部署路径和排查命令的诊断信息。
    /// </summary>
    private static string CreateNativeLoadDiagnostic(Exception exception)
    {
        var bridgePath = Path.Combine(AppContext.BaseDirectory, "libridemanager_rknn.so");
        var buildBridgePath = Path.Combine(Environment.CurrentDirectory, "cpp_rknn", "build", "libridemanager_rknn.so");
        var bridgeState = File.Exists(bridgePath) ? "present" : "missing";
        var buildBridgeState = File.Exists(buildBridgePath) ? "present" : "missing";
        return $"{exception.GetType().Name}: {exception.Message} "
            + $"output_bridge={bridgePath} ({bridgeState}), build_bridge={buildBridgePath} ({buildBridgeState}). "
            + "Build with: cmake -S cpp_rknn -B cpp_rknn/build -DRKNN_RUNTIME_DIR=/path/to/rknn_runtime && "
            + "cmake --build cpp_rknn/build --config Release. "
            + $"If a bridge is present, run: ldd {buildBridgePath}";
    }

    /// <summary>
    /// 相同错误只打印一次，避免 live test 每帧刷屏。
    /// </summary>
    private void ReportDiagnostic(string diagnostic)
    {
        if (string.Equals(_lastReportedDiagnostic, diagnostic, StringComparison.Ordinal))
        {
            return;
        }

        _lastReportedDiagnostic = diagnostic;
        Console.WriteLine($"RKNN diagnostic model={_modelPath}: {diagnostic}");
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
