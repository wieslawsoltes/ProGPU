#!/usr/bin/env python3
"""Verify the pinned Microsoft DirectX sample before oracle instrumentation."""

from __future__ import annotations

import argparse
import hashlib
import json
import pathlib
import subprocess
import sys


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--source", required=True, type=pathlib.Path)
    parser.add_argument("--lock", required=True, type=pathlib.Path)
    arguments = parser.parse_args()
    lock = json.loads(arguments.lock.read_text(encoding="utf-8"))
    source = arguments.source.resolve()
    head = subprocess.check_output(
        ["git", "-C", str(source), "rev-parse", "HEAD"], text=True
    ).strip()
    if head != lock["commit"]:
        raise SystemExit(f"Expected DirectX samples {lock['commit']}, found {head}.")

    for relative, expected in lock["files"].items():
        path = source / relative
        actual = hashlib.sha256(path.read_bytes()).hexdigest()
        if actual != expected:
            raise SystemExit(f"Hash mismatch for {relative}: {actual} != {expected}.")

    packages = (
        source / lock["sample"] / "packages.config"
    ).read_text(encoding="utf-8-sig")
    package_contract = (
        f'id="{lock["agilityPackage"]}" version="{lock["agilityVersion"]}"'
    )
    if package_contract not in packages:
        raise SystemExit(f"Missing pinned Agility SDK contract: {package_contract}.")

    shader = (source / lock["sample"] / "shaders.hlsl").read_text(
        encoding="utf-8-sig"
    )
    required_shader_contracts = (
        "result.position = position;",
        "result.color = color;",
        "return input.color;",
    )
    if any(value not in shader for value in required_shader_contracts):
        raise SystemExit("The pass-through HelloTriangle shader contract changed.")

    implementation = (
        source / lock["sample"] / "D3D12HelloTriangle.cpp"
    ).read_text(encoding="utf-8-sig")
    required_implementation_contracts = (
        "{ { 0.0f, 0.25f * m_aspectRatio, 0.0f }, { 1.0f, 0.0f, 0.0f, 1.0f } }",
        "{ { 0.25f, -0.25f * m_aspectRatio, 0.0f }, { 0.0f, 1.0f, 0.0f, 1.0f } }",
        "{ { -0.25f, -0.25f * m_aspectRatio, 0.0f }, { 0.0f, 0.0f, 1.0f, 1.0f } }",
        "const float clearColor[] = { 0.0f, 0.2f, 0.4f, 1.0f };",
        "m_commandList->DrawInstanced(3, 1, 0, 0);",
    )
    if any(value not in implementation for value in required_implementation_contracts):
        raise SystemExit("The pinned HelloTriangle draw contract changed.")

    print(
        "Verified Microsoft DirectX-Graphics-Samples "
        f"{head}, HelloTriangle, and Agility SDK {lock['agilityVersion']}."
    )
    return 0


if __name__ == "__main__":
    sys.exit(main())
