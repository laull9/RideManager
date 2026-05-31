using RideManager.Core;

namespace RideManager.Actuators;

/// <summary>
/// 定义刹车驱动器控制接口。
/// </summary>
public interface IBrakeController
{
    /// <summary>
    /// 根据安全决策触发刹车动作。
    /// </summary>
    Task ApplyAsync(SafetyDecision decision, CancellationToken cancellationToken);
}
