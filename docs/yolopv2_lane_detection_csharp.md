# YOLOPv2 CAM_FRONT 车道线检测接入说明

本文记录 `docs/examples/YOLOPv2` Python 示例到 RideManager C# 链路的关键实现点、ONNX 导出方式和 live test 结果。

## 示例关键点

来源文件：

- `docs/examples/YOLOPv2/demo.py`
- `docs/examples/YOLOPv2/utils/utils.py`
- `docs/examples/YOLOPv2/yolopv2.pt`

Python 示例处理流程：

1. 使用 `torch.jit.load(weights)` 加载 TorchScript 模型。
2. 读入 BGR 图片/视频帧，示例中先 resize 到 `1280x720`。
3. 使用 letterbox 缩放到模型输入尺寸，填充值为 `114`，再 BGR 转 RGB、CHW 排布、`float32 / 255.0`。
4. 模型输出三类结果：
   - 目标检测 head：原始 YOLO head + anchor grid。
   - `seg`：可行驶区域分割。
   - `ll`：车道线分割。
5. Python 后处理：
   - 检测 head 经 `split_for_trace_model` 解码为 `[x, y, w, h, obj, classes...]`，再 NMS。
   - 可行驶区域：`seg[:, :, 12:372, :]` 后插值，取 class 1。
   - 车道线：`ll[:, :, 12:372, :]` 后插值，`round()` 得到二值线条。

RideManager 当前实现使用固定 `640x640` letterbox 输入，ONNX 直接输出固定 `640x640` 分割图，C# 侧不再做 Python 示例里的 `12:372` 裁剪。

## ONNX 导出

新增脚本：

```shell
conda run -n ai python scripts/export_yolopv2_onnx.py \
  --weights docs/examples/YOLOPv2/yolopv2.pt \
  --output models/yolopv2.onnx \
  --img-size 640 \
  --opset 17
```

导出环境实测：

- `torch 2.12.0`
- `onnx 1.21.0`
- `onnxruntime 1.23.2`
- `opencv 4.13.0`

注意：PyTorch 2.12 默认新 ONNX exporter 会因为旧 TorchScript 模型参数签名失败。脚本中先对 wrapper 做 `torch.jit.trace(..., strict=False)`，再走 legacy exporter，并把输出整理为三个普通 tensor。

导出后的 ONNX Runtime 校验输出：

```text
detections:    [1, 25200, 85]
drivable_area: [1, 2, 640, 640]
lane_line:     [1, 1, 640, 640]
```

生成文件：

- `models/yolopv2.onnx`

## C# 接入点

配置：

- `config.toml` 中 `CAM_FRONT` 已切换到 `model = "yolopv2.onnx"`。
- `input_width/input_height` 保持 `640x640`。

运行时：

- `src/models/OnnxInferenceEngine.cs`
  - 继续解析 `detections [1,25200,85]` 为 YOLO 检测框。
  - 新增 `drivable_area [1,2,H,W]` 解析：前景通道大于背景通道时记为可行驶区域。
  - 新增 `lane_line [1,1,H,W]` 解析：阈值 `>= 0.5` 记为车道线。
  - 当前把分割 mask 的外接矩形转为 `CameraFinding`，标签为 `drivable_area` / `lane_line`，便于 live test、数据库和风险链路先跑通。
- `src/core/SafetyDecisionEngine.cs`
  - `lane_line` / `drivable_area` 风险权重为 `0.0`，避免正常道路结构触发障碍风险。
- `Program.cs`
  - `livetest` 支持 `--source` 参数，可临时用图片/视频/流地址覆盖某一路摄像头输入，不需要改 `config.toml`。

## Live Test

图片源测试命令：

```shell
dotnet run --no-build -- livetest \
  --camera CAM_FRONT \
  --source docs/examples/YOLOPv2/data/example.jpg \
  --duration 3 \
  --headless
```

实测输出包含：

```text
CamFront ... findings=[drivable_area:1.00,lane_line:1.00,motorcycle:0.96,...]
```

说明 C# 侧已经完成：

1. OpenCV 读取图片源。
2. CAM_FRONT 预处理。
3. `models/yolopv2.onnx` ONNX Runtime 推理。
4. YOLO 检测 + 可行驶区域 + 车道线后处理。
5. live test 指标输出。

尝试使用 `docs/examples/YOLOPv2/data/demo/together_video.gif` 时，本机 OpenCV 后端不能把 GIF 当视频流打开，会自动回退 synthetic 源。后续如需稳定视频 live test，建议使用 `.mp4` 或真实 `CAM_FRONT` 设备。

## 后续建议

- 当前分割结果用外接矩形显示，后续可以在 `CameraPipelineResult` 中加入 mask/overlay 数据，让 live preview 直接绘制半透明车道线和可行驶区域。
- RK3588 部署时可基于 `models/yolopv2.onnx` 转 RKNN，保持三个输出名和 C# 后处理语义一致。
- 若 CAM_FRONT 同时需要更准确的道路目标类别名，可给 `models/yolopv2.labels.txt` 增加 sidecar 标签文件。
