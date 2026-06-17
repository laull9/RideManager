using RideManager.Utils;

namespace RideManager.Sensors;

/// <summary>
/// 读取 BLE 雷达最新生命体征数据。
/// </summary>
public sealed class RadarBluetoothReader : ISensorReader, IAsyncDisposable
{
    private readonly SensorEndpointOptions _options;
    private readonly SemaphoreSlim _startGate = new(1, 1);
    private IRadarClient? _client;

    /// <summary>
    /// 创建雷达读取器。
    /// </summary>
    public RadarBluetoothReader(SensorEndpointOptions options)
    {
        _options = options;
    }

    /// <summary>
    /// 提前启动后台 BLE 雷达连接。
    /// </summary>
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (!_options.Enabled)
        {
            return;
        }

        await EnsureStartedAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 读取雷达心率、呼吸率和距离数据。
    /// </summary>
    public async Task<SensorSnapshot?> ReadAsync(CancellationToken cancellationToken)
    {
        if (!_options.Enabled)
        {
            return null;
        }

        var client = await EnsureStartedAsync(cancellationToken).ConfigureAwait(false);
        var frame = client.LatestFrame ?? await client.WaitForFrameAsync(TimeSpan.FromMilliseconds(1200), cancellationToken).ConfigureAwait(false);
        if (frame is null)
        {
            return null;
        }

        return new SensorSnapshot("RADAR", frame.ObservedAt, RadarSnapshotValues.Create(frame));
    }

    /// <summary>
    /// 停止后台 BLE 会话。
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        _startGate.Dispose();
        if (_client is not null)
        {
            await _client.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// 延迟启动雷达客户端，避免未启用传感器时占用蓝牙资源。
    /// </summary>
    private async Task<IRadarClient> EnsureStartedAsync(CancellationToken cancellationToken)
    {
        if (_client is not null)
        {
            return _client;
        }

        await _startGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_client is not null)
            {
                return _client;
            }

            _client = RadarClientFactory.Create(_options);
            await _client.StartAsync(cancellationToken).ConfigureAwait(false);
            return _client;
        }
        finally
        {
            _startGate.Release();
        }
    }
}
