using RideManager.Camera;
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
    DatabaseOptions Database);

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
    int Fps);

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
public sealed record SensorEndpointOptions(bool Enabled, string Transport, string Address);

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
