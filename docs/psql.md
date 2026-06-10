# PostgreSQL 数据库接口

本文档描述 RideManager 的 PostgreSQL 表模型、EF Core 接入方式和后续模块扩展约定。当前数据库模型由 `RideManagerDbContext` 维护，初始迁移为 `InitialDatabaseModel`。

## 连接与迁移

- 连接配置：`config.toml` 的 `[database].connection_string`
- EF Core 上下文：`src/data/RideManagerDbContext.cs`
- 实体模型：`src/data/RideManagerEntities.cs`
- 迁移目录：`src/data/Migrations/`
- 运行时迁移：`PostgresDetectionEventWriter.WriteAsync` 会在写入前调用 `Database.MigrateAsync`

常用命令：

```bash
sudo ./scripts/init_psql.sh
dotnet tool restore
dotnet tool run dotnet-ef migrations add <MigrationName> --output-dir src/data/Migrations
dotnet tool run dotnet-ef database update
```

`scripts/init_psql.sh` 默认只创建或更新 `ridemanager` 用户，并确保 `ridemanager` 数据库存在；它不会创建表，也不会执行 EF Core 迁移。可通过环境变量覆盖：`DB_NAME`、`DB_USER`、`DB_PASSWORD`、`POSTGRES_USER`、`PG_ADMIN_HOST`、`PG_ADMIN_PORT`、`PG_ADMIN_USER`、`PG_ADMIN_PASSWORD`。

## 设计原则

- 表名、字段名统一使用 `snake_case`。
- 主键统一为 `uuid`，由应用侧生成。
- 时间字段使用 `timestamp with time zone`，应用侧写入 UTC 时间。
- 可变结构数据使用 `jsonb` 保留原始负载，常用查询字段拆成普通列。
- 当前已实现写入 `safety_decisions`、`camera_frames`、`camera_findings`、`sensor_snapshots`、`sensor_readings`。
- 手机 App 蓝牙同步服务读取 `safety_decisions` 及其摄像头、传感器明细；App 设置变更请求写入 `system_events`。
- 未实现模块先预留表接口：设备注册、模型资产、运行会话、执行器命令、系统事件。

## 表关系

- `run_sessions` 1 - N `safety_decisions`
- `safety_decisions` 1 - N `camera_frames`
- `safety_decisions` 1 - N `camera_findings`
- `camera_frames` 1 - N `camera_findings`
- `safety_decisions` 1 - N `sensor_snapshots`
- `sensor_snapshots` 1 - N `sensor_readings`
- `safety_decisions` 1 - N `actuator_commands`
- `devices` 可被 `camera_frames`、`sensor_snapshots`、`actuator_commands` 引用

## 表接口

### devices

设备注册表，预留给摄像头、雷达、陀螺仪、刹车、语音播报等模块。

| 字段 | 类型 | 说明 |
|---|---|---|
| id | uuid | 主键 |
| code | varchar(64) | 设备编码，唯一，如 `CAM_FRONT`、`RADAR`、`BRAKE` |
| device_type | varchar(32) | `camera`、`sensor`、`actuator` |
| transport | varchar(32) | `opencv`、`bluetooth`、`serial` 等 |
| address | varchar(256) | 设备路径、蓝牙地址或串口路径 |
| enabled | boolean | 是否启用 |
| config_json | jsonb | 设备扩展配置 |
| created_at | timestamptz | 创建时间 |
| updated_at | timestamptz | 更新时间 |

索引：`code` 唯一索引。

### model_artifacts

算法模型资产表，预留给 ONNX/RKNN 模型版本、输入尺寸和标签配置。

| 字段 | 类型 | 说明 |
|---|---|---|
| id | uuid | 主键 |
| name | varchar(128) | 模型名称 |
| backend | varchar(16) | `onnx` 或 `rknn` |
| relative_path | varchar(512) | 相对 `models/` 的路径 |
| version | varchar(64) | 模型版本，可为空 |
| input_width | integer | 输入宽度 |
| input_height | integer | 输入高度 |
| labels_json | jsonb | 标签列表 |
| config_json | jsonb | 后处理阈值等扩展配置 |
| is_active | boolean | 是否启用 |
| created_at | timestamptz | 创建时间 |

索引：`name, backend, version`。

### run_sessions

运行会话表，预留给长期运行、前端筛选和运行配置追踪。

| 字段 | 类型 | 说明 |
|---|---|---|
| id | uuid | 主键 |
| started_at | timestamptz | 启动时间 |
| stopped_at | timestamptz | 停止时间 |
| host_name | varchar(128) | 主机名 |
| config_json | jsonb | 本次运行配置快照 |
| note | varchar(512) | 备注 |

索引：`started_at`。

### safety_decisions

主控安全决策表，是前端查看检测周期的主入口。

| 字段 | 类型 | 说明 |
|---|---|---|
| id | uuid | 主键 |
| run_session_id | uuid | 关联运行会话，可为空 |
| risk_level | varchar(32) | `Normal`、`Warning`、`Danger` |
| decided_at | timestamptz | 决策时间 |
| payload_json | jsonb | 原始 `SafetyDecision` 负载，包含 `cameraRiskAssessments` 等扩展决策字段 |
| created_at | timestamptz | 写库时间 |

索引：`decided_at`、`risk_level`、`run_session_id`。

### camera_frames

摄像头单帧处理表。正式运行时 `RideSupervisor` 会把每路成功处理的 `CameraPipelineResult` 转成帧状态，`PostgresDetectionEventWriter` 写入本表，并把同一摄像头的 finding 关联到该帧。摄像头打开失败的链路不会产生帧记录。

