#!/usr/bin/env python3
"""Own measured property data, not a word dictionary or vendor implementation."""

import argparse
import bisect
import hashlib
import json
from pathlib import Path
import re


def digest(data):
    return hashlib.sha256(data).hexdigest()


def generate(receipt_path, pin_path, unicode_path):
    pin = json.loads(pin_path.read_text())
    if (pin["schema"] != "native-edit-word-symbol-profile-v1"
            or pin["ordinaryProviderQualified"] is not False or pin["allUnicodeQualified"] is not False):
        raise ValueError("Measured profile cannot admit an ordinary/all-Unicode provider")
    if not 1 <= receipt_path.stat().st_size <= 128 * 1024 * 1024:
        raise ValueError("Original symbol observation exceeds its receipt bound")
    receipt_bytes = receipt_path.read_bytes()
    unicode_bytes = unicode_path.read_bytes()
    if not 1 <= len(receipt_bytes) <= 128 * 1024 * 1024 or digest(receipt_bytes) != pin["receiptSha256"]:
        raise ValueError("Not the exact complete pinned original symbol observation")
    if digest(unicode_bytes) != pin["unicodeSourceSha256"]:
        raise ValueError("Original Unicode17 property source changed")
    observed = json.loads(receipt_bytes)
    identity = observed["identity"]
    if (observed["schema"] != "native-edit-symbol-attributes-v1" or observed["completed"] is not True
            or observed["editGesturesObserved"] is not False or observed["error"] is not None
            or observed["desktopQualified"] is not False or observed["physicalInputQualified"] is not False
            or observed["symbolCount"] != pin["symbolCount"] or observed["expectedCases"] != pin["caseCount"]
            or any(identity[key] != pin[key] for key in ("osBuild", "architecture", "locale", "framework"))):
        raise ValueError("Pinned original observation domain/platform/completion mismatch")
    source = unicode_bytes.decode()
    match = re.search(r"array<std::uint32_t, (\d+)> unicode_line_break_ranges\{([^}]+)\}", source)
    if match is None:
        raise ValueError("Missing original packed line properties")
    values = [int(value) for value in re.findall(r"(\d+)U", match[2])]
    if len(values) != int(match[1]) or len(values) % 3:
        raise ValueError("Malformed original property inventory")
    ranges = list(zip(values[::3], values[1::3], values[2::3]))
    starts = [value[0] for value in ranges]
    if any(a > b or (index and a <= ranges[index - 1][1]) for index, (a, b, _) in enumerate(ranges)):
        raise ValueError("Original property ranges overlap")
    contexts = {}
    for case in observed["cases"]:
        scalar = case["scalar"]
        name = case["name"]
        suffix = name.rsplit("-", 1)[-1]
        if type(scalar) is not int or not 0 <= scalar <= 65535 or suffix not in ("latin", "variation", "cjk"):
            raise ValueError("Observation escaped the original BMP context domain")
        expected_source = {"latin": [97, scalar, 98, 32], "variation": [97, scalar, 65039, 98, 32],
                           "cjk": [19968, scalar, 20108, 32]}[suffix]
        source_bytes = case["requestedText"].encode("utf-16-le")
        actual_source = [int.from_bytes(source_bytes[i:i + 2], "little")
                         for i in range(0, len(source_bytes), 2)]
        attributes = case["scriptBreak"]
        if (name != f"bmp-{scalar:04X}-{suffix}" or case["requestedUtf16"] != expected_source
                or actual_source != expected_source or attributes["actualUtf16"] != expected_source
                or attributes["completed"] is not True or attributes["analyseHResult"] != 0
                or attributes["freeHResult"] != 0 or attributes["flags"] != 0x40):
            raise ValueError("Original native input/API changed")
        raw = attributes["rawBytes"]
        if len(raw) != len(expected_source) or any(type(value) is not int or not 0 <= value <= 255 for value in raw):
            raise ValueError("Original log-attribute unit coverage changed")
        group = contexts.setdefault(scalar, {"category": case["runtimeCategory"]})
        if suffix in group or group["category"] != case["runtimeCategory"]:
            raise ValueError("Duplicate/mixed original symbol context")
        group[suffix] = tuple(index for index, value in enumerate(raw) if value & 1)
    if len(observed["cases"]) != pin["caseCount"] or len(contexts) != pin["symbolCount"]:
        raise ValueError("Incomplete original symbol coverage")
    decisions = []
    unknown = 0
    for scalar, group in sorted(contexts.items()):
        if set(group) != {"category", "latin", "variation", "cjk"}:
            raise ValueError("Missing original independent context")
        index = bisect.bisect_right(starts, scalar) - 1
        if index < 0 or scalar > ranges[index][1]:
            raise ValueError("Missing original Unicode property")
        # Values25/3 are the original fixed unicode_line_break_class ID/AL ABI.
        # Only the stated intersection is considered; no other Unicode class,
        # script, symbol category or unobserved code point is generalized.
        if group["category"] != "OtherSymbol" or ranges[index][2] != 25:
            continue
        signature = tuple(group[name] for name in ("latin", "variation", "cjk"))
        if signature == ((), (), (1, 2)):
            decisions.append((scalar, 3))
        elif signature == ((1, 2), (1, 3), (1, 2)):
            decisions.append((scalar, 25))
        else:
            unknown += 1
    if (sum(value == 3 for _, value in decisions) != pin["alphabeticCount"]
            or sum(value == 25 for _, value in decisions) != pin["ideographicCount"]
            or unknown != pin["unqualifiedCount"]):
        raise ValueError("Pinned measured property domain changed")
    compact = []
    for scalar, value in decisions:
        if compact and scalar == compact[-1][1] + 1 and value == compact[-1][2]:
            compact[-1] = (compact[-1][0], scalar, value)
        else:
            compact.append((scalar, scalar, value))
    lines = ["// Generated by eng/progpu-generate-edit-word-symbol-profile.py; do not hand-edit.",
             "// Own original-API observations, not vendor implementation source or a word/index lookup.",
             f"// Source head {pin['sourceHead']}; run {pin['sourceRun']}; job {pin['sourceJob']}.",
             f"// Receipt SHA256 {pin['receiptSha256']}.",
             "// Only observed BMP So/rawID members with all-three-context AL/ID signatures.",
             "// Unobserved/nonmatching members remain unqualified; no ordinary provider admission.",
             "#pragma once", "", "#include <array>", "#include <cstdint>", "",
             "namespace progpu::native::text::detail {", "",
             f"inline constexpr std::array<std::uint32_t, {len(compact) * 3}> edit_symbol_line_class_ranges{{"]
    lines.extend(f"    {first}U, {last}U, {value}U," for first, last, value in compact)
    lines.extend(["};", "", "} // namespace progpu::native::text::detail", ""])
    return "\n".join(lines)


def main():
    root = Path(__file__).resolve().parent.parent
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--receipt", type=Path, required=True)
    parser.add_argument("--pin", type=Path, default=root / "eng/native-edit-word-symbol-profile.json")
    parser.add_argument("--unicode", type=Path, default=root / "src/ProGPU.Native/src/Text/progpu_native_unicode_data.generated.hpp")
    parser.add_argument("--check", type=Path)
    args = parser.parse_args()
    result = generate(args.receipt, args.pin, args.unicode)
    if args.check:
        if args.check.read_text() != result:
            raise ValueError("Checked-in measured property data is not reproducible")
        print("Pinned EDIT symbol property data is byte-for-byte reproducible")
    else:
        print(result, end="")


if __name__ == "__main__":
    main()
