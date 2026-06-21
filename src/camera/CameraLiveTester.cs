using System.Collections.Concurrent;
using OpenCvSharp;
using RideManager.Core;
using RideManager.Sensors;

namespace RideManager.Camera;

/// <summary>
/// 运行摄像头完整链路 live 测试。
/// </summary>
public sealed class CameraLiveTester
{
    private readonly IReadOnlyList<CameraPipeline> _pipelines;
    private readonly IReadOnlyDictionary<CameraId, CameraRiskOptions> _cameraRiskOptions;

    /// <summary>
    /// 创建摄像头 live 测试器。
    /// </summary>
    public CameraLiveTester(
        IReadOnlyList<CameraPipeline> pipelines,
        IReadOnlyDictionary<CameraId, CameraRiskOptions>? cameraRiskOptions = null)
    {
        _pipelines = pipelines;
        _cameraRiskOptions = cameraRiskOptions ?? new Dictionary<CameraId, CameraRiskOptions>();
    }

    /// <summary>
    /// 启动 live 测试，支持窗口预览和无窗口统计输出。
    /// </summary>
    public async Task RunAsync(CameraLiveTestOptions options, CancellationToken cancellationToken)
    {
        var activeCameras = CreateActiveSet(options.InitialCamera);
        var activeGate = new object();
        var stopAt = options.Duration is null ? (DateTimeOffset?)null : DateTimeOffset.UtcNow.Add(options.Duration.Value);
        var lastConsoleByCamera = new ConcurrentDictionary<CameraId, DateTimeOffset>();
        await using var previewServer = options.Headless
            ? null
            : new CameraLivePreviewServer(
                5088,
                () => GetActiveSnapshot(activeCameras, activeGate),
                camera => SetActiveFromText(activeCameras, activeGate, camera));

        Console.WriteLine(options.Headless
            ? "Live test started in headless mode."
            : $"Live test started. Preview: {previewServer?.Url}  Buttons: front/face/back/all.");

        var workers = _pipelines
            .Select(pipeline => RunPipelineLoopAsync(
                pipeline,
                options,
                stopAt,
                activeCameras,
                activeGate,
                previewServer,
                lastConsoleByCamera,
                _cameraRiskOptions,
                cancellationToken))
            .ToArray();
        await Task.WhenAll(workers);
    }

    /// <summary>
    /// 独立运行单条摄像头管线，避免多模型 live test 共用同一个外层循环 FPS。
    /// </summary>
    private static async Task RunPipelineLoopAsync(
        CameraPipeline pipeline,
        CameraLiveTestOptions options,
        DateTimeOffset? stopAt,
        HashSet<CameraId> activeCameras,
        object activeGate,
        CameraLivePreviewServer? previewServer,
        ConcurrentDictionary<CameraId, DateTimeOffset> lastConsoleByCamera,
        IReadOnlyDictionary<CameraId, CameraRiskOptions> cameraRiskOptions,
        CancellationToken cancellationToken)
    {
        var decisionEngine = new SafetyDecisionEngine(cameraRiskOptions: cameraRiskOptions);
        while (!cancellationToken.IsCancellationRequested && (stopAt is null || DateTimeOffset.UtcNow < stopAt))
        {
            var activeSnapshot = GetActiveSnapshot(activeCameras, activeGate);
            if (!activeSnapshot.Contains(pipeline.CameraId))
            {
                await Task.Delay(30, cancellationToken);
                continue;
            }

            using var result = await pipeline.ProcessLatestDetailedAsync(
                cancellationToken,
                includePreview: !options.Headless);
            if (result is null)
            {
                await Task.Delay(1, cancellationToken);
                continue;
            }

            var decision = decisionEngine.Decide(
                new[] { result.CameraId },
                result.Findings,
                Array.Empty<SensorSnapshot>(),
                new[] { result.ToFrameState() });
            if (options.Headless)
            {
                var now = DateTimeOffset.UtcNow;
                var lastConsoleAt = lastConsoleByCamera.GetOrAdd(result.CameraId, DateTimeOffset.MinValue);
                if (now - lastConsoleAt > TimeSpan.FromSeconds(1))
                {
                    Console.WriteLine(FormatMetrics(result, decision));
                    lastConsoleByCamera[result.CameraId] = now;
                }
            }
            else
            {
                DrawOverlay(result, activeSnapshot, decision);
                previewServer?.Publish(result);
            }

            await Task.Delay(1, cancellationToken);
        }
    }

    /// <summary>
    /// 根据初始摄像头创建启用集合。
    /// </summary>
    private HashSet<CameraId> CreateActiveSet(CameraId? initialCamera)
    {
        return initialCamera is null
            ? _pipelines.Select(pipeline => pipeline.CameraId).ToHashSet()
            : new HashSet<CameraId> { initialCamera.Value };
    }

