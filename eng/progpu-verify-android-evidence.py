#!/usr/bin/env python3
"""Verify Android x64 UI evidence; never substitutes for running the APK."""

import argparse
import hashlib
import json
import math
from pathlib import Path
import re
import signal
import struct
import subprocess
import sys
import zipfile
import zlib


PACKAGE = "com.progpu.samples"
WGPU_COMMIT = "33133da4ec5a0174cb21539ef2d3346f75200411"
LOG_LINE = re.compile(
    r"^\d\d-\d\d\s+\d\d:\d\d:\d\d\.\d+\s+(\d+)\s+\d+\s+([VDIWEF])\s+([^:]+):\s*(.*)$"
)
NUMBER = r"[0-9]+(?:\.[0-9]+)?"
FRAME = re.compile(
    r"^First frame: adapter='(.+)', backend=Vulkan, format=([^,]+), "
    r"physical=(\d+)x(\d+), scale=(" + NUMBER + r"), "
    r"surface=(" + NUMBER + r")ms, compositor=(" + NUMBER + r")ms, "
    r"present=(" + NUMBER + r")ms, total=(" + NUMBER + r")ms, "
    r"draws=(\d+), vectors=(\d+), text=(\d+), content=(" + NUMBER + r")x(" + NUMBER + r")\.$"
)


def require(condition, message):
    if not condition:
        raise ValueError(message)


def sha256(data):
    return hashlib.sha256(data).hexdigest()


def elf_x64(data, name):
    require(len(data) >= 64 and data[:4] == b"\x7fELF", f"{name}: not ELF")
    require(data[4:7] == b"\x02\x01\x01", f"{name}: requires ELF64 little-endian version 1")
    require(struct.unpack_from("<HH", data, 16) == (3, 62), f"{name}: requires x86-64 ET_DYN")
    table = struct.unpack_from("<Q", data, 32)[0]
    stride, count = struct.unpack_from("<HH", data, 54)
    require(count > 0 and stride >= 56 and table + count * stride <= len(data), f"{name}: invalid ELF program headers")
    segments = [struct.unpack_from("<IIQQQQQQ", data, table + index * stride) for index in range(count)]
    loads = [segment for segment in segments if segment[0] == 1]
    require(loads and all(segment[2] + segment[5] <= len(data) for segment in segments), f"{name}: truncated ELF segment")
    needed, soname = [], None
    for segment in segments:
        if segment[0] != 2:
            continue
        require(segment[5] % 16 == 0, f"{name}: invalid dynamic segment")
        entries = []
        terminated = False
        for offset in range(segment[2], segment[2] + segment[5], 16):
            tag, value = struct.unpack_from("<qQ", data, offset)
            if tag == 0:
                terminated = True
                break
            entries.append((tag, value))
        require(terminated, f"{name}: unterminated dynamic segment")
        strings = dict(entries)
        if any(tag in (1, 14) for tag, _ in entries):
            address, size = strings.get(5, -1), strings.get(10, 0)
            load = next((part for part in loads if part[3] <= address and address + size <= part[3] + part[5]), None)
            require(load is not None and size > 0, f"{name}: invalid dynamic string table")
            start = load[2] + address - load[3]
            for tag, value in entries:
                if tag not in (1, 14):
                    continue
                require(value < size, f"{name}: invalid dynamic string offset")
                end = data.find(b"\0", start + value, start + size)
                require(end >= 0, f"{name}: unterminated dynamic string")
                text = data[start + value:end].decode("ascii")
                require(text, f"{name}: empty dynamic library name")
                require(not re.search(r"\.so\.[0-9]|^ld-linux|^ld-musl|^libc\.musl", text), f"{name}: desktop ELF dependency {text}")
                if tag == 1:
                    needed.append(text)
                else:
                    soname = text
    return {"needed": needed, "soname": soname, "load_alignment": [part[7] for part in loads]}


