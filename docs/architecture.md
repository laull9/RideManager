# RideManager 架构说明

本文档记录当前 RideManager 上位机的运行时架构、核心模块边界和安全决策链路。项目目标部署在 RK3588，开发与联调环境可使用 ONNX Runtime，正式边缘部署可切换 RKNN。

## 运行时链路

1. `Program.cs` 读取 `config.toml`，构建摄像头、传感器、执行器、数据库和 App 同步配置。
2. `CameraPipelineFactory` 按启用的摄像头配置创建独立链路。单路摄像头打开失败时只禁用该链路，其他链路继续运行。
3. 每个摄像头链路独立执行采集、OpenCV 预处理、ONNX/RKNN 推理、后处理和性能统计。
4. 雷达等在线传感器通过 `ISensorReader` 统一产出 `SensorSnapshot`；GYRO 由外部 IMU 服务直接写入 PostgreSQL。
5. `RideSupervisor` 每轮收集所有可用摄像头 finding、帧状态和传感器快照，交给 `SafetyDecisionEngine`。
6. `SafetyDecisionEngine` 先生成单路 `CameraRiskAssessment`，再通过 `SafetyRiskFusion` 输出 `CompositeRiskAssessment` 和最终 `SafetyRiskLevel`。
7. `RideSupervisor` 根据最终风险触发刹车/语音执行器，并通过 `PostgresDetectionEventWriter` 写入 PostgreSQL。
8. `AppSyncServer` 通过蓝牙 GATT 协议向手机 App 分页同步最近数据、更多历史数据和设置变更审计。

## 模块边界

- `src/camera/`：摄像头采集、丢帧、预处理、模型后处理、live preview 和单路性能指标。
- `src/models/`：ONNX/RKNN 推理统一抽象。上层算法通过 `IInferenceEngine` 读取一致的输入输出结构。
- `src/core/`：主控综合决策、风险等级、单路摄像头风险窗口和多路融合。
- `src/sensors/`：雷达等传感器接入。当前雷达已实现蓝牙、macOS CoreBluetooth、Python fallback 和模拟源；串口 GYRO 读取器保留为诊断/回退能力，正式 GYRO 数据由外部 IMU 服务写入数据库。
- `src/actuators/`：刹车与语音播报执行器接口。当前语音播报可调用系统默认播放器播放预录音频，刹车仍以占位实现为主。
- `src/data/`：EF Core/PostgreSQL 表模型、迁移和检测事件写入。
- `src/appsync/`：手机 App 蓝牙同步协议、分页查询和平台外设宿主。
- `src/utils/`：配置加载、JSON source generation、HTTP listener 等公共工具。

## 摄像头风险模型

CAM_FRONT 和 CAM_BACK 使用 10 秒趋势窗口，避免单帧抖动直接触发危险：

- CAM_FRONT：只关注中心靠下碰撞走廊内的主风险目标。目标框面积、底部位置、横向中心偏移和标签权重共同形成距离代理分数。
- CAM_BACK：按鱼眼视角配置计算中心角度。中心区域可以升级 Danger，边缘区域只提供 Warning 分数，避免广角边缘误报。
- 每帧只取主风险目标进入窗口，不对多目标分数求和。
- `CameraRiskAssessment` 记录当前分数、近期均值、前半窗均值、趋势增量、峰值和主导标签。

CAM_FACE 不进入趋势窗口。`face_landmarks_106`、`fatigue_normal` 和 `fatigue_unknown` 只作为基础结果；`fatigue` 作为直接告警参与融合。

## 综合风险融合

`SafetyRiskFusion` 是三摄像头风险混合层，输入为单路趋势评估和非趋势摄像头直接告警，输出 `CompositeRiskAssessment`：

- 任一前/后摄像头趋势达到 `Danger`，最终风险为 `Danger`。
- 面部疲劳单独出现时最终风险为 `Warning`。
- 面部疲劳置信度较高且前/后摄像头已有 `Warning` 时，最终风险升级为 `Danger`。
- 前/后摄像头任一路 `Warning` 或其他非趋势高置信度告警会输出 `Warning`。
- 无活跃风险源时输出 `Normal`。

`CompositeRiskAssessment` 会随 `SafetyDecision` 写入 `safety_decisions.payload_json`，用于前端解释最终风险来源。它包含：

- `riskLevel`：融合后的风险等级。
- `primarySource`：主导风险来源，例如 `CAM_FRONT.trend`、`CAM_FACE.direct_alert` 或 `composite.camera_fusion`。
- `reasons`：人可读的融合原因。
- `contributions`：每个参与通道的来源、摄像头、风险等级、分数和标签。

## 数据持久化

当前正式写入：

- `safety_decisions`：最终风险和完整 `SafetyDecision` JSON。
- `camera_frames`：每路成功处理的帧状态和性能指标。
- `camera_findings`：目标、人脸关键点、疲劳等 finding。
- `sensor_snapshots` / `sensor_readings`：传感器快照和指标明细，包括 RADAR 与外部 IMU 服务写入的 GYRO。
- `system_events`：手机 App 设置变更审计等系统事件。

数据库结构详情见 `docs/psql.md`。

## 联调入口

```bash
dotnet test
dotnet run -- livetest --camera CAM_FRONT --source ./videos/test2.mp4
dotnet run -- liveradar
dotnet run -- liveapp
```

`livetest` 会显示与正式运行相同的摄像头风险评估字段；`liveapp` 只启动数据库检查和 App 蓝牙上位机，便于手机端独立联调。

GYRO 与 SPEAKER 的配置和协议见 `docs/gyro_speaker_modules.md`。
