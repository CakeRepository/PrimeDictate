#!/usr/bin/env python3
"""
Prepare QNN-friendly Moonshine artifacts for PrimeDictate.

This is a maintainer-only offline tool. End-user runtime inference remains pure
.NET/C# and uses ONNX Runtime directly inside PrimeDictate.

Expected calibration layout:

<calibration-dir>/
  encoder/              # Moonshine v2
    sample-000.npz
    ...
  decoder/              # Moonshine v2 merged decoder
    sample-000.npz
    ...
  preprocess/
    sample-000.npz
    ...
  encode/
    sample-000.npz
    ...
  uncached_decode/
    sample-000.npz
    ...
  cached_decode/
    sample-000.npz
    ...

Each .npz file must contain arrays keyed by the exact ONNX input names for that
stage. This keeps the pipeline reproducible and avoids baking stage-specific
capture logic into the quantizer itself.

The script writes generated QDQ models to:

<moonshine-model-dir>/qnn/
  encoder.qdq.onnx       # Moonshine v2
  decoder.qdq.onnx       # Moonshine v2
  preprocess.qdq.onnx
  encode.qdq.onnx
  uncached_decode.qdq.onnx
  cached_decode.qdq.onnx
  manifest.json

Notes:
- Best results typically come from quantizing float models. This scaffold will
  still attempt to preprocess and quantize the model files you point it at.
- Moonshine v2 sherpa downloads currently ship optimized .ort runtime files.
  ONNX Runtime's QNN quantization tools expect source .onnx files, so place
  encoder_model.onnx and decoder_model_merged.onnx in --source-model-dir.
- Calibration data should be captured from representative 16 kHz mono dictation
  inputs and, for decoder stages, representative intermediate tensors collected
  from a known-good CPU reference run.
"""

from __future__ import annotations

import argparse
import datetime as dt
import json
from pathlib import Path
from typing import Dict, Iterator, List, Sequence

import numpy as np
import onnxruntime as ort
from onnxruntime.quantization import CalibrationDataReader, QuantType, quantize
from onnxruntime.quantization.execution_providers.qnn import (
    get_qnn_qdq_config,
    qnn_preprocess_model,
)


STAGES_V1 = {
    "preprocess": ("preprocess.qdq.onnx", ("preprocess.onnx",)),
    "encode": ("encode.qdq.onnx", ("encode.onnx", "encode.int8.onnx")),
    "uncached_decode": (
        "uncached_decode.qdq.onnx",
        ("uncached_decode.onnx", "uncached_decode.int8.onnx"),
    ),
    "cached_decode": (
        "cached_decode.qdq.onnx",
        ("cached_decode.onnx", "cached_decode.int8.onnx"),
    ),
}

STAGES_V2 = {
    "encoder": (
        "encoder.qdq.onnx",
        (
            "encoder_model.onnx",
            "encoder_model_fp16.onnx",
            "encoder_model_int8.onnx",
            "encoder_model_quantized.onnx",
        ),
    ),
    "decoder": (
        "decoder.qdq.onnx",
        (
            "decoder_model_merged.onnx",
            "decoder_model_merged_fp16.onnx",
            "decoder_model_merged_int8.onnx",
            "decoder_model_merged_quantized.onnx",
        ),
    ),
}


class NpzCalibrationDataReader(CalibrationDataReader):
    def __init__(self, model_path: Path, samples_dir: Path) -> None:
        self._session = ort.InferenceSession(str(model_path), providers=["CPUExecutionProvider"])
        self._input_names = [item.name for item in self._session.get_inputs()]
        self._samples = self._load_samples(samples_dir)
        self._index = 0

    def get_next(self) -> Dict[str, np.ndarray] | None:
        if self._index >= len(self._samples):
            return None

        sample = self._samples[self._index]
        self._index += 1
        return sample

    def rewind(self) -> None:
        self._index = 0

    def _load_samples(self, samples_dir: Path) -> List[Dict[str, np.ndarray]]:
        if not samples_dir.is_dir():
            raise FileNotFoundError(f"Calibration directory not found: {samples_dir}")

        samples: List[Dict[str, np.ndarray]] = []
        for sample_path in sorted(samples_dir.glob("*.npz")):
            with np.load(sample_path, allow_pickle=False) as data:
                sample = {name: data[name] for name in self._input_names if name in data}

            missing = [name for name in self._input_names if name not in sample]
            if missing:
                raise ValueError(
                    f"Calibration sample '{sample_path}' is missing required inputs: {missing}"
                )

            samples.append(sample)

        if not samples:
            raise ValueError(f"No calibration .npz files found under {samples_dir}")

        return samples


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description="Quantize Moonshine stages for QNN EP")
    parser.add_argument(
        "--model-dir",
        required=True,
        help="Path to the installed Moonshine model directory where qnn artifacts will be written",
    )
    parser.add_argument(
        "--source-model-dir",
        help=(
            "Optional path to source ONNX files. Required for Moonshine v2 if "
            "--model-dir only contains sherpa .ort files."
        ),
    )
    parser.add_argument(
        "--calibration-dir",
        required=True,
        help="Path to the root calibration directory with stage subfolders",
    )
    parser.add_argument(
        "--activation-type",
        choices=["uint8", "uint16"],
        default="uint16",
        help="Activation quantization type for generated QDQ models",
    )
    parser.add_argument(
        "--weight-type",
        choices=["uint8", "int8"],
        default="uint8",
        help="Weight quantization type for generated QDQ models",
    )
    return parser.parse_args()


