using System.Diagnostics;
using System.Text;
using System.Text.Json;
using RideManager.Utils;

namespace RideManager.Sensors;

/// <summary>
/// 通过 Python/Bleak 子进程采集雷达 BLE 数据。
/// </summary>
public sealed class PythonRadarClient : IRadarClient
{
    private readonly SensorEndpointOptions _options;
    private readonly TimeSpan _restartDelay;
    private readonly CancellationTokenSource _stop = new();
    private readonly object _sync = new();
    private readonly object _processSync = new();
    private Task? _runTask;
    private Process? _process;
    private TaskCompletionSource<RadarFrame>? _nextFrame;

    public PythonRadarClient(SensorEndpointOptions options)
    {
        _options = options;
        _restartDelay = TimeSpan.FromSeconds(Math.Max(0.2, options.PythonRestartDelaySeconds));
    }

    public event EventHandler<RadarFrame>? FrameReceived;

    public event EventHandler<RadarHealth>? HealthReceived;

    public event EventHandler<RadarConnectionState>? StateChanged;

    public RadarFrame? LatestFrame { get; private set; }

    public RadarHealth? LatestHealth { get; private set; }

    public RadarConnectionState State { get; private set; } = RadarConnectionState.Idle();

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (_runTask is not null)
        {
            return Task.CompletedTask;
        }

