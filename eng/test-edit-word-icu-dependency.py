#!/usr/bin/env python3
"""CPU-only dependency contracts with synthetic source/static archives, no ICU."""

import argparse
import copy
import hashlib
import importlib.util
import io
import json
from pathlib import Path
import struct
import tarfile
import tempfile
import unittest
from unittest import mock


SPEC = importlib.util.spec_from_file_location(
    "edit_word_icu_dependency", Path(__file__).with_name("progpu-edit-word-icu-dependency.py"))
DEPENDENCY = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(DEPENDENCY)


def synthetic_archive(rid):
    kind, machine = DEPENDENCY.ARCHITECTURE.RID_MACHINES[rid]
    if kind == "elf":
        payload = bytearray(64)
        payload[:6] = b"\x7fELF\x02\x01"
        struct.pack_into("<HH", payload, 16, 1, machine)
    elif kind == "mach":
        payload = bytearray(32)
        payload[:4] = b"\xcf\xfa\xed\xfe"
        struct.pack_into("<I", payload, 4, machine)
        struct.pack_into("<I", payload, 12, 1)
    else:
        payload = bytearray(60)
        struct.pack_into("<HH", payload, 0, machine, 1)
    header = f"{'fixture.o/':<16}{0:<12}{0:<6}{0:<6}{'100644':<8}{len(payload):<10}`\n"
    return b"!<arch>\n" + header.encode("ascii") + payload


def synthetic_common_data():
    """A little-endian offset-TOC ICU common data file with three items."""
    header = bytearray(32)
    struct.pack_into("<HBB", header, 0, 32, 0xDA, 0x27)
    struct.pack_into("<HHBBBB", header, 4, 20, 0, 0, 0, 2, 0)
    header[12:16] = b"CmnD"
    items = [("icudt78l/a.res", bytes(range(16))), ("icudt78l/brkitr/line.brk", bytes(range(16, 48))),
             ("icudt78l/z.res", bytes(16))]
    names = b"".join(name.encode("ascii") + b"\0" for name, _ in items)
    table = 4 + 8 * len(items)
    position = (table + len(names) + 15) & ~15
    entries, name_position, body = [], table, b""
    for name, item in items:
        entries.append((name_position, position))
        name_position += len(name) + 1
        position += len(item)
    data = bytes(header) + struct.pack("<I", len(items)) + b"".join(struct.pack("<II", *entry) for entry in entries)
    data += names
    data += b"\xaa" * (32 + entries[0][1] - len(data))
    return data + b"".join(item for _, item in items)


SYNTHETIC_DATA = synthetic_common_data()
SYNTHETIC_ITEMS = ["icudt78l/brkitr/line.brk"]


class DependencyTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory(prefix="progpu-edit-icu-contract.")
        self.addCleanup(self.temporary.cleanup)
        self.root = Path(self.temporary.name).resolve()
        self.build = self.root / "build"
        self.build.mkdir()
        self.source = self.build / "edit-word-icu-source"
        files = {"icu/LICENSE": b"Synthetic original complete notice\n",
                 "icu/source/data/in/icudt78l.dat": SYNTHETIC_DATA,
                 "icu/source/common/sources.txt": b"fixture.cpp\n",
                 "icu/source/common/fixture.cpp": b"// synthetic source\n",
                 "icu/source/common/unicode/fixture.h": b"// synthetic header\n",
                 "icu/source/stubdata/stubdata.cpp": b"// synthetic stub\n"}
        self.archive = self.root / "source.tgz"
        with tarfile.open(self.archive, "w:gz") as archive:
            for name, data in files.items():
                entry = tarfile.TarInfo(name)
                entry.size = len(data)
                archive.addfile(entry, io.BytesIO(data))
                path = self.source / name
                path.parent.mkdir(parents=True, exist_ok=True)
                path.write_bytes(data)
        self.pin = DEPENDENCY.read_json(DEPENDENCY.PIN)
        self.pin["sourceSha256"] = DEPENDENCY.digest(self.archive)
        self.pin["licenseSha256"] = DEPENDENCY.digest(self.source / "icu/LICENSE")
        self.pin["dataSha256"] = DEPENDENCY.digest(self.source / self.pin["dataPath"])
        self.pin["dataItems"] = SYNTHETIC_ITEMS
        subset = DEPENDENCY.EMBED.filter_common_data(SYNTHETIC_DATA, SYNTHETIC_ITEMS)
        self.pin["embeddedDataSha256"] = hashlib.sha256(subset).hexdigest()
        (self.build / "progpu_edit_icu_data.cpp").write_text(
            "".join(DEPENDENCY.EMBED.source_chunks(DEPENDENCY.EMBED.embedded_data(SYNTHETIC_DATA, self.pin))),
            encoding="utf-8", newline="\n")
        self.pin_path = self.root / "pin.json"
        self.pin_path.write_text(json.dumps(self.pin), encoding="utf-8")
        patch = mock.patch.object(DEPENDENCY, "PIN", self.pin_path)
        patch.start()
        self.addCleanup(patch.stop)
        (self.build / "CMakeCache.txt").write_text(
            "CMAKE_GENERATOR:INTERNAL=Ninja\n"
            f"PROGPU_NATIVE_EDIT_WORD_ICU_SOURCE_ARCHIVE:FILEPATH={self.archive}\n", encoding="utf-8")
        self.package = self.root / "package"
        self.package.mkdir()

    def produce(self, rid="linux-x64", configuration="Release"):
        library = self.build / DEPENDENCY.library_name(rid)
        library.write_bytes(synthetic_archive(rid))
        args = argparse.Namespace(build_directory=self.build, source_archive=self.archive,
                                  library=library, rid=rid, configuration=configuration,
                                  compiler_id="Clang", compiler_version="20.1.0", compiler_target="")
        DEPENDENCY.record(args)
        return DEPENDENCY.read_json(self.build / DEPENDENCY.MARKER)

    def stage(self, rid="linux-x64"):
        destination = self.package / "runtimes" / rid / "native"
        destination.mkdir(parents=True, exist_ok=True)
        args = argparse.Namespace(build_directory=self.build, source_archive=self.archive,
                                  binary_directory=None, native_destination=destination, rid=rid)
        DEPENDENCY.stage(args)
        return destination

    def test_all_six_actual_object_formats_round_trip(self):
        for rid in DEPENDENCY.RIDS:
            with self.subTest(rid=rid):
                receipt = self.produce(rid)
                destination = self.stage(rid)
                self.assertEqual(receipt["verifiedObjects"], 1)
                self.assertEqual(receipt["pin"], self.pin)
                self.assertEqual(receipt["producer"]["configuration"], "Release")
                self.assertEqual((destination / "licenses/edit-word-icu/LICENSE").read_bytes(),
                                 (self.source / "icu/LICENSE").read_bytes())
        DEPENDENCY.verify_package(self.package, True)

    def test_no_dependency_is_optional_but_required_package_fails(self):
        DEPENDENCY.verify_package(self.package, False)
        with self.assertRaisesRegex(ValueError, "all six"):
            DEPENDENCY.verify_package(self.package, True)

    def test_partial_package_cannot_silently_disable_dependency(self):
        self.produce()
        self.stage()
        with self.assertRaisesRegex(ValueError, "all six"):
            DEPENDENCY.verify_package(self.package, False)

    def test_wrong_object_architecture_never_publishes_receipt(self):
        self.produce()
        marker = self.build / DEPENDENCY.MARKER
        previous = marker.read_bytes()
        library = self.build / DEPENDENCY.library_name("linux-x64")
        library.write_bytes(synthetic_archive("linux-arm64"))
        with self.assertRaisesRegex(ValueError, "does not match"):
            DEPENDENCY.record(argparse.Namespace(build_directory=self.build, source_archive=self.archive,
                library=library, rid="linux-x64", configuration="Release", compiler_id="Clang",
                compiler_version="20.1.0", compiler_target=""))
        self.assertEqual(previous, marker.read_bytes())

    def test_changed_source_archive_rejected(self):
        self.archive.write_bytes(self.archive.read_bytes() + b"changed")
        with self.assertRaisesRegex(ValueError, "reviewed release"):
            self.produce()

    def test_changed_source_header_or_data_rejected(self):
        for relative in ("icu/source/common/fixture.cpp", "icu/source/common/unicode/fixture.h",
                         self.pin["dataPath"], "icu/LICENSE"):
            with self.subTest(relative=relative):
                path = self.source / relative
                original = path.read_bytes()
                path.write_bytes(original + b"changed")
                with self.assertRaisesRegex(ValueError, "Modified ICU source input"):
                    self.produce()
                path.write_bytes(original)

    def test_generated_embedding_has_independent_golden_spelling(self):
        # Literal bytes/words, independent of the writer used by the fixture.
        expected = ('#include <bit>\n#include <cstdint>\n'
                    'static_assert(std::endian::native == std::endian::little);\n'
                    'extern "C" {\n'
                    'alignas(16) extern const std::uint32_t progpu_edit_icu_data[] = {\n'
                    '0x03020100U,0x07060504U,0x0b0a0908U,0x0f0e0d0cU,\n};\n}\n')
        self.assertEqual(expected, "".join(DEPENDENCY.EMBED.source_chunks(bytes(range(16)))))
        # The fixture embeds the subset: the common data header first.
        self.assertTrue((self.build / "progpu_edit_icu_data.cpp").read_text().splitlines()[5]
                        .startswith("0x27da0020U,"))

    def test_subset_keeps_only_the_pinned_items_byte_for_byte(self):
        subset = DEPENDENCY.EMBED.filter_common_data(SYNTHETIC_DATA, SYNTHETIC_ITEMS)
        self.assertEqual(SYNTHETIC_DATA[:32], subset[:32])
        count, name_offset, data_offset = struct.unpack_from("<III", subset, 32)
        self.assertEqual(1, count)
        self.assertEqual(b"icudt78l/brkitr/line.brk\0", subset[32 + name_offset:32 + name_offset + 25])
        self.assertEqual(0, data_offset % 16)
        self.assertEqual(bytes(range(16, 48)), subset[32 + data_offset:32 + data_offset + 32])
        with self.assertRaisesRegex(ValueError, "lacks a pinned item"):
            DEPENDENCY.EMBED.filter_common_data(SYNTHETIC_DATA, ["icudt78l/missing.res"])
        with self.assertRaisesRegex(ValueError, "not little-endian offset-TOC"):
            DEPENDENCY.EMBED.filter_common_data(bytes(64), SYNTHETIC_ITEMS)

    def test_changed_subset_identity_is_rejected(self):
        changed = dict(self.pin, embeddedDataSha256="0" * 64)
        with self.assertRaisesRegex(ValueError, "subset identity changed"):
            DEPENDENCY.EMBED.embedded_data(SYNTHETIC_DATA, changed)

    def test_changed_generated_source_never_publishes_receipt(self):
        path = self.build / "progpu_edit_icu_data.cpp"
        original = path.read_text()
        for changed in (original.replace("0x27da0020U", "0x27da0021U"),
                        original.replace("alignas(16)", "alignas(4)"), original + "// extra\n"):
            with self.subTest(changed=changed):
                path.write_text(changed, encoding="utf-8")
                with self.assertRaisesRegex(ValueError, "Generated ICU data source"):
                    self.produce()
                self.assertFalse((self.build / DEPENDENCY.MARKER).exists())
        path.write_text(original, encoding="utf-8")

    def test_generated_source_is_rechecked_before_staging(self):
        self.produce()
        path = self.build / "progpu_edit_icu_data.cpp"
        path.write_text(path.read_text().replace("0x27da0020U", "0x27da0021U"), encoding="utf-8")
        with self.assertRaisesRegex(ValueError, "Generated ICU data source"):
            self.stage()

    def test_added_header_cannot_shadow_original_include(self):
        (self.source / "icu/source/common/added.h").write_bytes(b"new")
        with self.assertRaisesRegex(ValueError, "added ICU"):
            self.produce()

    def test_different_configured_archive_rejected(self):
        other = self.root / "same-bytes-other-source.tgz"
        other.write_bytes(self.archive.read_bytes())
        cache = self.build / "CMakeCache.txt"
        cache.write_text(cache.read_text().replace(str(self.archive), str(other)), encoding="utf-8")
        with self.assertRaisesRegex(ValueError, "exact ICU source archive"):
            self.produce()

    def test_changed_built_archive_rejected_before_staging(self):
        self.produce()
        (self.build / DEPENDENCY.library_name("linux-x64")).write_bytes(synthetic_archive("linux-arm64"))
        with self.assertRaisesRegex(ValueError, "architecture/hash"):
            self.stage()
        self.assertFalse((self.package / "runtimes/linux-x64/native/sdk" / DEPENDENCY.MARKER).exists())

    def test_staging_does_not_overwrite_owned_payload(self):
        self.produce()
        destination = self.stage()
        marker = destination / "sdk" / DEPENDENCY.MARKER
        original = marker.read_bytes()
        with self.assertRaisesRegex(ValueError, "overwrite"):
            self.stage()
        self.assertEqual(original, marker.read_bytes())

    def test_nonrelease_producer_not_packaged(self):
        self.produce(configuration="Debug")
        with self.assertRaisesRegex(ValueError, "Release producer"):
            self.stage()

    def test_notice_tamper_detected_in_complete_package(self):
        for rid in DEPENDENCY.RIDS:
            self.produce(rid)
            self.stage(rid)
        (self.package / "runtimes/osx-arm64/native/licenses/edit-word-icu/LICENSE").write_bytes(b"changed")
        with self.assertRaisesRegex(ValueError, "notices changed"):
            DEPENDENCY.verify_package(self.package, True)

    def test_receipt_typed_controls_and_exact_pin(self):
        receipt = self.produce()
        for key, value in (("schemaVersion", True), ("verifiedObjects", True), ("rid", "linux-arm64"),
                           ("pin", dict(self.pin, version="78.2"))):
            with self.subTest(key=key):
                changed = copy.deepcopy(receipt)
                changed[key] = value
                with self.assertRaisesRegex(ValueError, "producer identity"):
                    DEPENDENCY.validate_metadata(changed, "linux-x64", self.pin)

    def test_duplicate_json_key_rejected(self):
        marker = self.root / "duplicate.json"
        marker.write_text('{"rid":"linux-x64","rid":"linux-arm64"}', encoding="utf-8")
        with self.assertRaisesRegex(ValueError, "Duplicate ICU"):
            DEPENDENCY.read_json(marker)

    def test_unknown_rid_rejected(self):
        with self.assertRaisesRegex(ValueError, "Unsupported private ICU RID"):
            DEPENDENCY.library_name("win-arm64ec")

    def test_sanitizer_uses_the_exact_selected_icu_archive(self):
        script = (DEPENDENCY.ROOT / "eng/build-progpu-native.sh").read_text(encoding="utf-8")
        sanitizer = script.split("sanitizer_options=(", 1)[1].split(
            'if ((${#module_options[@]}));', 1)[0]
        self.assertIn('-DPROGPU_NATIVE_EDIT_WORD_ICU_SOURCE_ARCHIVE="${edit_word_icu_archive}"', sanitizer)


if __name__ == "__main__":
    unittest.main()