def verify_apk(apk, wgpu_root):
    provider = wgpu_root / "lib/x86_64/libwgpu_native.so"
    expected = provider.read_bytes()
    provider_elf = elf_x64(expected, str(provider))
    require(provider_elf["soname"] == "libwgpu_native.so", "Unexpected provider SONAME")
    require(all(alignment == 16384 for alignment in provider_elf["load_alignment"]), "Provider must retain 16 KiB LOAD alignment")
    manifest = dict(line.split("=", 1) for line in (wgpu_root / "BUILD-MANIFEST.txt").read_text().splitlines() if "=" in line)
    require(manifest.get("wgpu-native-commit") == WGPU_COMMIT, "Unexpected wgpu-native commit")
    require(manifest.get("silk-net-webgpu-abi") == "2.23.0", "Unexpected Silk ABI")
    require(manifest.get("android-abis") == "x86_64", "Producer must be an explicitly x64-only build")
    require(manifest.get("runtime-backend") == "vulkan", "Producer must preserve Vulkan")
    hashes = {}
    for line in (wgpu_root / "SHA256SUMS").read_text().splitlines():
        digest, relative = line.split("  ", 1)
        relative_path = Path(relative)
        require(not relative_path.is_absolute() and ".." not in relative_path.parts, "Unsafe native manifest path")
        require(re.fullmatch(r"[0-9a-f]{64}", digest), "Invalid native manifest digest")
        require(sha256((wgpu_root / relative_path).read_bytes()) == digest, f"Native manifest mismatch: {relative}")
        hashes[relative] = digest
    require(hashes.get("lib/x86_64/libwgpu_native.so") == sha256(expected), "Provider is missing from native hashes")
    require("BUILD-MANIFEST.txt" in hashes, "Build provenance is missing from native hashes")
    native = []
    with zipfile.ZipFile(apk) as archive:
        names = archive.namelist()
        require(len(names) == len(set(names)), "APK contains duplicate entries")
        for name in names:
            require(not name.endswith(("libwebgpu_dawn.so", "libprogpu_native_dawn.so")), "UI-only gate must not carry Dawn or native-engine providers")
            if name.startswith("lib/") and not name.endswith("/"):
                require(name.startswith("lib/x86_64/"), f"Wrong ABI in APK: {name}")
            if name.endswith(".so"):
                require(name.startswith("lib/x86_64/"), f"Native library outside x64 APK layout: {name}")
                data = archive.read(name)
                elf = elf_x64(data, name)
                native.append({"entry": name, "sha256": sha256(data), "bytes": len(data), "elf": elf})
        packaged = archive.read("lib/x86_64/libwgpu_native.so")
        require(packaged == expected, "APK provider differs from the verified staged Android provider")
    return {"scope": "Android x64 UI-only wgpu-native; not media/native-engine qualification",
            "apk_sha256": sha256(apk.read_bytes()), "provider_sha256": sha256(expected),
            "native_libraries": native, "entries": names, "producer": manifest}


def first_frame(log, pid):
    frames = []
    for line in log.splitlines():
        parsed = LOG_LINE.match(line)
        if not parsed or parsed[1] != str(pid):
            continue
        level, tag, message = parsed[2], parsed[3].strip(), parsed[4]
        require(not (level == "F" or (level == "E" and tag in ("ProGPU.Android", "AndroidRuntime"))), f"App error: {line}")
        require(not re.search(r"FATAL EXCEPTION|Fatal signal|Unhandled exception|SIGSEGV", message, re.I), f"App failure: {line}")
        if tag == "ProGPU.Android" and message.startswith("First frame:"):
            match = FRAME.fullmatch(message)
            require(match is not None, f"Unexpected first-frame contract: {message}")
            numbers = [float(value) for value in match.groups()[2:]]
            require(all(math.isfinite(value) for value in numbers), "Nonfinite first-frame metric")
            width, height, scale = numbers[:3]
            draws, vectors, text, content_width, content_height = numbers[7:]
            require(min(width, height, scale, draws, content_width, content_height) > 0, "First frame has no rendered content")
            require(vectors + text > 0, "First frame has no vector/text geometry")
            frames.append({"pid": str(pid), "line": line, "adapter": match[1], "backend": "Vulkan",
                           "physical": [int(width), int(height)], "draws": int(draws),
                           "content": [content_width, content_height]})
    require(len(frames) <= 1, "Ambiguous first-frame evidence for one process")
    return frames[0] if frames else None


def verify_png(data):
    require(data[:8] == b"\x89PNG\r\n\x1a\n", "Screenshot is not PNG")
    offset, dimensions, compressed, ended = 8, None, bytearray(), False
    while offset < len(data):
        require(offset + 12 <= len(data), "Truncated PNG chunk")
        length = struct.unpack_from(">I", data, offset)[0]
        kind = data[offset + 4:offset + 8]
        end = offset + 8 + length
        require(end + 4 <= len(data), "Truncated PNG data")
        payload = data[offset + 8:end]
        require(zlib.crc32(kind + payload) & 0xFFFFFFFF == struct.unpack_from(">I", data, end)[0], "Invalid PNG CRC")
        if kind == b"IHDR":
            require(dimensions is None and offset == 8 and length == 13, "Invalid PNG header")
            width, height, depth, color, compression, filtering, interlace = struct.unpack(">IIBBBBB", payload)
            require(0 < width <= 16384 and 0 < height <= 16384 and width * height <= 20_000_000, "Invalid screenshot dimensions")
            require(depth == 8 and color in (2, 6) and compression == filtering == interlace == 0, "Unsupported screencap PNG format")
            dimensions = (width, height, 3 if color == 2 else 4)
        elif kind == b"IDAT":
            compressed.extend(payload)
        elif kind == b"IEND":
            require(length == 0 and end + 4 == len(data), "Invalid PNG end")
            ended = True
        offset = end + 4
    require(ended and dimensions is not None and compressed, "Incomplete screenshot")
    width, height, channels = dimensions
    size = height * (1 + width * channels)
    inflater = zlib.decompressobj()
    pixels = inflater.decompress(compressed, size + 1)
    require(inflater.eof and not inflater.unused_data and len(pixels) == size, "Invalid screenshot pixel stream")
    require(all(pixels[row * (1 + width * channels)] <= 4 for row in range(height)), "Invalid PNG row filter")
    return {"width": width, "height": height, "sha256": sha256(data)}


