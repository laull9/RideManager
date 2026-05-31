using RideManager.Utils;

namespace RideManager.Sensors;

/// <summary>
/// 提供无硬件环境下的雷达数据模拟器。
/// </summary>
public sealed class SimulatedRadarClient : IRadarClient
{
    private readonly SensorEndpointOptions _options;
    private readonly CancellationTokenSource _stop = new();
    private Task? _loopTask;
    private uint _sequence;
    private DateTimeOffset _startedAt;

    /// <summary>
    /// 创建模拟雷达客户端。
    /// </summary>
    public SimulatedRadarClient(SensorEndpointOptions options)
    {
        _options = options;
    }

    /// <summary>
    /// 雷达数据帧到达事件。
    /// </summary>
    public event EventHandler<RadarFrame>? FrameReceived;

    /// <summary>
    /// 雷达固件健康状态到达事件。
    /// </summary>
    public event EventHandler<RadarHealth>? HealthReceived;

    /// <summary>
    /// 连接状态变化事件。
    /// </summary>
    public event EventHandler<RadarConnectionState>? StateChanged;

    /// <summary>
    /// 获取最新数据帧。
    /// </summary>
    public RadarFrame? LatestFrame { get; private set; }

    /// <summary>
    /// 获取最新固件健康状态。
    /// </summary>
    public RadarHealth? LatestHealth { get; private set; }

    /// <summary>
    /// 获取当前连接状态。
    /// </summary>
    public RadarConnectionState State { get; private set; } = RadarConnectionState.Idle();

    /// <summary>
    /// 启动模拟数据流。
    /// </summary>
    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (_loopTask is not null)
        {
            return Task.CompletedTask;
        }

        _startedAt = DateTimeOffset.UtcNow;
        PublishState("connected", _options.DeviceName, "simulate", "simulated radar stream");
        _loopTask = Task.Run(() => RunAsync(_stop.Token), CancellationToken.None);
        return Task.CompletedTask;
    }

    /// <summary>
    /// 等待下一帧模拟雷达数据。
    /// </summary>
    public async Task<RadarFrame?> WaitForFrameAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        if (LatestFrame is not null)
        {
            return LatestFrame;
        }

        var completion = new TaskCompletionSource<RadarFrame>(TaskCreationOptions.RunContinuationsAsynchronously);
        void Handler(object? _, RadarFrame frame)
        {
            completion.TrySetResult(frame);
        }

        FrameReceived += Handler;
        try
        {
            var finished = await Task.WhenAny(completion.Task, Task.Delay(timeout, cancellationToken)).ConfigureAwait(false);
            return finished == completion.Task ? await completion.Task.ConfigureAwait(false) : null;
        }
        finally
        {
            FrameReceived -= Handler;
        }
    }

    /// <summary>
    /// 停止模拟数据流。
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        if (_loopTask is not null)
        {
            try
            {
                await _loopTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }

        _stop.Dispose();
    }

    /// <summary>
    /// 按 10 Hz 生成有规律波动的生命体征数据。
    /// </summary>
    private async Task RunAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var now = DateTimeOffset.UtcNow;
            var seconds = (now - _startedAt).TotalSeconds;
            var status = (byte)(RadarStatusFlags.Breath | RadarStatusFlags.Heart | RadarStatusFlags.Distance | RadarStatusFlags.Presence);
            var frame = new RadarFrame(
                1,
                ++_sequence,
                (uint)Math.Max(0, (now - _startedAt).TotalMilliseconds),
                15.5 + Math.Sin(seconds / 3.0) * 1.6,
                76.0 + Math.Sin(seconds / 4.0) * 5.0 + Math.Cos(seconds / 7.0) * 1.5,
                52.0 + Math.Sin(seconds / 2.0) * 3.0,
                status,
                now,
                true,
                true,
                true,
                true);

            LatestFrame = frame;
            FrameReceived?.Invoke(this, frame);

            if (_sequence % 20 == 0)
            {
                var health = new RadarHealth(1, frame.DeviceTimestampMs, _sequence, 0, 0, true, "simulated", now);
                LatestHealth = health;
                HealthReceived?.Invoke(this, health);
            }

            await Task.Delay(100, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// 发布连接状态。
    /// </summary>
    private void PublishState(string phase, string? deviceName, string? deviceAddress, string? message)
    {
        State = new RadarConnectionState(phase, deviceName, deviceAddress, message, DateTimeOffset.UtcNow);
        StateChanged?.Invoke(this, State);
    }
}
