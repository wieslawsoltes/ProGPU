#!/usr/bin/env python3
"""Generate native packed Unicode tables from ProGPU's managed generated data."""

from __future__ import annotations

import argparse
import re
from pathlib import Path


def parse_uint_array(source: str, name: str) -> list[int]:
    match = re.search(
        rf"{re.escape(name)}\s*=\s*\[(?P<body>.*?)\];",
        source,
        re.DOTALL,
    )
    if match is None:
        raise RuntimeError(f"Could not find managed array {name}")
    return [int(value) for value in re.findall(r"\b\d+\b", match.group("body"))]


def parse_hex_uint_array(source: str, name: str) -> list[int]:
    match = re.search(
        rf"{re.escape(name)}\s*=\s*\[(?P<body>.*?)\];",
        source,
        re.DOTALL,
    )
    if match is None:
        raise RuntimeError(f"Could not find managed array {name}")
    return [
        int(value, 16)
        for value in re.findall(r"0x([0-9A-Fa-f]+)", match.group("body"))
    ]


def parse_int_array(source: str, name: str) -> list[int]:
    match = re.search(
        rf"{re.escape(name)}\s*=\s*\[(?P<body>.*?)\];",
        source,
        re.DOTALL,
    )
    if match is None:
        raise RuntimeError(f"Could not find managed array {name}")
    return [int(value) for value in re.findall(r"-?\d+", match.group("body"))]


def parse_int_constant(source: str, name: str) -> int:
    match = re.search(rf"\b{re.escape(name)}\s*=\s*(-?\d+)\s*;", source)
    if match is None:
        raise RuntimeError(f"Could not find managed constant {name}")
    return int(match.group(1))


def parse_ulong_span(source: str, name: str) -> list[int]:
    match = re.search(
        rf"{re.escape(name)}\s*=>\s*\[(?P<body>.*?)\];",
        source,
        re.DOTALL,
    )
    if match is None:
        raise RuntimeError(f"Could not find managed span {name}")
    return [
        int(value, 16)
        for value in re.findall(r"0x([0-9A-Fa-f]+)UL", match.group("body"))
    ]


def parse_uint_span(source: str, name: str) -> list[int]:
    match = re.search(
        rf"{re.escape(name)}\s*=>\s*\[(?P<body>.*?)\];",
        source,
        re.DOTALL,
    )
    if match is None:
        raise RuntimeError(f"Could not find managed span {name}")
    return [int(value) for value in re.findall(r"\b\d+\b", match.group("body"))]


def format_values(values: list[int], per_line: int = 12) -> str:
    return "\n".join(
        "    " + ", ".join(f"{value}U" for value in values[index:index + per_line]) + ","
        for index in range(0, len(values), per_line)
    )


def format_u64_values(values: list[int], per_line: int = 4) -> str:
    return "\n".join(
        "    " + ", ".join(
            f"0x{value:016X}ULL" for value in values[index:index + per_line]
        ) + ","
        for index in range(0, len(values), per_line)
    )


def format_signed_values(values: list[int], per_line: int = 12) -> str:
    return "\n".join(
        "    " + ", ".join(str(value) for value in values[index:index + per_line]) + ","
        for index in range(0, len(values), per_line)
    )


