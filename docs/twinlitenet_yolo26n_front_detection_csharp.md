# CAM_FRONT 目标检测接入说明

本文记录 RideManager 前向摄像头目标检测链路，以及历史 TwinLiteNet 车道线/可行驶区域分割接入信息。

## 当前状态

- CAM_FRONT 正式运行专注于 `yolo26n.onnx` 目标检测。
- 车道线检测功能已放弃，`config.toml` 不再为 CAM_FRONT 启动 TwinLiteNet。
- 如果历史模型或自定义模型仍输出 `lane_line` / `drivable_area`，C# 前向摄像头分析器会过滤这些 finding，不进入 live test 目标列表、风险链路和数据库 finding。
- 风险分析使用 10 秒时间窗口。前向摄像头不再把同帧多个目标分数相加，而是先筛选中心靠下的碰撞走廊，再取本帧主风险目标；单目标分数综合标签权重、目标框面积距离代理、底部位置和横向中心偏移。
- `Warning` 表示目标进入过碰撞走廊或当前/近期分数偏高；`Danger` 只在目标非常近且持续存在，或 10 秒窗口内明显接近时触发。历史峰值只保持 Warning，不单独触发 Danger，避免远离目标造成误报。
- 单摄像头 live test 也会运行同一套风险监测：预览画面左上角显示 `risk/score/recent/delta/peak/labels`，headless 控制台输出同样字段。
- 正式运行时摄像头打开失败会输出 warning 并禁用该摄像头链路，其他摄像头继续运行。
- 数据库写入 `safety_decisions`、`camera_frames`、`camera_findings`、`sensor_snapshots`、`sensor_readings`，供前端读取状态、数据与决策。

当前 CAM_FRONT 配置：

```toml
[[cameras]]
id = "CAM_FRONT"
enabled = true
device = "/dev/video0"
model = "yolo26n.onnx"
width = 1280
height = 720
input_width = 640
input_height = 640
fps = 30
confidence_threshold = 0.35
pixel_format = "MJPG"

[[cameras.models]]
model = "yolo26n.onnx"
input_width = 640
input_height = 640
confidence_threshold = 0.35
```

单摄像头视频 live test：

```shell
dotnet run -- livetest --camera CAM_FRONT --source ./videos/test2.mp4
```

无窗口验证可追加 `--headless --duration 3`，控制台示例：

```text
CamFront risk=Danger score=1.00 recent=0.97 delta=+0.97 peak=1.00 labels=motorcycle/person/car ...
```

## 历史 TwinLiteNet 说明

## Python 示例关键点

来源文件：

- `docs/examples/TwinLiteNet/test_image.py`
- `docs/examples/TwinLiteNet/export.py`
- `docs/examples/TwinLiteNet/model/TwinLite.py`
- `docs/examples/TwinLiteNet/pretrained/best.pth`

TwinLiteNet 示例处理流程：

1. 使用 `TwinLiteNet()` 创建模型。
2. 原始 checkpoint 是 `torch.nn.DataParallel` 保存的权重，key 带 `module.` 前缀。
3. 示例把 BGR 图片 resize 到 `640x360`。
4. BGR 转 RGB，转 CHW，构造 `[1,3,360,640]`，并执行 `float32 / 255.0`。
5. 模型输出两个二分类分割 tensor：
   - `da`：Drivable Area，可行驶区域。
   - `ll`：Lane Line，车道线。
6. Python 示例对 `da/ll` 在 channel 维度执行 `argmax`，再把正类 mask 叠加回图片。

RideManager C# 侧使用通用 letterbox RGB/NCHW 预处理。对 `1280x720` 这类 16:9 输入，`640x360` 与示例 resize 等价；其他比例输入会保留比例并填充，便于统一坐标还原。

## ONNX 导出

新增脚本：

```shell
conda run -n ai python scripts/export_twinlitenet_onnx.py \
  --weights docs/examples/TwinLiteNet/pretrained/best.pth \
  --output models/twinlitenet.onnx \
  --input-height 360 \
  --input-width 640 \
  --opset 17
```

脚本处理了两个示例脚本中的部署痛点：

