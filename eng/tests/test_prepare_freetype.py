import importlib.util
import json
from pathlib import Path
import struct
import tempfile
import unittest
import uuid


SCRIPT = Path(__file__).resolve().parents[1] / "progpu-prepare-freetype.py"
SPEC = importlib.util.spec_from_file_location("prepare_freetype", SCRIPT)
PREPARE = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(PREPARE)
PIN = json.loads(PREPARE.PIN.read_text())


def archive(*members):
    data = bytearray(b"!<arch>\n")
    for name, payload in members:
        header = f"{name:<16}{0:<12}{0:<6}{0:<6}{'100644':<8}{len(payload):<10}`\n".encode("ascii")
        assert len(header) == 60
        data.extend(header)
        data.extend(payload)
        if len(payload) % 2:
            data.extend(b"\n")
    return bytes(data)


def elf(machine):
    data = bytearray(64)
    data[:6] = b"\x7fELF\x02\x01"
    struct.pack_into("<HH", data, 16, 1, machine)
    return bytes(data)


def mach(machine):
    data = bytearray(32)
    data[:4] = b"\xcf\xfa\xed\xfe"
    struct.pack_into("<I", data, 4, machine)
    struct.pack_into("<I", data, 12, 1)
    return bytes(data)


def coff(machine, big=False):
    if big:
        data = bytearray(56)
        data[:4] = b"\0\0\xff\xff"
        struct.pack_into("<HH", data, 4, 2, machine)
        # Independent wire identity from the Windows BIGOBJ contract, rather
        # than building a fixture from the parser's potentially wrong value.
        data[12:28] = uuid.UUID("d1baa1c7-baee-4ba9-af20-faf66aa4dcb8").bytes_le
    else:
        data = bytearray(60)
        struct.pack_into("<HH", data, 0, machine, 1)
    return bytes(data)


class FreeTypeArchiveTests(unittest.TestCase):
    def test_every_supported_architecture_requires_actual_object_bytes(self):
        for rid, (kind, machine) in PREPARE.RID_MACHINES.items():
            with self.subTest(rid=rid):
                payload = {"elf": elf, "mach": mach, "coff": coff}[kind](machine)
                self.assertEqual(2, PREPARE.verify_archive(archive(("a.o/", payload), ("b.o/", payload)), rid))

    def test_later_wrong_architecture_cannot_publish_a_partial_archive(self):
        with self.assertRaisesRegex(ValueError, "does not match"):
            PREPARE.verify_archive(archive(("a.o/", elf(62)), ("b.o/", elf(183))), "linux-x64")

    def test_bigobj_is_checked_and_hybrid_arm64ec_is_rejected(self):
        self.assertEqual(1, PREPARE.verify_archive(archive(("a.obj/", coff(0xAA64, True))), "win-arm64"))
        for big in (False, True):
            with self.subTest(big=big), self.assertRaises(ValueError):
                PREPARE.verify_archive(archive(("a.obj/", coff(0xA641, big))), "win-arm64")

    def test_bigobj_wrong_class_cannot_be_admitted_by_architecture_alone(self):
        payload = bytearray(coff(0xAA64, True))
        payload[12] ^= 1
        with self.assertRaisesRegex(ValueError, "Unexpected anonymous/import"):
            PREPARE.verify_archive(archive(("a.obj/", payload)), "win-arm64")

    def test_elf_mach_and_coff_architectures_do_not_substitute_for_each_other(self):
        for payload in (elf(183), mach(0x0100000C)):
            with self.assertRaises(ValueError):
                PREPARE.verify_archive(archive(("a.obj/", payload)), "win-arm64")

    def test_non_object_or_truncated_payloads_reject(self):
        payloads = [b"", b"BC\xc0\xde", b"MZ" + bytes(62), coff(0x8664)[:59]]
        fat = bytearray(mach(0x01000007))
        fat[:4] = bytes.fromhex("cafebabe")
        payloads.append(bytes(fat))
        executable = bytearray(elf(62))
        struct.pack_into("<H", executable, 16, 3)
        payloads.append(bytes(executable))
        for payload in payloads:
            with self.subTest(payload=payload[:8]), self.assertRaises(ValueError):
                PREPARE.verify_archive(archive(("a.o/", payload)), "linux-x64")

    def test_bsd_long_names_and_symbol_metadata_keep_real_object_checks(self):
        name = b"font-hinting-object.o\0"
        payload = name + mach(0x0100000C)
        data = archive(("__.SYMDEF", b"symbols"), (f"#1/{len(name)}", payload))
        self.assertEqual(1, PREPARE.verify_archive(data, "osx-arm64"))
        self.assertEqual(1, PREPARE.verify_archive(archive(("/", b"index"), ("//", b"names"),
                                                        ("/0", elf(62))), "linux-x64"))

    def test_metadata_alone_never_qualifies_a_library(self):
        with self.assertRaisesRegex(ValueError, "no independently verified"):
            PREPARE.verify_archive(archive(("/", b"index"), ("//", b"names")), "linux-x64")

    def test_bad_headers_extents_and_padding_reject(self):
        valid = archive(("a.o/", elf(62)), ("/", b"x"))
        cases = [valid[:7], valid[:40], valid[:-1], valid + b"x"]
        bad_padding = bytearray(valid)
        bad_padding[-1] = ord(" ")
        cases.append(bytes(bad_padding))
        bad_end = bytearray(valid)
        bad_end[66] = 0
        cases.append(bytes(bad_end))
        for data in cases:
            with self.subTest(length=len(data)), self.assertRaises(ValueError):
                PREPARE.verify_archive(data, "linux-x64")