| 字段 | 类型 | 说明 |
|---|---|---|
| id | uuid | 主键 |
| safety_decision_id | uuid | 关联安全决策，可为空 |
| device_id | uuid | 关联设备，可为空 |
| camera_id | varchar(32) | `CAM_FRONT`、`CAM_FACE`、`CAM_BACK` |
| captured_at | timestamptz | 采集时间 |
| width | integer | 原始帧宽度 |
| height | integer | 原始帧高度 |
| capture_latency_ms | double precision | 采集耗时 |
| preprocess_latency_ms | double precision | 预处理耗时 |
| inference_latency_ms | double precision | 推理耗时 |
| total_latency_ms | double precision | 总耗时 |
| fps | double precision | 当前 FPS |
| dropped_frames | bigint | 丢帧数 |
| metadata_json | jsonb | 扩展指标 |

索引：`camera_id, captured_at`。

### camera_findings

摄像头检测结果表。当前 `PostgresDetectionEventWriter` 已写入此表；同一轮决策中存在对应 `camera_frames` 时会写入 `camera_frame_id`。

| 字段 | 类型 | 说明 |
|---|---|---|
| id | uuid | 主键 |
| safety_decision_id | uuid | 关联安全决策 |
| camera_frame_id | uuid | 关联摄像头帧，可为空 |
| camera_id | varchar(32) | 摄像头编码 |
| label | varchar(128) | 检测标签 |
| confidence | double precision | 置信度，0 到 1 |
| observed_at | timestamptz | 观测时间 |
| box_x | double precision | 归一化目标框 x |
| box_y | double precision | 归一化目标框 y |
| box_width | double precision | 归一化目标框宽度 |
| box_height | double precision | 归一化目标框高度 |
| payload_json | jsonb | 原始 `CameraFinding` 负载 |

索引：`camera_id, observed_at`、`label`、`safety_decision_id`。

### sensor_snapshots

传感器快照表。当前雷达和陀螺仪读数接入后统一写入此表。

| 字段 | 类型 | 说明 |
|---|---|---|
| id | uuid | 主键 |
| safety_decision_id | uuid | 关联安全决策 |
| device_id | uuid | 关联设备，可为空 |
| sensor_name | varchar(64) | 传感器名称，如 `RADAR`、`GYRO` |
| observed_at | timestamptz | 观测时间 |
| values_json | jsonb | 原始指标字典 |

索引：`sensor_name, observed_at`、`safety_decision_id`。

### sensor_readings

传感器指标明细表，便于前端按指标名筛选和画曲线。

| 字段 | 类型 | 说明 |
|---|---|---|
| id | uuid | 主键 |
| sensor_snapshot_id | uuid | 关联传感器快照 |
| metric | varchar(64) | 指标名，如 `heart_rate`、`roll` |
| value | double precision | 指标值 |
| unit | varchar(32) | 单位，可为空 |

索引：`metric`、`sensor_snapshot_id`。

### actuator_commands

执行器命令表，预留给刹车和语音播报模块。后续真实控制器发出命令时写入，请求、完成和失败都更新同一行。

| 字段 | 类型 | 说明 |
|---|---|---|
| id | uuid | 主键 |
| safety_decision_id | uuid | 触发该命令的安全决策，可为空 |
| device_id | uuid | 关联设备，可为空 |
| actuator_name | varchar(64) | `BRAKE`、`SPEAKER` |
| command_type | varchar(64) | `apply_brake`、`play_warning` 等 |
| requested_at | timestamptz | 请求时间 |
| completed_at | timestamptz | 完成时间 |
| status | varchar(32) | `pending`、`completed`、`failed` |
| payload_json | jsonb | 命令参数 |
| error_message | varchar(1024) | 失败信息 |

索引：`actuator_name, requested_at`、`status`。

### system_events

系统事件表，用于生命周期、异常、诊断日志、前端告警，以及手机 App 请求的设置变更审计。App 同步协议的 `update_settings` 会写入 `source = app_sync`、`message = settings_update_requested` 的记录，`payload_json` 保存 `clientId` 和设置补丁。

| 字段 | 类型 | 说明 |
|---|---|---|
| id | uuid | 主键 |
| occurred_at | timestamptz | 发生时间 |
| source | varchar(64) | 来源模块 |
| level | varchar(16) | `debug`、`info`、`warning`、`error` |
| message | varchar(1024) | 事件摘要 |
| payload_json | jsonb | 扩展负载 |

索引：`source, occurred_at`、`level`。

## 前端常用查询

最近安全决策：

```sql
select id, risk_level, decided_at
from safety_decisions
order by decided_at desc
limit 50;
```

查看某次决策的摄像头检测结果：

```sql
select camera_id, label, confidence, observed_at, box_x, box_y, box_width, box_height
from camera_findings
where safety_decision_id = :decision_id
order by observed_at;
```

查看某次决策的摄像头帧状态：

```sql
select camera_id, captured_at, width, height,
       capture_latency_ms, preprocess_latency_ms, inference_latency_ms,
       total_latency_ms, fps, dropped_frames
from camera_frames
where safety_decision_id = :decision_id
order by captured_at;
```

查看某个传感器指标曲线：

```sql
select ss.observed_at, sr.metric, sr.value, sr.unit
from sensor_readings sr
join sensor_snapshots ss on ss.id = sr.sensor_snapshot_id
where ss.sensor_name = :sensor_name
  and sr.metric = :metric
order by ss.observed_at;
```

## 扩展约定

- 新增模块优先复用 `devices` 注册设备，业务流水再写对应明细表。
- 新增可查询字段时拆成普通列；只用于追溯的原始结构放入 `jsonb`。
- 新增表必须同时更新 `RideManagerEntities.cs`、`RideManagerDbContext.cs`、迁移文件和本文档。
- 前端读取列表优先从 `safety_decisions`、`camera_findings`、`sensor_readings` 查询，详情页再读取 `payload_json`。
