#!/usr/bin/env python3
"""Generate the native MIL decoder coverage ledger from implementation source."""

from __future__ import annotations

import argparse
import hashlib
import json
import re
import sys
from pathlib import Path


PROGPU_ROOT = Path(__file__).resolve().parent.parent
DEFAULT_PROTOCOL = PROGPU_ROOT / "eng/mil/wpf-mil-protocol.json"
DEFAULT_DECODER = (
    PROGPU_ROOT / "src/ProGPU.Native/src/Mil/progpu_native_mil.cpp"
)
DEFAULT_LEDGER = PROGPU_ROOT / "eng/mil/native-mil-command-coverage.json"

TOP_LEVEL_BEGIN = "    status apply_command("
TOP_LEVEL_END = "\n\n    struct shallow_fill_leaf"
RENDER_DATA_BEGIN = "    status append_render_stream("
RENDER_DATA_END = "\n\n    static void intersect_scope_clip"
CASE_PATTERN = re.compile(r"case\s+command::([a-z0-9_]+)\s*:")
COMMAND_REFERENCE_PATTERN = re.compile(r"command::([a-z0-9_]+)")


def sha256(path: Path) -> str:
    return hashlib.sha256(path.read_bytes()).hexdigest()


def extract_region(source: str, begin: str, end: str) -> str:
    begin_offset = source.find(begin)
    if begin_offset < 0:
        raise ValueError(f"Native MIL coverage anchor is missing: {begin!r}.")
    end_offset = source.find(end, begin_offset)
    if end_offset < 0:
        raise ValueError(f"Native MIL coverage anchor is missing: {end!r}.")
    return source[begin_offset:end_offset]


def unique_matches(pattern: re.Pattern[str], source: str) -> set[str]:
    return {match.group(1) for match in pattern.finditer(source)}


