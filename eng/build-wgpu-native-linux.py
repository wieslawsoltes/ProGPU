#!/usr/bin/env python3
"""Build the reviewed Linux dependency outside the product source tree.

Publish a fresh per-RID payload only after locked compilation, generated ABI
tests, ELF/export checks and exact input hashing. Never replace a consumer DLL,
NuGet cache entry or an existing payload. --verify works on any host.
"""
import argparse
import hashlib
import json
import os
from pathlib import Path
import platform
import shutil
import struct
import subprocess
import tempfile
import zipfile

ROOT = Path(__file__).resolve().parent.parent
INPUTS = ROOT / "eng/wgpu-xlib"
RIDS = {"linux-x64": ("x86_64", 62), "linux-arm64": ("aarch64", 183)}
PAYLOAD_FILES = {"libwgpu_native.so", "LICENSE.MIT", "LICENSE.APACHE", "wgpu-LICENSE.MIT", "wgpu-LICENSE.APACHE", "webgpu-headers-LICENSE"}


def sha(path):
    with Path(path).open("rb") as stream:
        return hashlib.file_digest(stream, "sha256").hexdigest()


def load_inputs():
    pin = json.loads((INPUTS / "build-inputs.json").read_text())
    for name, digest in pin["files"].items():
        if Path(name).name != name or sha(INPUTS / name) != digest:
            raise ValueError(f"Reviewed dependency input changed: {name}")
    return pin


def elf_machine(path, rid):
    with Path(path).open("rb") as stream:
        header = stream.read(64)
    if len(header) != 64 or header[:7] != b"\x7fELF\x02\x01\x01":
        raise ValueError("Runtime must be a complete little-endian ELF64 library")
    kind, machine = struct.unpack_from("<HH", header, 16)
    if kind != 3 or machine != RIDS[rid][1]:
        raise ValueError(f"Runtime ELF architecture/type does not match {rid}")


def verify_payload(payload, rid, pin=None):
    pin = pin or load_inputs()
    payload = Path(payload)
    receipt = json.loads((payload / "wgpu-native-linux.json").read_text())
    if receipt.get("schemaVersion") != 1 or receipt.get("rid") != rid or receipt.get("inputs") != pin:
        raise ValueError("Linux runtime receipt has different source, patch or architecture inputs")
    if receipt.get("abiTestsPassed") is not True or receipt.get("extensionExported") != pin["extension"]:
        raise ValueError("Linux runtime lacks ABI/export validation")
    hashes = receipt.get("files", {})
    if set(hashes) != PAYLOAD_FILES:
        raise ValueError("Linux runtime inventory is incomplete or contains unexpected files")
    if {p.name for p in payload.iterdir()} != PAYLOAD_FILES | {"wgpu-native-linux.json"}:
        raise ValueError("Linux runtime directory contains unrecorded files")
    for name, digest in hashes.items():
        path = payload / name
        if path.is_symlink() or not path.is_file() or sha(path) != digest:
            raise ValueError(f"Linux runtime file does not match its producer: {name}")
    elf_machine(payload / "libwgpu_native.so", rid)
    return receipt


def verify_package(package, rid):
    prefix = f"runtimes/{rid}/native/"
    expected = PAYLOAD_FILES | {"wgpu-native-linux.json"}
    with zipfile.ZipFile(package) as archive, tempfile.TemporaryDirectory(prefix="progpu-xlib-verify-") as temporary:
        entries = [item for item in archive.infolist() if item.filename.startswith(prefix)]
        if len(entries) != len(expected) or {item.filename[len(prefix):] for item in entries} != expected:
            raise ValueError("Linux package runtime inventory is missing, duplicated or unexpected")
        for item in entries:
            if item.file_size > 150 * 1024 * 1024:
                raise ValueError("Linux runtime package entry exceeds the payload bound")
            (Path(temporary) / item.filename[len(prefix):]).write_bytes(archive.read(item))
        return verify_payload(Path(temporary), rid)


