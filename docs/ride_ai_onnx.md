# ONNX Export Contract

本文档描述 `scripts/export_onnx.py --model student` 导出的 `models/export/ride_ai.onnx` 输入、输出和后处理约定。

## 导出命令

```powershell
python scripts/export_onnx.py --model student --checkpoint models/train/ride_ai/last.pt --output models/export/ride_ai.onnx --input-size 640
```

默认导出为单个 `.onnx` 文件，不生成外置 `.data` 文件。

## 输入

| 名称 | dtype | 形状 | 说明 |
| --- | --- | --- | --- |
| `images` | `float32` | `[batch, 3, height, width]` | RGB 图像，NCHW 排列，默认导出尺寸为 `[batch, 3, 640, 640]`。 |

输入预处理建议与 YOLOPv2 demo 保持一致：

1. 将图像 resize/letterbox 到导出尺寸，例如 `640x640`。
2. 按 RGB 顺序排列通道。
3. 转成 `float32`。
4. 像素值归一化到 `[0, 1]`。

默认导出启用动态 batch；如果导出时使用 `--fixed-batch`，batch 维度会固定为 `--batch-size`。

## 输出总览

学生模型导出的 ONNX 输出保持 YOLOPv2 风格，默认 `--export-num-classes 80`，所以每个检测 anchor 的通道数为 `5 + 80 = 85`，每个尺度的输出通道数为 `3 * 85 = 255`。

注意：当前 RideManager 正式 CAM_FRONT 链路已放弃车道线检测功能，只消费目标检测结果并做距离代理/透视位置/时间趋势风险分析。下面的 `drivable_logits` 与 `lane_logits` 仍属于模型导出兼容信息；如果模型输出这些分割结果，前向摄像头分析器会过滤 `drivable_area` / `lane_line` finding，不进入正式风险链路和数据库 finding。

`height=640`、`width=640` 时，输出形状如下：

| 顺序 | 名称 | dtype | 形状 | 说明 |
| --- | --- | --- | --- | --- |
| 1 | `pred_s8` | `float32` | `[batch, 255, 80, 80]` | stride 8 检测 head。 |
| 2 | `pred_s16` | `float32` | `[batch, 255, 40, 40]` | stride 16 检测 head。 |
| 3 | `pred_s32` | `float32` | `[batch, 255, 20, 20]` | stride 32 检测 head。 |
| 4 | `anchor_grid_s8` | `float32` | `[1, 3, 1, 1, 2]` | stride 8 anchor grid。 |
| 5 | `anchor_grid_s16` | `float32` | `[1, 3, 1, 1, 2]` | stride 16 anchor grid。 |
| 6 | `anchor_grid_s32` | `float32` | `[1, 3, 1, 1, 2]` | stride 32 anchor grid。 |
| 7 | `drivable_logits` | `float32` | `[batch, 2, 640, 640]` | 可行驶区域二分类 logits。 |
| 8 | `lane_logits` | `float32` | `[batch, 1, 640, 640]` | 车道线 logits。 |

如果导出尺寸不是 `640x640`，三层检测 head 的空间尺寸分别为：

- `pred_s8`: `[batch, 3 * (5 + export_num_classes), height / 8, width / 8]`
- `pred_s16`: `[batch, 3 * (5 + export_num_classes), height / 16, width / 16]`
- `pred_s32`: `[batch, 3 * (5 + export_num_classes), height / 32, width / 32]`

导出尺寸应能被 32 整除。

## 检测输出布局

检测输出按 YOLOPv2 raw head 形状组织：

```python
head = pred_s8.reshape(batch, 3, 5 + export_num_classes, 80, 80)
head = head.permute(0, 1, 3, 4, 2)
```

转换后单个 anchor 的最后一维布局为：

| 范围 | 含义 |
| --- | --- |
| `0:4` | box 参数。 |
| `4` | objectness。 |
| `5:` | class logits。 |

当前项目训练类别写入前 4 个类别通道：

| 类别 id | 类别名 |
| --- | --- |
| `0` | `person` |
| `1` | `vehicle` |
| `2` | `motorcycle` |
| `3` | `bicycle` |

如果使用默认 `--export-num-classes 80`，其余类别通道会填充为很低的 logits，方便保持 YOLOPv2 的 255 通道形状。

## Anchor Grid

导出的 anchor grid 与 YOLOPv2 demo 使用的 `split_for_trace_model(pred, anchor_grid)` 接口对齐。

| 名称 | stride | anchor 值 |
| --- | --- | --- |
| `anchor_grid_s8` | 8 | `(12, 16)`, `(19, 36)`, `(40, 28)` |
| `anchor_grid_s16` | 16 | `(36, 75)`, `(76, 55)`, `(72, 146)` |
| `anchor_grid_s32` | 32 | `(142, 110)`, `(192, 243)`, `(459, 401)` |

## 分割输出

`drivable_logits` 是二分类 logits：

- 通道 `0`: 背景。
- 通道 `1`: 可行驶区域。

常用后处理：

```python
drivable_mask = drivable_logits.argmax(axis=1)
```

`lane_logits` 是单通道车道线 logits。常用后处理：

```python
lane_prob = sigmoid(lane_logits)
lane_mask = lane_prob > threshold
```

`threshold` 可先从 `0.5` 开始调试。

## 与 YOLOPv2 的兼容边界

当前 ONNX 的输出名称和张量形状按 YOLOPv2 风格导出，方便复用原 YOLOPv2 的输入预处理、分割后处理和检测 head 读取流程。

需要注意：当前 `LightweightRoadStudent` 的内部检测分支是固定 `max_boxes` 检测头，不是真正的三尺度 anchor 检测头。因此导出的三尺度 `pred_s8/pred_s16/pred_s32` 是接口适配层生成的 YOLOPv2 形状，检测数值不等同于原生 YOLOPv2 raw head。若要完全复用 YOLOPv2 的 `split_for_trace_model + non_max_suppression` 并获得严格一致的检测语义，需要把学生模型检测头改成三尺度 YOLO head 后重新训练。

## 快速检查输出签名

```powershell
python -c "import onnx; m=onnx.load('models/export/ride_ai.onnx', load_external_data=False); print([(o.name, [d.dim_value or d.dim_param for d in o.type.tensor_type.shape.dim]) for o in m.graph.output])"
```

默认 640 导出应看到：

```text
pred_s8: [batch, 255, 80, 80]
pred_s16: [batch, 255, 40, 40]
pred_s32: [batch, 255, 20, 20]
anchor_grid_s8: [1, 3, 1, 1, 2]
anchor_grid_s16: [1, 3, 1, 1, 2]
anchor_grid_s32: [1, 3, 1, 1, 2]
drivable_logits: [batch, 2, 640, 640]
lane_logits: [batch, 1, 640, 640]
```
