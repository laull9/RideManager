using OpenCvSharp;

namespace RideManager.Camera;

/// <summary>
/// 运行摄像头完整链路 live 测试。
/// </summary>
public sealed class CameraLiveTester
{
    private readonly IReadOnlyList<CameraPipeline> _pipelines;

    /// <summary>
    /// 创建摄像头 live 测试器。
    /// </summary>
    public CameraLiveTester(IReadOnlyList<CameraPipeline> pipelines)
    {
        _pipelines = pipelines;
    }

    /// <summary>
    /// 启动 live 测试，支持窗口预览和无窗口统计输出。
    /// </summary>
    public async Task RunAsync(CameraLiveTestOptions options, CancellationToken cancellationToken)
    {
        var activeCameras = CreateActiveSet(options.InitialCamera);
        var activeGate = new object();
        var stopAt = options.Duration is null ? (DateTimeOffset?)null : DateTimeOffset.UtcNow.Add(options.Duration.Value);
        var lastConsoleAt = DateTimeOffset.MinValue;
        await using var previewServer = options.Headless
            ? null
            : new CameraLivePreviewServer(
                5088,
                () => GetActiveSnapshot(activeCameras, activeGate),
                camera => SetActiveFromText(activeCameras, activeGate, camera));

        Console.WriteLine(options.Headless
            ? "Live test started in headless mode."
            : $"Live test started. Preview: {previewServer?.Url}  Buttons: front/face/back/all.");

        while (!cancellationToken.IsCancellationRequested && (stopAt is null || DateTimeOffset.UtcNow < stopAt))
        {
            var activeSnapshot = GetActiveSnapshot(activeCameras, activeGate);
            foreach (var pipeline in _pipelines.Where(pipeline => activeSnapshot.Contains(pipeline.CameraId)))
            {
                using var result = await pipeline.ProcessLatestDetailedAsync(cancellationToken);
                if (result is null)
                {
                    continue;
                }

                if (options.Headless)
                {
                    if (DateTimeOffset.UtcNow - lastConsoleAt > TimeSpan.FromSeconds(1))
                    {
                        Console.WriteLine(FormatMetrics(result));
                        lastConsoleAt = DateTimeOffset.UtcNow;
                    }
                }
                else
                {
                    DrawOverlay(result, activeSnapshot);
                    previewServer?.Publish(result);
                }
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
    private static void DrawOverlay(CameraPipelineResult result, IReadOnlyCollection<CameraId> activeCameras)
    {
        var image = result.PreviewImage;
        var y = 32;
        DrawText(image, $"{result.CameraId} | active={string.Join(',', activeCameras)}", y);
        y += 30;
        DrawText(
            image,
            $"fps={result.Metrics.Fps:F1} total={result.Metrics.TotalLatencyMs:F1}ms pre={result.Metrics.PreprocessLatencyMs:F1}ms infer={result.Metrics.InferenceLatencyMs:F1}ms drop={result.Metrics.DroppedFrames}",
            y);

        foreach (var finding in result.Findings.Take(6))
        {
            y += 30;
            DrawText(image, $"{finding.Label} {finding.Confidence:P0}", y);

            if (finding.BoundingBox is not null)
            {
                DrawBox(image, finding);
            }
        }
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

        Cv2.Rectangle(image, rect, Scalar.LimeGreen, 2);
        DrawText(image, $"{finding.Label} {finding.Confidence:P0}", Math.Max(24, top - 6));
    }

    /// <summary>
    /// 绘制可读性较好的描边文字。
    /// </summary>
    private static void DrawText(Mat image, string text, int y)
    {
        var point = new Point(16, y);
        Cv2.PutText(image, text, point, HersheyFonts.HersheySimplex, 0.7, Scalar.Black, 4);
        Cv2.PutText(image, text, point, HersheyFonts.HersheySimplex, 0.7, Scalar.White, 2);
    }

    /// <summary>
    /// 格式化无窗口统计输出。
    /// </summary>
    private static string FormatMetrics(CameraPipelineResult result)
    {
        var labels = string.Join(',', result.Findings.Take(8).Select(finding => $"{finding.Label}:{finding.Confidence:F2}"));
        return $"{result.CameraId} fps={result.Metrics.Fps:F1} total={result.Metrics.TotalLatencyMs:F1}ms dropped={result.Metrics.DroppedFrames} findings=[{labels}]";
    }
}
