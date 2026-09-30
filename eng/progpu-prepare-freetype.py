#!/usr/bin/env python3
"""Prepare the signed, isolated FreeType dependency; never admit source Display."""

import argparse
import hashlib
import json
import os
from pathlib import Path
import re
import struct
import subprocess
import urllib.request


ROOT = Path(__file__).resolve().parents[1]
PIN = ROOT / "eng/native-freetype.json"
RID_MACHINES = {
    "linux-x64": ("elf", 62), "linux-arm64": ("elf", 183),
    "osx-x64": ("mach", 0x01000007), "osx-arm64": ("mach", 0x0100000C),
    "win-x64": ("coff", 0x8664), "win-arm64": ("coff", 0xAA64),
}
BIGOBJ_CLASS = bytes.fromhex("c7a1bad1eebaa94baf20faf66aa4dcbb8")
LEGAL_FILES = ("LICENSE.TXT", "docs/FTL.TXT", "docs/GPLv2.TXT",
               "src/bdf/README", "src/pcf/README")
LEGAL_PREFIXES = ("src/gzip/zlib.h", "src/base/fthash.c",
                  "include/freetype/internal/fthash.h", "src/autofit/ft-hb-ft.c",
                  "src/autofit/ft-hb-decls.h", "src/autofit/ft-hb-types.h",
                  "src/autofit/hb-script-list.h")


def run(command, *, cwd=None, env=None):
    return subprocess.run(command, cwd=cwd, env=env, check=True,
                          stdout=subprocess.PIPE, stderr=subprocess.PIPE,
                          text=True, timeout=600)


def release_identity(tag_object, commit, author, signature_status, pin):
    if tag_object != pin["tagObject"] or commit != pin["commit"] or author != pin["author"]:
        raise ValueError("FreeType release identity differs from the reviewed pin")
    signatures = []
    for line in signature_status.splitlines():
        if line.startswith("[GNUPG:] VALIDSIG "):
            fields = line.split()
            if len(fields) not in (11, 12) or not re.fullmatch(r"[0-9A-F]{40}", fields[2]):
                raise ValueError("Malformed FreeType signature status")
            # VALIDSIG carries the actual signing fingerprint and, for a
            # subkey signature, the primary fingerprint as its final field.
            signatures.append((fields[2], fields[-1]))
        if line.startswith(tuple("[GNUPG:] " + value for value in
                                 ("BADSIG", "ERRSIG", "REVKEYSIG", "EXPKEYSIG", "EXPSIG"))):
            raise ValueError("FreeType release signature is invalid, expired or revoked")
    if len(signatures) != 1 or pin["signerFingerprint"] not in signatures[0]:
        raise ValueError("FreeType requires one valid signature from the pinned signer")


def object_machine(data):
    if len(data) >= 64 and data[:4] == b"\x7fELF":
        if data[4:6] != b"\x02\x01" or struct.unpack_from("<H", data, 16)[0] != 1:
            raise ValueError("Expected a little-endian ELF64 object")
        return "elf", struct.unpack_from("<H", data, 18)[0]
    if len(data) >= 32 and data[:4] == b"\xcf\xfa\xed\xfe":
        if struct.unpack_from("<I", data, 12)[0] != 1:
            raise ValueError("Expected a thin Mach-O object")
        return "mach", struct.unpack_from("<I", data, 4)[0]
    if len(data) >= 56 and data[:4] == b"\x00\x00\xff\xff":
        if struct.unpack_from("<H", data, 4)[0] < 2 or data[12:28] != BIGOBJ_CLASS:
            raise ValueError("Unexpected anonymous/import COFF payload")
        return "coff", struct.unpack_from("<H", data, 6)[0]
    if len(data) >= 20:
        machine, sections = struct.unpack_from("<HH", data)
        if machine in (0x8664, 0xAA64, 0xA641) and sections > 0:
            if struct.unpack_from("<H", data, 16)[0] != 0 or 20 + sections * 40 > len(data):
                raise ValueError("Truncated or non-object COFF payload")
            return "coff", machine
    raise ValueError("Unknown static-library object format (including fat/bitcode payloads)")


