using RideManager.Core;
using RideManager.Utils;

namespace RideManager.Actuators;

/// <summary>
/// 提供语音播报系统的占位实现。
/// </summary>
public sealed class NoopSpeakerNotifier : ISpeakerNotifier
{
    private readonly ActuatorEndpointOptions _options;

    /// <summary>
    /// 创建语音播报占位通知器。
    /// </summary>
    public NoopSpeakerNotifier(ActuatorEndpointOptions options)
    {
        _options = options;
    }

    /// <summary>
    /// 当前不播放真实语音，后续接入音频输出或下位机协议。
    /// </summary>
    public Task NotifyAsync(SafetyDecision decision, CancellationToken cancellationToken)
    {
        _ = decision;
        _ = _options;
        return Task.CompletedTask;
    }
}
