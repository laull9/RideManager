using System.Diagnostics;

namespace RideManager.Camera;

/// <summary>
/// 串联单路摄像头的采集、预处理和分析流程。
/// </summary>
public sealed class CameraPipeline : IAsyncDisposable
{
    private readonly ICameraSource _source;
    private readonly IFramePreprocessor _preprocessor;
    private readonly ICameraAnalyzer _analyzer;
    private readonly Stopwatch _fpsClock = Stopwatch.StartNew();
    private long _processedFrames;

    /// <summary>
    /// 创建单路摄像头处理管线。
    /// </summary>
    public CameraPipeline(
        CameraId cameraId,
        ICameraSource source,
        IFramePreprocessor preprocessor,
        ICameraAnalyzer analyzer)
    {
        CameraId = cameraId;
        _source = source;
        _preprocessor = preprocessor;
        _analyzer = analyzer;
    }

    /// <summary>
    /// 获取当前管线对应的摄像头标识。
    /// </summary>
    public CameraId CameraId { get; }

    /// <summary>
    /// 处理最新一帧并返回检测结果。
    /// </summary>
    public async Task<IReadOnlyList<CameraFinding>> ProcessLatestAsync(CancellationToken cancellationToken)
    {
        using var result = await ProcessLatestDetailedAsync(cancellationToken, includePreview: false);
        return result?.Findings ?? Array.Empty<CameraFinding>();
    }

    /// <summary>
    /// 处理最新一帧并返回检测结果、预览图和性能指标。
    /// </summary>
    public async Task<CameraPipelineResult?> ProcessLatestDetailedAsync(
        CancellationToken cancellationToken,
        bool includePreview = true)
    {
        var totalWatch = Stopwatch.StartNew();
        var captureWatch = Stopwatch.StartNew();
        using var frame = await _source.ReadLatestAsync(cancellationToken);
        captureWatch.Stop();

        if (frame is null)
        {
            return null;
        }

        var preprocessWatch = Stopwatch.StartNew();
        using var processed = await _preprocessor.ProcessAsync(frame, cancellationToken);
        preprocessWatch.Stop();

        var inferenceWatch = Stopwatch.StartNew();
        var findings = await _analyzer.AnalyzeAsync(processed, cancellationToken);
        inferenceWatch.Stop();
        totalWatch.Stop();

        var fps = CalculateFps();
        var metrics = new CameraPipelineMetrics(
            captureWatch.Elapsed.TotalMilliseconds,
            preprocessWatch.Elapsed.TotalMilliseconds,
            inferenceWatch.Elapsed.TotalMilliseconds,
            totalWatch.Elapsed.TotalMilliseconds,
            fps,
            _source.DroppedFrames);

        return new CameraPipelineResult(
            CameraId,
            frame.CapturedAt,
            frame.Width,
            frame.Height,
            findings,
            metrics,
            includePreview ? processed.PreviewImage.Clone() : new OpenCvSharp.Mat());
    }

    /// <summary>
    /// 释放摄像头源。
    /// </summary>
    public ValueTask DisposeAsync()
    {
        if (_analyzer is IDisposable disposableAnalyzer)
        {
            disposableAnalyzer.Dispose();
        }

        return _source.DisposeAsync();
    }

    /// <summary>
    /// 计算当前管线平均 FPS。
    /// </summary>
    private double CalculateFps()
    {
        var frames = Interlocked.Increment(ref _processedFrames);
        var seconds = Math.Max(_fpsClock.Elapsed.TotalSeconds, 0.001);
        return frames / seconds;
    }
}
