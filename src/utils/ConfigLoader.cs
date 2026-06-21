using System.Text.Json.Serialization;
using System.Text.Json;
using RideManager.Camera;
using RideManager.Core;
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
            new DatabaseOptions(config.Database.ConnectionString),
            ParseAppSync(config.AppSync));
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
                new SensorEndpointOptions(false, "serial", "/dev/ttyS0", string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, false, false, 12.0, 10.0, 2.0, false, "python3", "scripts/ble_radar_stream.py", 8.0, 2.0)),
            new ActuatorOptions(new ActuatorEndpointOptions(false), new ActuatorEndpointOptions(false)),
            new DatabaseOptions(string.Empty),
            CreateDefaultAppSyncOptions());
    }

    /// <summary>
    /// 解析摄像头配置节点。
    /// </summary>
    private static CameraOptions ParseCamera(CameraToml value)
    {
        var modelOptions = value.Models
            .Select(model => new CameraModelOptions(
                model.Model,
                model.InputWidth > 0 ? model.InputWidth : value.InputWidth,
                model.InputHeight > 0 ? model.InputHeight : value.InputHeight,
                Math.Clamp(model.ConfidenceThreshold ?? value.ConfidenceThreshold, 0.0, 1.0),
                Math.Max(0.0, model.MaxFps),
                ClampCropStart(model.CropX),
                ClampCropStart(model.CropY),
                ClampCropSize(model.CropWidth),
                ClampCropSize(model.CropHeight)))
            .Where(model => !string.IsNullOrWhiteSpace(model.ModelName))
            .Select(ClampCropToImage)
            .ToArray();
        var primaryModel = !string.IsNullOrWhiteSpace(value.Model)
            ? value.Model
            : modelOptions.FirstOrDefault()?.ModelName ?? string.Empty;
        var primaryInputWidth = modelOptions.FirstOrDefault()?.InputWidth ?? value.InputWidth;
        var primaryInputHeight = modelOptions.FirstOrDefault()?.InputHeight ?? value.InputHeight;
        var primaryThreshold = modelOptions.FirstOrDefault()?.ConfidenceThreshold ?? value.ConfidenceThreshold;

        var cameraId = ParseCameraId(value.Id);

        return new CameraOptions(
            cameraId,
            value.Enabled,
            value.Device,
            primaryModel,
            value.Width,
            value.Height,
            primaryInputWidth,
            primaryInputHeight,
            value.Fps,
            Math.Clamp(primaryThreshold, 0.0, 1.0),
            value.PixelFormat)
        {
            Models = modelOptions,
            Risk = ParseCameraRisk(cameraId, value)
        };
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
            value.ReconnectDelaySeconds,
            value.PythonFallbackEnabled,
            string.IsNullOrWhiteSpace(value.PythonExecutable) ? "python3" : value.PythonExecutable,
            string.IsNullOrWhiteSpace(value.PythonScript) ? "scripts/ble_radar_stream.py" : value.PythonScript,
            value.PythonFallbackTimeoutSeconds <= 0 ? 8.0 : value.PythonFallbackTimeoutSeconds,
            value.PythonRestartDelaySeconds <= 0 ? value.ReconnectDelaySeconds : value.PythonRestartDelaySeconds);
    }

    /// <summary>
    /// 将裁剪起点约束到归一化图像范围内。
    /// </summary>
    private static double ClampCropStart(double value)
    {
        return Math.Clamp(value, 0.0, 1.0);
    }

    /// <summary>
    /// 将裁剪尺寸约束到归一化图像范围内。
    /// </summary>
    private static double ClampCropSize(double value)
    {
        return value <= 0 ? 1.0 : Math.Clamp(value, 0.0, 1.0);
    }

    /// <summary>
    /// 确保裁剪区域不会超出原图边界。
    /// </summary>
    private static CameraModelOptions ClampCropToImage(CameraModelOptions model)
    {
        var width = Math.Clamp(model.CropWidth, 0.001, 1.0 - model.CropX);
        var height = Math.Clamp(model.CropHeight, 0.001, 1.0 - model.CropY);
        return model with
        {
            CropWidth = width,
            CropHeight = height
        };
    }

    /// <summary>
    /// 解析摄像头风险算法参数，并为后向鱼眼摄像头提供可用默认值。
    /// </summary>
    private static CameraRiskOptions ParseCameraRisk(CameraId cameraId, CameraToml value)
    {
        var defaults = CameraRiskOptions.ForCamera(cameraId);
        return new CameraRiskOptions(
            ClampRange(value.FisheyeFovDegrees ?? defaults.FisheyeFovDegrees, 1.0, 220.0),
            ClampRange(value.FisheyeStrength ?? defaults.FisheyeStrength, 0.0, 1.0),
            ClampRange(value.RearCenterDangerAngleDegrees ?? defaults.RearCenterDangerAngleDegrees, 1.0, 120.0),
            ClampRange(value.RearEdgeWarningMinScore ?? defaults.RearEdgeWarningMinScore, 0.0, 1.0));
    }

    /// <summary>
    /// 将普通数值限制在指定区间。
    /// </summary>
    private static double ClampRange(double value, double min, double max)
    {
        return double.IsFinite(value) ? Math.Clamp(value, min, max) : min;
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
            2.0,
            true,
            "python3",
            "scripts/ble_radar_stream.py",
            8.0,
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
    /// 解析手机 App 同步配置。
    /// </summary>
    private static AppSyncOptions ParseAppSync(AppSyncToml value)
    {
        return new AppSyncOptions(
            value.Enabled,
            string.IsNullOrWhiteSpace(value.DeviceName) ? "RideManager" : value.DeviceName,
            string.IsNullOrWhiteSpace(value.ServiceUuid) ? "7f7d0001-4f52-4d32-9b2a-0f0b5a8b1000" : value.ServiceUuid,
            string.IsNullOrWhiteSpace(value.RxUuid) ? "7f7d0002-4f52-4d32-9b2a-0f0b5a8b1000" : value.RxUuid,
            string.IsNullOrWhiteSpace(value.TxUuid) ? "7f7d0003-4f52-4d32-9b2a-0f0b5a8b1000" : value.TxUuid,
            Math.Clamp(value.MaxPageSize, 1, 500),
            Math.Clamp(value.DefaultSyncWindowHours, 1.0, 168.0),
            Math.Clamp(value.NotifyChunkBytes, 64, 4096),
            Math.Clamp(value.MaxRequestBytes, 512, 65536));
    }

    /// <summary>
    /// 创建默认手机 App 同步配置。
    /// </summary>
    private static AppSyncOptions CreateDefaultAppSyncOptions()
    {
        return new AppSyncOptions(
            true,
            "RideManager",
            "7f7d0001-4f52-4d32-9b2a-0f0b5a8b1000",
            "7f7d0002-4f52-4d32-9b2a-0f0b5a8b1000",
            "7f7d0003-4f52-4d32-9b2a-0f0b5a8b1000",
            100,
            24.0,
            180,
            16384);
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

        public AppSyncToml AppSync { get; set; } = new();
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

        public List<CameraModelToml> Models { get; set; } = new();

        public double? FisheyeFovDegrees { get; set; }

        public double? FisheyeStrength { get; set; }

        public double? RearCenterDangerAngleDegrees { get; set; }

        public double? RearEdgeWarningMinScore { get; set; }
    }

    /// <summary>
    /// 表示摄像头内单个模型配置节点。
    /// </summary>
    private sealed class CameraModelToml
    {
        public string Model { get; set; } = string.Empty;

        public int InputWidth { get; set; }

        public int InputHeight { get; set; }

        public double? ConfidenceThreshold { get; set; }

        public double MaxFps { get; set; }

        public double CropX { get; set; }

        public double CropY { get; set; }

        public double CropWidth { get; set; } = 1.0;

        public double CropHeight { get; set; } = 1.0;
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

        public bool PythonFallbackEnabled { get; set; } = true;

        public string PythonExecutable { get; set; } = "python3";

        public string PythonScript { get; set; } = "scripts/ble_radar_stream.py";

        public double PythonFallbackTimeoutSeconds { get; set; } = 8.0;

        public double PythonRestartDelaySeconds { get; set; } = 2.0;
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

    /// <summary>
    /// 表示手机 App 同步配置节点。
    /// </summary>
    private sealed class AppSyncToml
    {
        public bool Enabled { get; set; } = true;

        public string DeviceName { get; set; } = "RideManager";

        public string ServiceUuid { get; set; } = string.Empty;

        public string RxUuid { get; set; } = string.Empty;

        public string TxUuid { get; set; } = string.Empty;

        public int MaxPageSize { get; set; } = 100;

        public double DefaultSyncWindowHours { get; set; } = 24.0;

        public int NotifyChunkBytes { get; set; } = 180;

        public int MaxRequestBytes { get; set; } = 16384;
    }
}
