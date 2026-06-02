#!/usr/bin/env python3
"""Convert RideManager ONNX models to RKNN models for the C++ bridge."""

from __future__ import annotations

import argparse
import json
import shutil
import sys
from pathlib import Path
from typing import Iterable


def parse_shape(value: str) -> list[int]:
    """Parse an input shape written as 1,3,640,640."""
    try:
        shape = [int(part.strip()) for part in value.split(",") if part.strip()]
    except ValueError as exc:
        raise argparse.ArgumentTypeError(f"invalid shape: {value}") from exc

    if not shape or any(dimension <= 0 for dimension in shape):
        raise argparse.ArgumentTypeError(f"shape must contain positive integers: {value}")

    return shape


def parse_float_list(value: str) -> list[float]:
    """Parse a comma separated numeric list for RKNN preprocessing config."""
    try:
        numbers = [float(part.strip()) for part in value.split(",") if part.strip()]
    except ValueError as exc:
        raise argparse.ArgumentTypeError(f"invalid numeric list: {value}") from exc

    if not numbers:
        raise argparse.ArgumentTypeError(f"numeric list is empty: {value}")

    return numbers


def try_inspect_onnx_input_shapes(model_path: Path) -> list[list[int]]:
    """Read static ONNX input shapes when the onnx package is available."""
    try:
        import onnx  # type: ignore
    except ImportError:
        return []

    model = onnx.load(str(model_path))
    shapes: list[list[int]] = []
    for graph_input in model.graph.input:
        tensor_type = graph_input.type.tensor_type
        if not tensor_type.HasField("shape"):
            continue

        shape: list[int] = []
        for dimension in tensor_type.shape.dim:
            if dimension.dim_value <= 0:
                shape = []
                break
            shape.append(int(dimension.dim_value))

        if shape:
            shapes.append(shape)

    return shapes


def normalize_multi_value(values: list[list[float]] | None) -> list[list[float]] | None:
    """Return None or the list-of-list shape expected by RKNN Toolkit."""
    return values if values else None


def build_parser() -> argparse.ArgumentParser:
    """Create the command line parser."""
    parser = argparse.ArgumentParser(description="Convert an ONNX model to RKNN for RideManager.")
    parser.add_argument("onnx", type=Path, help="Path to the source ONNX model.")
    parser.add_argument("-o", "--output", type=Path, help="Output .rknn path. Defaults to the ONNX stem.")
    parser.add_argument("--target", default="rk3588", help="RKNN target platform, default: rk3588.")
    parser.add_argument(
        "--input-shape",
        action="append",
        type=parse_shape,
        help="Static input shape, for example 1,3,640,640. Repeat for multi-input models.",
    )
    parser.add_argument(
        "--input-name",
        action="append",
        help="ONNX input name for models that need explicit load_onnx inputs. Repeat in input order.",
    )
    parser.add_argument(
        "--mean-values",
        action="append",
        type=parse_float_list,
        help="RKNN mean_values entry, for example 0,0,0. Omitted by default because C# sends float tensors.",
    )
    parser.add_argument(
        "--std-values",
        action="append",
        type=parse_float_list,
        help="RKNN std_values entry, for example 255,255,255. Omitted by default because C# sends float tensors.",
    )
    parser.add_argument("--quantize", action="store_true", help="Enable RKNN quantization during build.")
    parser.add_argument("--dataset", type=Path, help="Quantization dataset file required when --quantize is used.")
    parser.add_argument("--verify", action="store_true", help="Run a random float32 inference after export.")
    parser.add_argument("--runtime-target", help="Runtime target used by init_runtime during --verify.")
    parser.add_argument("--verbose", action="store_true", help="Enable RKNN Toolkit verbose logging.")
    parser.add_argument(
        "--no-manifest",
        action="store_true",
        help="Do not write the small .json manifest next to the RKNN model.",
    )
    return parser


def require_rknn():
    """Import RKNN Toolkit and return its RKNN class."""
    try:
        from rknn.api import RKNN  # type: ignore
    except ImportError as exc:
        raise SystemExit("rknn-toolkit2 is required: pip install rknn-toolkit2") from exc

    return RKNN


def check_status(operation: str, status: int) -> None:
    """Exit when an RKNN Toolkit operation fails."""
    if status != 0:
        raise SystemExit(f"{operation} failed: {status}")


def copy_labels_if_present(onnx_path: Path, rknn_path: Path) -> None:
    """Copy ONNX sidecar labels so RKNN post-processing resolves the same class names."""
    source = onnx_path.with_suffix(".labels.txt")
    target = rknn_path.with_suffix(".labels.txt")
    if source.exists() and source != target:
        shutil.copyfile(source, target)


def write_manifest(path: Path, args: argparse.Namespace, input_shapes: Iterable[list[int]]) -> None:
    """Write a compact conversion manifest for deployment diagnostics."""
    manifest = {
        "source_onnx": str(args.onnx),
        "output_rknn": str(path),
        "target_platform": args.target,
        "input_shapes": list(input_shapes),
        "input_names": args.input_name or [],
        "quantized": bool(args.quantize),
        "csharp_input": "float32 pointer from NativeFloatTensor",
    }
    path.with_suffix(".rknn.json").write_text(json.dumps(manifest, indent=2), encoding="utf-8")


def main() -> int:
    """Run the ONNX to RKNN conversion workflow."""
    parser = build_parser()
    args = parser.parse_args()
    onnx_path: Path = args.onnx
    if not onnx_path.exists():
        parser.error(f"ONNX model not found: {onnx_path}")

    output_path = args.output or onnx_path.with_suffix(".rknn")
    output_path.parent.mkdir(parents=True, exist_ok=True)

    input_shapes = args.input_shape or try_inspect_onnx_input_shapes(onnx_path)
    if not input_shapes:
        parser.error("input shape is required when ONNX static input shapes cannot be inferred")

    if args.quantize and not args.dataset:
        parser.error("--dataset is required when --quantize is enabled")

    if args.dataset and not args.dataset.exists():
        parser.error(f"dataset file not found: {args.dataset}")

    RKNN = require_rknn()
    rknn = RKNN(verbose=args.verbose)
    try:
        config = {"target_platform": args.target}
        mean_values = normalize_multi_value(args.mean_values)
        std_values = normalize_multi_value(args.std_values)
        if mean_values is not None:
            config["mean_values"] = mean_values
        if std_values is not None:
            config["std_values"] = std_values

        check_status("config", rknn.config(**config))
        load_kwargs = {"model": str(onnx_path), "input_size_list": input_shapes}
        if args.input_name:
            load_kwargs["inputs"] = args.input_name
        check_status("load_onnx", rknn.load_onnx(**load_kwargs))
        build_kwargs = {"do_quantization": bool(args.quantize)}
        if args.dataset:
            build_kwargs["dataset"] = str(args.dataset)
        check_status("build", rknn.build(**build_kwargs))
        check_status("export_rknn", rknn.export_rknn(str(output_path)))

        if args.verify:
            import numpy as np

            runtime_kwargs = {}
            if args.runtime_target:
                runtime_kwargs["target"] = args.runtime_target
            check_status("init_runtime", rknn.init_runtime(**runtime_kwargs))
            random_inputs = [np.random.random(shape).astype("float32") for shape in input_shapes]
            outputs = rknn.inference(inputs=random_inputs)
            print("verify outputs:", [getattr(output, "shape", None) for output in outputs])

        copy_labels_if_present(onnx_path, output_path)
        if not args.no_manifest:
            write_manifest(output_path, args, input_shapes)
    finally:
        rknn.release()

    print(f"exported: {output_path}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