    /// <summary>
    /// 获取当前启用摄像头快照。
    /// </summary>
    private static IReadOnlyCollection<CameraId> GetActiveSnapshot(HashSet<CameraId> activeCameras, object activeGate)
    {
        lock (activeGate)
        {
            return activeCameras.ToArray();
        }
    }

    /// <summary>
    /// 根据 Web 页面按钮切换启用摄像头。
    /// </summary>
    private void SetActiveFromText(HashSet<CameraId> activeCameras, object activeGate, string camera)
    {
        lock (activeGate)
        {
            if (camera.Equals("all", StringComparison.OrdinalIgnoreCase))
            {
                activeCameras.Clear();
                foreach (var pipeline in _pipelines)
                {
                    activeCameras.Add(pipeline.CameraId);
                }

                return;
            }

            var cameraId = camera.ToUpperInvariant() switch
            {
                "CAM_FRONT" or "FRONT" or "1" => CameraId.CamFront,
                "CAM_FACE" or "FACE" or "2" => CameraId.CamFace,
                "CAM_BACK" or "BACK" or "3" => CameraId.CamBack,
                _ => (CameraId?)null
            };

            if (cameraId is null)
            {
                return;
            }

            activeCameras.Clear();
            activeCameras.Add(cameraId.Value);
        }
    }

    /// <summary>
    /// 在预览图上绘制检测结果和性能指标。
    /// </summary>
    private static void DrawOverlay(
        CameraPipelineResult result,
        IReadOnlyCollection<CameraId> activeCameras,
        SafetyDecision decision)
    {
        var image = result.PreviewImage;
        DrawSegmentationMasks(image, result.Findings);

        var y = 32;
        DrawText(image, $"{result.CameraId} | active={string.Join(',', activeCameras)}", y);
        y += 30;
        DrawText(
            image,
            $"fps={result.Metrics.Fps:F1} total={result.Metrics.TotalLatencyMs:F1}ms pre={result.Metrics.PreprocessLatencyMs:F1}ms infer={result.Metrics.InferenceLatencyMs:F1}ms drop={result.Metrics.DroppedFrames}",
            y);
        y += 34;
        DrawRiskStatus(image, decision, result.CameraId, y);

        if (result.CameraId == CameraId.CamFace && TryGetFaceFatigueFinding(result.Findings, out var fatigueFinding))
        {
            y += 38;
            DrawFaceFatigueStatus(image, fatigueFinding, y);
        }

        foreach (var finding in result.Findings.Take(6))
        {
            y += 30;
            DrawText(image, $"{finding.Label} {finding.Confidence:P0}", y, GetFindingColor(finding));

            if (finding.Landmarks is { Count: > 0 })
            {
                DrawLandmarks(image, finding.Landmarks);
            }

            if (finding.BoundingBox is not null)
            {
                DrawBox(image, finding);
            }
        }
    }

    /// <summary>
    /// 绘制运行时风险监测状态。
    /// </summary>
    private static void DrawRiskStatus(Mat image, SafetyDecision decision, CameraId cameraId, int y)
    {
        var color = GetRiskColor(decision.RiskLevel);
        var text = FormatRiskSummary(decision, cameraId);
        var top = Math.Max(0, y - 28);
        var width = Math.Min(image.Width - 16, Math.Max(520, text.Length * 13));
        var rect = new Rect(8, top, width, 38);
        Cv2.Rectangle(image, rect, new Scalar(0, 0, 0), -1);
        Cv2.Rectangle(image, rect, color, 2);
        DrawTextAt(image, text, new Point(18, y), color, 0.62, 2);
    }

    /// <summary>
    /// 绘制 YOLOPv2 可行驶区域和车道线分割结果。
    /// </summary>
    private static void DrawSegmentationMasks(Mat image, IReadOnlyList<CameraFinding> findings)
    {
        foreach (var finding in findings.Where(finding => finding.SegmentationMask is not null))
        {
            DrawSegmentationMask(image, finding.SegmentationMask!);
        }
    }

