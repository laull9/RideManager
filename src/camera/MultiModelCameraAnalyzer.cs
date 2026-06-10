using OpenCvSharp;
using RideManager.Models;
using RideManager.Utils;

namespace RideManager.Camera;

/// <summary>
/// 在同一路摄像头帧上运行多个模型，并合并它们的检测结果。
/// </summary>
public sealed class MultiModelCameraAnalyzer : ICameraAnalyzer, IDisposable
{
    private readonly CameraId _cameraId;
    private readonly IReadOnlyList<ModelRunner> _runners;
    private readonly ModelRunner[] _cachedLaneRunners;
    private readonly ModelRunner[] _foregroundRunners;

    /// <summary>
    /// 创建多模型摄像头分析器。
    /// </summary>
    public MultiModelCameraAnalyzer(CameraId cameraId, IReadOnlyList<ModelRunner> runners)
    {
        _cameraId = cameraId;
        _runners = runners;
        _cachedLaneRunners = runners.Where(runner => runner.IsCachedLaneRunner).ToArray();
        _foregroundRunners = runners.Where(runner => !runner.IsCachedLaneRunner).ToArray();
    }

    /// <summary>
    /// 对当前帧按模型列表逐个推理，并合并输出。
    /// </summary>
    public async Task<IReadOnlyList<CameraFinding>> AnalyzeAsync(ProcessedFrame frame, CancellationToken cancellationToken)
    {
        var findings = new List<CameraFinding>();
        foreach (var runner in _cachedLaneRunners)
        {
            ScheduleCachedLaneRun(runner, frame, cancellationToken);
        }

        if (_foregroundRunners.Length == 1)
        {
            findings.AddRange(await AnalyzeForegroundRunnerAsync(_foregroundRunners[0], frame, cancellationToken));
        }
        else if (_foregroundRunners.Length > 1)
        {
            var tasks = new Task<IReadOnlyList<CameraFinding>>[_foregroundRunners.Length];
            for (var index = 0; index < _foregroundRunners.Length; index++)
            {
                var runner = _foregroundRunners[index];
                tasks[index] = Task.Run(
                    () => AnalyzeForegroundRunnerAsync(runner, frame, cancellationToken),
                    cancellationToken);
            }

            var foregroundResults = await Task.WhenAll(tasks);
            foreach (var result in foregroundResults)
            {
                findings.AddRange(result);
            }
        }

        foreach (var runner in _cachedLaneRunners)
        {
            findings.AddRange(runner.GetCachedFindings());
        }

        return findings;
    }

    /// <summary>
    /// 释放所有底层推理引擎。
    /// </summary>
    public void Dispose()
    {
        foreach (var runner in _runners)
        {
            runner.WaitForCachedRun();
            runner.Tensor.Dispose();
            if (runner.Engine is IDisposable disposable)
            {
                disposable.Dispose();
            }
        }
    }

    /// <summary>
    /// 在当前主路径上运行需要等待的模型。
    /// </summary>
    private async Task<IReadOnlyList<CameraFinding>> AnalyzeForegroundRunnerAsync(
        ModelRunner runner,
        ProcessedFrame frame,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ModelInputTensorFactory.FillRgbNchwTensor(
            frame.PreviewImage,
            runner.InputWidth,
            runner.InputHeight,
            runner.Tensor.Span);
        var output = await runner.Engine.RunAsync(
            new InferenceInput(
                $"{frame.CameraId}:{runner.ModelName}",
                runner.Tensor,
                runner.TensorDimensions,
                frame.OriginalWidth,
                frame.OriginalHeight),
            cancellationToken);

        return CreateFindings(output, frame.CapturedAt);
    }