def verify_archive(data, rid):
    if rid not in RID_MACHINES or data[:8] != b"!<arch>\n":
        raise ValueError("Expected an admitted RID and a regular static archive")
    expected = RID_MACHINES[rid]
    cursor, count = 8, 0
    while cursor < len(data):
        if cursor + 60 > len(data):
            raise ValueError("Truncated archive header")
        header = data[cursor:cursor + 60]
        if header[58:60] != b"`\n":
            raise ValueError("Invalid archive member header")
        try:
            name = header[:16].decode("ascii").strip()
            size = int(header[48:58].decode("ascii").strip())
        except (UnicodeError, ValueError) as error:
            raise ValueError("Invalid archive member metadata") from error
        start = cursor + 60
        end = start + size
        if size < 0 or end > len(data):
            raise ValueError("Truncated archive member")
        member = data[start:end]
        if name.startswith("#1/"):
            name_size = int(name[3:])
            if name_size <= 0 or name_size > len(member):
                raise ValueError("Invalid BSD archive name")
            name = member[:name_size].rstrip(b"\0").decode("ascii")
            member = member[name_size:]
        metadata = name in ("/", "//", "/SYM64/", "__.SYMDEF", "__.SYMDEF SORTED")
        if not metadata:
            if object_machine(member) != expected:
                raise ValueError(f"FreeType archive member {name} does not match {rid}")
            count += 1
        cursor = end + size % 2
        if cursor > len(data) or (size % 2 and data[end:cursor] != b"\n"):
            raise ValueError("Invalid archive member padding")
    if count == 0:
        raise ValueError("FreeType archive contains no independently verified objects")
    return count


def legal_prefix(data):
    text = data.decode("utf-8")
    prefix = re.match(r"\s*(?:(?:/\*.*?\*/)|(?://[^\n]*(?:\n|$))|\s)+", text, re.DOTALL)
    if prefix is None or "copyright" not in prefix[0].lower():
        raise ValueError("Missing leading upstream copyright/license comments")
    return prefix[0].encode("utf-8")


def fresh_workspace(value):
    requested = Path(value)
    if not requested.is_absolute() or requested.is_symlink():
        raise ValueError("FreeType requires an explicit nonsymlink absolute workspace")
    workspace = requested.resolve()
    if workspace == Path(workspace.anchor) or workspace == Path.home() or workspace.is_relative_to(ROOT):
        raise ValueError("FreeType source/build must remain outside the ProGPU repository/home/root")
    if workspace.exists() and (not workspace.is_dir() or any(workspace.iterdir())):
        raise ValueError("FreeType requires a fresh empty workspace; existing data is never overwritten")
    workspace.mkdir(parents=True, exist_ok=True)
    return workspace


