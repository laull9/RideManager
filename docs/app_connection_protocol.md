# 手机 App 蓝牙连接协议

本文档描述 RideManager 作为手机 App 可自动连接的轻量级蓝牙上位机服务。协议第一版面向最近数据同步、分页加载更多历史数据，以及 App 发起设置变更请求。

## 目标

- RideManager 默认开启 App 同步服务，配置节点为 `config.toml` 的 `[app_sync]`。
- 默认同步最近 24 小时安全决策数据，可通过分页游标继续加载更早数据。
- 手机端只需要一个 BLE 服务、一个写入特征和一个通知特征即可完成收发。
- 设置变更不直接热修改摄像头、模型或运行链路，而是写入 `system_events` 作为待应用请求，避免行驶中重建关键链路。

## 蓝牙服务

默认配置：

| 项 | 默认值 | 说明 |
|---|---|---|
| device_name | `RideManager` | 手机扫描到的设备名 |
| service_uuid | `7f7d0001-4f52-4d32-9b2a-0f0b5a8b1000` | App 同步服务 |
| rx_uuid | `7f7d0002-4f52-4d32-9b2a-0f0b5a8b1000` | 手机写入请求，Write 或 WriteWithoutResponse |
| tx_uuid | `7f7d0003-4f52-4d32-9b2a-0f0b5a8b1000` | RideManager 通知响应，Notify |
| default_sync_window_hours | `24.0` | 默认最近同步窗口 |
| max_page_size | `100` | 单页最大记录数 |
| notify_chunk_bytes | `180` | BLE 通知分片信封的目标最大字节数 |
| max_request_bytes | `16384` | 单个请求最大字节数 |

Linux/RK3588 使用 BlueZ 作为蓝牙适配器基础；macOS 使用 CoreBluetooth Peripheral 方向。当前 C# 主程序已经提供统一协议处理入口和平台宿主边界，平台外设宿主需要把 RX 特征写入内容传给 `AppSyncProtocolHandler`，再把返回 JSON 通过 TX 特征通知给手机端。

## 帧格式

请求是 UTF-8 JSON。响应的完整 AppSync JSON 会通过 TX notify 发送为一组可重组的 UTF-8 JSON 分片；手机端必须先按分片信封重组，再解析里面的完整响应 JSON。

请求通用格式：

```json
{
  "v": 1,
  "id": "client-request-id",
  "type": "sync_recent",
  "payload": {}
}
```

响应通用格式：

```json
{
  "v": 1,
  "id": "client-request-id",
  "type": "sync_recent",
  "status": "ok",
  "payload": {}
}
```

TX 通知分片信封：

```json
{
  "v": 1,
  "t": "chunk",
  "id": "42",
  "i": 0,
  "n": 3,
  "b": 512,
  "d": "base64-response-bytes"
}
```

- `id`：同一条响应的分片消息编号。
- `i`：当前分片序号，从 `0` 开始。
- `n`：该响应总分片数。
- `b`：完整响应 UTF-8 字节数。
- `d`：本片响应字节的 Base64。

手机端收到同一 `id` 的 `n` 片后，按 `i` 排序，Base64 解码并拼接，校验拼接后的字节数等于 `b`，再按 UTF-8 JSON 解析为上面的响应通用格式。`sensorSnapshots[].values` 会保留数据库中写入的所有传感器指标，例如 `heart_rate`、`breathing_rate`、`distance_cm`、`speed_kmh`、`cadence_rpm` 等。

错误响应：

```json
{
  "v": 1,
  "id": "client-request-id",
  "type": "error",
  "status": "bad_request",
  "payload": {
    "message": "unsupported request"
  }
}
```

## 请求类型

### hello

用于连接后握手，手机端读取协议能力。

请求：

```json
{ "v": 1, "id": "h1", "type": "hello", "payload": {} }
```

响应 payload：

