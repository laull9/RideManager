using RideManager.Utils;

namespace RideManager.Camera;

/// <summary>
/// 提供可编译运行的摄像头采集占位实现。
/// </summary>
public sealed class SimulatedCameraSource : ICameraSource
{
    private readonly CameraOptions _options;

    /// <summary>
    /// 创建模拟摄像头源。
    /// </summary>
    public SimulatedCameraSource(CameraOptions options)
    {
        _options = options;
    }

    /// <summary>
    /// 读取一帧空数据，后续替换为 OpenCV VideoCapture 或硬件零拷贝采集。
    /// </summary>
    public Task<CameraFrame?> ReadLatestAsync(CancellationToken cancellationToken)
    {
        var estimatedSize = Math.Max(1, _options.Width * _options.Height * 3);
        return Task.FromResult<CameraFrame?>(
            new CameraFrame(_options.Id, DateTimeOffset.UtcNow, new byte[Math.Min(estimatedSize, 1024)]));
    }
}
