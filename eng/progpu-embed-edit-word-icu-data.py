#!/usr/bin/env python3
"""Mechanical embedding of the exact reviewed ICU release's original data."""

import argparse
import hashlib
import json
from pathlib import Path
import struct


def source_chunks(data):
    """One canonical mechanical spelling shared by generation and admission."""
    if len(data) % 4:
        raise ValueError("ICU original release data is not uint32 aligned")
    yield ('#include <bit>\n#include <cstdint>\n'
           'static_assert(std::endian::native == std::endian::little);\n'
           'extern "C" {\n'
           'alignas(16) extern const std::uint32_t progpu_edit_icu_data[] = {\n')
    for offset in range(0, len(data), 4096):
        words = struct.unpack("<" + "I" * (len(data[offset:offset + 4096]) // 4),
                              data[offset:offset + 4096])
        for start in range(0, len(words), 8):
            yield ",".join(f"0x{word:08x}U" for word in words[start:start + 8]) + ",\n"
    yield '};\n}\n'


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--pin", required=True, type=Path)
    parser.add_argument("--data", required=True, type=Path)
    parser.add_argument("--output", required=True, type=Path)
    args = parser.parse_args()
    pin = json.loads(args.pin.read_text(encoding="utf-8"))
    data = args.data.read_bytes()
    if hashlib.sha256(data).hexdigest() != pin["dataSha256"] or len(data) % 4:
        raise ValueError("ICU original release data identity changed")
    # The admitted desktop targets are little-endian. uint32_t literals avoid
    # a target assembler/tool bootstrap and retain every original data byte.
    with args.output.open("w", encoding="utf-8", newline="\n") as output:
        output.writelines(source_chunks(data))


if __name__ == "__main__":
    main()
