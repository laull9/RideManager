using RideManager.Core;
using RideManager.Utils;

namespace RideManager.Actuators;

/// <summary>
/// 提供刹车驱动器的占位实现。
/// </summary>
public sealed class NoopBrakeController : IBrakeController
{
    private readonly ActuatorEndpointOptions _options;

    /// <summary>
    /// 创建刹车驱动器占位控制器。
    /// </summary>
    public NoopBrakeController(ActuatorEndpointOptions options)
    {
        _options = options;
    }

    /// <summary>
    /// 当前不执行真实刹车动作，后续根据下位机协议实现。
    /// </summary>
    public Task ApplyAsync(SafetyDecision decision, CancellationToken cancellationToken)
    {
        _ = decision;
        _ = _options;
        return Task.CompletedTask;
    }
}
