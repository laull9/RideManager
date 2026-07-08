using RideManager.Camera;
using RideManager.Core;
using RideManager.Models;

namespace RideManager.Utils;

/// <summary>
/// 表示整机运行所需的配置集合。
/// </summary>
public sealed record RideManagerOptions(
    IReadOnlyList<CameraOptions> Cameras,
    ModelOptions Models,
    SensorOptions Sensors,
    ActuatorOptions Actuators,
    DatabaseOptions Database,
    AppSyncOptions AppSync);

/// <summary>
/// 表示单个摄像头链路的配置。
/// </summary>
public sealed record CameraOptions(
    CameraId Id,
    bool Enabled,
    string Device,
    string ModelName,
    int Width,
    int Height,
    int InputWidth,
    int InputHeight,
    int Fps,
    double ConfidenceThreshold,
    string PixelFormat = "MJPG")
{
    /// <summary>
    /// 获取摄像头驱动协商使用的采集宽度；0 表示复用 Width。
    /// </summary>
    public int CaptureWidth { get; init; }

    /// <summary>
    /// 获取摄像头驱动协商使用的采集高度；0 表示复用 Height。
    /// </summary>
    public int CaptureHeight { get; init; }

    /// <summary>
    /// 获取摄像头驱动协商使用的采集帧率；0 表示复用 Fps。
    /// </summary>
    public int CaptureFps { get; init; }

    /// <summary>
    /// 获取当前摄像头要运行的模型列表；为空时使用旧版单模型字段。
    /// </summary>
    public IReadOnlyList<CameraModelOptions> Models { get; init; } = Array.Empty<CameraModelOptions>();

    /// <summary>
    /// 获取当前摄像头参与主控风险决策时使用的算法参数。
    /// </summary>
    public CameraRiskOptions Risk { get; init; } = CameraRiskOptions.ForCamera(Id);

    /// <summary>
    /// 获取实际请求摄像头驱动输出的宽度。
    /// </summary>
    public int EffectiveCaptureWidth => CaptureWidth > 0 ? CaptureWidth : Width;

    /// <summary>
    /// 获取实际请求摄像头驱动输出的高度。
    /// </summary>
    public int EffectiveCaptureHeight => CaptureHeight > 0 ? CaptureHeight : Height;

    /// <summary>
    /// 获取实际请求摄像头驱动输出的帧率。
    /// </summary>
    public int EffectiveCaptureFps => CaptureFps > 0 ? CaptureFps : Fps;

    /// <summary>
    /// 获取实际运行的模型列表，兼容旧版单模型配置。
    /// </summary>
    public IReadOnlyList<CameraModelOptions> EffectiveModels =>
        Models.Count > 0
            ? Models
            : new[]
            {
                new CameraModelOptions(ModelName, InputWidth, InputHeight, ConfidenceThreshold)
            };
}

/// <summary>
/// 表示单个摄像头链路内的一路模型配置。
/// </summary>
public sealed record CameraModelOptions(
    string ModelName,
    int InputWidth,
    int InputHeight,
    double ConfidenceThreshold,
    double MaxFps = 0.0,
    double CropX = 0.0,
    double CropY = 0.0,
    double CropWidth = 1.0,
    double CropHeight = 1.0);

/// <summary>
/// 表示推理运行时的配置。
/// </summary>
public sealed record ModelOptions(ModelBackend Backend, string Directory);

/// <summary>
/// 表示所有外部传感器的配置。
/// </summary>
public sealed record SensorOptions(SensorEndpointOptions Radar, SensorEndpointOptions Gyro);

/// <summary>
/// 表示单个传感器通讯端点的配置。
/// </summary>
public sealed record SensorEndpointOptions(
    bool Enabled,
    string Transport,
    string Address,
    string DeviceName,
    string ServiceUuid,
    string NotifyUuid,
    string ConfigUuid,
    string HealthUuid,
    bool MatchByService,
    bool SubscribeHealth,
    double ScanTimeoutSeconds,
    double ServicesTimeoutSeconds,
    double ReconnectDelaySeconds,
    bool PythonFallbackEnabled,
    string PythonExecutable,
    string PythonScript,
    double PythonFallbackTimeoutSeconds,
    double PythonRestartDelaySeconds)
{
    /// <summary>
    /// Gets the serial baud rate for text-line sensors such as the gyro module.
    /// </summary>
    public int BaudRate { get; init; } = 115200;

    /// <summary>
    /// Gets the maximum time to wait for one text-line sensor sample.
    /// </summary>
    public double ReadTimeoutSeconds { get; init; } = 0.2;
}

/// <summary>
/// 表示所有执行器的配置。
/// </summary>
public sealed record ActuatorOptions(ActuatorEndpointOptions Brake, ActuatorEndpointOptions Speaker);

/// <summary>
/// 表示单个执行器端点的配置。
/// </summary>
public sealed record ActuatorEndpointOptions(
    bool Enabled,
    string AssetDirectory = "assests",
    string WarningFile = "warning.wav",
    string DangerFile = "danger.wav",
    string PlayerCommand = "",
    double MinIntervalSeconds = 3.0);

/// <summary>
/// 表示数据库连接配置。
/// </summary>
public sealed record DatabaseOptions(string ConnectionString);

/// <summary>
/// 表示手机 App 蓝牙同步服务配置。
/// </summary>
public sealed record AppSyncOptions(
    bool Enabled,
    string DeviceName,
    string ServiceUuid,
    string RxUuid,
    string TxUuid,
    int MaxPageSize,
    double DefaultSyncWindowHours,
    int NotifyChunkBytes,
    int MaxRequestBytes);