def prepare(args):
    pin = json.loads(PIN.read_text())
    workspace = fresh_workspace(args.workspace)
    source, build, install = (workspace / name for name in ("source", "build", "install"))
    source.mkdir()
    keyring = workspace / "keyring"
    keyring.mkdir(mode=0o700)
    key_path = workspace / "release-key.asc"
    with urllib.request.urlopen(pin["keyUrl"], timeout=30) as response:
        if not response.url.startswith("https://"):
            raise ValueError("Release key download lost TLS")
        key = response.read(1024 * 1024 + 1)
    if not key or len(key) > 1024 * 1024:
        raise ValueError("Release key is empty or over budget")
    key_path.write_bytes(key)
    environment = dict(os.environ, GNUPGHOME=str(keyring))
    run([args.gpg, "--batch", "--import", str(key_path)], env=environment)
    run(["git", "init", "-q", str(source)])
    run(["git", "-C", str(source), "remote", "add", "origin", pin["repository"]])
    run(["git", "-C", str(source), "fetch", "--depth", "1", "origin", "tag", pin["tag"]])
    tag = run(["git", "-C", str(source), "rev-parse", "refs/tags/" + pin["tag"]]).stdout.strip()
    commit = run(["git", "-C", str(source), "rev-parse", pin["tag"] + "^{}"]).stdout.strip()
    author = run(["git", "-C", str(source), "show", "-s", "--format=%an <%ae>", commit]).stdout.strip()
    signature = run(["git", "-c", "gpg.program=" + args.gpg, "-C", str(source),
                     "verify-tag", "--raw", pin["tag"]], env=environment)
    release_identity(tag, commit, author, signature.stderr, pin)
    run(["git", "-C", str(source), "checkout", "--detach", "-q", commit])
    configure = [args.cmake, "-S", str(source), "-B", str(build), "-G", args.generator,
                 "-DCMAKE_BUILD_TYPE=Release", "-DBUILD_SHARED_LIBS=OFF",
                 "-DCMAKE_POSITION_INDEPENDENT_CODE=ON", "-DCMAKE_INTERPROCEDURAL_OPTIMIZATION=OFF",
                 "-DCMAKE_C_VISIBILITY_PRESET=hidden", "-DCMAKE_POLICY_DEFAULT_CMP0091=NEW",
                 "-DCMAKE_MSVC_RUNTIME_LIBRARY=MultiThreadedDLL", "-DCMAKE_INSTALL_LIBDIR=lib",
                 "-DCMAKE_INSTALL_PREFIX=" + str(install), "-DCMAKE_C_COMPILER=" + args.cc]
    configure.extend("-DFT_DISABLE_" + name + "=TRUE"
                     for name in ("ZLIB", "BZIP2", "PNG", "HARFBUZZ", "BROTLI"))
    if args.rid.startswith("osx-"):
        configure.append("-DCMAKE_OSX_ARCHITECTURES=" + ("arm64" if args.rid.endswith("arm64") else "x86_64"))
    run(configure)
    run([args.cmake, "--build", str(build), "--config", "Release", "--parallel", "2"])
    run([args.cmake, "--install", str(build), "--config", "Release"])
    library = install / "lib" / ("freetype.lib" if args.rid.startswith("win-") else "libfreetype.a")
    payload = library.read_bytes()
    count = verify_archive(payload, args.rid)
    legal = install / "share/progpu-freetype/licenses"
    legal.mkdir(parents=True)
    receipts = []
    for relative in LEGAL_FILES + LEGAL_PREFIXES:
        original = (source / relative).read_bytes()
        data = legal_prefix(original) if relative in LEGAL_PREFIXES else original
        destination = legal / (relative.replace("/", "__") + ".txt")
        destination.write_bytes(data)
        receipts.append({"source": relative, "sourceSha256": hashlib.sha256(original).hexdigest(),
                         "notice": destination.name, "noticeSha256": hashlib.sha256(data).hexdigest()})
    (legal / "NOTICE.txt").write_text(
        "Portions of this software are copyright © 2026 The FreeType Project "
        "(www.freetype.org). All rights reserved.\n"
        "FreeType 2.14.3 is consumed under the FreeType License (FTL); preserve "
        "the included original and contributed-component notices.\n", encoding="utf-8")
    manifest = {"schemaVersion": 1, "rid": args.rid, "pin": pin, "library": str(library),
                "sha256": hashlib.sha256(payload).hexdigest(), "verifiedObjects": count,
                "include": str(install / "include/freetype2"), "notices": receipts,
                "configure": configure, "compiler": run([args.cc, "--version"]).stdout.strip()
                if not args.rid.startswith("win-") else args.cc,
                "qualification": "signed-source-and-static-architecture-only"}
    (install / "progpu-freetype.json").write_text(json.dumps(manifest, indent=2) + "\n")
    print(json.dumps({"rid": args.rid, "manifest": str(install / "progpu-freetype.json"),
                      "verifiedObjects": count, "sha256": manifest["sha256"]}))


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--workspace", required=True)
    parser.add_argument("--rid", choices=RID_MACHINES, required=True)
    parser.add_argument("--cc", required=True)
    parser.add_argument("--generator", default="Ninja")
    parser.add_argument("--cmake", default="cmake")
    parser.add_argument("--gpg", default="gpg")
    args = parser.parse_args()
    try:
        prepare(args)
    except subprocess.CalledProcessError as error:
        print(error.stdout or "", end="")
        print(error.stderr or "", end="")
        raise


if __name__ == "__main__":
    main()