def stage_apk(build_log, destination, project_directory):
    decoder = json.JSONDecoder()
    documents = []
    for match in re.finditer(r'\{\s*"Properties"\s*:', build_log):
        try:
            document, _ = decoder.raw_decode(build_log[match.start():])
            if "ApkFileSigned" in document.get("Properties", {}):
                documents.append(document)
        except json.JSONDecodeError:
            continue
    require(len(documents) == 1, "Build did not emit one signed-APK metadata result")
    source = Path(documents[0]["Properties"]["ApkFileSigned"])
    if not source.is_absolute():
        source = project_directory / source
    source = source.resolve()
    require(source.is_file() and source.suffix == ".apk", "Build did not identify an existing signed APK")
    require(source != destination.resolve(), "Staged APK must not overwrite its build input")
    destination.write_bytes(source.read_bytes())


def run_command(arguments, timeout):
    require(arguments and timeout > 0, "A command and positive timeout are required")
    process = subprocess.Popen(arguments, start_new_session=True)
    try:
        return process.wait(timeout=timeout)
    except subprocess.TimeoutExpired:
        # Only the process group created for this invocation is owned here.
        import os
        os.killpg(process.pid, signal.SIGKILL)
        process.wait(timeout=10)
        print(f"Command exceeded {timeout}s: {arguments[0]}", file=sys.stderr)
        return 124


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    commands = parser.add_subparsers(dest="command", required=True)
    apk = commands.add_parser("apk")
    apk.add_argument("--apk", type=Path, required=True)
    apk.add_argument("--wgpu-root", type=Path, required=True)
    apk.add_argument("--output", type=Path, required=True)
    stage = commands.add_parser("stage-apk")
    stage.add_argument("--build-log", type=Path, required=True)
    stage.add_argument("--destination", type=Path, required=True)
    stage.add_argument("--project-directory", type=Path, required=True)
    frame = commands.add_parser("first-frame")
    frame.add_argument("--log", type=Path, required=True)
    frame.add_argument("--pid", required=True)
    frame.add_argument("--output", type=Path, required=True)
    screenshot = commands.add_parser("screenshot")
    screenshot.add_argument("--png", type=Path, required=True)
    screenshot.add_argument("--activity", type=Path, required=True)
    screenshot.add_argument("--output", type=Path, required=True)
    run = commands.add_parser("run-command")
    run.add_argument("--timeout", type=int, required=True)
    run.add_argument("arguments", nargs=argparse.REMAINDER)
    args = parser.parse_args()
    try:
        if args.command == "run-command":
            return run_command(args.arguments[1:] if args.arguments[:1] == ["--"] else args.arguments, args.timeout)
        if args.command == "stage-apk":
            stage_apk(args.build_log.read_text(errors="replace"), args.destination, args.project_directory)
            return 0
        if args.command == "apk":
            result = verify_apk(args.apk, args.wgpu_root)
        elif args.command == "first-frame":
            require(re.fullmatch(r"[1-9][0-9]*", args.pid), "Invalid application PID")
            result = first_frame(args.log.read_text(errors="replace"), args.pid)
            if result is None:
                return 1
        else:
            activity = args.activity.read_text(errors="replace")
            require(re.search(r"(?:topResumedActivity|mResumedActivity)[^\n]*com\.progpu\.samples/", activity), "Sample is not the resumed activity")
            result = verify_png(args.png.read_bytes())
        args.output.write_text(json.dumps(result, indent=2) + "\n")
        return 0
    except (ValueError, OSError, KeyError, zipfile.BadZipFile, zlib.error) as error:
        print(f"Android evidence rejected: {error}", file=sys.stderr)
        return 2


if __name__ == "__main__":
    sys.exit(main())