    /// <summary>
    /// 将 letterbox 输入空间的 mask 逆映射到原图预览空间并半透明叠加。
    /// </summary>
    private static void DrawSegmentationMask(Mat image, CameraSegmentationMask mask)
    {
        if (mask.Data.Length != mask.Width * mask.Height)
        {
            return;
        }

        using var inputMask = CreateMaskMat(mask);
        var region = GetMaskRegion(image, mask);
        if (region.Width <= 0 || region.Height <= 0)
        {
            return;
        }

        var crop = GetLetterboxContentRect(mask.Width, mask.Height, region.Width, region.Height);
        if (crop.Width <= 0 || crop.Height <= 0)
        {
            return;
        }

        using var croppedMask = new Mat(inputMask, crop);
        using var previewMask = new Mat();
        Cv2.Resize(croppedMask, previewMask, region.Size, 0, 0, InterpolationFlags.Nearest);

        var color = mask.Label.Equals("lane_line", StringComparison.OrdinalIgnoreCase)
            ? new Scalar(0, 0, 255)
            : new Scalar(0, 180, 60);
        var alpha = mask.Label.Equals("lane_line", StringComparison.OrdinalIgnoreCase) ? 0.75 : 0.35;

        using var imageRegion = new Mat(image, region);
        using var colorLayer = new Mat(region.Size, MatType.CV_8UC3, color);
        using var blended = new Mat();
        Cv2.AddWeighted(imageRegion, 1.0 - alpha, colorLayer, alpha, 0, blended);
        blended.CopyTo(imageRegion, previewMask);
    }

    /// <summary>
    /// 将 mask 所属的归一化原图区域转换为 OpenCV ROI。
    /// </summary>
    private static Rect GetMaskRegion(Mat image, CameraSegmentationMask mask)
    {
        var left = Math.Clamp((int)Math.Round(mask.RegionX * image.Width), 0, image.Width - 1);
        var top = Math.Clamp((int)Math.Round(mask.RegionY * image.Height), 0, image.Height - 1);
        var right = Math.Clamp((int)Math.Round((mask.RegionX + mask.RegionWidth) * image.Width), left + 1, image.Width);
        var bottom = Math.Clamp((int)Math.Round((mask.RegionY + mask.RegionHeight) * image.Height), top + 1, image.Height);
        return new Rect(left, top, right - left, bottom - top);
    }

    /// <summary>
    /// 创建 OpenCV 单通道 mask。
    /// </summary>
    private static Mat CreateMaskMat(CameraSegmentationMask mask)
    {
        var mat = new Mat(mask.Height, mask.Width, MatType.CV_8UC1);
        System.Runtime.InteropServices.Marshal.Copy(mask.Data, 0, mat.Data, mask.Data.Length);
        return mat;
    }

    /// <summary>
    /// 计算原图内容在 letterbox 输入中的区域。
    /// </summary>
    private static Rect GetLetterboxContentRect(int inputWidth, int inputHeight, int originalWidth, int originalHeight)
    {
        var scale = Math.Min((double)inputWidth / originalWidth, (double)inputHeight / originalHeight);
        var contentWidth = Math.Clamp((int)Math.Round(originalWidth * scale), 1, inputWidth);
        var contentHeight = Math.Clamp((int)Math.Round(originalHeight * scale), 1, inputHeight);
        var left = Math.Clamp((inputWidth - contentWidth) / 2, 0, inputWidth - 1);
        var top = Math.Clamp((inputHeight - contentHeight) / 2, 0, inputHeight - 1);
        return new Rect(left, top, Math.Min(contentWidth, inputWidth - left), Math.Min(contentHeight, inputHeight - top));
    }

    /// <summary>
    /// 在预览图上绘制归一化检测框。
    /// </summary>
    private static void DrawBox(Mat image, CameraFinding finding)
    {
        if (finding.BoundingBox is null)
        {
            return;
        }

        var box = finding.BoundingBox;
        var left = Math.Clamp((int)(box.X * image.Width), 0, image.Width - 1);
        var top = Math.Clamp((int)(box.Y * image.Height), 0, image.Height - 1);
        var width = Math.Max(1, (int)(box.Width * image.Width));
        var height = Math.Max(1, (int)(box.Height * image.Height));
        var rect = new Rect(left, top, Math.Min(width, image.Width - left), Math.Min(height, image.Height - top));

        var color = finding.SegmentationMask is not null
            ? Scalar.Yellow
            : GetFindingColor(finding);
        Cv2.Rectangle(image, rect, color, 2);
        DrawText(image, $"{finding.Label} {finding.Confidence:P0}", Math.Max(24, top - 6), color);
    }

    /// <summary>
    /// 从核心输出中获取面部疲劳状态。
    /// </summary>
    private static bool TryGetFaceFatigueFinding(IReadOnlyList<CameraFinding> findings, out CameraFinding fatigueFinding)
    {
        fatigueFinding = findings.FirstOrDefault(finding =>
            finding.Label.Equals("fatigue", StringComparison.OrdinalIgnoreCase)
            || finding.Label.Equals("fatigue_normal", StringComparison.OrdinalIgnoreCase)
            || finding.Label.Equals("fatigue_unknown", StringComparison.OrdinalIgnoreCase))!;
        return fatigueFinding is not null;
    }

