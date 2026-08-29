#!/usr/bin/env python3
"""Verify pinned Win2D implementation and sample sources before oracle use."""

from __future__ import annotations

import argparse
import hashlib
import json
import pathlib
import subprocess
import sys


def read_checkout_bytes(source: pathlib.Path, relative: str) -> bytes:
    path = source / relative
    if path.is_file():
        return path.read_bytes()
    return subprocess.check_output(
        ["git", "-C", str(source), "show", f"HEAD:{relative}"]
    )


def verify_checkout(source: pathlib.Path, contract: dict[str, object], name: str) -> None:
    source = source.resolve()
    expected_commit = str(contract["commit"])
    actual_commit = subprocess.check_output(
        ["git", "-C", str(source), "rev-parse", "HEAD"], text=True
    ).strip()
    if actual_commit != expected_commit:
        raise SystemExit(f"Expected {name} {expected_commit}, found {actual_commit}.")

    files = contract["files"]
    if not isinstance(files, dict):
        raise SystemExit(f"Invalid {name} file contract.")
    for relative, expected in files.items():
        contents = read_checkout_bytes(source, str(relative))
        actual = hashlib.sha256(contents).hexdigest()
        normalized = hashlib.sha256(contents.replace(b"\r\n", b"\n")).hexdigest()
        if actual != expected and normalized != expected:
            raise SystemExit(
                f"Hash mismatch for {name} {relative}: {actual} != {expected}."
            )


def require_text(path: pathlib.Path, values: tuple[str, ...], name: str) -> None:
    contents = path.read_text(encoding="utf-8-sig")
    missing = [value for value in values if value not in contents]
    if missing:
        raise SystemExit(f"{name} contract changed; missing: {', '.join(missing)}")


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--implementation", required=True, type=pathlib.Path)
    parser.add_argument("--samples", required=True, type=pathlib.Path)
    parser.add_argument("--lock", required=True, type=pathlib.Path)
    arguments = parser.parse_args()

    lock = json.loads(arguments.lock.read_text(encoding="utf-8"))
    verify_checkout(arguments.implementation, lock["implementation"], "Win2D")
    verify_checkout(arguments.samples, lock["samples"], "Win2D-Samples")

    require_text(
        arguments.implementation / "winrt/lib/drawing/CanvasDevice.abi.idl",
        ("IDIRECT3DDEVICE* direct3DDevice", "ID2D1MultiThread::Enter", "HRESULT Lock"),
        "Win2D device interop",
    )
    require_text(
        arguments.implementation / "winrt/docsrc/Interop.aml",
        ("GetOrCreate(ID2D1Device1* device", "GetOrCreate&lt;CanvasBitmap&gt;", "ID2D1Bitmap1*"),
        "Win2D native resource interop",
    )
    require_text(
        arguments.samples / "SimpleSample/SimpleSample/MainWindow.xaml.cs",
        ("args.DrawingSession.DrawEllipse", "args.DrawingSession.DrawText"),
        "Win2D SimpleSample",
    )
    require_text(
        arguments.samples / "ExampleGallery/ShapesExample.xaml.cs",
        ("ds.DrawLine", "ds.DrawRectangle", "ds.DrawRoundedRectangle", "ds.DrawCircle"),
        "Win2D shapes oracle",
    )
    require_text(
        arguments.implementation / "winrt/lib/geometry/CanvasPathBuilder.cpp",
        (
            "CanvasPathBuilder::AddArcAroundEllipse",
            "XMConvertToDegrees(rotationAngle)",
            "CanvasPathBuilder::CloseAndReturnPath",
        ),
        "Win2D path-builder contract",
    )
    require_text(
        arguments.implementation / "winrt/lib/drawing/CanvasStrokeStyle.cpp",
        (
            "m_startCap(CanvasCapStyle::Flat)",
            "m_dashCap(CanvasCapStyle::Square)",
            "m_miterLimit(10.0f)",
            "m_transformBehavior(CanvasStrokeTransformBehavior::Normal)",
        ),
        "Win2D stroke-style defaults",
    )
    require_text(
        arguments.samples / "ExampleGallery/ArcOptions.xaml.cs",
        (
            "new CanvasPathBuilder(sender)",
            "builder.AddArc",
            "CanvasGeometry.CreatePath(builder)",
            "ds.DrawGeometry",
        ),
        "Win2D geometry oracle",
    )
    require_text(
        arguments.samples / "ExampleGallery/GeometryOperations.xaml.cs",
        (
            "CanvasGeometry.CreateGroup(resourceCreator",
            "leftGeometry.CombineWith(rightGeometry",
            "leftGeometry.Transform(",
            "args.DrawingSession.FillGeometry(combinedGeometry",
        ),
        "Win2D geometry-operations oracle",
    )
    require_text(
        arguments.samples / "ExampleGallery/VectorArt.xaml.cs",
        ("args.DrawingSession.CreateLayer", "new Rect(0, 0, sceneSize.X, sceneSize.Y)"),
        "Win2D layer oracle",
    )

    print(
        "Verified Win2D "
        f"{lock['implementation']['commit']} and Win2D-Samples "
        f"{lock['samples']['commit']}."
    )
    return 0


if __name__ == "__main__":
    sys.exit(main())
