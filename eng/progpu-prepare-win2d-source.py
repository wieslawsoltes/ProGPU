#!/usr/bin/env python3
"""Prepare and verify the exact Win2D implementation/sample source oracles."""

from __future__ import annotations

import argparse
import json
import pathlib
import subprocess
import sys


def run(*arguments: str) -> None:
    subprocess.run(arguments, check=True)


def output(*arguments: str) -> str:
    return subprocess.check_output(arguments, text=True).strip()


def prepare_checkout(
    destination: pathlib.Path,
    repository: str,
    commit: str,
    name: str,
) -> None:
    if not (destination / ".git").is_dir():
        destination.parent.mkdir(parents=True, exist_ok=True)
        run(
            "git",
            "clone",
            "--filter=blob:none",
            "--no-checkout",
            repository,
            str(destination),
        )
    if output(
        "git", "-C", str(destination), "status", "--porcelain", "--untracked-files=no"
    ):
        raise SystemExit(f"Refusing to change the modified {name} checkout.")

    run("git", "-C", str(destination), "fetch", "--depth", "1", "origin", commit)
    run("git", "-C", str(destination), "checkout", "--detach", commit)
    actual = output("git", "-C", str(destination), "rev-parse", "HEAD")
    if actual != commit:
        raise SystemExit(f"Expected {name} {commit}, found {actual}.")


def main() -> int:
    repo_root = pathlib.Path(__file__).resolve().parent.parent
    parser = argparse.ArgumentParser()
    parser.add_argument(
        "--lock",
        type=pathlib.Path,
        default=repo_root / "eng/win2d.lock.json",
    )
    parser.add_argument(
        "--implementation",
        type=pathlib.Path,
        default=repo_root / "artifacts/win2d-source",
    )
    parser.add_argument(
        "--samples",
        type=pathlib.Path,
        default=repo_root / "artifacts/win2d-samples-source",
    )
    arguments = parser.parse_args()
    lock = json.loads(arguments.lock.read_text(encoding="utf-8"))

    implementation = lock["implementation"]
    samples = lock["samples"]
    prepare_checkout(
        arguments.implementation,
        str(implementation["repository"]),
        str(implementation["commit"]),
        "Win2D",
    )
    prepare_checkout(
        arguments.samples,
        str(samples["repository"]),
        str(samples["commit"]),
        "Win2D-Samples",
    )
    run(
        sys.executable,
        str(repo_root / "eng/progpu-verify-win2d-source.py"),
        "--implementation",
        str(arguments.implementation),
        "--samples",
        str(arguments.samples),
        "--lock",
        str(arguments.lock),
    )
    return 0


if __name__ == "__main__":
    sys.exit(main())
