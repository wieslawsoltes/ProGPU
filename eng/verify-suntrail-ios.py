#!/usr/bin/env python3
"""Check runtime-resolved WebGPU exports in the final, post-strip iOS app."""
import argparse
import plistlib
import re
import subprocess
from pathlib import Path


def verify(app: Path) -> None:
    with (app / "Info.plist").open("rb") as stream:
        executable = app / plistlib.load(stream)["CFBundleExecutable"]
    result = subprocess.run(
        ["xcrun", "dyld_info", "-exports", str(executable)],
        check=True, capture_output=True, text=True,
    )
    exports = set(re.findall(r"\b(_wgpu\w+)\s*$", result.stdout, re.MULTILINE))
    required = {
        "_wgpuCreateInstance", "_wgpuInstanceRequestAdapter",
        "_wgpuAdapterRequestDevice", "_wgpuDeviceCreateShaderModule",
        "_wgpuDeviceCreateRenderPipeline", "_wgpuQueueSubmit",
        "_wgpuSurfaceGetCurrentTexture", "_wgpuSurfacePresent",
    }
    missing = sorted(required - exports)
    if missing:
        raise SystemExit("Missing runtime WebGPU exports: " + ", ".join(missing))
    print(f"Verified {len(exports)} WebGPU exports in {executable.name}.")


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("app", type=Path, help="Final signed .app directory")
    verify(parser.parse_args().app)
