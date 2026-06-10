using RideManager.Actuators;
using RideManager.Camera;
using RideManager.Data;
using RideManager.Sensors;

namespace RideManager.Core;

/// <summary>
/// 负责驱动所有检测链路并持久化主控决策。
/// </summary>
public sealed class RideSupervisor
{
    private static readonly TimeSpan LoopDelay = TimeSpan.FromMilliseconds(200);

    private readonly IReadOnlyList<CameraPipeline> _cameraPipelines;
    private readonly IReadOnlyList<ISensorReader> _sensorReaders;
    private readonly IBrakeController _brakeController;
    private readonly ISpeakerNotifier _speakerNotifier;
    private readonly SafetyDecisionEngine _decisionEngine;
    private readonly IDetectionEventWriter _eventWriter;

    /// <summary>
    /// 创建主控调度器。
    /// </summary>
    public RideSupervisor(
        IReadOnlyList<CameraPipeline> cameraPipelines,
        IReadOnlyList<ISensorReader> sensorReaders,
        IBrakeController brakeController,
        ISpeakerNotifier speakerNotifier,
        SafetyDecisionEngine decisionEngine,
        IDetectionEventWriter eventWriter)
    {
        _cameraPipelines = cameraPipelines;
        _sensorReaders = sensorReaders;
        _brakeController = brakeController;
        _speakerNotifier = speakerNotifier;
        _decisionEngine = decisionEngine;
        _eventWriter = eventWriter;
    }

    /// <summary>
    /// 持续执行检测周期，直到收到取消信号。
    /// </summary>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            await RunOnceAsync(cancellationToken);
            await Task.Delay(LoopDelay, cancellationToken);
        }
    }

    /// <summary>
    /// 执行一次完整检测周期。
    /// </summary>
    public async Task RunOnceAsync(CancellationToken cancellationToken)
    {
        var cameraTasks = _cameraPipelines.Select(pipeline => pipeline.ProcessLatestDetailedAsync(cancellationToken, includePreview: false));
        var sensorTasks = _sensorReaders.Select(reader => reader.ReadAsync(cancellationToken));

        using var cameraResults = new CameraPipelineResultCollection(await Task.WhenAll(cameraTasks));
        var cameraFindings = cameraResults.Results.SelectMany(result => result.Findings).ToArray();
        var cameraFrames = cameraResults.Results.Select(result => result.ToFrameState()).ToArray();
        var sensorResults = (await Task.WhenAll(sensorTasks)).Where(snapshot => snapshot is not null).Cast<SensorSnapshot>().ToArray();
        var decision = _decisionEngine.Decide(
            _cameraPipelines.Select(pipeline => pipeline.CameraId).ToArray(),
            cameraFindings,
            sensorResults,
            cameraFrames);

        await ReactAsync(decision, cancellationToken);
        await _eventWriter.WriteAsync(decision, cancellationToken);
        Console.WriteLine($"RideManager cycle completed: {decision.RiskLevel}, cameras={cameraFindings.Length}, frames={cameraFrames.Length}, sensors={sensorResults.Length}");
    }

    /// <summary>
    /// 根据安全决策触发执行器动作。
    /// </summary>
    private async Task ReactAsync(SafetyDecision decision, CancellationToken cancellationToken)
    {
        if (decision.RiskLevel == SafetyRiskLevel.Danger)
        {
            await _brakeController.ApplyAsync(decision, cancellationToken);
        }

        if (decision.RiskLevel != SafetyRiskLevel.Normal)
        {
            await _speakerNotifier.NotifyAsync(decision, cancellationToken);
        }
    }

    /// <summary>
    /// 批量释放本轮摄像头处理结果里的预览资源。
    /// </summary>
    private sealed class CameraPipelineResultCollection : IDisposable
    {
        public CameraPipelineResultCollection(IEnumerable<CameraPipelineResult?> results)
        {
            Results = results.Where(result => result is not null).Cast<CameraPipelineResult>().ToArray();
        }

        public IReadOnlyList<CameraPipelineResult> Results { get; }

        public void Dispose()
        {
            foreach (var result in Results)
            {
                result.Dispose();
            }
        }
    }
}
