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
                frame.Tensor,
                frame.TensorDimensions,
                frame.OriginalWidth,
                frame.OriginalHeight),
            cancellationToken);

        if (output.Detections is { Count: > 0 })
        {
            var masksByLabel = (output.SegmentationMasks ?? Array.Empty<InferenceSegmentationMask>())
                .GroupBy(mask => mask.Label, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
            var landmarks = (output.Landmarks ?? Array.Empty<InferenceLandmark>())
                .Select(landmark => new CameraLandmark(landmark.X, landmark.Y))
                .ToArray();

            return output.Detections
                .Select(detection => CreateFinding(detection, masksByLabel, landmarks, frame.CapturedAt))
                .Where(IsRelevantFinding)
                .ToArray();
        }

        return output.Labels
            .Where(IsRelevantLabel)
            .Select(label => new CameraFinding(_cameraId, label, output.Confidence, frame.CapturedAt))
            .ToArray();
    }

    /// <summary>
    /// 将模型检测结果转换为摄像头 finding，并附带同标签分割 mask。
    /// </summary>
    private CameraFinding CreateFinding(
        InferenceDetection detection,
        IReadOnlyDictionary<string, InferenceSegmentationMask> masksByLabel,
        IReadOnlyList<CameraLandmark> landmarks,
        DateTimeOffset capturedAt)
    {
        var mask = masksByLabel.TryGetValue(detection.Label, out var segmentationMask)
            ? new CameraSegmentationMask(
                segmentationMask.Label,
                segmentationMask.Width,
                segmentationMask.Height,
                segmentationMask.Data)
            : null;

        return new CameraFinding(
            _cameraId,
            detection.Label,
            detection.Confidence,
            capturedAt,
            new CameraBoundingBox(detection.X, detection.Y, detection.Width, detection.Height),
            mask,
            landmarks.Count > 0 ? landmarks : null);
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

    /// <summary>
    /// 前向摄像头只保留目标检测结果，不再输出车道线或可行驶区域分割 finding。
    /// </summary>
    private bool IsRelevantFinding(CameraFinding finding)
    {
        return IsRelevantLabel(finding.Label);
    }

    /// <summary>
    /// 判断标签是否属于当前摄像头需要上报的目标。
    /// </summary>
    private bool IsRelevantLabel(string label)
    {
        return _cameraId != CameraId.CamFront || !IsRoadSegmentationLabel(label);
    }

    /// <summary>
    /// 判断标签是否为已放弃的道路分割输出。
    /// </summary>
    private static bool IsRoadSegmentationLabel(string label)
    {
        return label.Equals("lane_line", StringComparison.OrdinalIgnoreCase)
            || label.Equals("drivable_area", StringComparison.OrdinalIgnoreCase);
    }
}
