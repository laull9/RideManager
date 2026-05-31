using RideManager.Actuators;
using RideManager.Camera;
using RideManager.Core;
using RideManager.Data;
using RideManager.Models;
using RideManager.Sensors;
using RideManager.Utils;

var configPath = ReadOption(args, "--config") ?? "config.toml";
var options = ConfigLoader.Load(configPath);

if (args.Contains("liveradar", StringComparer.OrdinalIgnoreCase))
{
    var liveOptions = new RadarLiveTestOptions(
        ParseDuration(ReadOption(args, "--duration")),
        args.Contains("--headless", StringComparer.OrdinalIgnoreCase),
        args.Contains("--simulate", StringComparer.OrdinalIgnoreCase),
        ParsePort(ReadOption(args, "--port"), 5089));

    await new RadarLiveTester(options.Sensors.Radar).RunAsync(liveOptions, CancellationToken.None);
    return;
}

var runtimeSelector = new ModelRuntimeSelector(options.Models);
var cameraPipelines = CameraPipelineFactory.CreateThreeCameraPipelines(options.Cameras, runtimeSelector);
await using var cameraDisposer = new AsyncPipelineDisposer(cameraPipelines);

if (args.Contains("livetest", StringComparer.OrdinalIgnoreCase))
{
    var liveOptions = new CameraLiveTestOptions(
        ParseOptionalCamera(ReadOption(args, "--camera")),
        ParseDuration(ReadOption(args, "--duration")),
        args.Contains("--headless", StringComparer.OrdinalIgnoreCase));

    await new CameraLiveTester(cameraPipelines).RunAsync(liveOptions, CancellationToken.None);
    return;
}

await using var radarReader = new RadarBluetoothReader(options.Sensors.Radar);
var sensorReaders = new ISensorReader[]
{
    radarReader,
    new GyroSensorReader(options.Sensors.Gyro)
};

var supervisor = new RideSupervisor(
    cameraPipelines,
    sensorReaders,
    new NoopBrakeController(options.Actuators.Brake),
    new NoopSpeakerNotifier(options.Actuators.Speaker),
    new SafetyDecisionEngine(),
    new PostgresDetectionEventWriter(options.Database));

await supervisor.RunOnceAsync(CancellationToken.None);

/// <summary>
/// 读取命令行选项值。
/// </summary>
static string? ReadOption(string[] args, string name)
{
    for (var i = 0; i < args.Length - 1; i++)
    {
        if (args[i].Equals(name, StringComparison.OrdinalIgnoreCase))
        {
            return args[i + 1];
        }
    }

    return null;
}

/// <summary>
/// 解析 livetest 摄像头选择，未指定或 all 表示三路全开。
/// </summary>
static CameraId? ParseOptionalCamera(string? value)
{
    if (string.IsNullOrWhiteSpace(value) || value.Equals("all", StringComparison.OrdinalIgnoreCase))
    {
        return null;
    }

    return value.ToUpperInvariant() switch
    {
        "CAM_FRONT" or "FRONT" or "1" => CameraId.CamFront,
        "CAM_FACE" or "FACE" or "2" => CameraId.CamFace,
        "CAM_BACK" or "BACK" or "3" => CameraId.CamBack,
        _ => throw new ArgumentException($"Unsupported camera id: {value}")
    };
}

/// <summary>
/// 解析 livetest 运行时长，单位为秒。
/// </summary>
static TimeSpan? ParseDuration(string? value)
{
    if (string.IsNullOrWhiteSpace(value))
    {
        return null;
    }

    return double.TryParse(value, out var seconds) && seconds > 0
        ? TimeSpan.FromSeconds(seconds)
        : throw new ArgumentException($"Invalid duration seconds: {value}");
}

/// <summary>
/// 解析本地 Web 服务端口。
/// </summary>
static int ParsePort(string? value, int defaultPort)
{
    if (string.IsNullOrWhiteSpace(value))
    {
        return defaultPort;
    }

    return int.TryParse(value, out var port) && port is > 0 and < 65536
        ? port
        : throw new ArgumentException($"Invalid port: {value}");
}

/// <summary>
/// 批量释放摄像头管线。
/// </summary>
internal sealed class AsyncPipelineDisposer : IAsyncDisposable
{
    private readonly IReadOnlyList<CameraPipeline> _pipelines;

    /// <summary>
    /// 创建管线释放器。
    /// </summary>
    public AsyncPipelineDisposer(IReadOnlyList<CameraPipeline> pipelines)
    {
        _pipelines = pipelines;
    }

    /// <summary>
    /// 释放所有摄像头管线。
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        foreach (var pipeline in _pipelines)
        {
            await pipeline.DisposeAsync();
        }
    }
}