    /// <summary>
    /// 按配置的最高 FPS 在后台刷新裁剪区域缓存，当前帧不等待该模型完成。
    /// </summary>
    private void ScheduleCachedLaneRun(ModelRunner runner, ProcessedFrame frame, CancellationToken cancellationToken)
    {
        if (!runner.TryBeginCachedRun(DateTimeOffset.UtcNow))
        {
            return;
        }

        var region = CreateRegionView(frame.PreviewImage, runner.CropRegion);
        var capturedAt = frame.CapturedAt;
        var task = Task.Run(async () =>
        {
            try
            {
                using (region)
                {
                    ModelInputTensorFactory.FillRgbNchwTensor(
                        region,
                        runner.InputWidth,
                        runner.InputHeight,
                        runner.Tensor.Span);
                    var output = await runner.Engine.RunAsync(
                        new InferenceInput(
                            $"{frame.CameraId}:{runner.ModelName}:crop",
                            runner.Tensor,
                            runner.TensorDimensions,
                            region.Width,
                            region.Height),
                        cancellationToken);
                    runner.SetCachedFindings(CreateFindings(output, capturedAt, runner.CropRegion));
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (ObjectDisposedException)
            {
            }
            finally
            {
                runner.EndCachedRun();
            }
        }, CancellationToken.None);
        runner.SetCachedRunTask(task);
    }

    /// <summary>
    /// 创建归一化区域视图，后台任务拥有返回 Mat 头并共享底层帧数据引用。
    /// </summary>
    private static Mat CreateRegionView(Mat image, NormalizedRegion region)
    {
        var left = Math.Clamp((int)Math.Round(region.X * image.Width), 0, image.Width - 1);
        var top = Math.Clamp((int)Math.Round(region.Y * image.Height), 0, image.Height - 1);
        var right = Math.Clamp((int)Math.Round((region.X + region.Width) * image.Width), left + 1, image.Width);
        var bottom = Math.Clamp((int)Math.Round((region.Y + region.Height) * image.Height), top + 1, image.Height);
        return new Mat(image, new Rect(left, top, right - left, bottom - top));
    }

    /// <summary>
    /// 将统一推理输出转换为摄像头 finding。
    /// </summary>
    private IReadOnlyList<CameraFinding> CreateFindings(
        InferenceOutput output,
        DateTimeOffset capturedAt,
        NormalizedRegion? region = null)
    {
        if (output.Detections is { Count: > 0 })
        {
            var masksByLabel = (output.SegmentationMasks ?? Array.Empty<InferenceSegmentationMask>())
                .GroupBy(mask => mask.Label, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
            var landmarks = (output.Landmarks ?? Array.Empty<InferenceLandmark>())
                .Select(landmark => new CameraLandmark(landmark.X, landmark.Y))
                .ToArray();

            return output.Detections
                .Select(detection => CreateFinding(detection, masksByLabel, landmarks, capturedAt, region))
                .Where(IsRelevantFinding)
                .ToArray();
        }

        return output.Labels
            .Where(IsRelevantLabel)
            .Select(label => new CameraFinding(_cameraId, label, output.Confidence, capturedAt))
            .ToArray();
    }

    /// <summary>
    /// 将模型检测结果转换为摄像头 finding，并附带同标签分割 mask。
    /// </summary>
    private CameraFinding CreateFinding(
        InferenceDetection detection,
        IReadOnlyDictionary<string, InferenceSegmentationMask> masksByLabel,
        IReadOnlyList<CameraLandmark> landmarks,
        DateTimeOffset capturedAt,
        NormalizedRegion? region)
    {
        var mappedDetection = region is { } value
            ? MapDetectionToRegion(detection, value)
            : detection;
        var mask = masksByLabel.TryGetValue(detection.Label, out var segmentationMask)
            ? new CameraSegmentationMask(
                segmentationMask.Label,
                segmentationMask.Width,
                segmentationMask.Height,
                segmentationMask.Data,
                region?.X ?? 0.0,
                region?.Y ?? 0.0,
                region?.Width ?? 1.0,
                region?.Height ?? 1.0)
            : null;

        return new CameraFinding(
            _cameraId,
            mappedDetection.Label,
            mappedDetection.Confidence,
            capturedAt,
            new CameraBoundingBox(mappedDetection.X, mappedDetection.Y, mappedDetection.Width, mappedDetection.Height),
            mask,
            landmarks.Count > 0 ? landmarks : null);
    }

    /// <summary>
    /// 将 ROI 内归一化检测框映射回整帧归一化坐标。
    /// </summary>
    private static InferenceDetection MapDetectionToRegion(InferenceDetection detection, NormalizedRegion region)
    {
        return detection with
        {
            X = Math.Clamp(region.X + detection.X * region.Width, 0.0, 1.0),
            Y = Math.Clamp(region.Y + detection.Y * region.Height, 0.0, 1.0),
            Width = Math.Clamp(detection.Width * region.Width, 0.0, 1.0),
            Height = Math.Clamp(detection.Height * region.Height, 0.0, 1.0)
        };
    }

    /// <summary>
    /// 前向摄像头只上报目标检测结果，不再输出道路分割 finding。
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

    /// <summary>
    /// 表示一路模型运行器。
    /// </summary>
    public sealed class ModelRunner
    {
        /// <summary>
        /// 创建一路模型运行器，并为固定输入尺寸分配可复用 native tensor。
        /// </summary>
        public ModelRunner(string modelName, int inputWidth, int inputHeight, IInferenceEngine engine)
            : this(modelName, inputWidth, inputHeight, 0.0, 0.0, 0.0, 1.0, 1.0, engine)
        {
        }

        /// <summary>
        /// 创建一路模型运行器，并为固定输入尺寸分配可复用 native tensor。
        /// </summary>
        public ModelRunner(
            string modelName,
            int inputWidth,
            int inputHeight,
            double maxFps,
            double cropX,
            double cropY,
            double cropWidth,
            double cropHeight,
            IInferenceEngine engine)
        {
            ModelName = modelName;
            InputWidth = inputWidth;
            InputHeight = inputHeight;
            Engine = engine;
            Tensor = new NativeFloatTensor(3 * inputWidth * inputHeight);
            TensorDimensions = new[] { 1, 3, inputHeight, inputWidth };
            CropRegion = new NormalizedRegion(cropX, cropY, cropWidth, cropHeight);
            MaxFps = Math.Max(0.0, maxFps);
            IsCachedLaneRunner = IsTwinLiteNetModel(modelName) && MaxFps > 0.0;
            CachedMinimumInterval = MaxFps > 0.0
                ? TimeSpan.FromSeconds(1.0 / MaxFps)
                : TimeSpan.Zero;
        }

        /// <summary>
        /// 获取模型文件名。
        /// </summary>
        public string ModelName { get; }

        /// <summary>
        /// 获取模型输入宽度。
        /// </summary>
        public int InputWidth { get; }

        /// <summary>
        /// 获取模型输入高度。
        /// </summary>
        public int InputHeight { get; }

        /// <summary>
        /// 获取底层推理引擎。
        /// </summary>
        public IInferenceEngine Engine { get; }

        /// <summary>
        /// 获取该模型复用的 native 输入张量。
        /// </summary>
        public NativeFloatTensor Tensor { get; }

        /// <summary>
        /// 获取该模型固定输入维度。
        /// </summary>
        public IReadOnlyList<int> TensorDimensions { get; }

        /// <summary>
        /// 获取该模型是否作为低频异步车道线分割缓存运行。
        /// </summary>
        public bool IsCachedLaneRunner { get; }

        /// <summary>
        /// 获取缓存模型最高刷新 FPS；0 表示不启用异步缓存。
        /// </summary>
        public double MaxFps { get; }

        /// <summary>
        /// 获取模型输入所使用的原图裁剪区域。
        /// </summary>
        public NormalizedRegion CropRegion { get; }

        /// <summary>
        /// 获取缓存模型最短刷新间隔。
        /// </summary>
        private TimeSpan CachedMinimumInterval { get; }

        private readonly object _cacheGate = new();
        private IReadOnlyList<CameraFinding> _cachedFindings = Array.Empty<CameraFinding>();
        private Task? _cachedRunTask;
        private DateTimeOffset _nextAllowedCachedRunAt = DateTimeOffset.MinValue;
        private int _cachedRunInProgress;

        /// <summary>
        /// 尝试开始一次后台缓存推理。
        /// </summary>
        public bool TryBeginCachedRun(DateTimeOffset now)
        {
            if (!IsCachedLaneRunner)
            {
                return false;
            }

            lock (_cacheGate)
            {
                if (now < _nextAllowedCachedRunAt || _cachedRunInProgress != 0)
                {
                    return false;
                }

                _cachedRunInProgress = 1;
                _nextAllowedCachedRunAt = now.Add(CachedMinimumInterval);
                return true;
            }
        }

        /// <summary>
        /// 完成后台缓存推理。
        /// </summary>
        public void EndCachedRun()
        {
            lock (_cacheGate)
            {
                _cachedRunInProgress = 0;
            }
        }

        /// <summary>
        /// 记录当前后台缓存任务，便于释放时等待 native 资源不被提前销毁。
        /// </summary>
        public void SetCachedRunTask(Task task)
        {
            lock (_cacheGate)
            {
                _cachedRunTask = task;
            }
        }

        /// <summary>
        /// 等待当前后台缓存任务结束。
        /// </summary>
        public void WaitForCachedRun()
        {
            Task? task;
            lock (_cacheGate)
            {
                task = _cachedRunTask;
            }

            try
            {
                task?.GetAwaiter().GetResult();
            }
            catch (OperationCanceledException)
            {
            }
            catch (ObjectDisposedException)
            {
            }
        }

        /// <summary>
        /// 更新最新缓存结果。
        /// </summary>
        public void SetCachedFindings(IReadOnlyList<CameraFinding> findings)
        {
            lock (_cacheGate)
            {
                _cachedFindings = findings.ToArray();
            }
        }

        /// <summary>
        /// 读取最新缓存结果快照。
        /// </summary>
        public IReadOnlyList<CameraFinding> GetCachedFindings()
        {
            lock (_cacheGate)
            {
                return _cachedFindings;
            }
        }

        /// <summary>
        /// 判断模型名是否为 TwinLiteNet。
        /// </summary>
        private static bool IsTwinLiteNetModel(string modelName)
        {
            return Path.GetFileName(modelName).Contains("twinlite", StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>
    /// 表示归一化原图区域。
    /// </summary>
    public readonly record struct NormalizedRegion(double X, double Y, double Width, double Height);
}