```json
{
  "deviceName": "RideManager",
  "protocol": "RideManager.AppSync",
  "version": 1,
  "defaultSyncWindowHours": 24,
  "maxPageSize": 100,
  "capabilities": ["sync_recent", "load_more", "update_settings", "ping"]
}
```

### sync_recent

同步最近数据。未传 `hours` 时使用 `default_sync_window_hours`，即最近 24 小时。结果按 `decidedAt` 倒序返回。

请求 payload：

```json
{
  "hours": 24,
  "limit": 100,
  "cursor": null
}
```

响应 payload：

```json
{
  "items": [
    {
      "id": "d3e2f9d0-0000-0000-0000-000000000001",
      "decidedAt": "2026-06-10T10:20:30+00:00",
      "riskLevel": "Warning",
      "payload": {},
      "cameraFindings": [],
      "sensorSnapshots": []
    }
  ],
  "nextCursor": "base64url-cursor",
  "hasMore": true
}
```

### load_more

加载更早数据。`cursor` 使用上一页响应里的 `nextCursor`。

请求 payload：

```json
{
  "cursor": "base64url-cursor",
  "limit": 100
}
```

响应 payload 与 `sync_recent` 相同。

### update_settings

App 请求修改设置。当前版本不会直接改写 `config.toml` 或热重启摄像头链路，而是写入 `system_events`，来源为 `app_sync`，消息为 `settings_update_requested`。服务端响应中 `requiresRestart=true` 表示该请求需要后续由运维流程或下一次启动应用。

请求 payload：

```json
{
  "client_id": "phone-a",
  "patch": {
    "cameras": {
      "CAM_BACK": { "enabled": true }
    }
  }
}
```

响应 payload：

```json
{
  "eventId": "d3e2f9d0-0000-0000-0000-000000000002",
  "acceptedAt": "2026-06-10T10:21:00+00:00",
  "requiresRestart": true,
  "message": "settings update accepted and stored as a pending system event"
}
```

### ping

连接保活。

请求：

```json
{ "v": 1, "id": "p1", "type": "ping", "payload": {} }
```

响应 payload：

```json
{ "pong": "2026-06-10T10:22:00+00:00" }
```

## 手机端同步建议

1. 扫描 `service_uuid`，优先匹配设备名 `RideManager`。
2. 连接后订阅 `tx_uuid` 通知，再向 `rx_uuid` 写 `hello`。
3. 首次同步发送 `sync_recent`，不指定 `hours` 时取最近 24 小时。
4. 用户下拉或进入历史页时发送 `load_more`。
5. App 本地用 `id` 去重；服务端分页按倒序返回，历史追加到列表尾部。
6. 设置修改后展示 `accepted` 状态，并提示需要重启或后续应用。

## 专用 Live Test

手机 App 联调时可以只启动数据库检查和 App 蓝牙上位机，不启动摄像头、雷达、陀螺仪、刹车、语音播报和主控决策循环：

```bash
dotnet run -- liveapp
```

等价别名：

```bash
dotnet run -- liveappsync
```

常用参数：

```bash
dotnet run -- liveapp --duration 300
dotnet run -- liveapp --require-database
dotnet run -- liveapp --config ./config.toml
```

- `--duration`：运行秒数；不传时一直运行，按 Ctrl+C 退出。
- `--require-database`：数据库不可连接时直接退出；不传时会打印 warning，但仍启动蓝牙服务，方便先测手机连接和握手。
- `--config`：指定配置文件，默认读取项目根目录 `config.toml`。

## 代码位置

- 配置模型：`src/utils/RideManagerOptions.cs`
- 配置读取：`src/utils/ConfigLoader.cs`
- 协议处理：`src/appsync/AppSyncProtocolHandler.cs`
- 数据读取：`src/appsync/PostgresAppSyncRepository.cs`
- 服务生命周期：`src/appsync/AppSyncServer.cs`
- 专用联调：`src/appsync/AppSyncLiveTester.cs`
- 测试：`tests/AppSyncProtocolTests.cs`