        _runTask = Task.Run(() => RunAsync(_stop.Token), CancellationToken.None);
        return Task.CompletedTask;
    }

    public async Task<RadarFrame?> WaitForFrameAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        if (LatestFrame is not null)
        {
            return LatestFrame;
        }

        var completion = new TaskCompletionSource<RadarFrame>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_sync)
        {
            _nextFrame = completion;
        }

        try
        {
            var timeoutTask = Task.Delay(timeout, cancellationToken);
            var finished = await Task.WhenAny(completion.Task, timeoutTask).ConfigureAwait(false);
            return finished == completion.Task ? await completion.Task.ConfigureAwait(false) : null;
        }
        finally
        {
            lock (_sync)
            {
                if (ReferenceEquals(_nextFrame, completion))
                {
                    _nextFrame = null;
                }
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        KillCurrentProcess();
        if (_runTask is not null)
        {
            try
            {
                await _runTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }

        _stop.Dispose();
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            Process? process = null;
            Task? stdoutTask = null;
            Task? stderrTask = null;
            try
            {
                process = StartProcess();
                lock (_processSync)
                {
                    _process = process;
                }

                PublishState("python_running", _options.DeviceName, NormalizeAddress(_options.Address), $"pid={process.Id}");
                stdoutTask = ReadStdoutAsync(process, cancellationToken);
                stderrTask = ReadStderrAsync(process, cancellationToken);
                await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);

                var message = process.ExitCode == 0
                    ? "python radar process exited"
                    : $"python radar process exited with code {process.ExitCode}";
                PublishState("python_exited", _options.DeviceName, NormalizeAddress(_options.Address), message);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                PublishState("python_error", _options.DeviceName, NormalizeAddress(_options.Address), ex.Message);
            }
            finally
            {
                if (process is not null)
                {
                    TerminateProcess(process);
                    await AwaitReaderAsync(stdoutTask).ConfigureAwait(false);
                    await AwaitReaderAsync(stderrTask).ConfigureAwait(false);
                    process.Dispose();
                }

                lock (_processSync)
                {
                    if (ReferenceEquals(_process, process))
                    {
                        _process = null;
                    }
                }
            }

            if (!cancellationToken.IsCancellationRequested)
            {
                PublishState("python_restarting", _options.DeviceName, NormalizeAddress(_options.Address), $"restart in {_restartDelay.TotalSeconds:F1}s");
                await Task.Delay(_restartDelay, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private Process StartProcess()
    {
        var scriptPath = ResolveScriptPath(_options.PythonScript);
        var startInfo = new ProcessStartInfo
        {
            FileName = string.IsNullOrWhiteSpace(_options.PythonExecutable) ? "python3" : _options.PythonExecutable,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            CreateNoWindow = true
        };

        startInfo.ArgumentList.Add(scriptPath);
        startInfo.ArgumentList.Add("--jsonl");
        startInfo.ArgumentList.Add("--name");
        startInfo.ArgumentList.Add(_options.DeviceName);
        startInfo.ArgumentList.Add("--service-uuid");
        startInfo.ArgumentList.Add(_options.ServiceUuid);
        startInfo.ArgumentList.Add("--notify-uuid");
        startInfo.ArgumentList.Add(_options.NotifyUuid);
        startInfo.ArgumentList.Add("--health-uuid");
        startInfo.ArgumentList.Add(_options.HealthUuid);
        startInfo.ArgumentList.Add("--scan-timeout");
        startInfo.ArgumentList.Add(Math.Max(1.0, _options.ScanTimeoutSeconds).ToString("0.###"));
        startInfo.ArgumentList.Add("--connect-timeout");
        startInfo.ArgumentList.Add(Math.Max(1.0, _options.ServicesTimeoutSeconds).ToString("0.###"));

        if (!RadarProtocol.IsPlaceholderAddress(_options.Address))
        {
            startInfo.ArgumentList.Add("--address");
            startInfo.ArgumentList.Add(_options.Address);
        }

        if (_options.MatchByService)
        {
            startInfo.ArgumentList.Add("--by-service");
        }

        if (!_options.SubscribeHealth)
        {
            startInfo.ArgumentList.Add("--no-health");
        }

        PublishState("python_starting", _options.DeviceName, NormalizeAddress(_options.Address), $"{startInfo.FileName} {scriptPath}");
        var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        return process.Start()
            ? process
            : throw new InvalidOperationException("Unable to start python radar process.");
    }

    private async Task ReadStdoutAsync(Process process, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var line = await process.StandardOutput.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            if (line is null)
            {
                return;
            }

            ProcessLine(line);
        }
    }

    private async Task ReadStderrAsync(Process process, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var line = await process.StandardError.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            if (line is null)
            {
                return;
            }

            if (!string.IsNullOrWhiteSpace(line))
            {
                PublishState("python_stderr", _options.DeviceName, NormalizeAddress(_options.Address), line.Trim());
            }
        }
    }

    private void ProcessLine(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return;
        }

        try
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            if (!root.TryGetProperty("type", out var typeElement))
            {
                ProcessRawPayload(root, line);
                return;
            }

            var type = typeElement.GetString() ?? string.Empty;
            switch (type)
            {
                case "frame":
                    if (root.TryGetProperty("payload", out var framePayload))
                    {
                        PublishFrame(framePayload.GetRawText());
                    }

                    break;
                case "health":
                    if (root.TryGetProperty("payload", out var healthPayload))
                    {
                        PublishHealth(healthPayload.GetRawText());
                    }

                    break;
                case "state":
                    PublishState(
                        ReadString(root, "phase") ?? "python_state",
                        ReadString(root, "deviceName") ?? _options.DeviceName,
                        ReadString(root, "deviceAddress") ?? NormalizeAddress(_options.Address),
                        ReadString(root, "message"));
                    break;
                default:
                    PublishState("python_stdout", _options.DeviceName, NormalizeAddress(_options.Address), line);
                    break;
            }
        }
        catch (Exception ex)
        {
            PublishState("python_parse_error", _options.DeviceName, NormalizeAddress(_options.Address), $"{ex.Message}: {line}");
        }
    }

    private void ProcessRawPayload(JsonElement root, string line)
    {
        if (root.TryGetProperty("seq", out _))
        {
            PublishFrame(line);
            return;
        }

        if (root.TryGetProperty("up", out _) || root.TryGetProperty("nt", out _))
        {
            PublishHealth(line);
            return;
        }

        PublishState("python_stdout", _options.DeviceName, NormalizeAddress(_options.Address), line);
    }

    private void PublishFrame(string json)
    {
        var frame = RadarProtocol.ParseFrame(Encoding.UTF8.GetBytes(json), DateTimeOffset.UtcNow);
        LatestFrame = frame;
        FrameReceived?.Invoke(this, frame);

        lock (_sync)
        {
            _nextFrame?.TrySetResult(frame);
            _nextFrame = null;
        }
    }

    private void PublishHealth(string json)
    {
        var health = RadarProtocol.ParseHealth(Encoding.UTF8.GetBytes(json), DateTimeOffset.UtcNow);
        LatestHealth = health;
        HealthReceived?.Invoke(this, health);
    }

    private void PublishState(string phase, string? deviceName, string? deviceAddress, string? message)
    {
        State = new RadarConnectionState(phase, deviceName, deviceAddress, message, DateTimeOffset.UtcNow);
        StateChanged?.Invoke(this, State);
    }

    private void KillCurrentProcess()
    {
        lock (_processSync)
        {
            if (_process is not null)
            {
                TerminateProcess(_process);
            }
        }
    }

    private static void TerminateProcess(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
        }
        catch (NotSupportedException)
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill();
                }
            }
            catch (InvalidOperationException)
            {
            }
        }
    }

    private static async Task AwaitReaderAsync(Task? task)
    {
        if (task is null)
        {
            return;
        }

        try
        {
            await task.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
        catch (InvalidOperationException)
        {
        }
    }

    private static string ResolveScriptPath(string path)
    {
        if (Path.IsPathRooted(path) || File.Exists(path))
        {
            return path;
        }

        var fromBaseDirectory = Path.Combine(AppContext.BaseDirectory, path);
        return File.Exists(fromBaseDirectory) ? fromBaseDirectory : path;
    }

    private static string? NormalizeAddress(string address)
    {
        return RadarProtocol.IsPlaceholderAddress(address) ? null : address;
    }

    private static string? ReadString(JsonElement root, string name)
    {
        return root.TryGetProperty(name, out var value) ? value.GetString() : null;
    }
}
