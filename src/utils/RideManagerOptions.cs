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
    /// 获取当前摄像头要运行的模型列表；为空时使用旧版单模型字段。
    /// </summary>
    public IReadOnlyList<CameraModelOptions> Models { get; init; } = Array.Empty<CameraModelOptions>();

    /// <summary>
    /// 获取当前摄像头参与主控风险决策时使用的算法参数。
    /// </summary>
    public CameraRiskOptions Risk { get; init; } = CameraRiskOptions.ForCamera(Id);

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
    double PythonRestartDelaySeconds);

/// <summary>
/// 表示所有执行器的配置。
/// </summary>
public sealed record ActuatorOptions(ActuatorEndpointOptions Brake, ActuatorEndpointOptions Speaker);

/// <summary>
/// 表示单个执行器端点的配置。
/// </summary>
public sealed record ActuatorEndpointOptions(bool Enabled);

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