    /// <summary>
    /// 绘制 CAM_FACE 核心疲劳判断状态。
    /// </summary>
    private static void DrawFaceFatigueStatus(Mat image, CameraFinding finding, int y)
    {
        var (status, color) = finding.Label.ToLowerInvariant() switch
        {
            "fatigue" => ("FATIGUE WARNING", new Scalar(0, 0, 255)),
            "fatigue_normal" => ("FATIGUE NORMAL", Scalar.LimeGreen),
            "fatigue_unknown" => ("FATIGUE UNKNOWN", new Scalar(0, 220, 255)),
            _ => ("FATIGUE UNKNOWN", new Scalar(0, 220, 255))
        };
        var text = $"{status} {finding.Confidence:P0}";
        var top = Math.Max(0, y - 28);
        var width = Math.Min(image.Width - 16, Math.Max(320, text.Length * 17));
        var rect = new Rect(8, top, width, 38);
        Cv2.Rectangle(image, rect, new Scalar(0, 0, 0), -1);
        Cv2.Rectangle(image, rect, color, 2);
        DrawTextAt(image, text, new Point(18, y), color, 0.78, 2);
    }

    /// <summary>
    /// 根据检测类别选择预览颜色。
    /// </summary>
    private static Scalar GetFindingColor(CameraFinding finding)
    {
        return finding.Label.ToLowerInvariant() switch
        {
            "fatigue" => new Scalar(0, 0, 255),
            "fatigue_normal" => Scalar.LimeGreen,
            "fatigue_unknown" => new Scalar(0, 220, 255),
            "face_landmarks_106" => new Scalar(255, 220, 0),
            _ => finding.SegmentationMask is not null ? Scalar.Yellow : Scalar.LimeGreen
        };
    }

    /// <summary>
    /// 根据风险等级选择预览颜色。
    /// </summary>
    private static Scalar GetRiskColor(SafetyRiskLevel riskLevel)
    {
        return riskLevel switch
        {
            SafetyRiskLevel.Danger => new Scalar(0, 0, 255),
            SafetyRiskLevel.Warning => new Scalar(0, 220, 255),
            _ => Scalar.LimeGreen
        };
    }

    /// <summary>
    /// 在预览图上绘制 PFLD 人脸关键点。
    /// </summary>
    private static void DrawLandmarks(Mat image, IReadOnlyList<CameraLandmark> landmarks)
    {
        foreach (var landmark in landmarks)
        {
            var x = Math.Clamp((int)Math.Round(landmark.X * image.Width), 0, image.Width - 1);
            var y = Math.Clamp((int)Math.Round(landmark.Y * image.Height), 0, image.Height - 1);
            Cv2.Circle(image, new Point(x, y), 2, Scalar.Red, -1);
            Cv2.Circle(image, new Point(x, y), 3, Scalar.White, 1);
        }
    }

    /// <summary>
    /// 绘制可读性较好的描边文字。
    /// </summary>
    private static void DrawText(Mat image, string text, int y, Scalar? color = null)
    {
        DrawTextAt(image, text, new Point(16, y), color ?? Scalar.White, 0.7, 2);
    }

    /// <summary>
    /// 在指定位置绘制描边文字。
    /// </summary>
    private static void DrawTextAt(Mat image, string text, Point point, Scalar color, double scale, int thickness)
    {
        Cv2.PutText(image, text, point, HersheyFonts.HersheySimplex, scale, Scalar.Black, thickness + 2);
        Cv2.PutText(image, text, point, HersheyFonts.HersheySimplex, scale, color, thickness);
    }

    /// <summary>
    /// 格式化无窗口统计输出。
    /// </summary>
    private static string FormatMetrics(CameraPipelineResult result, SafetyDecision decision)
    {
        var labels = string.Join(',', result.Findings.Take(8).Select(finding => $"{finding.Label}:{finding.Confidence:F2}"));
        return $"{result.CameraId} {FormatRiskSummary(decision, result.CameraId)} fps={result.Metrics.Fps:F1} total={result.Metrics.TotalLatencyMs:F1}ms pre={result.Metrics.PreprocessLatencyMs:F1}ms infer={result.Metrics.InferenceLatencyMs:F1}ms dropped={result.Metrics.DroppedFrames} findings=[{labels}]";
    }

    /// <summary>
    /// 格式化当前摄像头的风险监测摘要。
    /// </summary>
    private static string FormatRiskSummary(SafetyDecision decision, CameraId cameraId)
    {
        var assessment = decision.CameraRiskAssessments.FirstOrDefault(value => value.CameraId == cameraId);
        if (assessment is null)
        {
            return $"risk={decision.RiskLevel}";
        }

        var labels = assessment.LeadingLabels.Count == 0
            ? "-"
            : string.Join('/', assessment.LeadingLabels);
        return $"risk={assessment.RiskLevel} score={assessment.CurrentScore:F2} recent={assessment.RecentAverageScore:F2} delta={assessment.TrendScoreDelta:+0.00;-0.00;0.00} peak={assessment.PeakScore:F2} labels={labels}";
    }
}
