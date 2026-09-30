import hashlib
import importlib.util
import json
from pathlib import Path
import struct
import tempfile
import unittest
from unittest.mock import patch

SCRIPT = Path(__file__).resolve().parents[1] / "progpu-verify-freetype.py"
SPEC = importlib.util.spec_from_file_location("verify_freetype", SCRIPT)
VERIFY = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(VERIFY)
PIN = json.loads(VERIFY.PREPARE.PIN.read_text())


class FontInstallationAdmissionTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory()
        self.addCleanup(self.temporary.cleanup)
        self.workspace = Path(self.temporary.name).resolve()
        install = self.workspace / "install"
        cache = self.workspace / "build/CMakeCache.txt"
        cache.parent.mkdir()
        cache.write_text("\n".join([
            "CMAKE_BUILD_TYPE:STRING=Release", "BUILD_SHARED_LIBS:BOOL=OFF",
            "CMAKE_POSITION_INDEPENDENT_CODE:BOOL=ON", "CMAKE_INTERPROCEDURAL_OPTIMIZATION:BOOL=OFF",
            "CMAKE_C_VISIBILITY_PRESET:STRING=hidden", "CMAKE_MSVC_RUNTIME_LIBRARY:STRING=MultiThreadedDLL",
            "CMAKE_INSTALL_LIBDIR:PATH=lib", "CMAKE_INSTALL_PREFIX:PATH=" + install.as_posix(),
            *("FT_DISABLE_" + name + ":BOOL=TRUE" for name in ("ZLIB", "BZIP2", "PNG", "HARFBUZZ", "BROTLI"))]) + "\n")
        include = install / "include/freetype2"
        (include / "freetype").mkdir(parents=True)
        (include / "ft2build.h").write_text("/* original test header */\n")
        (include / "freetype/freetype.h").write_text(
            "#define FREETYPE_MAJOR 2\n#define FREETYPE_MINOR 14\n#define FREETYPE_PATCH 3\n")
        library = install / "lib/libfreetype.a"
        library.parent.mkdir()
        # Original archive/ELF metadata fixture, not a foreign implementation.
        elf = bytearray(64)
        elf[:6] = b"\x7fELF\x02\x01"
        struct.pack_into("<HH", elf, 16, 1, 62)
        header = f"{'font.o/':<16}{0:<12}{0:<6}{0:<6}{'100644':<8}{64:<10}`\n".encode()
        library.write_bytes(b"!<arch>\n" + header + elf)
        legal = install / "share/progpu-freetype/licenses"
        legal.mkdir(parents=True)
        notices = []
        for name in VERIFY.PREPARE.LEGAL_FILES + VERIFY.PREPARE.LEGAL_PREFIXES:
            source = self.workspace / "source" / name
            source.parent.mkdir(parents=True, exist_ok=True)
            source.write_bytes(b"/* Copyright original fixture. */\n")
            notice = name.replace("/", "__") + ".txt"
            (legal / notice).write_bytes(source.read_bytes())
            notices.append({"source": name, "sourceSha256": VERIFY.digest(source),
                            "notice": notice, "noticeSha256": VERIFY.digest(legal / notice)})
        (legal / "NOTICE.txt").write_text("FreeType Project original fixture credit.\n")
        self.receipt = {"schemaVersion": 1, "rid": "linux-x64", "pin": PIN,
                        "library": str(library), "include": str(include),
                        "sha256": VERIFY.digest(library), "verifiedObjects": 1,
                        "headers": VERIFY.inventory(include), "notices": notices,
                        "creditSha256": VERIFY.digest(legal / "NOTICE.txt"),
                        "publicApiProbe": {"version": PIN["version"], "interpreters": [35, 40],
                                           "independentPolicies": True, "invalidPolicyRejected": True},
                        "glyphProbe": {"glyphBatchControls": True, "nativeHintsObserved": True,
                                       "slotDifferential": True, "nativeFaultAtomicity": True, "fixedWidthTransport": True,
                                       "actualDeviceFrame": True, "hintedProjectionSIMD": True},
                        "transportProbe": {"fixedWidthTransport": True, "exactIntegerDifferential": True,
                                           "atomicTailControls": True},
                        "cacheProbe": {"boundedOwnedCache": True, "exactGenerationReuse": True,
                                       "nativeFaultAtomicity": True}}
        self.manifest = install / "progpu-freetype.json"
        self.save()
        self.release = patch.object(VERIFY, "verify_source")
        self.verify_release = self.release.start()
        self.addCleanup(self.release.stop)

    def save(self):
        self.manifest.write_text(json.dumps(self.receipt))

    def validate(self, rid="linux-x64"):
        return VERIFY.validate_install(str(self.manifest), rid)

    def test_exact_install_revalidates_release_without_changing_any_file(self):
        before = VERIFY.inventory(self.workspace)
        result = self.validate()
        self.verify_release.assert_called_once_with(self.workspace, PIN)
        self.assertEqual("linux-x64", result["rid"])
        self.assertEqual(before, VERIFY.inventory(self.workspace))

    def test_wrong_pin_rid_schema_and_object_count_reject(self):
        for key, value in (("pin", {}), ("rid", "linux-arm64"), ("schemaVersion", True),
                           ("verifiedObjects", True), ("verifiedObjects", 2)):
            saved = self.receipt[key]
            self.receipt[key] = value
            self.save()
            with self.subTest(key=key, value=value), self.assertRaises(ValueError):
                self.validate()
            self.receipt[key] = saved

    def test_every_probe_is_fail_closed(self):
        for key in ("publicApiProbe", "glyphProbe", "transportProbe", "cacheProbe"):
            saved = self.receipt.pop(key)
            self.save()
            with self.subTest(key=key), self.assertRaises(ValueError):
                self.validate()
            self.receipt[key] = saved

    def test_numeric_true_is_not_a_successful_producer_control(self):
        self.receipt["cacheProbe"]["boundedOwnedCache"] = 1
        self.save()
        with self.assertRaises(ValueError): self.validate()

    def test_changed_actual_build_configuration_rejects(self):
        cache = self.workspace / "build/CMakeCache.txt"
        original = cache.read_text()
        for old, new in (("Release", "Debug"), ("SHARED_LIBS:BOOL=OFF", "SHARED_LIBS:BOOL=ON"),
                         ("INDEPENDENT_CODE:BOOL=ON", "INDEPENDENT_CODE:BOOL=OFF"),
                         ("FT_DISABLE_PNG:BOOL=TRUE", "FT_DISABLE_PNG:BOOL=FALSE"),
                         ("MultiThreadedDLL", "MultiThreaded")):
            cache.write_text(original.replace(old, new))
            with self.subTest(old=old), self.assertRaises(ValueError): self.validate()
        cache.write_text(original + "BUILD_SHARED_LIBS:BOOL=OFF\n")
        with self.assertRaisesRegex(ValueError, "Duplicate"): self.validate()

    def test_forged_new_hash_does_not_replace_architecture_validation(self):
        library = Path(self.receipt["library"])
        data = bytearray(library.read_bytes())
        struct.pack_into("<H", data, 8 + 60 + 18, 183)
        library.write_bytes(data)
        self.receipt["sha256"] = hashlib.sha256(data).hexdigest()
        self.save()
        with self.assertRaisesRegex(ValueError, "does not match"):
            self.validate()

    def test_altered_added_missing_and_redirected_headers_reject(self):
        include = Path(self.receipt["include"])
        header = include / "ft2build.h"
        original = header.read_bytes()
        header.write_bytes(original + b"changed")
        with self.assertRaises(ValueError): self.validate()
        header.write_bytes(original)
        extra = include / "extra.h"
        extra.write_text("unlisted")
        with self.assertRaises(ValueError): self.validate()
        extra.unlink()
        header.unlink()
        with self.assertRaises(ValueError): self.validate()
        header.write_bytes(original)
        target = include / "copied.h"
        target.write_bytes(original)
        header.unlink()
        header.symlink_to(target)
        with self.assertRaises(ValueError): self.validate()

    def test_notices_credit_source_and_extra_legal_files_reject(self):
        legal = self.manifest.parent / "share/progpu-freetype/licenses"
        for path in (legal / self.receipt["notices"][0]["notice"], legal / "NOTICE.txt",
                     self.workspace / "source" / self.receipt["notices"][0]["source"]):
            original = path.read_bytes()
            path.write_bytes(original + b"changed")
            with self.subTest(path=path), self.assertRaises(ValueError): self.validate()
            path.write_bytes(original)
        (legal / "undeclared.txt").write_text("unlisted")
        with self.assertRaises(ValueError): self.validate()

    def test_manifest_input_redirection_and_duplicate_keys_reject(self):
        self.receipt["library"] = self.receipt["include"] + "/ft2build.h"
        self.save()
        with self.assertRaises(ValueError): self.validate()
        self.manifest.write_text('{"schemaVersion":1,"schemaVersion":1}')
        with self.assertRaisesRegex(ValueError, "Duplicate"): self.validate()

    def test_signature_rejection_is_not_ignored(self):
        self.verify_release.side_effect = ValueError("bad release signature")
        with self.assertRaisesRegex(ValueError, "bad release signature"):
            self.validate()

    def test_staging_requires_the_actual_product_configuration_input(self):
        build = self.workspace / "product"
        build.mkdir()
        cache = build / "CMakeCache.txt"
        original = "PROGPU_NATIVE_FREETYPE_MANIFEST:FILEPATH=" + self.manifest.as_posix() + \
            "\nPROGPU_NATIVE_FREETYPE_RID:STRING=linux-x64\n"
        cache.write_text(original)
        VERIFY.verify_product_input(str(build), str(self.manifest), "linux-x64")
        cache.write_text(original.replace("linux-x64", "linux-arm64"))
        with self.assertRaises(ValueError): VERIFY.verify_product_input(str(build), str(self.manifest), "linux-x64")
        cache.write_text(original.replace(self.manifest.as_posix(), ""))
        with self.assertRaises(ValueError): VERIFY.verify_product_input(str(build), str(self.manifest), "linux-x64")

    def test_staging_keeps_notices_and_archive_and_never_overwrites_targets(self):
        destination = self.workspace / "package/runtimes/linux-x64/native"
        destination.mkdir(parents=True)
        result = VERIFY.stage_install(str(self.manifest), "linux-x64", str(destination))
        self.assertEqual(Path(self.receipt["library"]).read_bytes(), Path(result["library"]).read_bytes())
        self.assertEqual(VERIFY.inventory(self.manifest.parent / "share/progpu-freetype/licenses"),
                         VERIFY.inventory(Path(result["notices"])))
        self.assertEqual("linux-x64", VERIFY.validate_staged(str(destination), "linux-x64")["rid"])
        before = VERIFY.inventory(destination)
        with self.assertRaisesRegex(ValueError, "overwrite"):
            VERIFY.stage_install(str(self.manifest), "linux-x64", str(destination))
        self.assertEqual(before, VERIFY.inventory(destination))

    def test_staged_architecture_pin_and_notice_admission_is_independent(self):
        destination = self.workspace / "package/runtimes/linux-x64/native"
        destination.mkdir(parents=True)
        result = VERIFY.stage_install(str(self.manifest), "linux-x64", str(destination))
        marker = Path(result["metadata"])
        metadata = json.loads(marker.read_text())
        metadata["pin"]["version"] = "changed"
        marker.write_text(json.dumps(metadata))
        with self.assertRaisesRegex(ValueError, "identity"): VERIFY.validate_staged(str(destination), "linux-x64")
        metadata["pin"] = PIN
        archive = Path(result["library"])
        data = bytearray(archive.read_bytes())
        struct.pack_into("<H", data, 8 + 60 + 18, 183)
        archive.write_bytes(data)
        metadata["sha256"] = VERIFY.digest(archive)
        marker.write_text(json.dumps(metadata))
        with self.assertRaisesRegex(ValueError, "does not match"): VERIFY.validate_staged(str(destination), "linux-x64")
        archive.write_bytes(Path(self.receipt["library"]).read_bytes())
        metadata["sha256"] = VERIFY.digest(archive)
        marker.write_text(json.dumps(metadata))
        credit = Path(result["notices"]) / "NOTICE.txt"
        credit.write_text("missing original credit")
        with self.assertRaisesRegex(ValueError, "notices"): VERIFY.validate_staged(str(destination), "linux-x64")

    def test_partial_six_rid_package_never_qualifies(self):
        package = self.workspace / "package"
        package.mkdir()
        self.assertEqual([], VERIFY.validate_package(str(package), False))
        with self.assertRaises(FileNotFoundError): VERIFY.validate_package(str(package), True)
        destination = package / "runtimes/linux-x64/native"
        destination.mkdir(parents=True)
        VERIFY.stage_install(str(self.manifest), "linux-x64", str(destination))
        with self.assertRaises(FileNotFoundError): VERIFY.validate_package(str(package), False)

    def test_one_conflicting_notice_prevents_any_archive_or_marker_write(self):
        destination = self.workspace / "package/runtimes/linux-x64/native"
        legal = destination / "licenses/freetype"
        legal.mkdir(parents=True)
        (legal / "caller-data").write_text("preserve")
        before = VERIFY.inventory(destination)
        with self.assertRaisesRegex(ValueError, "overwrite"):
            VERIFY.stage_install(str(self.manifest), "linux-x64", str(destination))
        self.assertEqual(before, VERIFY.inventory(destination))


if __name__ == "__main__":
    unittest.main()
