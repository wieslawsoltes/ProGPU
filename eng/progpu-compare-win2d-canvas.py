#!/usr/bin/env python3
"""Compare portable Win2D Canvas frames across ProGPU GPU backends."""

from __future__ import annotations

import argparse
import json
import pathlib
import sys


def read_ppm(path: pathlib.Path) -> tuple[int, int, bytes]:
    with path.open("rb") as stream:
        if stream.readline().strip() != b"P6":
            raise ValueError(f"{path} is not a binary PPM.")
        dimensions = stream.readline()
        while dimensions.startswith(b"#"):
            dimensions = stream.readline()
        width, height = (int(value) for value in dimensions.split())
        if stream.readline().strip() != b"255":
            raise ValueError(f"{path} does not use 8-bit PPM channels.")
        pixels = stream.read()
    if len(pixels) != width * height * 3:
        raise ValueError(f"{path} has {len(pixels)} pixel bytes.")
    return width, height, pixels


def compare(reference_path: pathlib.Path, candidate_path: pathlib.Path) -> dict:
    width, height, reference = read_ppm(reference_path)
    candidate_width, candidate_height, candidate = read_ppm(candidate_path)
    if (candidate_width, candidate_height) != (width, height):
        raise ValueError(
            f"{candidate_path} is {candidate_width}x{candidate_height}; "
            f"expected {width}x{height}."
        )

    differences = [abs(left - right) for left, right in zip(reference, candidate)]
    changed_pixels = sum(
        any(differences[offset : offset + 3])
        for offset in range(0, len(differences), 3)
    )
    pixels_over_one = sum(
        max(differences[offset : offset + 3]) > 1
        for offset in range(0, len(differences), 3)
    )
    maximum = max(differences, default=0)
    mean = sum(differences) / len(differences) if differences else 0.0
    pixel_count = width * height

    # Solid primitives, bitmaps, clips, and interiors are deterministic. A
    # bounded allowance covers backend shader rounding at antialiased curve
    # edges while rejecting displaced geometry, broken clipping, or color drift.
    changed_pixel_limit = max(4, pixel_count // 200)
    pixels_over_one_limit = max(1, pixel_count // 1000)
    passed = (
        maximum <= 3
        and changed_pixels <= changed_pixel_limit
        and pixels_over_one <= pixels_over_one_limit
        and mean <= 0.01
    )
    return {
        "Reference": str(reference_path),
        "Candidate": str(candidate_path),
        "Width": width,
        "Height": height,
        "Exact": changed_pixels == 0,
        "ChangedPixels": changed_pixels,
        "ChangedPixelLimit": changed_pixel_limit,
        "PixelsOver1": pixels_over_one,
        "PixelsOver1Limit": pixels_over_one_limit,
        "MaximumChannelDifference": maximum,
        "MeanAbsoluteChannelDifference": mean,
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
        "Contract": "ProGPU portable Win2D Canvas D3D12/Metal/Vulkan differential",
        "Tolerance": {
            "MaximumChannelDifference": 3,
            "ChangedPixelMaximumPercent": 0.5,
            "PixelsOver1MaximumPercent": 0.1,
            "MeanAbsoluteChannelDifferenceMaximum": 0.01,
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
