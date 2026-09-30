#!/usr/bin/env python3
"""Read-only admission of an exact signed external font dependency installation."""

import argparse
import hashlib
import importlib.util
import json
import os
from pathlib import Path, PurePosixPath
import re
import shutil

SCRIPT = Path(__file__).with_name("progpu-prepare-freetype.py")
SPEC = importlib.util.spec_from_file_location("progpu_prepare_freetype", SCRIPT)
PREPARE = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(PREPARE)


def unique_object(pairs):
    result = {}
    for key, value in pairs:
        if key in result:
            raise ValueError("Duplicate dependency receipt key: " + key)
        result[key] = value
    return result


def canonical(value, *, directory=False):
    path = Path(value)
    # Paths flow into native build-tool argument lists, never generated CMake.
    if not path.is_absolute() or any(char in str(path) for char in "\n\r;,\"$"):
        raise ValueError("Dependency paths must be unambiguous absolute paths")
    resolved = path.resolve(strict=True)
    if path.absolute() != resolved or (not resolved.is_dir() if directory else not resolved.is_file()):
        raise ValueError("Dependency paths must be canonical and not symlinks/junctions")
    return resolved


def digest(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def inventory(root):
    result = []
    for path in sorted(root.rglob("*")):
        if path.is_symlink():
            raise ValueError("Dependency inventory contains a symlink")
        if path.is_file():
            result.append({"path": path.relative_to(root).as_posix(), "sha256": digest(path)})
    return result


def read_json(path):
    if path.stat().st_size > 1024 * 1024:
        raise ValueError("Dependency receipt exceeds its size bound")
    return json.loads(path.read_text(encoding="utf-8"), object_pairs_hook=unique_object)


def exact_json(left, right):
    # Python's bool == int must not admit numeric substitutes for controls.
    return json.dumps(left, sort_keys=True) == json.dumps(right, sort_keys=True)


def verify_configuration(workspace, install):
    cache = canonical(str(workspace / "build/CMakeCache.txt"))
    values = {}
    for line in cache.read_text(encoding="utf-8").splitlines():
        if line and not line.startswith(("#", "//")):
            match = re.fullmatch(r"([^:=]+):[^=]+=(.*)", line)
            if match:
                if match[1] in values:
                    raise ValueError("Duplicate dependency build configuration key")
                values[match[1]] = match[2]
    expected = {"CMAKE_BUILD_TYPE": "Release", "BUILD_SHARED_LIBS": "OFF",
                "CMAKE_POSITION_INDEPENDENT_CODE": "ON", "CMAKE_INTERPROCEDURAL_OPTIMIZATION": "OFF",
                "CMAKE_C_VISIBILITY_PRESET": "hidden", "CMAKE_MSVC_RUNTIME_LIBRARY": "MultiThreadedDLL",
                "CMAKE_INSTALL_LIBDIR": "lib"}
    expected.update({"FT_DISABLE_" + name: "TRUE" for name in ("ZLIB", "BZIP2", "PNG", "HARFBUZZ", "BROTLI")})
    if any(values.get(key) != value for key, value in expected.items()) or \
            Path(values.get("CMAKE_INSTALL_PREFIX", "")).resolve() != install:
        raise ValueError("Dependency build configuration differs from static isolated Release policy")


def package_metadata(validated):
    receipt = read_json(Path(validated["manifest"]))
    return {"schemaVersion": 1, "rid": validated["rid"], "pin": receipt["pin"],
            "library": "progpu_native_freetype.lib" if validated["rid"].startswith("win-") else "libprogpu_native_freetype.a",
            "sha256": validated["sha256"], "notices": inventory(Path(validated["notices"]))}


def verify_product_input(build_value, manifest, rid):
    build = canonical(build_value, directory=True)
    cache = canonical(str(build / "CMakeCache.txt"))
    values = {}
    for line in cache.read_text(encoding="utf-8").splitlines():
        match = re.fullmatch(r"(PROGPU_NATIVE_FREETYPE_(?:MANIFEST|RID)):[^=]+=(.*)", line)
        if match:
            if match[1] in values:
                raise ValueError("Duplicate native product dependency input")
            values[match[1]] = match[2]
    if values.get("PROGPU_NATIVE_FREETYPE_RID") != rid or \
            canonical(values.get("PROGPU_NATIVE_FREETYPE_MANIFEST", "")) != canonical(manifest):
        raise ValueError("Native product was not configured with this exact font dependency")


def verify_source(workspace, pin):
    source = canonical(str(workspace / "source"), directory=True)
    keyring = canonical(str(workspace / "keyring"), directory=True)
    if os.name == "nt":
        program = Path(os.environ.get("ProgramFiles", "C:/Program Files")) / "Git/usr/bin/gpg.exe"
        converter = program.with_name("cygpath.exe")
        if not program.is_file() or not converter.is_file():
            raise ValueError("The admitted Git for Windows GnuPG tools are unavailable")
    else:
        program = shutil.which("gpg")
        converter = None
        if not program:
            raise ValueError("GnuPG is required to revalidate the prepared source release")
    environment = PREPARE.gpg_environment(keyring, converter)
    tag = PREPARE.run(["git", "-C", str(source), "rev-parse", "refs/tags/" + pin["tag"]]).stdout.strip()
    commit = PREPARE.run(["git", "-C", str(source), "rev-parse", "HEAD"]).stdout.strip()
    resolved_tag = PREPARE.run(["git", "-C", str(source), "rev-parse", pin["tag"] + "^{}"]).stdout.strip()
    author = PREPARE.run(["git", "-C", str(source), "show", "-s", "--format=%an <%ae>", commit]).stdout.strip()
    if commit != resolved_tag or PREPARE.run(["git", "-C", str(source), "status", "--porcelain",
                                             "--untracked-files=all"]).stdout.strip():
        raise ValueError("The prepared font release checkout changed")
    signature = PREPARE.run(["git", "-c", "gpg.program=" + str(program), "-C", str(source),
                             "verify-tag", "--raw", pin["tag"]], env=environment)
    PREPARE.release_identity(tag, commit, author, signature.stderr, pin)


def validate_install(value, rid):
    manifest = canonical(value)
    install = manifest.parent
    workspace = install.parent
    if manifest.name != "progpu-freetype.json" or install.name != "install" or \
            workspace == Path(workspace.anchor) or workspace == Path.home() or workspace.is_relative_to(PREPARE.ROOT) or \
            any((parent / ".git").exists() for parent in (workspace, *workspace.parents)):
        raise ValueError("Dependency admission requires the exact external producer installation")
    receipt = read_json(manifest)
    pin = read_json(PREPARE.PIN)
    if type(receipt.get("schemaVersion")) is not int or receipt["schemaVersion"] != 1 or \
            rid not in PREPARE.RID_MACHINES or receipt.get("rid") != rid or not exact_json(receipt.get("pin"), pin):
        raise ValueError("Dependency receipt identity differs from the reviewed pin/RID")
    verify_configuration(workspace, install)
    library = canonical(receipt["library"])
    include = canonical(receipt["include"], directory=True)
    expected_library = install / "lib" / ("freetype.lib" if rid.startswith("win-") else "libfreetype.a")
    if library != expected_library or include != install / "include/freetype2":
        raise ValueError("Dependency receipt redirected an installed input")
    payload = library.read_bytes()
    if hashlib.sha256(payload).hexdigest() != receipt.get("sha256") or \
            type(receipt.get("verifiedObjects")) is not int or \
            PREPARE.verify_archive(payload, rid) != receipt["verifiedObjects"]:
        raise ValueError("Dependency archive identity/architecture changed")
    actual_headers = inventory(include)
    if not actual_headers or receipt.get("headers") != actual_headers or \
            not (include / "ft2build.h").is_file() or not (include / "freetype/freetype.h").is_file():
        raise ValueError("Dependency header inventory changed or is incomplete")
    header = (include / "freetype/freetype.h").read_text(encoding="utf-8")
    version = [re.search(r"^\s*#define\s+FREETYPE_" + name + r"\s+(\d+)\s*$", header, re.MULTILINE)
               for name in ("MAJOR", "MINOR", "PATCH")]
    if any(match is None for match in version) or ".".join(match[1] for match in version) != pin["version"]:
        raise ValueError("Dependency headers do not match the pinned version")
    probes = {"publicApiProbe": {"version": pin["version"], "interpreters": [35, 40],
                                "independentPolicies": True, "invalidPolicyRejected": True},
              "glyphProbe": {"glyphBatchControls": True, "nativeHintsObserved": True,
                             "slotDifferential": True, "nativeFaultAtomicity": True, "fixedWidthTransport": True,
                             "actualDeviceFrame": True, "hintedProjectionSIMD": True, "retainedAnchorPoints": True},
              "transportProbe": {"fixedWidthTransport": True, "exactIntegerDifferential": True, "atomicTailControls": True},
              "cacheProbe": {"boundedOwnedCache": True, "exactGenerationReuse": True, "nativeFaultAtomicity": True}}
    if any(not exact_json(receipt.get(key), expected) for key, expected in probes.items()):
        raise ValueError("Dependency producer controls are missing or incomplete")
    legal = canonical(str(install / "share/progpu-freetype/licenses"), directory=True)
    notices = receipt.get("notices")
    required = PREPARE.LEGAL_FILES + PREPARE.LEGAL_PREFIXES
    if not isinstance(notices, list) or [item.get("source") for item in notices] != list(required):
        raise ValueError("Dependency original notice inventory changed")
    for item in notices:
        expected_name = item["source"].replace("/", "__") + ".txt"
        if item.get("notice") != expected_name or PurePosixPath(expected_name).name != expected_name or \
                digest(canonical(str(legal / expected_name))) != item.get("noticeSha256"):
            raise ValueError("Dependency original notices changed")
        if digest(canonical(str(workspace / "source" / item["source"]))) != item.get("sourceSha256"):
            raise ValueError("Dependency source notice identity changed")
    credit = canonical(str(legal / "NOTICE.txt"))
    if digest(credit) != receipt.get("creditSha256") or "FreeType Project" not in credit.read_text(encoding="utf-8"):
        raise ValueError("Dependency redistribution credit changed")
    if {item["path"] for item in inventory(legal)} != {item["notice"] for item in notices} | {"NOTICE.txt"}:
        raise ValueError("Dependency legal inventory contains undeclared files")
    verify_source(workspace, pin)
    return {"library": library.as_posix(), "include": include.as_posix(), "notices": legal.as_posix(),
            "manifest": manifest.as_posix(), "rid": rid, "sha256": receipt["sha256"]}


def stage_install(value, rid, destination_value):
    validated = validate_install(value, rid)
    destination = canonical(destination_value, directory=True)
    if destination.name != "native" or destination.parent.name != rid:
        raise ValueError("Font dependency staging requires the exact native RID destination")
    sdk = destination / "sdk"
    if sdk.exists():
        canonical(str(sdk), directory=True)
    library_name = "progpu_native_freetype.lib" if rid.startswith("win-") else "libprogpu_native_freetype.a"
    archive = sdk / library_name
    marker = sdk / "progpu-native-font-dependency.json"
    legal = destination / "licenses/freetype"
    if archive.exists() or marker.exists() or legal.exists() or archive.is_symlink() or marker.is_symlink() or legal.is_symlink():
        raise ValueError("Refusing to overwrite an existing font dependency or notices")
    if (destination / "licenses").exists():
        canonical(str(destination / "licenses"), directory=True)
    metadata = package_metadata(validated)
    # All inputs and fresh target identities preflight before any writes. Each
    # native payload is still unqualified until its whole exact-head CI succeeds.
    sdk.mkdir(exist_ok=True)
    legal.parent.mkdir(exist_ok=True)
    shutil.copytree(validated["notices"], legal)
    with archive.open("xb") as output:
        output.write(Path(validated["library"]).read_bytes())
    with marker.open("x", encoding="utf-8") as output:
        output.write(json.dumps(metadata, indent=2) + "\n")
    return {"rid": rid, "library": str(archive), "notices": str(legal), "metadata": str(marker)}


def validate_staged(destination_value, rid):
    destination = canonical(destination_value, directory=True)
    sdk = canonical(str(destination / "sdk"), directory=True)
    marker = canonical(str(sdk / "progpu-native-font-dependency.json"))
    metadata = read_json(marker)
    library_name = "progpu_native_freetype.lib" if rid.startswith("win-") else "libprogpu_native_freetype.a"
    if type(metadata.get("schemaVersion")) is not int or metadata["schemaVersion"] != 1 or \
            metadata.get("rid") != rid or metadata.get("library") != library_name or \
            not exact_json(metadata.get("pin"), read_json(PREPARE.PIN)):
        raise ValueError("Staged font dependency identity differs from the reviewed pin/RID")
    archive = canonical(str(sdk / library_name))
    if digest(archive) != metadata.get("sha256"):
        raise ValueError("Staged font archive hash changed")
    PREPARE.verify_archive(archive.read_bytes(), rid)
    legal = canonical(str(destination / "licenses/freetype"), directory=True)
    expected_names = {name.replace("/", "__") + ".txt" for name in PREPARE.LEGAL_FILES + PREPARE.LEGAL_PREFIXES} | {"NOTICE.txt"}
    actual = inventory(legal)
    if not exact_json(actual, metadata.get("notices")) or {item["path"] for item in actual} != expected_names or \
            "FreeType Project" not in (legal / "NOTICE.txt").read_text(encoding="utf-8"):
        raise ValueError("Staged font notices are incomplete or changed")
    return {"rid": rid, "sha256": metadata["sha256"]}


def validate_package(root_value, require):
    root = canonical(root_value, directory=True)
    destinations = [(rid, root / "runtimes" / rid / "native") for rid in PREPARE.RID_MACHINES]
    present = any((destination / "sdk/progpu-native-font-dependency.json").exists() or
                  (destination / "licenses/freetype").exists() or
                  (destination / "sdk" / ("progpu_native_freetype.lib" if rid.startswith("win-") else "libprogpu_native_freetype.a")).exists()
                  for rid, destination in destinations)
    return [validate_staged(str(destination), rid) for rid, destination in destinations] if require or present else []


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    inputs = parser.add_mutually_exclusive_group(required=True)
    inputs.add_argument("--manifest")
    inputs.add_argument("--package-root")
    parser.add_argument("--rid", choices=PREPARE.RID_MACHINES)
    parser.add_argument("--native-destination")
    parser.add_argument("--build-directory")
    parser.add_argument("--metadata-only", action="store_true")
    parser.add_argument("--require", action="store_true")
    args = parser.parse_args()
    if args.package_root:
        if args.rid or args.native_destination or args.metadata_only or args.build_directory:
            parser.error("Package admission does not accept producer arguments")
        result = validate_package(args.package_root, args.require)
    else:
        if not args.rid or args.require or (args.native_destination and args.metadata_only):
            parser.error("Producer admission requires an exact RID and one output mode")
        if args.build_directory:
            verify_product_input(args.build_directory, args.manifest, args.rid)
        result = stage_install(args.manifest, args.rid, args.native_destination) if args.native_destination \
            else validate_install(args.manifest, args.rid)
        if args.metadata_only:
            result = package_metadata(result)
    print(json.dumps(result))


if __name__ == "__main__":
    main()
