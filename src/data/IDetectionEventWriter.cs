using RideManager.Core;

namespace RideManager.Data;

/// <summary>
/// 定义检测事件持久化接口。
/// </summary>
public interface IDetectionEventWriter
{
    /// <summary>
    /// 写入一次主控决策。
    /// </summary>
    Task WriteAsync(SafetyDecision decision, CancellationToken cancellationToken);
}