def format_machine(name: str, source: str) -> str:
    arrays = {
        "trans_keys": ("std::uint8_t", parse_uint_array(source, "s_trans_keys")),
        "key_spans": ("std::uint8_t", parse_uint_array(source, "s_key_spans")),
        "index_offsets": ("std::uint16_t", parse_uint_array(source, "s_index_offsets")),
        "indices": ("std::uint8_t", parse_uint_array(source, "s_indicies")),
        "trans_targets": ("std::uint8_t", parse_uint_array(source, "s_trans_targs")),
        "trans_actions": ("std::uint8_t", parse_uint_array(source, "s_trans_actions")),
        "to_state_actions": ("std::uint8_t", parse_uint_array(source, "s_to_state_actions")),
        "from_state_actions": ("std::uint8_t", parse_uint_array(source, "s_from_state_actions")),
        "eof_transitions": ("std::int16_t", parse_int_array(source, "s_eof_trans")),
    }
    blocks: list[str] = []
    for suffix, (kind, values) in arrays.items():
        formatted = format_signed_values(values) if kind == "std::int16_t" else format_values(values)
        blocks.append(
            f"inline constexpr std::array<{kind}, {len(values)}> "
            f"unicode_{name}_{suffix}{{{{\n{formatted}\n}}}};"
        )
    blocks.append(
        f"inline constexpr std::uint16_t unicode_{name}_state_count = "
        f"{len(arrays['key_spans'][1])}U;"
    )
    blocks.append(
        f"inline constexpr std::uint16_t unicode_{name}_start_state = "
        f"{parse_int_constant(source, 'StartState')}U;"
    )
    return "\n\n".join(blocks)


def pack_tag(value: str) -> int:
    if not value:
        return 0
    if len(value) != 4 or any(ord(character) > 0x7F for character in value):
        raise RuntimeError(f"Invalid OpenType script tag {value!r}")
    result = 0
    for character in value:
        result = result << 8 | ord(character)
    return result


