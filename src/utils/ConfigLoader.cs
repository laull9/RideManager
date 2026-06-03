using System.Text.Json.Serialization;
using System.Text.Json;
using RideManager.Camera;
using RideManager.Models;
using Tomlyn;

namespace RideManager.Utils;

/// <summary>
/// 从 config.toml 读取应用配置。
/// </summary>
public static class ConfigLoader
{
    /// <summary>
    /// 加载配置文件，缺失时返回可运行的默认骨架配置。
    /// </summary>
    public static RideManagerOptions Load(string path)
    {
        if (!File.Exists(path))
        {
            return CreateDefaults();
        }

        var config = TomlSerializer.Deserialize<ConfigToml>(File.ReadAllText(path), SerializerOptions) ?? new ConfigToml();
        return new RideManagerOptions(
            config.Cameras.Select(ParseCamera).ToArray(),
            new ModelOptions(ParseBackend(config.Models.Backend), config.Models.Directory),
            new SensorOptions(ParseEndpoint(config.Sensors.Radar), ParseEndpoint(config.Sensors.Gyro)),
            new ActuatorOptions(ParseActuator(config.Actuators.Brake), ParseActuator(config.Actuators.Speaker)),
            new DatabaseOptions(config.Database.ConnectionString));
    }

    /// <summary>
    /// 定义 TOML 与 C# 属性之间的命名规则。
    /// </summary>
    private static readonly TomlSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
    };

    /// <summary>
    /// 创建默认配置，方便在配置文件缺失时启动骨架。
    /// </summary>
    private static RideManagerOptions CreateDefaults()
    {
        return new RideManagerOptions(
            new[]
            {
                new CameraOptions(CameraId.CamFront, true, "/dev/video0", "yolo26n.onnx", 1280, 720, 640, 640, 30, 0.35),
                new CameraOptions(CameraId.CamFace, true, "/dev/video1", "pfld_lite.onnx", 640, 480, 112, 112, 30, 0.6),
                new CameraOptions(CameraId.CamBack, true, "/dev/video2", "yolo26n.onnx", 1280, 720, 640, 640, 30, 0.35)
            },
            new ModelOptions(ModelBackend.Onnx, "models"),
            new SensorOptions(
                CreateDefaultRadarEndpoint(),
                new SensorEndpointOptions(false, "serial", "/dev/ttyS0", string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, false, false, 12.0, 10.0, 2.0)),
            new ActuatorOptions(new ActuatorEndpointOptions(false), new ActuatorEndpointOptions(false)),
            new DatabaseOptions(string.Empty));
    }

    /// <summary>
    /// 解析摄像头配置节点。
    /// </summary>
    private static CameraOptions ParseCamera(CameraToml value)
    {
        return new CameraOptions(
            ParseCameraId(value.Id),
            value.Enabled,
            value.Device,
            value.Model,
            value.Width,
            value.Height,
            value.InputWidth,
            value.InputHeight,
            value.Fps,
            Math.Clamp(value.ConfidenceThreshold, 0.0, 1.0),
            value.PixelFormat);
    }

    /// <summary>
    /// 解析传感器端点配置节点。
    /// </summary>
    private static SensorEndpointOptions ParseEndpoint(SensorEndpointToml value)
    {
        return new SensorEndpointOptions(
            value.Enabled,
            value.Transport,
            value.Address,
            value.DeviceName,
            value.ServiceUuid,
            value.NotifyUuid,
            value.ConfigUuid,
            value.HealthUuid,
            value.MatchByService,
            value.SubscribeHealth,
            value.ScanTimeoutSeconds,
            value.ServicesTimeoutSeconds,
            value.ReconnectDelaySeconds);
    }

    /// <summary>
    /// 创建雷达端点默认配置。
    /// </summary>
    private static SensorEndpointOptions CreateDefaultRadarEndpoint()
    {
        return new SensorEndpointOptions(
            true,
            "bluetooth",
            string.Empty,
            "EVADAR-C6",
            "0000ad01-0000-1000-8000-00805f9b34fb",
            "0000ad02-0000-1000-8000-00805f9b34fb",
            "0000ad03-0000-1000-8000-00805f9b34fb",
            "0000ad04-0000-1000-8000-00805f9b34fb",
            true,
            true,
            12.0,
            10.0,
            2.0);
    }

    /// <summary>
    /// 解析执行器端点配置节点。
    /// </summary>
    private static ActuatorEndpointOptions ParseActuator(ActuatorEndpointToml value)
    {
        return new ActuatorEndpointOptions(value.Enabled);
    }

    /// <summary>
    /// 解析摄像头枚举值。
    /// </summary>
    private static CameraId ParseCameraId(string value)
    {
        return value.ToUpperInvariant() switch
        {
            "CAM_FACE" => CameraId.CamFace,
            "CAM_BACK" => CameraId.CamBack,
            _ => CameraId.CamFront
        };
    }

    /// <summary>
    /// 解析模型后端枚举值。
    /// </summary>
    private static ModelBackend ParseBackend(string value)
    {
        return value.Trim().ToLowerInvariant() switch
        {
            "onnx" => ModelBackend.Onnx,
            "rknn" => ModelBackend.Rknn,
            _ => throw new InvalidOperationException($"Unsupported models.backend: {value}")
        };
    }

    /// <summary>
    /// 表示 config.toml 根节点。
    /// </summary>
    private sealed class ConfigToml
    {
        public DatabaseToml Database { get; set; } = new();

        public ModelsToml Models { get; set; } = new();

        public List<CameraToml> Cameras { get; set; } = new();

        public SensorsToml Sensors { get; set; } = new();

        public ActuatorsToml Actuators { get; set; } = new();
    }

    /// <summary>
    /// 表示数据库配置节点。
    /// </summary>
    private sealed class DatabaseToml
    {
        [JsonPropertyName("connection_string")]
        public string ConnectionString { get; set; } = string.Empty;
    }

    /// <summary>
    /// 表示模型配置节点。
    /// </summary>
    private sealed class ModelsToml
    {
        public string Backend { get; set; } = "onnx";

        public string Directory { get; set; } = "models";
    }

    /// <summary>
    /// 表示摄像头配置节点。
    /// </summary>
    private sealed class CameraToml
    {
        public string Id { get; set; } = "CAM_FRONT";

        public bool Enabled { get; set; } = true;

        public string Device { get; set; } = "/dev/video0";

        public string Model { get; set; } = string.Empty;

        public int Width { get; set; } = 1280;

        public int Height { get; set; } = 720;

        public int InputWidth { get; set; } = 640;

        public int InputHeight { get; set; } = 640;

        public int Fps { get; set; } = 30;

        public double ConfidenceThreshold { get; set; } = 0.25;

        public string PixelFormat { get; set; } = "MJPG";
    }

    /// <summary>
    /// 表示传感器配置节点集合。
    /// </summary>
    private sealed class SensorsToml
    {
        public SensorEndpointToml Radar { get; set; } = new();

        public SensorEndpointToml Gyro { get; set; } = new();
    }

    /// <summary>
    /// 表示传感器端点配置节点。
    /// </summary>
    private sealed class SensorEndpointToml
    {
        public bool Enabled { get; set; }

        public string Transport { get; set; } = string.Empty;

        public string Address { get; set; } = string.Empty;

        public string DeviceName { get; set; } = string.Empty;

        public string ServiceUuid { get; set; } = string.Empty;

        public string NotifyUuid { get; set; } = string.Empty;

        public string ConfigUuid { get; set; } = string.Empty;

        public string HealthUuid { get; set; } = string.Empty;

        public bool MatchByService { get; set; }

        public bool SubscribeHealth { get; set; } = true;

        public double ScanTimeoutSeconds { get; set; } = 12.0;

        public double ServicesTimeoutSeconds { get; set; } = 10.0;

        public double ReconnectDelaySeconds { get; set; } = 2.0;
    }

    /// <summary>
    /// 表示执行器配置节点集合。
    /// </summary>
    private sealed class ActuatorsToml
    {
        public ActuatorEndpointToml Brake { get; set; } = new();

        public ActuatorEndpointToml Speaker { get; set; } = new();
    }

    /// <summary>
    /// 表示执行器端点配置节点。
    /// </summary>
    private sealed class ActuatorEndpointToml
    {
        public bool Enabled { get; set; }
    }
}
