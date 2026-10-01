"""Synthetic generator schema controls, not original native item evidence."""

import copy
import hashlib
import importlib.util
import json
from pathlib import Path
from types import SimpleNamespace
import unittest


SPEC = importlib.util.spec_from_file_location("edit_items", Path(__file__).parents[1] /
    "progpu-generate-edit-word-item-profile.py")
GENERATOR = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(GENERATOR)


class Input:
    def __init__(self, data):
        self.data = data

    def read_text(self):
        return self.data.decode()

    def read_bytes(self):
        return self.data

    def stat(self):
        return SimpleNamespace(st_size=len(self.data))


class Evidence:
    def __init__(self, files):
        self.files = files

    def __truediv__(self, filename):
        return Input(self.files[filename])


class ItemPropertyGeneratorTests(unittest.TestCase):
    def fixture(self):
        case = {"Name": "schema-only", "requestedText": "de", "requestedUtf16": [100, 101],
            "scriptBreak": {"completed": True, "analyseHResult": 0, "freeHResult": 0,
                "actualUtf16": [100, 101], "flags": 0x40, "classification": {
                    "completed": True, "itemByteSize": 8, "terminalPosition": 2, "runs": [
                        {"start": 0, "end": 1, "script": 26, "rawAnalysis": 26,
                         "directScriptBreak": {"hResult": 0, "inputStart": 0, "inputUtf16": [100], "rawBytes": [13]}},
                        {"start": 1, "end": 2, "script": 24, "rawAnalysis": 24,
                         "directScriptBreak": {"hResult": 0, "inputStart": 1, "inputUtf16": [101], "rawBytes": [4]}}]}}}
        receipt = {"schema": "native-edit-word-selection-v1", "completed": True, "error": None,
            "desktopQualified": False, "physicalInputQualified": False, "hostShown": False,
            "expectedCases": 1, "cases": [case], "identity": {"osBuild": "schema-only",
                "architecture": "schema-only", "locale": "schema-only", "framework": "schema-only"}}
        # Deliberately synthetic script ownership. This validates the generator
        # protocol, never a real Unicode property or a native product paragraph.
        source = b"array<std::uint32_t, 2> unicode_script_tags{1634885986U, 1751474802U}\n" \
            b"array<std::uint32_t, 6> unicode_script_ranges{100U, 100U, 0U, 101U, 101U, 1U}"
        pin = {"schema": "native-edit-word-item-profile-v1", "sourceHead": "schema-only",
            "sourceRun": 1, "sourceJob": 2, "unicodeSourceSha256": hashlib.sha256(source).hexdigest(),
            "scriptDomains": ["arab", "hebr"], "trainingCases": 1, "observedScalars": 2,
            "heldoutItemCases": 0, "engines": {"26": {"flags": 1, "firstSoft": True},
                                               "24": {"flags": 2, "firstSoft": False}},
            "ordinaryProviderQualified": False, "allUnicodeQualified": False, **receipt["identity"]}
        return receipt, pin, source

    def generate(self, receipt, pin, source, filename="contexts.json", stale_hash=False):
        raw = json.dumps(receipt).encode()
        pin = copy.deepcopy(pin)
        pin["receipts"] = {filename: "0" * 64 if stale_hash else hashlib.sha256(raw).hexdigest()}
        return GENERATOR.generate(Evidence({filename: raw}), Input(json.dumps(pin).encode()), Input(source))

    def test_reproducible_scalar_properties_are_not_word_positions(self):
        fixture = self.fixture()
        actual = self.generate(*fixture)
        self.assertEqual(actual, self.generate(*fixture))
        self.assertIn("100U, 100U, 26U, 1U", actual)
        self.assertIn("101U, 101U, 24U, 2U", actual)

    def test_receipt_and_original_property_hashes_are_authoritative(self):
        with self.assertRaisesRegex(ValueError, "exact complete pinned"):
            self.generate(*self.fixture(), stale_hash=True)
        receipt, pin, source = self.fixture()
        with self.assertRaisesRegex(ValueError, "script source changed"):
            self.generate(receipt, pin, source + b" ")

    def test_no_partial_or_rewritten_original_item_is_accepted(self):
        for key, value in (("inputStart", 1), ("inputUtf16", [101]), ("hResult", -1), ("rawBytes", [])):
            receipt, pin, source = self.fixture()
            receipt["cases"][0]["scriptBreak"]["classification"]["runs"][0]["directScriptBreak"][key] = value
            with self.assertRaisesRegex(ValueError, "engine/input/attributes"):
                self.generate(receipt, pin, source)

    def test_native_partition_cannot_be_combined_or_truncated(self):
        receipt, pin, source = self.fixture()
        receipt["cases"][0]["scriptBreak"]["classification"]["runs"][1]["start"] = 0
        with self.assertRaisesRegex(ValueError, "partition changed"):
            self.generate(receipt, pin, source)

    def test_direct_entry_policy_cannot_be_guessed_or_widened(self):
        for flags, soft in ((3, True), (1, False)):
            receipt, pin, source = self.fixture()
            pin["engines"]["26"] = {"flags": flags, "firstSoft": soft}
            with self.assertRaisesRegex(ValueError, "entry behavior"):
                self.generate(receipt, pin, source)
        receipt, pin, source = self.fixture()
        pin["engines"]["24"]["flags"] = 6
        with self.assertRaisesRegex(ValueError, "whitespace exit was invented"):
            self.generate(receipt, pin, source)

    def test_ambiguous_scalar_engines_cannot_become_a_guessed_range(self):
        receipt, pin, source = self.fixture()
        case = receipt["cases"][0]
        case["requestedText"] = "dd"
        case["requestedUtf16"] = case["scriptBreak"]["actualUtf16"] = [100, 100]
        case["scriptBreak"]["classification"]["runs"][1]["directScriptBreak"]["inputUtf16"] = [100]
        pin["observedScalars"] = 1
        with self.assertRaisesRegex(ValueError, "ambiguous/unqualified engine"):
            self.generate(receipt, pin, source)

    def test_heldout_item_seams_do_not_train_properties(self):
        receipt, pin, source = self.fixture()
        receipt["cases"][0]["Name"] = "script-entry-arabic-latin-ltr"
        heldout = copy.deepcopy(receipt["cases"][0])
        heldout["Name"] = "script-entry-arabic-cjk-rtl"
        # Deliberately change its seam/engine record: generator never consumes
        # that held-out record. In real use the exact whole-receipt hash still
        # protects ALL bytes before selecting the declared training subset.
        heldout["scriptBreak"]["classification"]["runs"][0]["script"] = 999
        receipt["cases"].append(heldout)
        receipt["expectedCases"] = 2
        pin["heldoutItemCases"] = 1
        actual = self.generate(receipt, pin, source, "item-contexts.json")
        self.assertIn("100U, 100U, 26U, 1U", actual)
        self.assertNotIn("999U", actual)

    def test_platform_and_general_provider_claims_are_rejected(self):
        receipt, pin, source = self.fixture()
        receipt["identity"]["osBuild"] = "different"
        with self.assertRaisesRegex(ValueError, "platform is incomplete"):
            self.generate(receipt, pin, source)
        receipt, pin, source = self.fixture()
        pin["allUnicodeQualified"] = True
        with self.assertRaisesRegex(ValueError, "cannot qualify"):
            self.generate(receipt, pin, source)


if __name__ == "__main__":
    unittest.main()
