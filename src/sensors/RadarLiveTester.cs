using RideManager.Utils;

namespace RideManager.Sensors;

/// <summary>
/// 运行雷达 BLE 通信 live 测试。
/// </summary>
public sealed class RadarLiveTester
{
    private readonly SensorEndpointOptions _options;

    /// <summary>
    /// 创建雷达 live 测试器。
    /// </summary>
    public RadarLiveTester(SensorEndpointOptions options)
    {
        _options = options;
    }

    /// <summary>
    /// 启动雷达采集和 Web 曲线页面。
    /// </summary>
    public async Task RunAsync(RadarLiveTestOptions options, CancellationToken cancellationToken)
    {
        var history = new RadarHistory(600);
        await using var client = RadarClientFactory.Create(_options, options.Simulated);
        await using var server = options.Headless
            ? null
            : new RadarLivePreviewServer(options.Port, () => CreateState(client, history));

        client.StateChanged += (_, state) =>
            Console.WriteLine($"RADAR state={state.Phase} name={state.DeviceName ?? "--"} address={state.DeviceAddress ?? "--"} message={state.Message ?? "--"}");
        client.FrameReceived += (_, frame) =>
        {
            history.Add(frame);
            Console.WriteLine(
                $"RADAR frame seq={frame.Sequence} st=0x{frame.Status:X2} hr={FormatValue(frame.HasHeartRate, frame.HeartRateBpm)} br={FormatValue(frame.HasBreathingRate, frame.BreathingRateBpm)} d={FormatValue(frame.HasDistance, frame.DistanceCm)} presence={frame.HasPresence}");
        };
        client.HealthReceived += (_, health) =>
            Console.WriteLine($"RADAR health up={health.UptimeMs}ms notify={health.NotifyCount} dropped={health.DroppedNotifyCount} stale={health.RadarStaleMs}ms connected={health.ClientConnected} fw={health.FirmwareVersion ?? "--"}");
        await client.StartAsync(cancellationToken).ConfigureAwait(false);

        Console.WriteLine(options.Headless
            ? "Radar live test started in headless mode."
            : $"Radar live test started. Preview: {server?.Url}");

        var stopAt = options.Duration is null ? (DateTimeOffset?)null : DateTimeOffset.UtcNow.Add(options.Duration.Value);
        var lastConsoleAt = DateTimeOffset.MinValue;
        while (!cancellationToken.IsCancellationRequested && (stopAt is null || DateTimeOffset.UtcNow < stopAt))
        {
            if (DateTimeOffset.UtcNow - lastConsoleAt >= TimeSpan.FromSeconds(1))
            {
                Console.WriteLine(FormatConsole(client));
                lastConsoleAt = DateTimeOffset.UtcNow;
            }

            await Task.Delay(100, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// 创建浏览器轮询使用的状态对象。
    /// </summary>
    private static RadarLiveState CreateState(IRadarClient client, RadarHistory history)
    {
        var now = DateTimeOffset.UtcNow;
        var frames = history.Snapshot();
        var start = frames.Count == 0 ? now : frames[0].ObservedAt;

        return new RadarLiveState(
            client.State,
            client.LatestFrame,
            client.LatestHealth,
            frames.Select(frame => new RadarHistoryPoint(
                (frame.ObservedAt - start).TotalSeconds,
                frame.HasHeartRate ? frame.HeartRateBpm : null,
                frame.HasBreathingRate ? frame.BreathingRateBpm : null,
                frame.HasDistance ? frame.DistanceCm : null,
                frame.HasPresence,
                frame.Sequence,
                frame.Status)).ToArray(),
            now);
    }

    /// <summary>
    /// 格式化终端状态输出。
    /// </summary>
    private static string FormatConsole(IRadarClient client)
    {
        var frame = client.LatestFrame;
        if (frame is null)
        {
            return $"RADAR {client.State.Phase}: {client.State.Message}";
        }

        var hr = frame.HasHeartRate ? $"{frame.HeartRateBpm:F1}" : "--";
        var br = frame.HasBreathingRate ? $"{frame.BreathingRateBpm:F1}" : "--";
        var distance = frame.HasDistance ? $"{frame.DistanceCm:F1}" : "--";
        var staleMs = Math.Max(0, (DateTimeOffset.UtcNow - frame.ObservedAt).TotalMilliseconds);
        return $"RADAR {client.State.Phase} seq={frame.Sequence} hr={hr} br={br} d={distance}cm presence={frame.HasPresence} stale={staleMs:F0}ms";
    }

    private static string FormatValue(bool valid, double? value)
    {
        return valid && value is not null ? $"{value:F1}" : "--";
    }
}

/// <summary>
/// 浏览器页面使用的雷达 live 状态。
/// </summary>
public sealed record RadarLiveState(
    RadarConnectionState Connection,
    RadarFrame? LatestFrame,
    RadarHealth? LatestHealth,
    IReadOnlyList<RadarHistoryPoint> History,
    DateTimeOffset ServerTime);

/// <summary>
/// 浏览器页面使用的历史点。
/// </summary>
public sealed record RadarHistoryPoint(
    double Seconds,
    double? HeartRateBpm,
    double? BreathingRateBpm,
    double? DistanceCm,
    bool Presence,
    uint Sequence,
    byte Status);
