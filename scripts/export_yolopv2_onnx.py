#!/usr/bin/env python3
"""Export YOLOPv2 TorchScript checkpoints to ONNX for RideManager."""

from __future__ import annotations

import argparse
from pathlib import Path

import numpy as np
import onnx
import onnxruntime as ort
import torch


class YoloPv2OnnxWrapper(torch.nn.Module):
    """Wrap YOLOPv2 demo output into ONNX-friendly tensors."""

    def __init__(self, model: torch.jit.ScriptModule):
        super().__init__()
        self.model = model

    def forward(self, image: torch.Tensor):
        (raw_predictions, anchor_grid), drivable_area, lane_line = self.model(image)
        detections = decode_yolo_head(raw_predictions, anchor_grid)
        return detections, drivable_area, lane_line


def decode_yolo_head(raw_predictions, anchor_grid):
    decoded = []
    strides = (8, 16, 32)
    for index, stride in enumerate(strides):
        prediction = raw_predictions[index]
        batch_size, _, grid_y, grid_x = prediction.shape
        prediction = prediction.view(batch_size, 3, 85, grid_y, grid_x)
        prediction = prediction.permute(0, 1, 3, 4, 2).contiguous()
        prediction = prediction.sigmoid()

        grid = make_grid(grid_x, grid_y, prediction.device)
        xy = (prediction[..., 0:2] * 2.0 - 0.5 + grid) * stride
        wh = (prediction[..., 2:4] * 2.0) ** 2 * anchor_grid[index]
        decoded.append(torch.cat((xy, wh, prediction[..., 4:]), dim=-1).view(batch_size, -1, 85))

    return torch.cat(decoded, dim=1)


def make_grid(grid_x: int, grid_y: int, device: torch.device):
    yv, xv = torch.meshgrid(
        torch.arange(grid_y, device=device),
        torch.arange(grid_x, device=device),
        indexing="ij",
    )
    return torch.stack((xv, yv), 2).view(1, 1, grid_y, grid_x, 2).float()


def export_onnx(weights_path: Path, output_path: Path, image_size: int, opset: int):
    output_path.parent.mkdir(parents=True, exist_ok=True)
    model = torch.jit.load(str(weights_path), map_location="cpu").eval()
    wrapper = YoloPv2OnnxWrapper(model).eval()
    dummy = torch.zeros((1, 3, image_size, image_size), dtype=torch.float32)
    traced = torch.jit.trace(wrapper, dummy, strict=False, check_trace=False)

    with torch.no_grad():
        torch.onnx.export(
            traced,
            dummy,
            str(output_path),
            input_names=["input"],
            output_names=["detections", "drivable_area", "lane_line"],
            opset_version=opset,
            do_constant_folding=True,
            dynamic_axes=None,
            dynamo=False,
        )


def verify_onnx(output_path: Path, image_size: int):
    model = onnx.load(str(output_path))
    onnx.checker.check_model(model)

    session = ort.InferenceSession(str(output_path), providers=["CPUExecutionProvider"])
    input_name = session.get_inputs()[0].name
    dummy = np.zeros((1, 3, image_size, image_size), dtype=np.float32)
    outputs = session.run(None, {input_name: dummy})
    return [tuple(output.shape) for output in outputs]


def parse_args():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument(
        "--weights",
        default="docs/examples/YOLOPv2/yolopv2.pt",
        type=Path,
        help="YOLOPv2 TorchScript .pt path.",
    )
    parser.add_argument("--output", default="models/yolopv2.onnx", type=Path)
    parser.add_argument("--img-size", default=640, type=int)
    parser.add_argument("--opset", default=17, type=int)
    parser.add_argument("--skip-verify", action="store_true")
    return parser.parse_args()


def main():
    args = parse_args()
    export_onnx(args.weights, args.output, args.img_size, args.opset)
    print(f"exported: {args.output}")
    if not args.skip_verify:
        shapes = verify_onnx(args.output, args.img_size)
        print("verified output shapes:", shapes)


if __name__ == "__main__":
    main()
