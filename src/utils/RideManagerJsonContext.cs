using System.Text.Json;
using System.Text.Json.Serialization;
using RideManager.Camera;
using RideManager.Core;
using RideManager.Sensors;

namespace RideManager.Utils;

/// <summary>
/// 提供 trimmed/self-contained 运行时可用的 System.Text.Json source generation 上下文。
/// </summary>
[JsonSourceGenerationOptions(JsonSerializerDefaults.Web, UseStringEnumConverter = true)]
[JsonSerializable(typeof(SafetyDecision))]
[JsonSerializable(typeof(CameraFrameState))]
[JsonSerializable(typeof(CameraFinding))]
[JsonSerializable(typeof(IReadOnlyDictionary<string, double>))]
[JsonSerializable(typeof(RadarLiveState))]
internal sealed partial class RideManagerJsonContext : JsonSerializerContext;
