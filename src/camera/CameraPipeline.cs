namespace RideManager.Camera;

/// <summary>
/// 串联单路摄像头的采集、预处理和分析流程。
/// </summary>
public sealed class CameraPipeline
{
    private readonly ICameraSource _source;
    private readonly IFramePreprocessor _preprocessor;
    private readonly ICameraAnalyzer _analyzer;

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
        var frame = await _source.ReadLatestAsync(cancellationToken);
        if (frame is null)
        {
            return Array.Empty<CameraFinding>();
        }

        var processed = await _preprocessor.ProcessAsync(frame, cancellationToken);
        return await _analyzer.AnalyzeAsync(processed, cancellationToken);
    }
}
