#!/usr/bin/env python3
"""Compare ProGPU cross-platform frames with the native D3D12 sample oracle."""

from __future__ import annotations

import argparse
import json
import pathlib
import sys


def read_ppm(path: pathlib.Path) -> tuple[int, int, bytes]:
    with path.open("rb") as stream:
        if stream.readline().strip() != b"P6":
            raise ValueError(f"{path} is not a binary PPM.")
        line = stream.readline()
        while line.startswith(b"#"):
            line = stream.readline()
        width, height = (int(value) for value in line.split())
        if stream.readline().strip() != b"255":
            raise ValueError(f"{path} does not use 8-bit PPM channels.")
        pixels = stream.read()
    if len(pixels) != width * height * 3:
        raise ValueError(f"{path} has {len(pixels)} pixel bytes.")
    return width, height, pixels


def sample(pixels: bytes, width: int, x: int, y: int) -> list[int]:
    offset = (y * width + x) * 3
    return list(pixels[offset : offset + 3])


def compare(reference_path: pathlib.Path, candidate_path: pathlib.Path) -> dict:
    width, height, reference = read_ppm(reference_path)
    candidate_width, candidate_height, candidate = read_ppm(candidate_path)
    if (candidate_width, candidate_height) != (width, height):
        raise ValueError(
            f"{candidate_path} is {candidate_width}x{candidate_height}; "
            f"expected {width}x{height}."
        )
    differences = [abs(left - right) for left, right in zip(reference, candidate)]
    pixel_count = width * height
    maximum = max(differences)
    channels_over_three = sum(value > 3 for value in differences)
    pixels_over_three = sum(
        max(differences[offset : offset + 3]) > 3
        for offset in range(0, len(differences), 3)
    )
    mean = sum(differences) / len(differences)
    probes = [(8, 8), (640, 280), (560, 480), (720, 480)]
    probe_differences = [
        max(
            abs(left - right)
            for left, right in zip(
                sample(reference, width, x, y),
                sample(candidate, width, x, y),
            )
        )
        for x, y in probes
    ]
    # Hardware edge ownership and subpixel interpolation can differ at triangle
    # boundaries. Interior color interpolation and the untouched clear region
    # remain tight; aggregate allowances cover less than one percent of pixels.
    passed = (
        max(probe_differences) <= 3
        and pixels_over_three <= pixel_count // 100
        and channels_over_three <= (pixel_count * 3) // 100
        and mean <= 0.35
    )
    return {
        "Reference": str(reference_path),
        "Candidate": str(candidate_path),
        "Width": width,
        "Height": height,
        "MaximumChannelDifference": maximum,
        "ChannelsOver3": channels_over_three,
        "PixelsOver3": pixels_over_three,
        "MeanAbsoluteChannelDifference": mean,
        "ProbeMaximumDifferences": probe_differences,
        "Passed": passed,
    }


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--reference", required=True, type=pathlib.Path)
    parser.add_argument("--candidate", required=True, action="append", type=pathlib.Path)
    parser.add_argument("--output", required=True, type=pathlib.Path)
    arguments = parser.parse_args()
    results = [compare(arguments.reference, path) for path in arguments.candidate]
    contract = {
        "Contract": "Microsoft D3D12HelloTriangle native-to-ProGPU differential",
        "Tolerance": {
            "InteriorProbeMaximum": 3,
            "PixelsOver3MaximumPercent": 1,
            "ChannelsOver3MaximumPercent": 1,
            "MeanAbsoluteChannelDifferenceMaximum": 0.35,
        },
        "Results": results,
        "Passed": all(result["Passed"] for result in results),
    }
    arguments.output.parent.mkdir(parents=True, exist_ok=True)
    arguments.output.write_text(json.dumps(contract, indent=2) + "\n", encoding="utf-8")
    print(json.dumps(contract, indent=2))
    return 0 if contract["Passed"] else 1


if __name__ == "__main__":
    sys.exit(main())
