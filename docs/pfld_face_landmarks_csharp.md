# PFLD CAM_FACE 106 点人脸关键点接入说明

本文记录 `docs/examples/pfld_106_face_landmarks` Python 示例到 RideManager C# 面部摄像头链路的接入方式、模型输入输出约定和 live test 使用方法。

## 示例关键点

来源文件：

- `docs/examples/pfld_106_face_landmarks/onnxrt_inference.py`
- `docs/examples/pfld_106_face_landmarks/pytorch2onnx.py`
- `docs/examples/pfld_106_face_landmarks/models/lite.py`

Python 示例处理流程：

1. 使用 OpenCV 读入 BGR 图片。
2. 将图片直接 resize 到 `112x112`。
3. 保持 BGR 通道顺序，转换为 CHW/NCHW，`float32 / 255.0`。
4. 使用 ONNX Runtime 运行模型。
5. 取第二个输出 tensor，即 `output`，形状为 `[1, 212]`。
6. 将 `212` 个值 reshape 为 `106 x 2`，每个点为归一化的 `(x, y)` 坐标，再乘以原图宽高绘制。

与前向道路模型不同，PFLD 示例不做 letterbox，也不做 RGB 转换；当前 C# 链路先用 YuNet 在整帧中找人脸，再对人脸 ROI 使用 BGR resize 预处理。

## 模型文件

当前模型放置位置：

- `models/pfld_lite.onnx`
- `models/face_detection_yunet_2023mar.onnx`

示例导出脚本中的约定：

```text
input:  [1, 3, 112, 112]
output1: 中间特征图
output: [1, 212]，106 个归一化关键点
```

RideManager C# 后处理会自动识别长度为 `106 * 2` 的输出，并转换为统一的 `face_landmarks_106` finding。

YuNet 示例来源：

- `docs/examples/yunet_demo.py`

YuNet 在 C# 侧通过统一的 `IInferenceEngine` 包装层运行，不依赖 OpenCvSharp `FaceDetectorYN`。当前 `face_detection_yunet_2023mar.onnx` 为固定 `640x640` 输入，C# 会先把整帧 resize 到 `640x640`，再将检测框映射回原始 CAM_FACE 帧；`[models].backend = "rknn"` 时会自动使用同名 `.rknn` 模型。

OpenCV demo 封装后的 YuNet 输出格式为每行 15 个值：

```text
x, y, width, height, right_eye_x, right_eye_y, left_eye_x, left_eye_y,
nose_x, nose_y, right_mouth_x, right_mouth_y, left_mouth_x, left_mouth_y, score
```

当前场景为单人骑行，因此只选择面积最大的检测框作为人脸 ROI。

## C# 接入点

配置：

- `config.toml` 中 `CAM_FACE` 已切换为 `model = "pfld_lite.onnx"`。
- `CAM_FACE` 的 `input_width/input_height` 已设为 `112x112`。
- live test 可通过 `--camera CAM_FACE --source ...` 临时覆盖面部摄像头输入。

运行时：

- `src/camera/FacePipelineFramePreprocessor.cs`
  - CAM_FACE 两阶段链路使用整帧占位预处理，保留原始画面给 YuNet。
- `src/camera/FaceCameraAnalyzer.cs`
  - 先使用 `face_detection_yunet_2023mar.onnx/.rknn` 对整帧做人脸检测。
  - 只保留面积最大的单张人脸。
  - 将人脸框扩张为正方形 ROI，越界部分补黑边。
  - 把 ROI resize 到 `112x112` 后送入 `pfld_lite.onnx/.rknn`。
  - 将 PFLD 关键点从 ROI 坐标映射回整帧归一化坐标。
- `src/camera/FaceFatigueEstimator.cs`
  - 基于 106 点中左右眼上下眼睑与眼角距离估算单帧眼部开合度。
  - 输出 `fatigue` 或 `fatigue_normal`。
- `src/camera/CameraPipelineFactory.cs`
  - 根据模型文件名包含 `pfld` 自动选择面部两阶段链路。
- `src/models/InferenceOutputParser.cs`
  - 解码 YuNet 的 `cls/obj/bbox` stride 输出，box 公式与 OpenCV `FaceDetectorYN` 官方实现保持一致：`cx=(col+dx)*stride`、`cy=(row+dy)*stride`、`w=exp(dw)*stride`、`h=exp(dh)*stride`。
  - 兼容 YuNet 常见的 `[1,N,C]`、`[1,C,N]`、NCHW 和 NHWC 输出排布。
  - 识别 `[1, 212]` / `[212]` 的 PFLD 关键点输出。
  - 生成 `InferenceLandmark` 列表。
  - 根据关键点外接范围生成一条 `face_landmarks_106` finding，便于统一显示和数据库 payload 存储。
- `src/camera/CameraLiveTester.cs`
  - live preview 会在原图上绘制 106 个关键点，并显示关键点外接框。
  - CAM_FACE 预览会读取核心链路输出的 `fatigue` / `fatigue_normal` / `fatigue_unknown` finding，并在画面顶部标注当前人脸疲劳状态。
  - `fatigue` 使用红色框和 `FATIGUE WARNING` 标注，`fatigue_normal` 使用绿色 `FATIGUE NORMAL` 标注。
- `src/core/SafetyDecisionEngine.cs`
  - `face_landmarks_106` / `fatigue_normal` 作为面部基础结果，不直接触发安全告警。
  - `fatigue` 会作为 CAM_FACE 风险标签参与告警。

## ONNX Runtime 日志

`pfld_lite.onnx` 中部分 initializer 仍出现在 graph input 内，ONNX Runtime 会默认打印类似 `Initializer conv1.weight appears in graph inputs` 的优化提示。该提示不影响推理结果，但会刷屏干扰 live test。

当前 C# 运行时已在 `OnnxInferenceEngine` 和 `FaceCameraAnalyzer` 的 `SessionOptions` 中设置：

```csharp
LogSeverityLevel = OrtLoggingLevel.ORT_LOGGING_LEVEL_ERROR
```

这样会保留真正的 Error/Fatal 级别日志，同时屏蔽模型导出结构导致的 Warning 噪音。

## Live Test

使用真实面部摄像头：

```shell
dotnet run -- livetest --camera CAM_FACE --duration 10
```

使用合成源进行链路冒烟测试：

```shell
dotnet run -- livetest --camera CAM_FACE --source synthetic --duration 3 --headless
```

使用图片或视频文件测试时，将 `--source` 指向本地路径即可：

```shell
dotnet run -- livetest \
  --camera CAM_FACE \
  --source path/to/face.jpg \
  --duration 5
```

headless 输出中出现类似下面的 finding，说明 C# 侧已完成模型加载、预处理、推理和后处理：

```text
CamFace ... findings=[face_landmarks_106:0.99,fatigue_normal:0.91]
```

窗口预览中会同时绘制人脸框、106 个关键点，以及核心疲劳状态条：

```text
FATIGUE NORMAL 91%
```

## 注意事项

- 当前疲劳判断是单帧眼部开合度规则，适合作为 live 链路和数据闭环的第一版；稳定量产前建议加入时间窗口/PERCLOS、打哈欠和头部姿态规则。
- RK3588 部署时运行 `python3 scripts/convert_project_onnx_to_rknn.py`，可一次转换当前全部具体 ONNX 模型。YuNet 保持 `640x640 BGR NCHW` 原始数值输入，PFLD 保持 `112x112 BGR NCHW / 255` 输入和 `106 x 2` 输出语义。
