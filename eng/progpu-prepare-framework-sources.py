#!/usr/bin/env python3
"""Prepare exact framework source revisions without recursive repository links."""

import json
from pathlib import Path
import re
import subprocess


ROOT = Path(__file__).resolve().parents[1]
SOURCES = {
    "LibreWPF": ["src/ProGPU.Wpf.Interop", "src/ProGPU.Compatibility", "eng"],
    "LibreWinForms": ["src/ProGPU.Drawing", "eng"],
}


def git(path, *arguments):
    return subprocess.check_output(
        ["git", "-c", "submodule.recurse=false", "-C", str(path), *arguments],
        text=True,
    ).strip()


def prepare(name, revision):
    if name not in SOURCES or re.fullmatch(r"[0-9a-f]{40}", revision) is None:
        raise ValueError("Framework sources require a known repository and full commit SHA")
    path = ROOT / "external" / name
    remote = f"https://github.com/wieslawsoltes/{name}.git"
    if path.is_symlink():
        raise RuntimeError(f"Refusing to change a symbolic-link checkout: {path}")
    if not (path / ".git").is_dir():
        if path.exists() and any(path.iterdir()):
            raise RuntimeError(f"Refusing to replace a nonempty source directory: {path}")
        path.mkdir(parents=True, exist_ok=True)
        git(path, "init", "--quiet")
        git(path, "remote", "add", "origin", remote)
    if git(path, "remote", "get-url", "origin") != remote:
        raise RuntimeError(f"Unexpected framework source remote: {path}")
    if git(path, "status", "--porcelain", "--untracked-files=all"):
        raise RuntimeError(f"Preserve local changes before preparing framework sources: {path}")
    git(path, "sparse-checkout", "init", "--cone")
    git(path, "sparse-checkout", "set", *SOURCES[name])
    git(path, "fetch", "--quiet", "--depth=1", "--filter=blob:none", "origin", revision)
    git(path, "checkout", "--quiet", "--detach", revision)
    if git(path, "rev-parse", "HEAD") != revision:
        raise RuntimeError(f"Framework checkout did not select the exact revision: {path}")
    for relative in SOURCES[name]:
        if not (path / relative).is_dir():
            raise RuntimeError(f"Pinned source directory is missing: {path / relative}")
    print(f"{name}: {revision} ({path})", flush=True)


def main():
    manifest = json.loads((ROOT / "eng/framework-sources.json").read_text())
    if manifest.get("schemaVersion") != 1 or set(manifest.get("repositories", {})) != set(SOURCES):
        raise ValueError("Unsupported framework source manifest")
    # Validate the whole manifest before changing either checkout.
    for revision in manifest["repositories"].values():
        if not isinstance(revision, str) or re.fullmatch(r"[0-9a-f]{40}", revision) is None:
            raise ValueError("Framework sources require full commit SHAs")
    for name, revision in manifest["repositories"].items():
        prepare(name, revision)


if __name__ == "__main__":
    main()
