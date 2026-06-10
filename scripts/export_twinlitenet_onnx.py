#!/usr/bin/env python3
"""Export TwinLiteNet PyTorch checkpoints to ONNX for RideManager."""

from __future__ import annotations

import argparse
import sys
from collections import OrderedDict
from pathlib import Path

import numpy as np
import onnx
import onnxruntime as ort
import torch


def load_twinlitenet(example_dir: Path):
    sys.path.insert(0, str(example_dir))
    from model.TwinLite import TwinLiteNet  # noqa: PLC0415

    return TwinLiteNet()


def normalize_state_dict(checkpoint):
    state_dict = checkpoint.get("state_dict", checkpoint) if isinstance(checkpoint, dict) else checkpoint
    normalized = OrderedDict()
    for key, value in state_dict.items():
        normalized[key.removeprefix("module.")] = value
    return normalized


def export_onnx(
    weights_path: Path,
    output_path: Path,
    example_dir: Path,
    input_height: int,
    input_width: int,
    opset: int,
):
    output_path.parent.mkdir(parents=True, exist_ok=True)
    model = load_twinlitenet(example_dir).eval()
    checkpoint = torch.load(weights_path, map_location="cpu")
    model.load_state_dict(normalize_state_dict(checkpoint))

    dummy = torch.zeros((1, 3, input_height, input_width), dtype=torch.float32)
    with torch.no_grad():
        torch.onnx.export(
            model,
            dummy,
            str(output_path),
            input_names=["images"],
            output_names=["da", "ll"],
            opset_version=opset,
            do_constant_folding=True,
            dynamic_axes=None,
            dynamo=False,
        )


def verify_onnx(output_path: Path, input_height: int, input_width: int):
    model = onnx.load(str(output_path))
    onnx.checker.check_model(model)

    session = ort.InferenceSession(str(output_path), providers=["CPUExecutionProvider"])
    input_name = session.get_inputs()[0].name
    dummy = np.zeros((1, 3, input_height, input_width), dtype=np.float32)
    outputs = session.run(None, {input_name: dummy})
    return [tuple(output.shape) for output in outputs]


def parse_args():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument(
        "--weights",
        default="docs/examples/TwinLiteNet/pretrained/best.pth",
        type=Path,
        help="TwinLiteNet .pth checkpoint path.",
    )
    parser.add_argument("--output", default="models/twinlitenet.onnx", type=Path)
    parser.add_argument("--example-dir", default="docs/examples/TwinLiteNet", type=Path)
    parser.add_argument("--input-height", default=360, type=int)
    parser.add_argument("--input-width", default=640, type=int)
    parser.add_argument("--opset", default=17, type=int)
    parser.add_argument("--skip-verify", action="store_true")
    return parser.parse_args()


def main():
    args = parse_args()
    export_onnx(
        args.weights,
        args.output,
        args.example_dir,
        args.input_height,
        args.input_width,
        args.opset,
    )
    print(f"exported: {args.output}")
    if not args.skip_verify:
        shapes = verify_onnx(args.output, args.input_height, args.input_width)
        print("verified output shapes:", shapes)


if __name__ == "__main__":
    main()
