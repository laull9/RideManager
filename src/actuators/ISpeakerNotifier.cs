using RideManager.Core;

namespace RideManager.Actuators;

/// <summary>
/// 定义语音播报接口。
/// </summary>
public interface ISpeakerNotifier
{
    /// <summary>
    /// 根据安全决策播放提示。
    /// </summary>
    Task NotifyAsync(SafetyDecision decision, CancellationToken cancellationToken);
}
