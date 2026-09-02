#!/usr/bin/env python3
"""Compare the portable Direct2D COM fixture across ProGPU GPU backends."""

from __future__ import annotations

import argparse
import json
import pathlib
import sys


PROBES = (
    (2, 2),
    (10, 10),
    (40, 14),
    (46, 14),
    (25, 5),
    (29, 9),
    (22, 12),
    (23, 12),
    (22, 13),
    (23, 13),
    (26, 20),
    (4, 34),
    (2, 34),
    (18, 28),
    (46, 36),
    (60, 24),
    (8, 56),
    (1, 56),
    (24, 56),
    (30, 49),
    (33, 49),
    (57, 8),
    (62, 8),
    (38, 56),
    (56, 56),
    (63, 50),
)
EXPECTED_SIZE = (64, 64)


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
    if (width, height) != EXPECTED_SIZE:
        raise ValueError(
            f"{path} is {width}x{height}; expected the "
            f"{EXPECTED_SIZE[0]}x{EXPECTED_SIZE[1]} Direct2D fixture."
        )
    return width, height, pixels


def sample(pixels: bytes, width: int, x: int, y: int) -> bytes:
    offset = (y * width + x) * 3
    return pixels[offset : offset + 3]


def compare(reference_path: pathlib.Path, candidate_path: pathlib.Path) -> dict:
    width, height, reference = read_ppm(reference_path)
    candidate_width, candidate_height, candidate = read_ppm(candidate_path)
    if (candidate_width, candidate_height) != (width, height):
        raise ValueError(
            f"{candidate_path} is {candidate_width}x{candidate_height}; "
            f"expected {width}x{height}."
        )

    differences = [abs(left - right) for left, right in zip(reference, candidate)]
    changed_offsets = [
        offset
        for offset in range(0, len(differences), 3)
        if any(differences[offset : offset + 3])
    ]
    changed_pixels = len(changed_offsets)
    pixels_over_one = sum(
        max(differences[offset : offset + 3]) > 1
        for offset in range(0, len(differences), 3)
    )
    maximum = max(differences, default=0)
    mean = sum(differences) / len(differences) if differences else 0.0
    probe_differences = [
        max(
            abs(left - right)
            for left, right in zip(
                sample(reference, width, x, y),
                sample(candidate, width, x, y),
            )
        )
        for x, y in PROBES
    ]
    changed_bounds = None
    if changed_offsets:
        changed_x = [(offset // 3) % width for offset in changed_offsets]
        changed_y = [(offset // 3) // width for offset in changed_offsets]
        changed_bounds = [
            min(changed_x),
            min(changed_y),
            max(changed_x),
            max(changed_y),
        ]

    # The twenty-six clear/gradient/bitmap/bitmap-brush/path/stroke/interior,
    # clip, opacity-mask, compatible-target, and layer
    # probes must remain within one channel level.
    # Metal and llvmpipe/Vulkan currently differ at no more than 305
    # analytic/vector edge pixels, all by exactly one level. The bounded
    # whole-frame allowance
    # rejects a displaced edge, color drift, lost primitive, or backend CPU
    # substitute.
    passed = (
        maximum <= 1
        and changed_pixels <= 320
        and pixels_over_one == 0
        and mean <= 0.03
        and max(probe_differences, default=0) <= 1
    )
    return {
        "Reference": str(reference_path),
        "Candidate": str(candidate_path),
        "Width": width,
        "Height": height,
        "Exact": changed_pixels == 0,
        "ChangedPixels": changed_pixels,
        "ChangedPixelLimit": 320,
        "ChangedBounds": changed_bounds,
        "PixelsOver1": pixels_over_one,
        "MaximumChannelDifference": maximum,
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
        "Contract": "ProGPU portable Direct2D COM D3D12/Metal/Vulkan differential",
        "Fixture": (
            "64x64 clear, linear-gradient rectangle, radial-gradient ellipse, "
            "nearest-sampled BGRA bitmap, repeated nearest-sampled BGRA "
            "bitmap-brush rectangle, an alpha-ignore BGRA bitmap, stroked "
            "ellipse, and path-filled and "
            "stroked triangle, solid stroked rectangle, solid rounded "
            "rectangle, aliased and antialiased clips, opacity masks, a "
            "compatible target, and opacity/geometric-mask layers"
        ),
        "Tolerance": {
            "SemanticProbeMaximum": 1,
            "MaximumChannelDifference": 1,
            "ChangedPixelLimit": 320,
            "PixelsOver1Limit": 0,
            "MeanAbsoluteChannelDifferenceMaximum": 0.03,
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