def build_ledger(protocol_path: Path, decoder_path: Path) -> dict[str, object]:
    protocol = json.loads(protocol_path.read_text(encoding="utf-8"))
    commands = protocol.get("commands")
    if not isinstance(commands, list) or not commands:
        raise ValueError("The MIL protocol manifest has no command table.")

    decoder_source = decoder_path.read_text(encoding="utf-8")
    top_level_region = extract_region(
        decoder_source, TOP_LEVEL_BEGIN, TOP_LEVEL_END
    )
    render_data_region = extract_region(
        decoder_source, RENDER_DATA_BEGIN, RENDER_DATA_END
    )
    top_level = unique_matches(CASE_PATTERN, top_level_region)
    render_data_references = unique_matches(
        COMMAND_REFERENCE_PATTERN, render_data_region
    )

    command_names = {
        str(entry["name"])
        for entry in commands
        if isinstance(entry, dict) and "name" in entry
    }
    unknown_top_level = top_level - command_names
    unknown_render_data = render_data_references - command_names
    if unknown_top_level or unknown_render_data:
        raise ValueError(
            "The native decoder references commands absent from the WPF "
            "manifest: "
            f"top-level={sorted(unknown_top_level)}, "
            f"render-data={sorted(unknown_render_data)}."
        )

    by_value = {
        int(entry["value"]): entry
        for entry in commands
        if isinstance(entry, dict)
    }
    try:
        first_render_value = next(
            value
            for value, entry in by_value.items()
            if entry["name"] == "draw_line"
        )
        last_render_value = next(
            value
            for value, entry in by_value.items()
            if entry["name"] == "pop"
        )
    except StopIteration as error:
        raise ValueError(
            "The canonical WPF render-data command range is missing."
        ) from error

    render_data_commands = {
        str(entry["name"])
        for value, entry in by_value.items()
        if first_render_value <= value <= last_render_value
    }
    missing_render_data = render_data_commands - render_data_references
    if missing_render_data:
        raise ValueError(
            "The native render-data compiler does not reference every "
            "canonical WPF opcode: "
            f"{sorted(missing_render_data)}."
        )
    top_level_render_data = top_level & render_data_commands
    if top_level_render_data:
        raise ValueError(
            "Nested WPF render-data opcodes must not be accepted as "
            "top-level channel packets: "
            f"{sorted(top_level_render_data)}."
        )

    ledger_commands: list[dict[str, object]] = []
    counts = {
        "sentinel": 0,
        "topLevelDecoder": 0,
        "nestedRenderDataDispatch": 0,
        "notDispatched": 0,
    }
    for entry in commands:
        if not isinstance(entry, dict):
            raise ValueError("The MIL command table contains a non-object.")
        name = str(entry["name"])
        value = int(entry["value"])
        if name in ("invalid", "validate_structure_order"):
            path = "sentinel"
            counts["sentinel"] += 1
        elif name in top_level:
            path = "top-level-decoder"
            counts["topLevelDecoder"] += 1
        elif name in render_data_commands:
            path = "nested-render-data-dispatch"
            counts["nestedRenderDataDispatch"] += 1
        else:
            path = "not-dispatched"
            counts["notDispatched"] += 1
        ledger_entry: dict[str, object] = {
            "name": name,
            "wpfName": str(entry["wpfName"]),
            "value": value,
            "executionPath": path,
        }
        if "layout" in entry:
            ledger_entry["layout"] = str(entry["layout"])
        ledger_commands.append(ledger_entry)

    return {
        "schemaVersion": 1,
        "meaning": {
            "top-level-decoder": (
                "The transactional channel decoder has an explicit dispatch "
                "case. This proves packet recognition, not full semantic or "
                "pixel parity for every value combination."
            ),
            "nested-render-data-dispatch": (
                "The canonical RenderData opcode is explicitly framed and "
                "dispatched by the retained scene compiler. This does not "
                "promise support for every value or resource combination; "
                "unsupported forms fail closed."
            ),
            "not-dispatched": (
                "No native top-level decoder case exists. The packet is an "
                "explicit parity gap or belongs to a platform/transport "
                "boundary that requires a typed portable replacement."
            ),
            "sentinel": "The value is not a retail packet.",
        },
        "source": {
            "protocol": {
                "path": "eng/mil/wpf-mil-protocol.json",
                "sha256": sha256(protocol_path),
            },
            "decoder": {
                "path": "src/ProGPU.Native/src/Mil/progpu_native_mil.cpp",
                "sha256": sha256(decoder_path),
            },
        },
        "summary": {
            "commandCount": len(ledger_commands),
            **counts,
        },
        "commands": ledger_commands,
    }


def render_ledger(ledger: dict[str, object]) -> str:
    return json.dumps(ledger, indent=2, ensure_ascii=False) + "\n"


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--protocol", type=Path, default=DEFAULT_PROTOCOL)
    parser.add_argument("--decoder", type=Path, default=DEFAULT_DECODER)
    parser.add_argument("--ledger", type=Path, default=DEFAULT_LEDGER)
    parser.add_argument("--check", action="store_true")
    args = parser.parse_args()

    ledger = build_ledger(
        args.protocol.resolve(),
        args.decoder.resolve(),
    )
    ledger_text = render_ledger(ledger)
    if args.check:
        actual = (
            args.ledger.read_text(encoding="utf-8")
            if args.ledger.is_file()
            else None
        )
        if actual != ledger_text:
            raise ValueError(
                f"Generated native MIL coverage ledger is stale: "
                f"{args.ledger}"
            )
        summary = ledger["summary"]
        print(
            "Native MIL coverage ledger is current: "
            f"{summary['topLevelDecoder']} top-level, "
            f"{summary['nestedRenderDataDispatch']} render-data, "
            f"{summary['notDispatched']} not dispatched."
        )
        return 0

    args.ledger.parent.mkdir(parents=True, exist_ok=True)
    args.ledger.write_text(ledger_text, encoding="utf-8")
    summary = ledger["summary"]
    print(
        f"Generated {args.ledger}: "
        f"{summary['topLevelDecoder']} top-level, "
        f"{summary['nestedRenderDataDispatch']} render-data, "
        f"{summary['notDispatched']} not dispatched."
    )
    return 0


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except (FileNotFoundError, ValueError, KeyError, TypeError) as error:
        print(f"error: {error}", file=sys.stderr)
        raise SystemExit(1)
