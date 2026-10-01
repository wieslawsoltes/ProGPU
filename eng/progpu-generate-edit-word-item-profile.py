#!/usr/bin/env python3
"""Generate bounded observed item properties, never word/seam/index answers."""

import argparse
import bisect
import hashlib
import json
from pathlib import Path
import re


def packed(source, name):
    match = re.search(r"array<std::uint32_t, (\d+)> " + name + r"\{([^}]+)\}", source)
    if match is None:
        raise ValueError("Missing original script properties")
    values = [int(value) for value in re.findall(r"(\d+)U", match[2])]
    if len(values) != int(match[1]):
        raise ValueError("Incomplete original packed properties")
    return values


def item_training_case(name):
    # The remaining103 item requests are not read for properties or seams.
    return ((name.startswith("arabic-entry-") and name.endswith("-ltr"))
            or (name.startswith("script-entry-") and name.endswith("-latin-ltr")
                and not name.startswith("script-entry-myanmar-")))


def decode_units(units):
    if any(type(value) is not int or not 0 <= value <= 65535 for value in units):
        raise ValueError("Malformed original UTF16 units")
    return bytes(byte for unit in units for byte in (unit & 255, unit >> 8)).decode("utf-16-le")


def generate(evidence, pin_path, unicode_path):
    pin = json.loads(pin_path.read_text())
    if (pin["schema"] != "native-edit-word-item-profile-v1" or pin["ordinaryProviderQualified"] is not False
            or pin["allUnicodeQualified"] is not False):
        raise ValueError("Measured item properties cannot qualify a general provider")
    source_bytes = unicode_path.read_bytes()
    if hashlib.sha256(source_bytes).hexdigest() != pin["unicodeSourceSha256"]:
        raise ValueError("Original Unicode17 script source changed")
    source = source_bytes.decode()
    tags = packed(source, "unicode_script_tags")
    data = packed(source, "unicode_script_ranges")
    if len(data) % 3:
        raise ValueError("Malformed original script ranges")
    ranges = list(zip(data[::3], data[1::3], data[2::3]))
    if any(first > last or tag >= len(tags) or (index and first <= ranges[index - 1][1])
           for index, (first, last, tag) in enumerate(ranges)):
        raise ValueError("Invalid original script partition")
    starts = [first for first, _, _ in ranges]
    observed = {}
    first_soft = {int(engine): set() for engine in pin["engines"]}
    white_exit = {int(engine): set() for engine in pin["engines"]}
    training_count = heldout_count = 0
    for filename, expected_hash in pin["receipts"].items():
        path = evidence / filename
        if not 1 <= path.stat().st_size <= 128 * 1024 * 1024:
            raise ValueError("Original item receipt exceeded its bound")
        raw = path.read_bytes()
        if hashlib.sha256(raw).hexdigest() != expected_hash:
            raise ValueError("Not the exact complete pinned native item receipt")
        receipt = json.loads(raw)
        if (receipt["schema"] != "native-edit-word-selection-v1" or receipt["completed"] is not True
                or receipt["error"] is not None or receipt["desktopQualified"] is not False
                or receipt["physicalInputQualified"] is not False or receipt["hostShown"] is not False
                or len(receipt["cases"]) != receipt["expectedCases"]
                or any(receipt["identity"][key] != pin[key]
                       for key in ("osBuild", "architecture", "locale", "framework"))):
            raise ValueError("Original native item inventory/platform is incomplete")
        for case in receipt["cases"]:
            if filename == "item-contexts.json" and not item_training_case(case["Name"]):
                heldout_count += 1
                continue
            training_count += 1
            units = case["requestedUtf16"]
            if decode_units(units) != case["requestedText"]:
                raise ValueError("Original source was rewritten")
            attributes = case["scriptBreak"]
            classification = attributes["classification"]
            if (attributes["completed"] is not True or attributes["analyseHResult"] != 0
                    or attributes["freeHResult"] != 0 or attributes["actualUtf16"] != units
                    or attributes["flags"] != (0x140 if case.get("RightToLeft") else 0x40)
                    or classification["completed"] is not True or classification["itemByteSize"] != 8
                    or classification["terminalPosition"] != len(units)):
                raise ValueError("Actual native item API failed")
            end = 0
            for run in classification["runs"]:
                if run["start"] != end or not end < run["end"] <= len(units):
                    raise ValueError("Native item partition changed")
                end = run["end"]
                engine = run["script"]
                direct = run["directScriptBreak"]
                if (engine != run["rawAnalysis"] & 1023 or direct["hResult"] != 0
                        or direct["inputStart"] != run["start"]
                        or direct["inputUtf16"] != units[run["start"]:end]
                        or len(direct["rawBytes"]) != end - run["start"]
                        or any(type(value) is not int or not 0 <= value <= 255 for value in direct["rawBytes"])):
                    raise ValueError("Actual native item engine/input/attributes changed")
                if engine in first_soft and not direct["rawBytes"][0] & 2:
                    first_soft[engine].add(bool(direct["rawBytes"][0] & 1))
                if engine in white_exit:
                    for previous, current in zip(direct["rawBytes"], direct["rawBytes"][1:]):
                        if current & 2 and not previous & 2:
                            white_exit[engine].add(bool(current & 1))
                # Decode each COMPLETE actual native item; no surrogate-half
                # properties, synthetic SCRIPT_ANALYSIS or fabricated items.
                for scalar in map(ord, decode_units(direct["inputUtf16"])):
                    index = bisect.bisect_right(starts, scalar) - 1
                    if index < 0 or scalar > ranges[index][1]:
                        raise ValueError("Missing original script property")
                    tag = tags[ranges[index][2]].to_bytes(4, "big").decode("ascii")
                    if tag in pin["scriptDomains"]:
                        observed.setdefault(scalar, set()).add(engine)
            if end != len(units):
                raise ValueError("Missing terminal original item")
    if (training_count != pin["trainingCases"] or heldout_count != pin["heldoutItemCases"]
            or len(observed) != pin["observedScalars"]):
        raise ValueError("Measured/held-out item domain changed")
    for engine, properties in pin["engines"].items():
        if (type(properties["flags"]) is not int or properties["flags"] not in (0, 1, 2, 6)
                or type(properties["firstSoft"]) is not bool
                or first_soft[int(engine)] != {properties["firstSoft"]}):
            raise ValueError("Measured native item entry behavior changed")
        if properties["flags"] & 4 and white_exit[int(engine)] != {True}:
            raise ValueError("Measured native item whitespace exit was invented")
    compact = []
    for scalar, engines in sorted(observed.items()):
        if len(engines) != 1 or str(next(iter(engines))) not in pin["engines"]:
            raise ValueError("An ambiguous/unqualified engine was silently classified")
        engine = next(iter(engines))
        flags = pin["engines"][str(engine)]["flags"]
        if compact and scalar == compact[-1][1] + 1 and (engine, flags) == compact[-1][2:]:
            compact[-1] = (compact[-1][0], scalar, engine, flags)
        else:
            compact.append((scalar, scalar, engine, flags))
    lines = ["// Generated by eng/progpu-generate-edit-word-item-profile.py; do not hand-edit.",
        "// Only original observed scalar/engine properties; no strings, words, positions or seam answers.",
        f"// Source head {pin['sourceHead']}; run {pin['sourceRun']}; job {pin['sourceJob']}.",
        "// The declared103 held-out item inputs do not train these properties.",
        "#pragma once", "", "#include <array>", "#include <cstdint>", "",
        "namespace progpu::native::text::detail {", "",
        f"inline constexpr std::array<std::uint32_t, {len(compact) * 4}> edit_item_property_ranges{{"]
    lines.extend(f"    {first}U, {last}U, {engine}U, {flags}U," for first, last, engine, flags in compact)
    lines.extend(["};", "", f"inline constexpr std::array<std::uint32_t, {len(pin['scriptDomains'])}> edit_item_observed_script_domains{{"])
    lines.extend(f"    {int.from_bytes(('lao ' if tag == 'laoo' else tag).encode(), 'big')}U,"
                 for tag in pin["scriptDomains"])
    lines.extend(["};", "", "} // namespace progpu::native::text::detail", ""])
    return "\n".join(lines)


def main():
    root = Path(__file__).resolve().parent.parent
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--evidence", type=Path, required=True)
    parser.add_argument("--pin", type=Path, default=root / "eng/native-edit-word-item-profile.json")
    parser.add_argument("--unicode", type=Path, default=root / "src/ProGPU.Native/src/Text/progpu_native_unicode_data.generated.hpp")
    parser.add_argument("--check", type=Path)
    args = parser.parse_args()
    result = generate(args.evidence, args.pin, args.unicode)
    if args.check:
        if args.check.read_text() != result:
            raise ValueError("Checked-in measured item properties are not reproducible")
        print("Pinned EDIT item property data is byte-for-byte reproducible")
    else:
        print(result, end="")


if __name__ == "__main__":
    main()
