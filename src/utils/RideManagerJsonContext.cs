using System.Text.Json;
using System.Text.Json.Serialization;
using RideManager.AppSync;
using RideManager.Camera;
using RideManager.Core;
using RideManager.Sensors;

namespace RideManager.Utils;

/// <summary>
/// 提供 trimmed/self-contained 运行时可用的 System.Text.Json source generation 上下文。
/// </summary>
[JsonSourceGenerationOptions(
    JsonSerializerDefaults.Web,
    UseStringEnumConverter = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(SafetyDecision))]
[JsonSerializable(typeof(SafetyRiskLevel))]
[JsonSerializable(typeof(CameraFrameState))]
[JsonSerializable(typeof(CameraPipelineMetrics))]
[JsonSerializable(typeof(CameraFinding))]
[JsonSerializable(typeof(CameraBoundingBox))]
[JsonSerializable(typeof(CameraSegmentationMask))]
[JsonSerializable(typeof(CameraLandmark))]
[JsonSerializable(typeof(CameraId))]
[JsonSerializable(typeof(SensorSnapshot))]
[JsonSerializable(typeof(CameraRiskAssessment))]
[JsonSerializable(typeof(IReadOnlyList<CameraFinding>))]
[JsonSerializable(typeof(IReadOnlyList<SensorSnapshot>))]
[JsonSerializable(typeof(IReadOnlyList<CameraRiskAssessment>))]
[JsonSerializable(typeof(IReadOnlyList<CameraFrameState>))]
[JsonSerializable(typeof(IReadOnlyList<CameraLandmark>))]
[JsonSerializable(typeof(IReadOnlyList<string>))]
[JsonSerializable(typeof(IReadOnlyDictionary<string, double>))]
[JsonSerializable(typeof(Dictionary<string, double>))]
[JsonSerializable(typeof(byte[]))]
[JsonSerializable(typeof(AppSyncRequest))]
[JsonSerializable(typeof(AppSyncResponse))]
[JsonSerializable(typeof(AppSyncPage))]
[JsonSerializable(typeof(AppSyncDecisionRecord))]
[JsonSerializable(typeof(AppSyncCameraFindingRecord))]
[JsonSerializable(typeof(AppSyncSensorSnapshotRecord))]
[JsonSerializable(typeof(AppSyncSettingsUpdateResult))]
[JsonSerializable(typeof(AppSyncSettingsUpdateEvent))]
[JsonSerializable(typeof(AppSyncPing))]
[JsonSerializable(typeof(AppSyncError))]
[JsonSerializable(typeof(AppSyncHello))]
[JsonSerializable(typeof(IReadOnlyList<AppSyncDecisionRecord>))]
[JsonSerializable(typeof(IReadOnlyList<AppSyncCameraFindingRecord>))]
[JsonSerializable(typeof(IReadOnlyList<AppSyncSensorSnapshotRecord>))]
[JsonSerializable(typeof(RadarLiveState))]
[JsonSerializable(typeof(RadarConnectionState))]
[JsonSerializable(typeof(RadarFrame))]
[JsonSerializable(typeof(RadarHealth))]
[JsonSerializable(typeof(RadarHistoryPoint))]
[JsonSerializable(typeof(IReadOnlyList<RadarHistoryPoint>))]
internal sealed partial class RideManagerJsonContext : JsonSerializerContext;
