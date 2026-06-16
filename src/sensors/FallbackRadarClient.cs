using RideManager.Utils;

namespace RideManager.Sensors;

/// <summary>
/// 先使用原生 C# BLE 客户端，超时无帧时切换到 Python BLE 采集进程。
/// </summary>
public sealed class FallbackRadarClient : IRadarClient
{
    private readonly SensorEndpointOptions _options;
    private readonly Func<IRadarClient> _createPrimary;
    private readonly SemaphoreSlim _switchGate = new(1, 1);
    private readonly CancellationTokenSource _stop = new();
    private IRadarClient? _primary;
    private IRadarClient? _fallback;
    private IRadarClient? _active;
    private Task? _monitorTask;
    private DateTimeOffset _primaryStartedAt;
    private bool _started;

    public FallbackRadarClient(SensorEndpointOptions options, Func<IRadarClient> createPrimary)
    {
        _options = options;
        _createPrimary = createPrimary;
    }

    public event EventHandler<RadarFrame>? FrameReceived;

    public event EventHandler<RadarHealth>? HealthReceived;

    public event EventHandler<RadarConnectionState>? StateChanged;

    public RadarFrame? LatestFrame { get; private set; }

    public RadarHealth? LatestHealth { get; private set; }

    public RadarConnectionState State { get; private set; } = RadarConnectionState.Idle();

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (_started)
        {
            return;
        }

        _started = true;
        try
        {
            _primary = _createPrimary();
            Subscribe(_primary);
            _active = _primary;
            _primaryStartedAt = DateTimeOffset.UtcNow;
            await _primary.StartAsync(cancellationToken).ConfigureAwait(false);
            _monitorTask = Task.Run(() => MonitorPrimaryAsync(_stop.Token), CancellationToken.None);
        }
        catch (Exception ex)
        {
            PublishState("fallback", _options.DeviceName, _options.Address, $"native radar start failed: {ex.Message}");
            await SwitchToFallbackAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task<RadarFrame?> WaitForFrameAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        var active = _active;
        if (active is null)
        {
            await StartAsync(cancellationToken).ConfigureAwait(false);
            active = _active;
        }

        if (active is null)
        {
            return null;
        }

        var frame = active.LatestFrame ?? await active.WaitForFrameAsync(timeout, cancellationToken).ConfigureAwait(false);
        if (frame is not null || ReferenceEquals(active, _fallback) || !_options.PythonFallbackEnabled)
        {
            return frame;
        }

        var fallbackTimeout = TimeSpan.FromSeconds(Math.Max(1.0, _options.PythonFallbackTimeoutSeconds));
        var elapsed = DateTimeOffset.UtcNow - _primaryStartedAt;
        if (elapsed < fallbackTimeout)
        {
            return null;
        }

        PublishState("fallback", _options.DeviceName, _options.Address, $"native radar produced no frame within {fallbackTimeout.TotalSeconds:F1}s");
        await SwitchToFallbackAsync(cancellationToken).ConfigureAwait(false);
        return _fallback is null
            ? null
            : _fallback.LatestFrame ?? await _fallback.WaitForFrameAsync(timeout, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        if (_monitorTask is not null)
        {
            try
            {
                await _monitorTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }

        if (_primary is not null)
        {
            await _primary.DisposeAsync().ConfigureAwait(false);
        }

        if (_fallback is not null)
        {
            await _fallback.DisposeAsync().ConfigureAwait(false);
        }

        _stop.Dispose();
        _switchGate.Dispose();
    }

    private async Task MonitorPrimaryAsync(CancellationToken cancellationToken)
    {
        var timeout = TimeSpan.FromSeconds(Math.Max(1.0, _options.PythonFallbackTimeoutSeconds));
        try
        {
            if (_primary is null || ReferenceEquals(_active, _fallback))
            {
                return;
            }

            var frame = _primary.LatestFrame ?? await _primary.WaitForFrameAsync(timeout, cancellationToken).ConfigureAwait(false);
            if (frame is null && !cancellationToken.IsCancellationRequested)
            {
                PublishState("fallback", _options.DeviceName, _options.Address, $"native radar produced no frame within {timeout.TotalSeconds:F1}s");
                await SwitchToFallbackAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            PublishState("fallback", _options.DeviceName, _options.Address, $"native radar monitor failed: {ex.Message}");
            await SwitchToFallbackAsync(CancellationToken.None).ConfigureAwait(false);
        }
    }

    private async Task SwitchToFallbackAsync(CancellationToken cancellationToken)
    {
        await _switchGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_fallback is not null)
            {
                _active = _fallback;
                return;
            }

            if (_primary is not null)
            {
                await _primary.DisposeAsync().ConfigureAwait(false);
                _primary = null;
            }

            _fallback = new PythonRadarClient(_options);
            Subscribe(_fallback);
            _active = _fallback;
            await _fallback.StartAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _switchGate.Release();
        }
    }

    private void Subscribe(IRadarClient client)
    {
        client.StateChanged += OnStateChanged;
        client.FrameReceived += OnFrameReceived;
        client.HealthReceived += OnHealthReceived;
    }

    private void OnStateChanged(object? sender, RadarConnectionState state)
    {
        State = state;
        StateChanged?.Invoke(this, state);
    }

    private void OnFrameReceived(object? sender, RadarFrame frame)
    {
        LatestFrame = frame;
        FrameReceived?.Invoke(this, frame);
    }

    private void OnHealthReceived(object? sender, RadarHealth health)
    {
        LatestHealth = health;
        HealthReceived?.Invoke(this, health);
    }

    private void PublishState(string phase, string? deviceName, string? deviceAddress, string? message)
    {
        var state = new RadarConnectionState(phase, deviceName, deviceAddress, message, DateTimeOffset.UtcNow);
        State = state;
        StateChanged?.Invoke(this, state);
    }
}
