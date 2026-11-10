#!/usr/bin/env python3
"""Admit the original prepared dependency and emit immutable Rust-link receipts.

Only read-only verification is invoked. Importing the original preparation module
provides its exact signature/archive helpers; no preparation/build/probe is run.
"""
import hashlib
import importlib.util
import json
import shutil
from pathlib import Path
import sys

eng = Path(__file__).resolve().parent / "eng"
spec = importlib.util.spec_from_file_location("progpu_verify_freetype", eng / "progpu-verify-freetype.py")
verify = importlib.util.module_from_spec(spec)
spec.loader.exec_module(verify)

manifest, rid, output_directory = sys.argv[1:]
verify.PREPARE.ROOT = Path(__file__).resolve().parents[1]
result = verify.validate_install(manifest, rid)
receipt = verify.read_json(Path(result["manifest"]))

def inventory_digest(value):
    return hashlib.sha256(json.dumps(value, sort_keys=True, separators=(",", ":")).encode("utf-8")).hexdigest()

result.update({
    "manifestSha256": verify.digest(Path(result["manifest"])),
    "headersSha256": inventory_digest(verify.inventory(Path(result["include"]))),
    "noticesSha256": inventory_digest(verify.inventory(Path(result["notices"]))),
    "pin": receipt["pin"],
    "headerVersion": [2, 14, 3],
})
# Retain private link inputs only after full admission, then compare copied bytes
# against that admitted generation. A concurrent source change cannot substitute
# another archive between verification and the eventual linker input.
output = Path(output_directory)
private_library = output / ("progpu_verified_freetype.lib" if rid.startswith("win-") else "libprogpu_verified_freetype.a")
shutil.copyfile(result["library"], private_library)
if verify.digest(private_library) != result["sha256"]:
    private_library.unlink()
    raise ValueError("Verified FreeType archive changed during private retention")
notice_output = output / "freetype-notices"
if notice_output.exists():
    shutil.rmtree(notice_output)
shutil.copytree(result["notices"], notice_output, symlinks=False)
if inventory_digest(verify.inventory(notice_output)) != result["noticesSha256"]:
    shutil.rmtree(notice_output)
    raise ValueError("Verified FreeType notices changed during private retention")
result["privateLibrary"] = str(private_library)
result["retainedNotices"] = str(notice_output)
print(json.dumps(result, sort_keys=True, separators=(",", ":")))