def to_quant_type(value: str) -> QuantType:
    mapping = {
        "uint8": QuantType.QUInt8,
        "uint16": QuantType.QUInt16,
        "int8": QuantType.QInt8,
    }
    return mapping[value]


def find_first_existing(directory: Path, names: Sequence[str]) -> Path | None:
    for name in names:
        path = directory / name
        if path.is_file():
            return path

    return None


def ensure_stage_inputs(
    model_dir: Path,
    source_model_dir: Path,
    calibration_dir: Path,
) -> Iterator[tuple[str, Path, Path, Path]]:
    if find_first_existing(source_model_dir, STAGES_V2["encoder"][1]) and find_first_existing(
        source_model_dir,
        STAGES_V2["decoder"][1],
    ):
        print("Detected Moonshine v2 (2-stage) model layout")
        stages = STAGES_V2
    elif (model_dir / "encoder_model.ort").is_file() and (
        model_dir / "decoder_model_merged.ort"
    ).is_file():
        raise FileNotFoundError(
            "Moonshine v2 source ONNX files were not found. The installed sherpa "
            "folder contains encoder_model.ort and decoder_model_merged.ort, but "
            "QNN quantization needs encoder_model.onnx and decoder_model_merged.onnx. "
            "Download/export those source ONNX files and pass their directory with "
            "--source-model-dir."
        )
    else:
        print("Detected Moonshine v1 (4-stage) model layout")
        stages = STAGES_V1

    for stage, (output_file_name, candidate_names) in stages.items():
        model_path = find_first_existing(source_model_dir, candidate_names)
        if model_path is None:
            expected = ", ".join(candidate_names)
            raise FileNotFoundError(
                f"Required Moonshine stage model not found for '{stage}' under "
                f"{source_model_dir}. Expected one of: {expected}"
            )

        samples_dir = calibration_dir / stage
        output_path = model_dir / "qnn" / output_file_name
        yield stage, model_path, samples_dir, output_path


def main() -> None:
    args = parse_args()
    model_dir = Path(args.model_dir).expanduser().resolve()
    source_model_dir = (
        Path(args.source_model_dir).expanduser().resolve()
        if args.source_model_dir
        else model_dir
    )
    calibration_dir = Path(args.calibration_dir).expanduser().resolve()
    output_dir = model_dir / "qnn"
    output_dir.mkdir(parents=True, exist_ok=True)

    activation_type = to_quant_type(args.activation_type)
    weight_type = to_quant_type(args.weight_type)

    manifest = {
        "generated_utc": dt.datetime.utcnow().replace(microsecond=0).isoformat() + "Z",
        "onnxruntime_version": ort.__version__,
        "source_model_dir": str(model_dir),
        "source_onnx_dir": str(source_model_dir),
        "calibration_dir": str(calibration_dir),
        "activation_type": args.activation_type,
        "weight_type": args.weight_type,
        "stages": {},
    }

    for stage, model_path, samples_dir, qdq_path in ensure_stage_inputs(
        model_dir,
        source_model_dir,
        calibration_dir,
    ):
        print(f"Preparing {stage}: {model_path.name}")
        reader = NpzCalibrationDataReader(model_path, samples_dir)

        preprocessed_path = output_dir / f"{stage}.preprocessed.onnx"

        model_changed = qnn_preprocess_model(str(model_path), str(preprocessed_path))
        model_to_quantize = preprocessed_path if model_changed else model_path

        qnn_config = get_qnn_qdq_config(
            str(model_to_quantize),
            reader,
            activation_type=activation_type,
            weight_type=weight_type,
        )

        quantize(str(model_to_quantize), str(qdq_path), qnn_config)

        manifest["stages"][stage] = {
            "source_model": str(model_path),
            "preprocessed_model": str(preprocessed_path) if model_changed else None,
            "output_model": str(qdq_path),
            "calibration_samples": len(reader._samples),
        }

    manifest_path = output_dir / "manifest.json"
    manifest_path.write_text(json.dumps(manifest, indent=2), encoding="utf-8")
    print(f"Wrote QNN Moonshine artifacts to {output_dir}")
    print(f"Manifest: {manifest_path}")


if __name__ == "__main__":
    main()
