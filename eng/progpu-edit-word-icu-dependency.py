#!/usr/bin/env python3
"""Record/stage the reviewed private ICU build; never fetch or execute native code."""

import argparse
import hashlib
import importlib.util
import json
from pathlib import Path
import re
import shutil
import tarfile


ROOT = Path(__file__).resolve().parents[1]
PIN = ROOT / "eng/native-edit-word-icu.json"
MARKER = "progpu-native-edit-word-dependency.json"
SPEC = importlib.util.spec_from_file_location(
    "progpu_archive_architecture", Path(__file__).with_name("progpu-prepare-freetype.py"))
ARCHITECTURE = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(ARCHITECTURE)
RIDS = tuple(ARCHITECTURE.RID_MACHINES)
EMBED_SPEC = importlib.util.spec_from_file_location(
    "progpu_edit_icu_embedding", Path(__file__).with_name("progpu-embed-edit-word-icu-data.py"))
EMBED = importlib.util.module_from_spec(EMBED_SPEC)
EMBED_SPEC.loader.exec_module(EMBED)


def unique_object(pairs):
    result = {}
    for key, value in pairs:
        if key in result:
            raise ValueError("Duplicate ICU receipt key: " + key)
        result[key] = value
    return result


def read_json(path):
    if path.stat().st_size > 1024 * 1024:
        raise ValueError("ICU receipt exceeds its size bound")
    return json.loads(path.read_text(encoding="utf-8"), object_pairs_hook=unique_object)


def stream_digest(stream):
    checksum = hashlib.sha256()
    for chunk in iter(lambda: stream.read(1024 * 1024), b""):
        checksum.update(chunk)
    return checksum.hexdigest()


def digest(path):
    with path.open("rb") as stream:
        return stream_digest(stream)


def exact_json(left, right):
    return json.dumps(left, sort_keys=True) == json.dumps(right, sort_keys=True)


def canonical(value, *, directory=False):
    path = Path(value)
    if not path.is_absolute() or any(c in str(path) for c in '\n\r;"$'):
        raise ValueError("ICU inputs require unambiguous absolute paths")
    resolved = path.resolve(strict=True)
    if path != resolved or (not path.is_dir() if directory else not path.is_file()):
        raise ValueError("ICU inputs must be canonical, not symlinks/junctions")
    return resolved


def library_name(rid):
    if rid not in RIDS:
        raise ValueError("Unsupported private ICU RID")
    return "progpu_native_edit_icu.lib" if rid.startswith("win-") else "libprogpu_native_edit_icu.a"


def source_identity(archive, extracted, pin):
    """Verify every used upstream common/header/stub/data input against the pin."""
    archive = canonical(archive)
    extracted = canonical(extracted, directory=True)
    if digest(archive) != pin["sourceSha256"]:
        raise ValueError("ICU source archive differs from the reviewed release")
    selected = set()
    with tarfile.open(archive, "r:gz") as source:
        for entry in source:
            name = entry.name.removeprefix("./")
            if not (name in ("icu/LICENSE", pin["dataPath"]) or
                    name.startswith(("icu/source/common/", "icu/source/stubdata/"))):
                continue
            if entry.isdir():
                continue
            if not entry.isfile() or ".." in Path(name).parts or name in selected:
                raise ValueError("Unexpected ICU source inventory entry")
            selected.add(name)
            actual = canonical(extracted / name)
            with source.extractfile(entry) as original:
                if digest(actual) != stream_digest(original):
                    raise ValueError("Modified ICU source input: " + name)
    required = {"icu/LICENSE", pin["dataPath"], "icu/source/common/sources.txt",
                "icu/source/stubdata/stubdata.cpp"}
    if not required.issubset(selected) or digest(extracted / "icu/LICENSE") != pin["licenseSha256"] or \
            digest(extracted / pin["dataPath"]) != pin["dataSha256"]:
        raise ValueError("Incomplete ICU source/data/original-notice identity")
    # An extra header can shadow an original include even if every original file
    # is intact. Reject additions in the directories compiled by the product.
    for relative in ("icu/source/common", "icu/source/stubdata"):
        for path in (extracted / relative).rglob("*"):
            if path.is_symlink() or (path.is_file() and path.relative_to(extracted).as_posix() not in selected):
                raise ValueError("Unexpected added ICU compilation input")