class FreeTypeProvenanceTests(unittest.TestCase):
    def signature(self, fingerprint=None):
        fingerprint = fingerprint or PIN["signerFingerprint"]
        return f"[GNUPG:] VALIDSIG {fingerprint} 2026-03-22 1774192173 0 4 0 17 2 00\n"

    def verify(self, status, **changes):
        values = {"tagObject": PIN["tagObject"], "commit": PIN["commit"], "author": PIN["author"]}
        values.update(changes)
        PREPARE.release_identity(values["tagObject"], values["commit"], values["author"], status, PIN)

    def test_exact_release_and_pinned_signature_are_required(self):
        self.verify(self.signature())
        for field in ("tagObject", "commit", "author"):
            with self.subTest(field=field), self.assertRaises(ValueError):
                self.verify(self.signature(), **{field: "wrong"})

    def test_missing_forged_repeated_and_malformed_signature_status_rejects(self):
        for status in ("", self.signature("A" * 40), self.signature() * 2,
                       "[GNUPG:] VALIDSIG " + PIN["signerFingerprint"]):
            with self.subTest(status=status), self.assertRaises(ValueError):
                self.verify(status)

    def test_expired_revoked_bad_and_error_status_cannot_be_hidden_by_validsig(self):
        for bad in ("BADSIG", "ERRSIG", "REVKEYSIG", "EXPKEYSIG", "EXPSIG"):
            with self.subTest(bad=bad), self.assertRaises(ValueError):
                self.verify(self.signature() + "[GNUPG:] " + bad + " details\n")

    def test_primary_fingerprint_may_identify_its_real_signing_subkey(self):
        self.verify(self.signature("A" * 40).rstrip() + " " + PIN["signerFingerprint"] + "\n")

    def test_legal_prefix_preserves_notices_without_importing_implementation(self):
        source = b"/* Copyright original. */\n/* Permission notice. */\n#include <x>\nint source_code;"
        prefix = PREPARE.legal_prefix(source)
        self.assertIn(b"Permission notice", prefix)
        self.assertNotIn(b"#include", prefix)
        self.assertNotIn(b"source_code", prefix)
        with self.assertRaises(ValueError):
            PREPARE.legal_prefix(b"int source_code;")

    def test_existing_workspace_is_never_overwritten(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory).resolve()
            sentinel = root / "caller-data"
            sentinel.write_bytes(b"keep")
            with self.assertRaises(ValueError):
                PREPARE.fresh_workspace(str(root))
            self.assertEqual(b"keep", sentinel.read_bytes())

    def test_relative_symlink_repository_home_and_files_are_rejected(self):
        for value in ("relative", str(PREPARE.ROOT), str(Path.home()), str(PREPARE.PIN)):
            with self.subTest(value=value), self.assertRaises(ValueError):
                PREPARE.fresh_workspace(value)
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory).resolve()
            target = root / "target"
            target.mkdir()
            alias = root / "alias"
            alias.symlink_to(target, target_is_directory=True)
            with self.assertRaises(ValueError):
                PREPARE.fresh_workspace(str(alias))
            self.assertEqual([], list(target.iterdir()))

    def test_ancestor_alias_and_another_git_tree_are_not_external_workspaces(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory).resolve()
            target = root / "target"
            target.mkdir()
            alias = root / "alias"
            alias.symlink_to(target, target_is_directory=True)
            with self.assertRaises(ValueError):
                PREPARE.fresh_workspace(str(alias / "child"))
            self.assertFalse((target / "child").exists())
            (target / ".git").mkdir()
            with self.assertRaises(ValueError):
                PREPARE.fresh_workspace(str(target / "child"))
            self.assertFalse((target / "child").exists())


if __name__ == "__main__":
    unittest.main()
