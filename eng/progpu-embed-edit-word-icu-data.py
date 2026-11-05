#!/usr/bin/env python3
"""Mechanical embedding of the reviewed subset of the exact ICU release's original data.

The classifier opens one line break iterator (en_US) over dictionary text and
looks up scripts (compiled into the ICU common library). It needs the
break-iterator bundles of en_US and root, the line rules and the dictionaries
of the scripts the line rules break by dictionary; nothing else of the 33 MB
common data file. The pin names the kept items; the subset is written with
the original items byte-for-byte in their original order, and its digest is
pinned too, so generation and admission produce one exact embedding."""

import argparse
import hashlib
import json
from pathlib import Path
import struct


def align16(value):
    return (value + 15) & ~15


def filter_common_data(data, items):
    """The ICU common data file (offset TOC, "CmnD") reduced to the named items.

    The header is copied unchanged; the table of contents, the names and the
    items are rewritten in the original (sorted) order, items 16-byte aligned
    and padded with 0xaa as ICU's own packager does. Every named item must be
    present; the result is a valid common data file for udata_setCommonData."""
    if len(data) < 32:
        raise ValueError("ICU common data is truncated")
    header_size, magic1, magic2 = struct.unpack_from("<HBB", data, 0)
    big_endian, charset, char_size = struct.unpack_from("<BBB", data, 8)
    if (magic1, magic2) != (0xDA, 0x27) or data[12:16] != b"CmnD" or big_endian != 0 or \
            charset != 0 or char_size != 2 or header_size % 16 or header_size + 4 > len(data):
        raise ValueError("ICU original release data is not little-endian offset-TOC common data")
    count = struct.unpack_from("<I", data, header_size)[0]
    if header_size + 4 + 8 * count > len(data):
        raise ValueError("ICU common data table of contents is truncated")
    entries = [struct.unpack_from("<II", data, header_size + 4 + 8 * index) for index in range(count)]
    body = len(data) - header_size
    found = []
    for index, (name_offset, data_offset) in enumerate(entries):
        end = entries[index + 1][1] if index + 1 < count else body
        terminator = data.find(b"\0", header_size + name_offset)
        if terminator < 0 or not data_offset <= end <= body:
            raise ValueError("ICU common data table of contents is malformed")
        found.append((data[header_size + name_offset:terminator].decode("ascii"),
                      data[header_size + data_offset:header_size + end]))
    wanted = set(items)
    if len(wanted) != len(items):
        raise ValueError("Duplicate ICU data item in the pin")
    kept = [(name, item) for name, item in found if name in wanted]
    if {name for name, _ in kept} != wanted:
        raise ValueError("ICU original release data lacks a pinned item")
    names = b"".join(name.encode("ascii") + b"\0" for name, _ in kept)
    table = 4 + 8 * len(kept)
    position = align16(table + len(names))
    offsets = []
    name_position = table
    for name, item in kept:
        offsets.append((name_position, position))
        name_position += len(name) + 1
        position = align16(position + len(item))
    output = bytearray(data[:header_size])
    output += struct.pack("<I", len(kept))
    for name_offset, data_offset in offsets:
        output += struct.pack("<II", name_offset, data_offset)
    output += names
    for (_, data_offset), (_, item) in zip(offsets, kept):
        output += b"\xaa" * (header_size + data_offset - len(output))
        output += item
    output += b"\xaa" * (header_size + position - len(output))
    return bytes(output)


def embedded_data(data, pin):
    """The verified original data reduced to the pinned items, verified."""
    if hashlib.sha256(data).hexdigest() != pin["dataSha256"] or len(data) % 4:
        raise ValueError("ICU original release data identity changed")
    subset = filter_common_data(data, pin["dataItems"])
    if hashlib.sha256(subset).hexdigest() != pin["embeddedDataSha256"]:
        raise ValueError("ICU embedded data subset identity changed")
    return subset


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
    data = embedded_data(args.data.read_bytes(), pin)
    # The admitted desktop targets are little-endian. uint32_t literals avoid
    # a target assembler/tool bootstrap and retain every byte of the subset.
    with args.output.open("w", encoding="utf-8", newline="\n") as output:
        output.writelines(source_chunks(data))


if __name__ == "__main__":
    main()