def cache_inputs(build, archive):
    values = {}
    for line in (build / "CMakeCache.txt").read_text(encoding="utf-8").splitlines():
        match = re.fullmatch(r"([^:=]+):[^=]+=(.*)", line)
        if match:
            if match[1] in values:
                raise ValueError("Duplicate ICU build configuration key")
            values[match[1]] = match[2]
    if canonical(values.get("PROGPU_NATIVE_EDIT_WORD_ICU_SOURCE_ARCHIVE", "")) != canonical(archive):
        raise ValueError("Native build did not select this exact ICU source archive")
    return values


def embedded_identity(build, pin):
    data = (build / "edit-word-icu-source" / pin["dataPath"]).read_bytes()
    if hashlib.sha256(data).hexdigest() != pin["dataSha256"]:
        raise ValueError("ICU original release data changed before embedding verification")
    expected = hashlib.sha256()
    for chunk in EMBED.source_chunks(data):
        expected.update(chunk.encode("utf-8"))
    actual = canonical(build / "progpu_edit_icu_data.cpp")
    if digest(actual) != expected.hexdigest():
        raise ValueError("Generated ICU data source differs from the exact original-data embedding")
    return expected.hexdigest()


def record(args):
    pin = read_json(PIN)
    build = canonical(args.build_directory, directory=True)
    archive = canonical(args.source_archive)
    library = canonical(args.library)
    if library.name != library_name(args.rid) or library.parent not in (build, build / args.configuration):
        raise ValueError("Unexpected private ICU build output")
    cache = cache_inputs(build, archive)
    source_identity(archive, build / "edit-word-icu-source", pin)
    embedded_hash = embedded_identity(build, pin)
    objects = ARCHITECTURE.verify_archive(library.read_bytes(), args.rid)
    metadata = {
        "schemaVersion": 1, "rid": args.rid, "pin": pin,
        "library": library.name, "sha256": digest(library), "verifiedObjects": objects,
        "embeddedSourceSha256": embedded_hash,
        "notices": [{"path": "LICENSE", "sha256": pin["licenseSha256"]}],
        "producer": {"compilerId": args.compiler_id, "compilerVersion": args.compiler_version,
                     "compilerTarget": args.compiler_target, "configuration": args.configuration,
                     "generator": cache.get("CMAKE_GENERATOR", ""), "cxxStandard": 20,
                     "privateStatic": True, "symbolSuffix": pin["symbolSuffix"]},
    }
    validate_metadata(metadata, args.rid, pin)
    marker = library.parent / MARKER
    temporary = marker.with_suffix(".json.tmp")
    temporary.write_text(json.dumps(metadata, indent=2) + "\n", encoding="utf-8")
    temporary.replace(marker)


def validate_metadata(metadata, rid, pin):
    producer = metadata.get("producer", {})
    if type(metadata.get("schemaVersion")) is not int or metadata["schemaVersion"] != 1 or \
            metadata.get("rid") != rid or not exact_json(metadata.get("pin"), pin) or \
            metadata.get("library") != library_name(rid) or \
            not re.fullmatch(r"[0-9a-f]{64}", metadata.get("sha256", "")) or \
            not re.fullmatch(r"[0-9a-f]{64}", metadata.get("embeddedSourceSha256", "")) or \
            type(metadata.get("verifiedObjects")) is not int or metadata["verifiedObjects"] <= 0 or \
            not exact_json(metadata.get("notices"), [{"path": "LICENSE", "sha256": pin["licenseSha256"]}]) or \
            producer.get("compilerId") not in ("Clang", "AppleClang", "GNU", "MSVC") or \
            not isinstance(producer.get("compilerVersion"), str) or not producer["compilerVersion"] or \
            not isinstance(producer.get("compilerTarget"), str) or \
            not isinstance(producer.get("generator"), str) or not producer["generator"] or \
            producer.get("configuration") not in ("Debug", "Release", "RelWithDebInfo", "MinSizeRel") or \
            type(producer.get("cxxStandard")) is not int or producer["cxxStandard"] != 20 or \
            producer.get("privateStatic") is not True or producer.get("symbolSuffix") != pin["symbolSuffix"]:
        raise ValueError("Private ICU producer identity differs from the reviewed pin/RID/policy")


