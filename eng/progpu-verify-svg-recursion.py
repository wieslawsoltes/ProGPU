#!/usr/bin/env python3
"""Verify the three pinned recursive SVG failures, not rendered-image parity."""

import json
from pathlib import Path
import re
import sys


FIXTURES = (
    ("resvg|tests/structure/image/recursive-2", 300, 300),
    ("w3c|struct-image-12-b", 480, 360),
    ("w3c|struct-use-08-b", 480, 360),
)
PROVIDERS = (("Reference-win-x64", "X64"), ("Reference-win-arm64", "Arm64"),
             ("Portable-linux-x64", "X64"))
FONT_HASHES = ("ffa8b2e7600f0e6213e5f35362154887e37accce0ff613006f9d1a2adaf1fc69",
               "7d2494f409eca08073006614ff6a8ccfb6cf7746e39102c432a6d8b945e6e2f3")


def require(condition, message):
    if not condition:
        raise ValueError(message)


def sha256(value):
    return isinstance(value, str) and re.fullmatch(r"[a-fA-F0-9]{64}", value) and int(value, 16) != 0


def verify(root):
    inputs = {}
    drawing_hashes = {}
    for provider, architecture in PROVIDERS:
        folder = root / provider

        def read(name):
            return json.loads((folder / name).read_text(encoding="utf-8-sig"))

        workers = read("workers.json")
        require(len(workers) == len(FIXTURES), f"{provider}: all three workers are required")
        identity = None
        for index, (key, width, height) in enumerate(FIXTURES):
            worker = workers[index]
            require(worker["Key"] == key and type(worker["ExitCode"]) is int and worker["ExitCode"] == 0
                    and worker["TimedOut"] is False and worker["HasResult"] is True,
                    f"{provider}/{key}: incomplete or reordered worker")
            receipt = read(f"{index}-identity.json")
            require((receipt["Key"], receipt["Width"], receipt["Height"], receipt["Architecture"])
                    == (key, width, height, architecture), f"{provider}/{key}: input identity differs")
            require(receipt["Runtime"].startswith(".NET 10."), f"{provider}: wrong runtime")
            for field in ("FixtureSha256", "SourceTextSha256", "ReferenceSha256"):
                require(sha256(receipt[field]), f"{provider}/{key}: invalid {field}")
            pair = (receipt["SourceTextSha256"], receipt["ReferenceSha256"])
            require(pair == inputs.setdefault(key, pair), f"{provider}/{key}: source/reference bytes differ")
            drawing = receipt["Drawing"]
            for assembly in (drawing, receipt["Svg"]):
                require(assembly["Name"] and assembly["Path"] and sha256(assembly["Sha256"]),
                        f"{provider}/{key}: missing assembly provenance")
            current_identity = (drawing["Name"], drawing["Path"], drawing["Sha256"], receipt["Svg"]["Sha256"])
            if identity is None:
                identity = current_identity
            require(identity == current_identity, f"{provider}: assemblies changed between workers")
            token = "cc7b13ffcd2ddd51" if provider.startswith("Reference") else "c29c9752855ee183"
            require(drawing["Name"] == "System.Drawing.Common, Version=10.0.0.0, Culture=neutral, PublicKeyToken=" + token,
                    f"{provider}: wrong System.Drawing implementation")
            if provider.startswith("Reference"):
                require("/shared/Microsoft.WindowsDesktop.App/" in drawing["Path"].replace("\\", "/"),
                        f"{provider}: reference is not Microsoft Windows Desktop")
            else:
                fonts = receipt["Fonts"]
                require(fonts["SourceFileCount"] == fonts["LoadedFaceCount"] == (21 if index == 0 else 22)
                        and fonts["InventorySha256"] == FONT_HASHES[0 if index == 0 else 1],
                        f"{provider}/{key}: pinned font environment differs")
            drawing_hashes[provider] = drawing["Sha256"]
            result = read(f"{index}-result.json")
            require(result["Outcome"] == "Exception", f"{provider}/{key}: failure contract changed")
            error = result["Exception"]
            require(error["Type"] == "System.ArgumentException" and error["Parameter"] is None
                    and "System.Drawing.Graphics.ScaleTransform(" in error["Detail"],
                    f"{provider}/{key}: must reject the recursive scale, not a cleanup operation")
            cause = read(f"{index}-first-drawing-exception.json")
            origin = "ScaleTransform" if provider.startswith("Reference") else "ValidateWorldTransform"
            require(cause["Type"] == "System.ArgumentException" and cause["Parameter"] is None
                    and f"System.Drawing.Graphics.{origin}(" in cause["Detail"],
                    f"{provider}/{key}: an earlier drawing error was masked")
    for provider, _ in PROVIDERS[:2]:
        require(drawing_hashes[provider] != drawing_hashes["Portable-linux-x64"],
                "The native reference cannot use the portable binary")


if __name__ == "__main__":
    try:
        require(len(sys.argv) == 2, "Expected the downloaded recursion evidence directory")
        verify(Path(sys.argv[1]))
    except (OSError, ValueError, KeyError, TypeError, AttributeError) as error:
        print(f"SVG recursion reference mismatch: {error}", file=sys.stderr)
        sys.exit(1)
    print("All three pinned SVG failures match Microsoft x64 and ARM64; no rendering parity is claimed.")