- 不硬编码 CUDA，可在 CPU 环境导出。
- 自动去掉 checkpoint key 的 `module.` 前缀。
- 导出后使用 `onnx.checker` 和 ONNX Runtime 校验。

实测导出输出：

```text
da: [1, 2, 360, 640]
ll: [1, 2, 360, 640]
```

生成文件：

- `models/twinlitenet.onnx`

## C# 接入点

配置：

- 历史多模型方案中 `twinlitenet.onnx` 使用 `640x360`。
- `yolo26n.onnx` 使用固定 `640x640`。
- 当前正式配置已移除 TwinLiteNet，仅保留 `yolo26n.onnx`。

示例配置：

```toml
[[cameras]]
id = "CAM_FRONT"
model = "twinlitenet.onnx"
input_width = 640
input_height = 360

[[cameras.models]]
model = "twinlitenet.onnx"
input_width = 640
input_height = 360
confidence_threshold = 0.35
max_fps = 3
crop_x = 0.0
crop_y = 0.333333
crop_width = 1.0
crop_height = 0.666667

[[cameras.models]]
model = "yolo26n.onnx"
input_width = 640
input_height = 640
confidence_threshold = 0.35
```

历史运行时：

- `src/utils/RideManagerOptions.cs`
  - 新增 `CameraModelOptions`。
  - `CameraOptions.EffectiveModels` 兼容旧版单模型配置。
- `src/utils/ConfigLoader.cs`
  - 支持 `[[cameras.models]]` 多模型 TOML 节点。
- `src/camera/ModelInputTensorFactory.cs`
  - 复用道路模型的 letterbox、RGB、NCHW、`/255` 预处理。
- `src/camera/MultiModelCameraAnalyzer.cs`
  - YOLO26n 作为主检测模型同步每帧运行。
  - 历史 TwinLiteNet 可按 `max_fps` 异步刷新缓存，并按 `crop_*` 只处理配置的原图区域。
  - 当前 CAM_FRONT 会过滤 `lane_line` / `drivable_area`，避免道路分割结果继续参与正式输出。
- `src/models/InferenceOutputParser.cs`
  - 支持 TwinLiteNet `da/ll` 二分类分割输出。
  - RKNN 重命名输出为 `output0/output1` 时，会根据 `twinlitenet` 模型名按输出顺序识别 `DA/LL`。
- `src/camera/CameraLiveTester.cs`
  - live test 改为每条管线独立 worker，避免多模型/多摄像头串行测试时 FPS 被同一个外层循环锁成一样。
  - 绘制分割 mask 时支持把 ROI 内 mask 映射回整帧对应区域。

## Live Test

图片源测试命令：

```shell
dotnet run --no-build -- livetest \
  --camera CAM_FRONT \
  --source docs/examples/TwinLiteNet/images/cc73b69d-b31c28dc.jpg \
  --duration 3 \
  --headless
```

历史多模型输出曾包含：

```text
CamFront fps=1.1 total=648.3ms infer=648.2ms dropped=0 findings=[drivable_area:1.00,lane_line:1.00,car:0.61,car:0.53,car:0.38]
```

说明历史 C# 侧完成过：

1. OpenCV 读取图片源。
2. CAM_FRONT 多模型预处理。
3. `models/twinlitenet.onnx` ONNX Runtime 推理并解析可行驶区域/车道线。
4. `models/yolo26n.onnx` ONNX Runtime 推理并解析目标检测。
5. 合并结果并输出 live test 指标。

## RKNN 转换建议

RK3588 部署时可沿用现有转换脚本：

```shell
conda run -n ai python scripts/convert_project_onnx_to_rknn.py
```

注意点：

- 当前正式 CAM_FRONT 不再转换或部署 TwinLiteNet；仅在保留历史实验时需要关注它。
- `twinlitenet.onnx` 输入是 `[1,3,360,640]`。
- `yolo26n.onnx` 输入是 `[1,3,640,640]`。
- RKNN 如果重命名 TwinLiteNet 输出，C# 会按模型名和输出顺序兜底：第 0 个二分类分割输出是 `drivable_area`，第 1 个是 `lane_line`。