def validate_payload(metadata, rid, pin, library, license_path, *, release=True):
    validate_metadata(metadata, rid, pin)
    if release and metadata["producer"]["configuration"] != "Release":
        raise ValueError("Packaged ICU requires a Release producer")
    library = canonical(library)
    license_path = canonical(license_path)
    if digest(library) != metadata["sha256"] or \
            ARCHITECTURE.verify_archive(library.read_bytes(), rid) != metadata["verifiedObjects"] or \
            digest(license_path) != pin["licenseSha256"]:
        raise ValueError("Private ICU archive architecture/hash or original notices changed")


def stage(args):
    pin = read_json(PIN)
    build = canonical(args.build_directory, directory=True)
    binary = canonical(args.binary_directory or build, directory=True)
    if binary not in (build, build / "Release"):
        raise ValueError("Unexpected private ICU binary directory")
    cache_inputs(build, args.source_archive)
    source_identity(args.source_archive, build / "edit-word-icu-source", pin)
    metadata = read_json(binary / MARKER)
    if embedded_identity(build, pin) != metadata.get("embeddedSourceSha256"):
        raise ValueError("Private ICU producer embedded-source identity changed")
    library = binary / library_name(args.rid)
    license_path = build / "edit-word-icu-source/icu/LICENSE"
    validate_payload(metadata, args.rid, pin, library, license_path)
    if args.native_destination is None:
        return
    destination = canonical(args.native_destination, directory=True)
    if destination.name != "native" or destination.parent.name != args.rid or destination.parent.parent.name != "runtimes":
        raise ValueError("ICU staging requires the exact RID native destination")
    targets = (destination / "sdk" / library.name, destination / "sdk" / MARKER,
               destination / "licenses/edit-word-icu")
    if any(path.exists() or path.is_symlink() for path in targets):
        raise ValueError("Refusing to overwrite an existing ICU staged dependency")
    (destination / "sdk").mkdir(exist_ok=True)
    targets[2].mkdir(parents=True)
    shutil.copyfile(library, targets[0])
    shutil.copyfile(license_path, targets[2] / "LICENSE")
    # Publish the marker last; partial copies are never an admitted dependency.
    shutil.copyfile(binary / MARKER, targets[1])


def verify_package(root, required):
    root = canonical(root, directory=True)
    pin = read_json(PIN)
    present = []
    for rid in RIDS:
        native = root / "runtimes" / rid / "native"
        present.append(any(path.exists() for path in (native / "sdk" / MARKER,
                       native / "sdk" / library_name(rid), native / "licenses/edit-word-icu")))
    if not required and not any(present):
        return
    if not all(present):
        raise ValueError("Private ICU package requires complete inputs for all six desktop RIDs")
    for rid in RIDS:
        native = root / "runtimes" / rid / "native"
        validate_payload(read_json(native / "sdk" / MARKER), rid, pin,
                         native / "sdk" / library_name(rid), native / "licenses/edit-word-icu/LICENSE")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    commands = parser.add_subparsers(dest="command", required=True)
    produce = commands.add_parser("record")
    produce.add_argument("--library", required=True)
    produce.add_argument("--configuration", required=True)
    produce.add_argument("--compiler-id", required=True)
    produce.add_argument("--compiler-version", required=True)
    produce.add_argument("--compiler-target", default="")
    staging = commands.add_parser("stage")
    staging.add_argument("--binary-directory")
    staging.add_argument("--native-destination")
    for command in (produce, staging):
        command.add_argument("--build-directory", required=True)
        command.add_argument("--source-archive", required=True)
        command.add_argument("--rid", required=True, choices=RIDS)
    package = commands.add_parser("verify-package")
    package.add_argument("--package-root", required=True)
    package.add_argument("--require", action="store_true")
    args = parser.parse_args()
    if args.command == "record":
        record(args)
    elif args.command == "stage":
        stage(args)
    else:
        verify_package(args.package_root, args.require)


if __name__ == "__main__":
    main()
