# GYRO 与 SPEAKER 模块说明

本文档记录陀螺仪 6 轴传感器与系统语音播报模块的配置、协议和运行边界。

## GYRO 6 轴传感器

正式 GYRO 数据现在由外部 IMU 服务直接写入 PostgreSQL，不再通过 RideManager 串口读取接入主控循环。`src/sensors/GyroSensorReader.cs` 保留为 Linux 诊断/回退能力；macOS、Windows 或其他平台即使配置启用，也会输出一次提示后跳过，不影响其他传感器与摄像头链路。

配置示例：

```toml
[sensors.gyro]
enabled = false
transport = "serial"
address = ""
baud_rate = 115200
read_timeout_seconds = 0.2
```

外部 IMU 服务写入表：

- `sensor_snapshots.sensor_name = GYRO`
- `sensor_snapshots.safety_decision_id` 可以为空
- `sensor_snapshots.device_id` 可以为空；非空时必须是 `devices.id` 中存在的真实设备 UUID
- `sensor_readings.metric = acc_x/acc_y/acc_z/gyro_x/gyro_y/gyro_z/roll/pitch/yaw`
- `sensor_readings.unit = g`、`deg/s` 或 `degree`

串口诊断/回退运行方式：

- `transport = "serial"` 或 `"uart"` 时，启动读取前会使用 `stty -F <address> <baud_rate> raw -echo min 0 time 2` 配置串口。
- 每个主控周期最多等待 `read_timeout_seconds` 秒读取一行，超时返回空快照，避免阻塞整车主循环。
- 串口设备不可用、无权限或读取失败时，只记录一次 warning，并在后续周期尝试重新打开。

支持两种文本协议，均以换行结尾：

```text
roll,pitch,yaw,accel_x,accel_y,accel_z
```

示例：

```text
1.2,-0.4,3.8,0.01,0.02,9.81
```

或 key/value：

```text
roll=1.2 pitch=-0.4 yaw=3.8 ax=0.01 ay=0.02 az=9.81
```

字段别名：

| 输出指标 | 支持输入名 |
| --- | --- |
| `roll` | `roll`, `gx`, `gyro_x` |
| `pitch` | `pitch`, `gy`, `gyro_y` |
| `yaw` | `yaw`, `gz`, `gyro_z` |
| `accel_x` | `ax`, `accx`, `accel_x`, `acceleration_x` |
| `accel_y` | `ay`, `accy`, `accel_y`, `acceleration_y` |
| `accel_z` | `az`, `accz`, `accel_z`, `acceleration_z` |

成功解析后会写入：

- `SensorSnapshot.SensorName = "GYRO"`
- `sensor_snapshots.sensor_name = GYRO`
- `sensor_readings.metric = roll/pitch/yaw/accel_x/accel_y/accel_z`
- `sensor_readings.unit = deg` 用于姿态角，`m/s2` 用于加速度

正式主控循环中，`RideSupervisor` 不再创建串口 GYRO 读取器。外部 IMU 服务可独立写入 `sensor_snapshots`，`safety_decision_id` 为空时 App 同步会把它映射为 `riskLevel = "SensorOnly"` 的记录。

## GYRO 到手机 App

GYRO 数据复用现有 App 蓝牙同步服务，不需要额外 BLE service。确认 `config.toml` 中 `[app_sync] enabled = true` 后，手机端通过 `sync_recent` 或 `load_more` 会收到：

- `sensorSnapshots[].sensorName = "GYRO"`
- `sensorSnapshots[].values.acc_x/acc_y/acc_z/gyro_x/gyro_y/gyro_z/roll/pitch/yaw`
- `sensorSnapshots[].readings[]` 明细列表，包含 `metric`、`value`、`unit`

协议握手 `hello` 的 `capabilities` 包含 `gyro_sensor` 和 `sensor_readings`。手机端按 `sensorName` 过滤即可展示陀螺仪姿态和加速度曲线。

## SPEAKER 系统语音播报

当前实现位于 `src/actuators/SystemSpeakerNotifier.cs`，正式运行时由 `RideSupervisor` 在最终风险不是 `Normal` 时调用；摄像头 `livetest` 也会在风险不是 `Normal` 时触发同一套语音播报。它直接使用系统默认扬声器播放预录音频文件，不依赖语音合成。

配置示例：

```toml
[actuators.speaker]
enabled = true
asset_directory = "assests"
warning_file = "warning.wav"
danger_file = "danger.wav"
player_command = ""
min_interval_seconds = 3.0
```

RK3588/开发板如果 `ffplay` 不可用，可直接指定 `ffmpeg` 输出到 ALSA：

```toml
player_command = "ffmpeg -re -i {asset} -ac 2 -ar 48000 -sample_fmt s16 -f alsa plughw:1,0"
```

播放规则：

- `Warning` 播放 `warning_file`。
- `Danger` 播放 `danger_file`。
- `Normal` 不播放。
- 风险升级会立即播放；同等级重复播报至少间隔 `min_interval_seconds`。
- 新播报开始前会停止仍在运行的旧播放器进程。

播放器选择：

- `player_command` 非空时直接使用该命令；需要把音频文件放在命令中间时，用 `{asset}` 作为占位符。
- macOS 联调环境自动尝试 `afplay`。
- Linux/RK3588 自动尝试 `aplay`、`paplay`、`ffmpeg`、`ffplay`。

音频文件不存在或系统找不到播放器时，只输出一次 warning，不中断主控循环。当前仓库保留 `assests/` 目录作为音频资源目录；部署时放入 `warning.wav` 和 `danger.wav`，或在配置中改成实际文件名。
