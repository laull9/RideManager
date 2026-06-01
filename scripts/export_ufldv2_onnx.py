#!/usr/bin/env python3
"""Export UFLDv2 PyTorch checkpoints to ONNX for RideManager."""

from __future__ import annotations

import argparse
import importlib.util
import sys
import types
from pathlib import Path

import torch


def initialize_weights(*models):
    for model in models:
        _initialize_weight(model)


def _initialize_weight(module):
    if isinstance(module, list):
        for child in module:
            _initialize_weight(child)
    elif isinstance(module, torch.nn.Conv2d):
        torch.nn.init.kaiming_normal_(module.weight, nonlinearity="relu")
        if module.bias is not None:
            torch.nn.init.constant_(module.bias, 0)
    elif isinstance(module, torch.nn.Linear):
        module.weight.data.normal_(0.0, std=0.01)
    elif isinstance(module, torch.nn.BatchNorm2d):
        torch.nn.init.constant_(module.weight, 1)
        torch.nn.init.constant_(module.bias, 0)
    elif isinstance(module, torch.nn.Module):
        for child in module.children():
            _initialize_weight(child)


class OnnxWrapper(torch.nn.Module):
    def __init__(self, model):
        super().__init__()
        self.model = model

    def forward(self, image):
        prediction = self.model(image)
        return (
            prediction["loc_row"],
            prediction["loc_col"],
            prediction["exist_row"],
            prediction["exist_col"],
        )


def load_config(path: Path) -> types.SimpleNamespace:
    spec = importlib.util.spec_from_file_location("ufldv2_export_config", path)
    if spec is None or spec.loader is None:
        raise RuntimeError(f"Cannot load config: {path}")

    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    values = {
        name: getattr(module, name)
        for name in dir(module)
        if not name.startswith("_")
    }
    return types.SimpleNamespace(**values)


def register_lightweight_common_module():
    common = types.ModuleType("utils.common")
    common.initialize_weights = initialize_weights
    sys.modules["utils.common"] = common


def build_model(example_dir: Path, cfg: types.SimpleNamespace):
    sys.path.insert(0, str(example_dir))
    register_lightweight_common_module()
    from model.model_culane import parsingNet

    return parsingNet(
        pretrained=False,
        backbone=cfg.backbone,
        num_grid_row=cfg.num_cell_row,
        num_cls_row=cfg.num_row,
        num_grid_col=cfg.num_cell_col,
        num_cls_col=cfg.num_col,
        num_lane_on_row=cfg.num_lanes,
        num_lane_on_col=cfg.num_lanes,
        use_aux=cfg.use_aux,
        input_height=cfg.train_height,
        input_width=cfg.train_width,
        fc_norm=cfg.fc_norm,
    )


def load_checkpoint(model: torch.nn.Module, checkpoint_path: Path):
    checkpoint = torch.load(checkpoint_path, map_location="cpu")
    state_dict = checkpoint["model"] if "model" in checkpoint else checkpoint
    compatible_state_dict = {
        key[7:] if key.startswith("module.") else key: value
        for key, value in state_dict.items()
    }
    incompatible = model.load_state_dict(compatible_state_dict, strict=False)
    if incompatible.missing_keys or incompatible.unexpected_keys:
        raise RuntimeError(
            "Checkpoint does not match UFLDv2 model. "
            f"Missing keys: {incompatible.missing_keys[:8]}, "
            f"unexpected keys: {incompatible.unexpected_keys[:8]}"
        )


def export_onnx(model: torch.nn.Module, cfg: types.SimpleNamespace, output_path: Path, opset: int):
    output_path.parent.mkdir(parents=True, exist_ok=True)
    external_data_path = Path(str(output_path) + ".data")
    if external_data_path.exists():
        external_data_path.unlink()
    wrapper = OnnxWrapper(model).eval()
    dummy = torch.ones((1, 3, cfg.train_height, cfg.train_width), dtype=torch.float32)
    with torch.no_grad():
        torch.onnx.export(
            wrapper,
            dummy,
            str(output_path),
            input_names=["input"],
            output_names=["loc_row", "loc_col", "exist_row", "exist_col"],
            opset_version=opset,
            external_data=False,
            do_constant_folding=True,
        )


def verify_onnx(output_path: Path, cfg: types.SimpleNamespace):
    import onnx
    import onnxruntime as ort
    import numpy as np

    model = onnx.load(str(output_path))
    onnx.checker.check_model(model)

    session = ort.InferenceSession(str(output_path), providers=["CPUExecutionProvider"])
    input_name = session.get_inputs()[0].name
    dummy = np.ones((1, 3, cfg.train_height, cfg.train_width), dtype=np.float32)
    outputs = session.run(None, {input_name: dummy})
    return [tuple(output.shape) for output in outputs]


def parse_args():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument(
        "--example-dir",
        default="docs/examples/Ultra-Fast-Lane-Detection-v2",
        type=Path,
    )
    parser.add_argument(
        "--config",
        default="docs/examples/Ultra-Fast-Lane-Detection-v2/configs/tusimple_res18.py",
        type=Path,
    )
    parser.add_argument(
        "--checkpoint",
        default="docs/examples/Ultra-Fast-Lane-Detection-v2/tusimple_res18.pth",
        type=Path,
    )
    parser.add_argument("--output", default="models/ufldv2_tusimple_res18.onnx", type=Path)
    parser.add_argument("--opset", default=17, type=int)
    parser.add_argument("--skip-verify", action="store_true")
    return parser.parse_args()


def main():
    args = parse_args()
    cfg = load_config(args.config)
    model = build_model(args.example_dir, cfg)
    load_checkpoint(model, args.checkpoint)
    export_onnx(model, cfg, args.output, args.opset)
    print(f"exported: {args.output}")
    if not args.skip_verify:
        shapes = verify_onnx(args.output, cfg)
        print("verified output shapes:", shapes)


if __name__ == "__main__":
    main()
