using RideManager.Models;

namespace RideManager.Camera;

/// <summary>
/// 将摄像头预处理结果送入模型推理并转换为检测结果。
/// </summary>
public sealed class CameraAnalyzer : ICameraAnalyzer, IDisposable
{
    private readonly CameraId _cameraId;
    private readonly IInferenceEngine _inferenceEngine;

    /// <summary>
    /// 创建摄像头算法分析器。
    /// </summary>
    public CameraAnalyzer(CameraId cameraId, IInferenceEngine inferenceEngine)
    {
        _cameraId = cameraId;
        _inferenceEngine = inferenceEngine;
    }

    /// <summary>
    /// 执行单帧分析。
    /// </summary>
    public async Task<IReadOnlyList<CameraFinding>> AnalyzeAsync(ProcessedFrame frame, CancellationToken cancellationToken)
    {
        var output = await _inferenceEngine.RunAsync(
            new InferenceInput(
                frame.CameraId.ToString(),
                frame.TensorData,
                frame.TensorDimensions,
                frame.OriginalWidth,
                frame.OriginalHeight),
            cancellationToken);

        if (output.Detections is { Count: > 0 })
        {
            return output.Detections
                .Select(detection => new CameraFinding(
                    _cameraId,
                    detection.Label,
                    detection.Confidence,
                    frame.CapturedAt,
                    new CameraBoundingBox(detection.X, detection.Y, detection.Width, detection.Height)))
                .ToArray();
        }

        return output.Labels
            .Select(label => new CameraFinding(_cameraId, label, output.Confidence, frame.CapturedAt))
            .ToArray();
    }

    /// <summary>
    /// 释放底层推理引擎资源。
    /// </summary>
    public void Dispose()
    {
        if (_inferenceEngine is IDisposable disposable)
        {
            disposable.Dispose();
        }
    }
}
