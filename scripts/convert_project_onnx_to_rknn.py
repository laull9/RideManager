#!/usr/bin/env python3
"""Convert every ONNX model currently used by RideManager to RKNN."""

from __future__ import annotations

import argparse
import subprocess
import sys
from dataclasses import dataclass
from pathlib import Path


@dataclass(frozen=True)
class ModelSpec:
    """Describe one concrete model and its static NCHW input shape."""

    name: str
    input_shape: tuple[int, ...]


MODEL_SPECS = (
    ModelSpec("face_detection_yunet_2023mar.onnx", (1, 3, 640, 640)),
    ModelSpec("pfld_lite.onnx", (1, 3, 112, 112)),
    ModelSpec("yolo26n.onnx", (1, 3, 640, 640)),
    ModelSpec("yolopv2.onnx", (1, 3, 640, 640)),
)


def build_parser() -> argparse.ArgumentParser:
    """Create the command line parser."""
    parser = argparse.ArgumentParser(
        description="Convert RideManager's concrete ONNX model set with the generic RKNN converter."
    )
    parser.add_argument("--models-dir", type=Path, default=Path("models"), help="Model directory, default: models.")
    parser.add_argument("--target", default="rk3588", help="RKNN target platform, default: rk3588.")
    parser.add_argument("--verify", action="store_true", help="Run random float32 inference after each export.")
    parser.add_argument("--runtime-target", help="Runtime target passed to init_runtime during --verify.")
    parser.add_argument("--verbose", action="store_true", help="Enable RKNN Toolkit verbose logging.")
    return parser


def converter_command(
    converter: Path,
    models_dir: Path,
    model: ModelSpec,
    args: argparse.Namespace,
) -> list[str]:
    """Build one invocation of the existing generic conversion script."""
    command = [
        sys.executable,
        str(converter),
        str(models_dir / model.name),
        "--target",
        args.target,
        "--input-shape",
        ",".join(str(dimension) for dimension in model.input_shape),
    ]
    if args.verify:
        command.append("--verify")
    if args.runtime_target:
        command.extend(("--runtime-target", args.runtime_target))
    if args.verbose:
        command.append("--verbose")
    return command


def main() -> int:
    """Convert the complete project model set, stopping on the first failure."""
    args = build_parser().parse_args()
    script_dir = Path(__file__).resolve().parent
    converter = script_dir / "convert_onnx_to_rknn.py"
    models_dir = args.models_dir.resolve()

    missing = [model.name for model in MODEL_SPECS if not (models_dir / model.name).is_file()]
    if missing:
        raise SystemExit(f"missing required ONNX models in {models_dir}: {', '.join(missing)}")

    for model in MODEL_SPECS:
        print(f"converting {model.name} ...", flush=True)
        subprocess.run(converter_command(converter, models_dir, model, args), check=True)

    print(f"converted {len(MODEL_SPECS)} models in {models_dir}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
