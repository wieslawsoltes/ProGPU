#!/usr/bin/env python3
"""Verify and stage owned Windows wgpu inputs; never alter consumer/cache DLLs."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import shutil
import struct
import subprocess
import tempfile
import zipfile

ROOT = Path(__file__).resolve().parent.parent
MACHINES = {"win-x64": 0x8664, "win-arm64": 0xAA64}
DEPENDENCY_FILES = {"wgpu_native.dll", "LICENSE.MIT", "LICENSE.APACHE", "wgpu-LICENSE.MIT", "wgpu-LICENSE.APACHE", "webgpu-headers-LICENSE"}
PAYLOAD_FILES = DEPENDENCY_FILES | {"wgpu-native-build.json"}
RECEIPT = "wgpu-native-windows.json"
REQUIRED_EXPORTS = {"wgpuCreateInstance", "wgpuInstanceRequestAdapter", "wgpuAdapterRequestDevice", "wgpuDeviceCreateShaderModule", "wgpuDeviceCreateComputePipeline", "wgpuGetVersion"}


def sha(path):
    with Path(path).open("rb") as stream:
        return hashlib.file_digest(stream, "sha256").hexdigest()


def load_inputs():
    native = json.loads((ROOT / "eng/progpu-native-wgpu.version.json").read_text())
    build = json.loads((ROOT / "eng/wgpu-dxc/build-inputs.json").read_text())
    shader = json.loads((ROOT / "eng/wgpu-hlsl/build-inputs.json").read_text())
    if sha(ROOT / "eng/wgpu-dxc/Cargo.lock") != build["lockSha256"]:
        raise ValueError("Reviewed Windows dependency lock changed")
    if shader["wgpuRevision"] != build["wgpuRevision"] or sha(ROOT / "eng/wgpu-hlsl/naga-workgroup-zero-init.patch") != shader["patchSha256"]:
        raise ValueError("Reviewed HLSL source revision or patch changed")
    return {"native": native, "build": build, "shader": shader}


def pe_machine(path, rid):
    with Path(path).open("rb") as stream:
        header = stream.read(64)
        if len(header) != 64 or header[:2] != b"MZ":
            raise ValueError("Runtime DOS header is invalid")
        offset = struct.unpack_from("<I", header, 60)[0]
        size = stream.seek(0, 2)
        if offset < 64 or offset > size - 26:
            raise ValueError("Runtime PE header is truncated")
        stream.seek(offset)
        pe = stream.read(26)
        if pe[:4] != b"PE\0\0" or struct.unpack_from("<H", pe, 4)[0] != MACHINES[rid]:
            raise ValueError("Runtime PE architecture differs from selected RID")
        optional_size, flags, magic = struct.unpack_from("<HHH", pe, 20)
        if optional_size < 112 or offset + 24 + optional_size > size or not flags & 0x2000 or magic != 0x20B:
            raise ValueError("Runtime must be a complete PE32+ DLL")


def verify_files(directory, hashes, expected):
    if not isinstance(hashes, dict) or set(hashes) != expected:
        raise ValueError("Runtime hash inventory is incomplete or unexpected")
    for name, digest in hashes.items():
        path = directory / name
        if path.is_symlink() or not path.is_file() or sha(path) != digest:
            raise ValueError(f"Runtime file differs from producer: {name}")


def verify_dependency(directory, rid, inputs=None, exact=True):
    inputs = inputs or load_inputs()
    directory = Path(directory)
    manifest_path = directory / "wgpu-native-build.json"
    if manifest_path.is_symlink():
        raise ValueError("Dependency manifest must not be a link")
    manifest = json.loads(manifest_path.read_text())
    native, build = inputs["native"], inputs["build"]
    expected = {"schemaVersion": 2, "rid": rid, "backendAbi": native["backendAbi"], "nativeRevision": native["revision"],
                "headersRevision": native["webGpuHeadersRevision"], "wgpuRevision": build["wgpuRevision"],
                "compilerFeature": build["feature"], "lockSha256": build["lockSha256"], "shaderInputs": inputs["shader"]}
    if any(manifest.get(key) != value for key, value in expected.items()):
        raise ValueError("Native dependency source, patch or architecture differs from reviewed inputs")
    if not REQUIRED_EXPORTS.issubset(set(manifest.get("exports", []))):
        raise ValueError("Native dependency lacks required export verification")
    verify_files(directory, manifest.get("files"), DEPENDENCY_FILES)
    if manifest.get("librarySha256") != manifest["files"]["wgpu_native.dll"]:
        raise ValueError("Native library hash disagrees with inventory")
    if exact and {p.name for p in directory.iterdir()} != PAYLOAD_FILES:
        raise ValueError("Dependency directory contains unrecorded files")
    pe_machine(directory / "wgpu_native.dll", rid)
    return manifest


def verify_payload(directory, rid, inputs=None):
    inputs = inputs or load_inputs()
    directory = Path(directory)
    receipt_path = directory / RECEIPT
    if receipt_path.is_symlink():
        raise ValueError("Runtime receipt must not be a link")
    receipt = json.loads(receipt_path.read_text())
    if receipt.get("schemaVersion") != 1 or receipt.get("rid") != rid or receipt.get("inputs") != inputs:
        raise ValueError("Runtime receipt differs from reviewed inputs")
    if {p.name for p in directory.iterdir()} != PAYLOAD_FILES | {RECEIPT}:
        raise ValueError("Runtime directory contains missing or unrecorded files")
    verify_files(directory, receipt.get("files"), PAYLOAD_FILES)
    verify_dependency(directory, rid, inputs, exact=False)
    return receipt


def stage(dependency, output, rid):
    inputs = load_inputs()
    verify_dependency(dependency, rid, inputs)
    output = Path(output)
    if output.exists() or output.is_symlink():
        raise ValueError("Runtime output must be a new directory")
    output.parent.mkdir(parents=True, exist_ok=True)
    pending = Path(tempfile.mkdtemp(prefix=".wgpu-windows-", dir=output.parent))
    try:
        # Retain the admitted bytes before the copy, so a concurrent source change fails.
        hashes = {name: sha(Path(dependency) / name) for name in PAYLOAD_FILES}
        for name in PAYLOAD_FILES:
            shutil.copyfile(Path(dependency) / name, pending / name)
        receipt = {"schemaVersion": 1, "rid": rid, "inputs": inputs, "files": hashes, "qualification": "package-input-only"}
        (pending / RECEIPT).write_text(json.dumps(receipt, indent=2) + "\n")
        verify_payload(pending, rid, inputs)
        # Windows rename fails if the destination appeared during staging.
        if output.exists():
            raise ValueError("Runtime output appeared during staging")
        os.rename(pending, output)
    finally:
        if pending.exists():
            shutil.rmtree(pending)
    return receipt


def verify_package(package, rid):
    prefix = f"runtimes/{rid}/native/"
    expected = PAYLOAD_FILES | {RECEIPT}
    with zipfile.ZipFile(package) as archive, tempfile.TemporaryDirectory(prefix="wgpu-windows-verify-") as temporary:
        entries = [entry for entry in archive.infolist() if entry.filename.startswith(prefix)]
        if len(entries) != len(expected) or {entry.filename[len(prefix):] for entry in entries} != expected:
            raise ValueError("Package runtime inventory is missing, duplicated or unexpected")
        for entry in entries:
            if entry.file_size > 150 * 1024 * 1024 or (entry.external_attr >> 16) & 0o170000 == 0o120000:
                raise ValueError("Package runtime entry exceeds bound or is a link")
            (Path(temporary) / entry.filename[len(prefix):]).write_bytes(archive.read(entry))
        return verify_payload(temporary, rid)


def prepare_source(directory, apply=True):
    """Apply the reviewed patch only to the caller's isolated pinned checkout."""
    inputs = load_inputs()["shader"]
    directory = Path(directory).resolve(strict=True)
    def git(*args):
        return subprocess.check_output(["git", "-C", str(directory), *args], text=True, timeout=60).strip()
    if Path(git("rev-parse", "--show-toplevel")).resolve() != directory or git("rev-parse", "HEAD") != inputs["wgpuRevision"]:
        raise ValueError("Expected the isolated pinned wgpu checkout root")
    if git("ls-files", "--others", "--exclude-standard"):
        raise ValueError("Isolated dependency contains untracked files")
    expected = {entry["path"]: entry for entry in inputs["modifiedFiles"]}
    if set(git("diff", "--name-only", "HEAD").splitlines()) - set(expected):
        raise ValueError("Isolated dependency has unrelated modifications")
    hashes = {name: sha(directory / name) for name in expected}
    if apply and all(hashes[name] == entry["originalSha256"] for name, entry in expected.items()):
        patch = ROOT / "eng/wgpu-hlsl/naga-workgroup-zero-init.patch"
        subprocess.run(["git", "-C", str(directory), "apply", "--check", str(patch)], check=True, timeout=60)
        subprocess.run(["git", "-C", str(directory), "apply", str(patch)], check=True, timeout=60)
    if any(sha(directory / name) != entry["patchedSha256"] for name, entry in expected.items()):
        raise ValueError("Dependency implementation differs from reviewed patched bytes")
    return inputs


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--rid", choices=MACHINES)
    group = parser.add_mutually_exclusive_group(required=True)
    group.add_argument("--verify", type=Path)
    group.add_argument("--verify-dependency", type=Path)
    group.add_argument("--verify-package", type=Path)
    group.add_argument("--stage", type=Path)
    group.add_argument("--prepare-source", type=Path)
    group.add_argument("--verify-source", type=Path)
    parser.add_argument("--output", type=Path)
    args = parser.parse_args()
    if args.prepare_source or args.verify_source:
        result = prepare_source(args.prepare_source or args.verify_source, apply=args.prepare_source is not None)
    else:
        if not args.rid or (args.stage and not args.output):
            parser.error("--rid is required; --stage also requires --output")
        if args.verify:
            result = verify_payload(args.verify, args.rid)
        elif args.verify_dependency:
            result = verify_dependency(args.verify_dependency, args.rid)
        elif args.verify_package:
            result = verify_package(args.verify_package, args.rid)
        else:
            result = stage(args.stage, args.output, args.rid)
    print(json.dumps(result, sort_keys=True))


if __name__ == "__main__":
    main()