def run(args, *, cwd=None, env=None, timeout=900):
    print("+", " ".join(map(str, args)), flush=True)
    subprocess.run(list(map(str, args)), cwd=cwd, env=env, check=True, timeout=timeout)


def capture(args, *, cwd=None):
    return subprocess.check_output(list(map(str, args)), cwd=cwd, text=True, timeout=60).strip()


def checkout(url, revision, destination):
    run(["git", "init", destination])
    run(["git", "remote", "add", "origin", url], cwd=destination)
    run(["git", "fetch", "--depth=1", "origin", revision], cwd=destination)
    run(["git", "checkout", "--detach", "FETCH_HEAD"], cwd=destination)
    if capture(["git", "rev-parse", "HEAD"], cwd=destination) != revision:
        raise ValueError("Dependency checkout did not resolve the pinned commit")


def build(rid, work_root, output_root, libclang):
    pin = load_inputs()
    if platform.system() != "Linux" or platform.machine() != RIDS[rid][0]:
        raise ValueError("Build on the matching native Linux architecture; cross builds do not qualify this payload")
    output = output_root / rid / "native"
    if output.exists():
        raise ValueError(f"Refusing to replace existing payload: {output}")
    libclang = libclang.resolve(strict=True)
    clang_version = capture(["clang-18", "--version"])
    if "clang version 18." not in clang_version:
        raise ValueError("Pinned bindgen requires Clang 18")
    # Resolve the actual requested libclang, not an ambient newer installation.
    import ctypes
    clang = ctypes.CDLL(str(libclang))
    class CXString(ctypes.Structure):
        _fields_ = [("data", ctypes.c_void_p), ("flags", ctypes.c_uint)]
    clang.clang_getClangVersion.restype = CXString
    clang.clang_getCString.argtypes = [CXString]
    clang.clang_getCString.restype = ctypes.c_char_p
    clang.clang_disposeString.argtypes = [CXString]
    version_string = clang.clang_getClangVersion()
    try:
        libclang_version = clang.clang_getCString(version_string).decode()
    finally:
        clang.clang_disposeString(version_string)
    if "clang version 18." not in libclang_version:
        raise ValueError("Selected libclang is not major version 18")
    rust_version = capture(["rustc", f"+{pin['rustVersion']}", "--version"])
    if not rust_version.startswith(f"rustc {pin['rustVersion']} "):
        raise ValueError("Rust toolchain does not match the reviewed input")
    work_root.mkdir(parents=True, exist_ok=True)
    work = Path(tempfile.mkdtemp(prefix=f"{rid}-", dir=work_root))
    # Retain failed sources/logs for diagnosis. Successful work can be removed by
    # the caller after the immutable payload and receipts have been archived.
    native, wgpu = work / "wgpu-native", work / "wgpu"
    checkout("https://github.com/gfx-rs/wgpu-native.git", pin["nativeRevision"], native)
    checkout("https://github.com/gfx-rs/wgpu.git", pin["wgpuRevision"], wgpu)
    run(["git", "submodule", "update", "--init", "ffi/webgpu-headers"], cwd=native)
    if capture(["git", "rev-parse", "HEAD"], cwd=native / "ffi/webgpu-headers") != pin["headersRevision"]:
        raise ValueError("WebGPU header revision mismatch")
    run(["git", "apply", "--check", INPUTS / "wgpu-xlib-owned.patch"], cwd=wgpu)
    run(["git", "apply", INPUTS / "wgpu-xlib-owned.patch"], cwd=wgpu)
    run(["git", "apply", "--check", INPUTS / "wgpu-native-xlib-entry.patch"], cwd=native)
    run(["git", "apply", INPUTS / "wgpu-native-xlib-entry.patch"], cwd=native)
    config = native / ".cargo/config.toml"
    config.parent.mkdir(exist_ok=True)
    config.write_text('[patch."https://github.com/gfx-rs/wgpu"]\n' + "".join(
        f'{crate} = {{ path = {json.dumps(str(wgpu / crate))} }}\n'
        for crate in ("d3d12", "naga", "wgpu-core", "wgpu-hal", "wgpu-types")))
    if sha(native / "Cargo.lock") != pin["lockSha256"]:
        raise ValueError("Patched dependency lock does not match the reviewed inventory")
    clang_directory = work / "libclang"
    clang_directory.mkdir()
    (clang_directory / "libclang.so").symlink_to(libclang)
    environment = dict(os.environ, LIBCLANG_PATH=str(clang_directory), CARGO_TARGET_DIR=str(work / "target"))
    cargo = ["cargo", f"+{pin['rustVersion']}"]
    run(cargo + ["build", "--release", "--locked"], cwd=native, env=environment)
    run(cargo + ["test", "--release", "--locked", "--lib"], cwd=native, env=environment)
    if sha(native / "Cargo.lock") != pin["lockSha256"]:
        raise ValueError("Compilation changed the reviewed dependency lock")
    library = work / "target/release/libwgpu_native.so"
    elf_machine(library, rid)
    exports = capture(["nm", "-D", "--defined-only", library])
    symbols = {line.split()[-1] for line in exports.splitlines() if line.split()}
    for symbol in (pin["extension"], "wgpuCreateInstance", "wgpuInstanceCreateSurface", "wgpuSurfaceConfigure", "wgpuQueueSubmit"):
        if symbol not in symbols:
            raise ValueError(f"Runtime is missing export {symbol}")
    output.parent.mkdir(parents=True, exist_ok=True)
    candidate = Path(tempfile.mkdtemp(prefix=".native-", dir=output.parent))
    shutil.copy2(library, candidate / library.name)
    for source, prefix in ((native, ""), (wgpu, "wgpu-")):
        for license_name in ("LICENSE.MIT", "LICENSE.APACHE"):
            shutil.copy2(source / license_name, candidate / (prefix + license_name))
    shutil.copy2(native / "ffi/webgpu-headers/LICENSE", candidate / "webgpu-headers-LICENSE")
    receipt = {
        "schemaVersion": 1, "rid": rid, "inputs": pin,
        "abiTestsPassed": True, "extensionExported": pin["extension"],
        "rustVersion": rust_version, "clangVersion": clang_version,
        "libclangVersion": libclang_version, "libclangSha256": sha(libclang),
        "elfVersionRequirements": capture(["readelf", "--version-info", library]),
        "producerCommit": capture(["git", "rev-parse", "HEAD"], cwd=ROOT),
        "files": {name: sha(candidate / name) for name in sorted(PAYLOAD_FILES)},
    }
    (candidate / "wgpu-native-linux.json").write_text(json.dumps(receipt, indent=2) + "\n")
    verify_payload(candidate, rid, pin)
    candidate.rename(output)
    print(f"Verified Linux runtime: {output}", flush=True)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--rid", choices=RIDS, required=True)
    parser.add_argument("--verify", type=Path, help="Verify an existing per-RID native directory without executing it")
    parser.add_argument("--verify-package", type=Path, help="Verify the real packed per-RID runtime and receipt")
    parser.add_argument("--work-root", type=Path, default=ROOT / "artifacts/wgpu-native-linux")
    parser.add_argument("--output-root", type=Path, default=ROOT / "artifacts/progpu-xlib/package")
    parser.add_argument("--libclang", type=Path, default=Path("/usr/lib/llvm-18/lib/libclang.so.1"))
    args = parser.parse_args()
    if args.verify and args.verify_package:
        parser.error("Choose directory or package verification, not both")
    if args.verify_package:
        verify_package(args.verify_package, args.rid)
        print(f"Verified packaged {args.rid}: {args.verify_package}")
    elif args.verify:
        verify_payload(args.verify, args.rid)
        print(f"Verified {args.rid}: {args.verify}")
    else:
        build(args.rid, args.work_root.resolve(), args.output_root.resolve(), args.libclang)


if __name__ == "__main__":
    main()
