"""Generator rejection controls only; synthetic records are not native evidence."""

import copy
import hashlib
import importlib.util
import json
from pathlib import Path
from types import SimpleNamespace
import unittest


SPEC = importlib.util.spec_from_file_location("edit_symbols", Path(__file__).parents[1] /
    "progpu-generate-edit-word-symbol-profile.py")
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


class MeasuredSymbolGeneratorTests(unittest.TestCase):
    def fixture(self):
        # Three synthetic rawID members: one AL, one ID, one nonmatching.
        # They deliberately do not use the production fixture words/scalars.
        values = ((100, ((), (), (1, 2))), (101, ((1, 2), (1, 3), (1, 2))),
                  (102, ((1,), (1,), (1,))))
        cases = []
        for scalar, signature in values:
            for context, positions in zip(("latin", "variation", "cjk"), signature):
                source = {"latin": [97, scalar, 98, 32], "variation": [97, scalar, 65039, 98, 32],
                          "cjk": [19968, scalar, 20108, 32]}[context]
                cases.append({"scalar": scalar, "name": f"bmp-{scalar:04X}-{context}",
                    "requestedText": "".join(chr(unit) for unit in source), "requestedUtf16": source,
                    "runtimeCategory": "OtherSymbol", "scriptBreak": {
                        "actualUtf16": source, "completed": True, "analyseHResult": 0,
                        "freeHResult": 0, "flags": 0x40,
                        "rawBytes": [5 if index in positions else 4 for index in range(len(source))]}})
        receipt = {"schema": "native-edit-symbol-attributes-v1", "completed": True,
            "editGesturesObserved": False, "error": None, "desktopQualified": False,
            "physicalInputQualified": False, "symbolCount": 3, "expectedCases": 9,
            "identity": {"osBuild": "schema-only", "architecture": "schema-only",
                         "locale": "schema-only", "framework": "schema-only"}, "cases": cases}
        unicode = b"array<std::uint32_t, 3> unicode_line_break_ranges{100U, 102U, 25U}"
        pin = {"schema": "native-edit-word-symbol-profile-v1", "sourceHead": "schema-only",
            "sourceRun": 1, "sourceJob": 2, "symbolCount": 3, "caseCount": 9,
            "alphabeticCount": 1, "ideographicCount": 1, "unqualifiedCount": 1,
            "ordinaryProviderQualified": False, "allUnicodeQualified": False,
            "unicodeSourceSha256": hashlib.sha256(unicode).hexdigest(), **receipt["identity"]}
        return receipt, pin, unicode

    def generate(self, receipt, pin, unicode, *, stale_hash=False):
        raw = json.dumps(receipt).encode()
        pin = copy.deepcopy(pin)
        pin["receiptSha256"] = "0" * 64 if stale_hash else hashlib.sha256(raw).hexdigest()
        return GENERATOR.generate(Input(raw), Input(json.dumps(pin).encode()), Input(unicode))

    def test_exact_generation_is_reproducible_and_does_not_fill_unknown_gap(self):
        fixture = self.fixture()
        actual = self.generate(*fixture)
        self.assertEqual(actual, self.generate(*fixture))
        self.assertIn("100U, 100U, 3U", actual)
        self.assertIn("101U, 101U, 25U", actual)
        self.assertNotIn("102U,", actual)

    def test_receipt_hash_rejects_replacement(self):
        with self.assertRaisesRegex(ValueError, "exact complete pinned"):
            self.generate(*self.fixture(), stale_hash=True)

    def test_unicode_source_hash_rejects_replacement(self):
        receipt, pin, unicode = self.fixture()
        with self.assertRaisesRegex(ValueError, "property source changed"):
            self.generate(receipt, pin, unicode + b" ")

    def test_extra_source_tail_is_not_silently_truncated(self):
        receipt, pin, unicode = self.fixture()
        receipt["cases"][0]["requestedText"] += "x"
        with self.assertRaisesRegex(ValueError, "native input/API"):
            self.generate(receipt, pin, unicode)

    def test_duplicate_and_missing_contexts_are_rejected(self):
        for change in (lambda cases: cases.__setitem__(1, copy.deepcopy(cases[0])), lambda cases: cases.pop()):
            receipt, pin, unicode = self.fixture()
            change(receipt["cases"])
            with self.assertRaisesRegex(ValueError, "Duplicate|Incomplete"):
                self.generate(receipt, pin, unicode)

    def test_native_failures_and_raw_unit_loss_are_rejected(self):
        for key, value in (("analyseHResult", -1), ("freeHResult", -1),
                           ("completed", False), ("flags", 0), ("rawBytes", [4])):
            receipt, pin, unicode = self.fixture()
            receipt["cases"][0]["scriptBreak"][key] = value
            with self.assertRaisesRegex(ValueError, "native input/API|unit coverage"):
                self.generate(receipt, pin, unicode)

    def test_platform_and_qualification_changes_are_rejected(self):
        receipt, pin, unicode = self.fixture()
        receipt["identity"]["architecture"] = "different"
        with self.assertRaisesRegex(ValueError, "domain/platform"):
            self.generate(receipt, pin, unicode)
        receipt, pin, unicode = self.fixture()
        pin["ordinaryProviderQualified"] = True
        with self.assertRaisesRegex(ValueError, "cannot admit"):
            self.generate(receipt, pin, unicode)

    def test_nonmatching_member_cannot_be_promoted_to_qualified(self):
        receipt, pin, unicode = self.fixture()
        for case, positions in zip(receipt["cases"][-3:], ((1, 2), (1, 3), (1, 2))):
            case["scriptBreak"]["rawBytes"] = [5 if index in positions else 4
                for index in range(len(case["requestedUtf16"]))]
        with self.assertRaisesRegex(ValueError, "domain changed"):
            self.generate(receipt, pin, unicode)


if __name__ == "__main__":
    unittest.main()