def generate(root: Path) -> str:
    script_path = root / "src/ProGPU.Text/UnicodeScriptData.Generated.cs"
    combining_path = root / "src/ProGPU.Text/UnicodeCombiningClassData.Generated.cs"
    bidi_path = root / "src/ProGPU.Text/Bidi/UnicodeBidiData.Generated.cs"
    grapheme_path = root / "src/ProGPU.Text/UnicodeGraphemeData.Generated.cs"
    arabic_path = root / "src/ProGPU.Text/ArabicJoiningData.Generated.cs"
    joining_fallback_path = root / "src/ProGPU.Text/UnicodeJoiningFallbackData.Generated.cs"
    directional_path = root / "src/ProGPU.Text/UnicodeDirectionalData.Generated.cs"
    line_break_path = root / "src/ProGPU.Text/UnicodeLineBreakData.Generated.cs"
    indic_shaping_path = root / "src/ProGPU.Text/IndicShapingData.Generated.cs"
    use_shaping_path = root / "src/ProGPU.Text/UseShapingData.Generated.cs"
    script_source = script_path.read_text(encoding="utf-8")
    combining_source = combining_path.read_text(encoding="utf-8")
    bidi_source = bidi_path.read_text(encoding="utf-8")
    grapheme_source = grapheme_path.read_text(encoding="utf-8")
    arabic_source = arabic_path.read_text(encoding="utf-8")
    joining_fallback_source = joining_fallback_path.read_text(encoding="utf-8")
    directional_source = directional_path.read_text(encoding="utf-8")
    line_break_source = line_break_path.read_text(encoding="utf-8")
    indic_shaping_source = indic_shaping_path.read_text(encoding="utf-8")
    use_shaping_source = use_shaping_path.read_text(encoding="utf-8")
    machine_sources = {
        name: (root / f"src/ProGPU.Text/{managed}SyllableMachineData.Generated.cs").read_text(encoding="utf-8")
        for name, managed in (
            ("indic", "Indic"),
            ("use", "Use"),
            ("myanmar", "Myanmar"),
            ("khmer", "Khmer"),
        )
    }

    scripts_match = re.search(
        r"s_scripts\s*=\s*\[(?P<body>.*?)\];",
        script_source,
        re.DOTALL,
    )
    if scripts_match is None:
        raise RuntimeError("Could not find managed script tags")
    scripts = re.findall(r'"([^"]*)"', scripts_match.group("body"))
    script_ranges = parse_uint_array(script_source, "s_ranges")
    combining_ranges = parse_uint_array(combining_source, "s_ranges")
    bidi_class_ranges = parse_ulong_span(bidi_source, "ClassRanges")
    bidi_bracket_records = parse_ulong_span(bidi_source, "BracketRecords")
    grapheme_ranges = parse_uint_array(grapheme_source, "s_graphemeRanges")
    extended_pictographic_ranges = parse_uint_array(
        grapheme_source, "s_extendedPictographicRanges"
    )
    indic_conjunct_ranges = parse_uint_array(
        grapheme_source, "s_indicConjunctRanges"
    )
    arabic_joining_packed = parse_uint_span(arabic_source, "Packed")
    joining_fallback_ranges = parse_uint_span(joining_fallback_source, "s_ranges")
    mirror_pairs = parse_hex_uint_array(directional_source, "s_mirrorPairs")
    vertical_pairs = parse_hex_uint_array(directional_source, "s_verticalPairs")
    line_break_ranges = parse_uint_array(line_break_source, "s_ranges")
    line_break_quotation_categories = parse_uint_array(
        line_break_source, "s_quotationCategories"
    )
    line_break_mark_ranges = parse_uint_array(line_break_source, "s_markRanges")
    line_break_east_asian_ranges = parse_uint_array(
        line_break_source, "s_eastAsianRanges"
    )
    line_break_unassigned_ranges = parse_uint_array(
        line_break_source, "s_unassignedRanges"
    )
    indic_shaping_values = parse_uint_array(indic_shaping_source, "s_values")
    indic_shaping_data = parse_uint_array(indic_shaping_source, "s_u8")
    use_shaping_data8 = parse_uint_array(use_shaping_source, "s_u8")
    use_shaping_data16 = parse_uint_array(use_shaping_source, "s_u16")
    machine_data = "\n\n".join(
        format_machine(name, source) for name, source in machine_sources.items()
    )
    if (len(script_ranges) % 3 != 0 or len(combining_ranges) % 3 != 0 or
            len(mirror_pairs) % 2 != 0 or len(vertical_pairs) % 2 != 0):
        raise RuntimeError("Managed Unicode range tables are malformed")
    highest_script_index = max(script_ranges[2::3], default=0)
    if highest_script_index >= len(scripts):
        raise RuntimeError("Managed Unicode script table references a missing tag")

    packed_scripts = [pack_tag(value) for value in scripts]
    return f'''// <auto-generated />
// Source: ProGPU.Text UnicodeScriptData.Generated.cs,
// UnicodeCombiningClassData.Generated.cs, UnicodeGraphemeData.Generated.cs,
// UnicodeLineBreakData.Generated.cs,
// IndicShapingData.Generated.cs, UseShapingData.Generated.cs, the four
// ProGPU syllable-machine generated sources,
// ArabicJoiningData.Generated.cs, UnicodeJoiningFallbackData.Generated.cs,
// UnicodeDirectionalData.Generated.cs, and Bidi/UnicodeBidiData.Generated.cs.
// Regenerate with: ./eng/generate-native-unicode-tables.py --write

#ifndef PROGPU_NATIVE_UNICODE_DATA_GENERATED_HPP
#define PROGPU_NATIVE_UNICODE_DATA_GENERATED_HPP

#include <array>
#include <cstdint>

namespace progpu::native::text::detail {{

inline constexpr std::array<std::uint32_t, {len(packed_scripts)}> unicode_script_tags{{
{format_values(packed_scripts)}
}};

inline constexpr std::array<std::uint32_t, {len(script_ranges)}> unicode_script_ranges{{
{format_values(script_ranges)}
}};

inline constexpr std::array<std::uint32_t, {len(combining_ranges)}> unicode_combining_class_ranges{{
{format_values(combining_ranges)}
}};

inline constexpr std::array<std::uint64_t, {len(bidi_class_ranges)}> unicode_bidi_class_ranges{{
{format_u64_values(bidi_class_ranges)}
}};

inline constexpr std::array<std::uint64_t, {len(bidi_bracket_records)}> unicode_bidi_bracket_records{{
{format_u64_values(bidi_bracket_records)}
}};

inline constexpr std::array<std::uint32_t, {len(grapheme_ranges)}> unicode_grapheme_ranges{{
{format_values(grapheme_ranges)}
}};

inline constexpr std::array<std::uint32_t, {len(extended_pictographic_ranges)}> unicode_extended_pictographic_ranges{{
{format_values(extended_pictographic_ranges)}
}};

inline constexpr std::array<std::uint32_t, {len(indic_conjunct_ranges)}> unicode_indic_conjunct_ranges{{
{format_values(indic_conjunct_ranges)}
}};

inline constexpr std::array<std::uint8_t, {len(arabic_joining_packed)}> unicode_arabic_joining_packed{{
{format_values(arabic_joining_packed)}
}};

inline constexpr std::array<std::uint32_t, {len(joining_fallback_ranges)}> unicode_joining_fallback_ranges{{
{format_values(joining_fallback_ranges)}
}};

inline constexpr std::array<std::uint32_t, {len(mirror_pairs)}> unicode_mirror_pairs{{
{format_values(mirror_pairs)}
}};

inline constexpr std::array<std::uint32_t, {len(vertical_pairs)}> unicode_vertical_pairs{{
{format_values(vertical_pairs)}
}};

inline constexpr std::array<std::uint32_t, {len(line_break_ranges)}> unicode_line_break_ranges{{
{format_values(line_break_ranges)}
}};

inline constexpr std::array<std::uint32_t, {len(line_break_quotation_categories)}> unicode_line_break_quotation_categories{{
{format_values(line_break_quotation_categories)}
}};

inline constexpr std::array<std::uint32_t, {len(line_break_mark_ranges)}> unicode_line_break_mark_ranges{{
{format_values(line_break_mark_ranges)}
}};

inline constexpr std::array<std::uint32_t, {len(line_break_east_asian_ranges)}> unicode_line_break_east_asian_ranges{{
{format_values(line_break_east_asian_ranges)}
}};

inline constexpr std::array<std::uint32_t, {len(line_break_unassigned_ranges)}> unicode_line_break_unassigned_ranges{{
{format_values(line_break_unassigned_ranges)}
}};

inline constexpr std::array<std::uint16_t, {len(indic_shaping_values)}> unicode_indic_shaping_values{{
{format_values(indic_shaping_values)}
}};

inline constexpr std::array<std::uint8_t, {len(indic_shaping_data)}> unicode_indic_shaping_data{{
{format_values(indic_shaping_data)}
}};

inline constexpr std::array<std::uint8_t, {len(use_shaping_data8)}> unicode_use_shaping_data8{{
{format_values(use_shaping_data8)}
}};

inline constexpr std::array<std::uint16_t, {len(use_shaping_data16)}> unicode_use_shaping_data16{{
{format_values(use_shaping_data16)}
}};

{machine_data}

}} // namespace progpu::native::text::detail

#endif
'''


def main() -> int:
    parser = argparse.ArgumentParser()
    mode = parser.add_mutually_exclusive_group(required=True)
    mode.add_argument("--write", action="store_true")
    mode.add_argument("--verify", action="store_true")
    args = parser.parse_args()

    root = Path(__file__).resolve().parent.parent
    output = root / "src/ProGPU.Native/src/Text/progpu_native_unicode_data.generated.hpp"
    generated = generate(root)
    if args.verify:
        if not output.exists() or output.read_text(encoding="utf-8") != generated:
            raise RuntimeError(
                "Native Unicode tables are stale; run "
                "./eng/generate-native-unicode-tables.py --write"
            )
        print(f"Verified {output}")
        return 0

    output.write_text(generated, encoding="utf-8", newline="\n")
    print(f"Generated {output}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
