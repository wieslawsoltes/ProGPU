#!/usr/bin/env python3
"""Generate ProGPU's packed Unicode 17 line-break property source."""

from __future__ import annotations

import argparse
import hashlib
from pathlib import Path


EXPECTED_SHA256 = "e6a18fa91f8f6a6f8e534b1d3f128c21ada45bfe152eb6b1bcc5e15fd8ac92e6"
EXPECTED_UNICODE_DATA_SHA256 = "2e1efc1dcb59c575eedf5ccae60f95229f706ee6d031835247d843c11d96470c"
EXPECTED_EAST_ASIAN_WIDTH_SHA256 = "ea7ce50f3444a050333448dffef1cadd9325af55cbb764b4a2280faf52170a33"

CLASSES = [
    "XX", "AI", "AK", "AL", "AP", "AS", "B2", "BA", "BB", "BK",
    "CB", "CJ", "CL", "CM", "CP", "CR", "EB", "EM", "EX", "GL",
    "H2", "H3", "HH", "HL", "HY", "ID", "IN", "IS", "JL", "JT",
    "JV", "LF", "NL", "NS", "NU", "OP", "PO", "PR", "QU", "RI",
    "SA", "SG", "SP", "SY", "VF", "VI", "WJ", "ZW", "ZWJ",
]


def code_range(raw: str) -> tuple[int, int]:
    values = raw.strip().split("..")
    return int(values[0], 16), int(values[-1], 16)


def parse(source: str) -> list[int]:
    classes = {name: index for index, name in enumerate(CLASSES)}
    records: list[tuple[int, int, int]] = []
    for line in source.splitlines():
        body = line.split("#", 1)[0].strip()
        if not body:
            continue
        fields = [field.strip() for field in body.split(";")]
        if len(fields) != 2 or fields[1] not in classes:
            raise RuntimeError(f"Malformed LineBreak.txt record: {line}")
        start, end = code_range(fields[0])
        records.append((start, end, classes[fields[1]]))
    records.sort()
    return [value for record in records for value in record]


def parse_unicode_categories(source: str) -> tuple[list[int], list[int], list[int]]:
    punctuation: list[tuple[int, int, int]] = []
    marks: list[tuple[int, int, int]] = []
    assigned: list[tuple[int, int]] = []
    pending: tuple[int, str] | None = None
    for line in source.splitlines():
        fields = line.split(";")
        if len(fields) < 3:
            raise RuntimeError("Malformed UnicodeData.txt record")
        code_point = int(fields[0], 16)
        name = fields[1]
        category = fields[2]
        if name.endswith(", First>"):
            pending = (code_point, category)
            continue
        if name.endswith(", Last>"):
            if pending is None or pending[1] != category:
                raise RuntimeError("Malformed UnicodeData First/Last range")
            start = pending[0]
            pending = None
        else:
            start = code_point
        assigned.append((start, code_point))
        if category in ("Pi", "Pf"):
            punctuation.append((start, code_point, 1 if category == "Pi" else 2))
        if category in ("Mn", "Mc"):
            marks.append((start, code_point, 1))
    if pending is not None:
        raise RuntimeError("Unterminated UnicodeData First/Last range")
    unassigned: list[tuple[int, int, int]] = []
    cursor = 0
    for start, end in sorted(assigned):
        if start > cursor:
            unassigned.append((cursor, start - 1, 1))
        cursor = max(cursor, end + 1)
    if cursor <= 0x10FFFF:
        unassigned.append((cursor, 0x10FFFF, 1))
    return (
        [value for record in punctuation for value in record],
        [value for record in marks for value in record],
        [value for record in unassigned for value in record],
    )


def parse_east_asian(source: str) -> list[int]:
    records: list[tuple[int, int, int]] = []
    for line in source.splitlines():
        body = line.split("#", 1)[0].strip()
        if not body:
            continue
        fields = [field.strip() for field in body.split(";")]
        if len(fields) != 2:
            raise RuntimeError(f"Malformed EastAsianWidth.txt record: {line}")
        if fields[1] in ("F", "W", "H"):
            start, end = code_range(fields[0])
            records.append((start, end, 1))
    records.sort()
    return [value for record in records for value in record]


def format_values(values: list[int]) -> str:
    return "\n".join(
        "        " + ", ".join(str(value) for value in values[index:index + 12]) + ","
        for index in range(0, len(values), 12)
    )


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--line-break", type=Path, required=True)
    parser.add_argument("--unicode-data", type=Path, required=True)
    parser.add_argument("--east-asian-width", type=Path, required=True)
    args = parser.parse_args()
    sources = (
        (args.line_break, EXPECTED_SHA256, "LineBreak.txt"),
        (args.unicode_data, EXPECTED_UNICODE_DATA_SHA256, "UnicodeData.txt"),
        (args.east_asian_width, EXPECTED_EAST_ASIAN_WIDTH_SHA256, "EastAsianWidth.txt"),
    )
    decoded: list[str] = []
    for path, expected, label in sources:
        data = path.read_bytes()
        digest = hashlib.sha256(data).hexdigest()
        if digest != expected:
            raise RuntimeError(
                f"{label} SHA-256 mismatch: {digest}; expected {expected}"
            )
        decoded.append(data.decode("utf-8"))
    values = parse(decoded[0])
    punctuation, marks, unassigned = parse_unicode_categories(decoded[1])
    east_asian = parse_east_asian(decoded[2])
    root = Path(__file__).resolve().parent.parent
    output = root / "src/ProGPU.Text/UnicodeLineBreakData.Generated.cs"
    source = f'''namespace ProGPU.Text;

// <auto-generated />
// Unicode 17.0.0 UAX #14 Line_Break property data. Generated by
// eng/generate-unicode-line-break-table.py from pinned official UCD files.
internal static class UnicodeLineBreakData
{{
    // Triples are inclusive start, inclusive end, and the stable native class ID.
    internal static readonly uint[] s_ranges =
    [
{format_values(values)}
    ];

    // General_Category Pi=1/Pf=2, Mn/Mc, and East_Asian_Width F/W/H.
    internal static readonly uint[] s_quotationCategories =
    [
{format_values(punctuation)}
    ];

    internal static readonly uint[] s_markRanges =
    [
{format_values(marks)}
    ];

    internal static readonly uint[] s_eastAsianRanges =
    [
{format_values(east_asian)}
    ];

    internal static readonly uint[] s_unassignedRanges =
    [
{format_values(unassigned)}
    ];
}}
'''
    output.write_text(source, encoding="utf-8", newline="\n")
    print(f"Generated {output}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
