"""Schema rejection controls only; these fixtures are not native EDIT evidence."""

import copy
import importlib.util
import json
from pathlib import Path
from types import SimpleNamespace
import unittest
from unittest.mock import patch

SPEC = importlib.util.spec_from_file_location("edit_observer", Path(__file__).parents[1] / "progpu-observe-edit-word-policy.py")
OBSERVER = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(OBSERVER)


class EditPolicyReceiptTests(unittest.TestCase):
    def receipt(self):
        text = "a\U0001f600b "
        source = OBSERVER.utf16(text)
        attributes = [{"index": index, "utf16": unit, "raw": 4, "reserved": 0,
                       "softBreak": False, "whiteSpace": False, "charStop": True,
                       "wordStop": False, "invalid": False} for index, unit in enumerate(source)]
        types = [{"index": index, "utf16": unit, "raw": 0, "space": False, "punctuation": False,
                  "control": False, "blank": False, "alphabetic": False} for index, unit in enumerate(source)]
        bidi_types = [{"index": index, "utf16": unit, "raw": 0} for index, unit in enumerate(source)]
        processing_types = []
        for index, unit in enumerate(source):
            value = {"index": index, "utf16": unit, "raw": 0, "reserved": 0}
            value.update({name: False for name in ("nonspacing", "diacritic", "vowelMark", "symbol",
                "katakana", "hiragana", "halfWidth", "fullWidth", "ideograph", "kashida", "lexical",
                "highSurrogate", "lowSurrogate", "alphabetic")})
            processing_types.append(value)
        observation = {"completed": True, "actualText": text, "actualUtf16": source, "flags": 0x40,
                       "hdc": 0, "charset": -1, "attributeByteSize": 1, "analyseHResult": 0, "freeHResult": 0,
                       "rawBytes": [4] * len(source), "attributes": attributes,
                       "classification": {"completed": True, "terminalPosition": len(source), "itemByteSize": 8,
                           "characterTypesSucceeded": True, "ctype1": types,
                           "ctype2Succeeded": True, "ctype2Error": 0, "ctype2": bidi_types,
                           "ctype3Succeeded": True, "ctype3Error": 0, "ctype3": processing_types,
                           "runs": [{"start": 0, "end": len(source), "script": 5, "rawAnalysis": 5,
                                     "rawPropertiesFirst": 0, "needsWordBreaking": False,
                                     "directScriptBreak": {"hResult": 0, "inputStart": 0,
                                         "inputUtf16": source, "rawBytes": [4] * len(source)}}]}}
        case = {"requestedText": text, "requestedUtf16": source, "scriptBreak": observation,
                "Indices": [0], "gestures": [{"actualText": text, "actualUtf16": source,
                    "coordinateUnavailable": True, "anchor": None, "steps": []} for _ in range(2)]}
        cases = []
        for index in range(72):
            value = copy.deepcopy(case)
            value["Name"] = "schema-control-" + str(index)
            cases.append(value)
        return {"schema": "native-edit-word-selection-v1", "completed": True, "error": None,
                "hostShown": False, "desktopQualified": False, "physicalInputQualified": False,
                "expectedCases": 72, "cases": cases, "identity": {"architecture": "X64", "processId": 7,
                    "framework": "schema-only", "os": "schema-only", "osBuild": "schema-only",
                    "locale": "en-US", "uiLocale": "en-US", "probeSha256": "hash",
                    "formsPath": "Microsoft.WindowsDesktop.App/System.Windows.Forms.dll", "formsSha256": "hash",
                    "nativeTextModules": [{"name": "usp10.dll", "path": "usp10.dll",
                        "fileSha256": "hash", "fileVersion": "schema-only"}]}}

    def verify(self, receipt):
        with patch.object(Path, "is_file", return_value=True), patch.object(Path, "stat", return_value=SimpleNamespace(st_size=1)), \
                patch.object(Path, "read_text", return_value=json.dumps(receipt)), patch.object(OBSERVER, "sha", return_value="hash"):
            return OBSERVER.verify_receipt(Path("control.json"), "contexts", {"pid": 7}, Path("control.dll"))

    def test_schema_only_control_is_accepted(self):
        self.assertEqual(self.verify(self.receipt())["cases"], 72)

    def test_native_bitfield_invention_is_rejected(self):
        value = self.receipt()
        value["cases"][0]["scriptBreak"]["attributes"][0]["wordStop"] = True
        with self.assertRaisesRegex(ValueError, "bitfield"):
            self.verify(value)

    def test_utf16_surrogate_tail_rewrite_is_rejected(self):
        value = self.receipt()
        value["cases"][0]["scriptBreak"]["actualUtf16"][2] = 0
        with self.assertRaisesRegex(ValueError, "UTF16|source/API"):
            self.verify(value)

    def test_fneedswordbreaking_wrong_bit_is_rejected(self):
        value = self.receipt()
        value["cases"][0]["scriptBreak"]["classification"]["runs"][0]["rawPropertiesFirst"] = 1 << 18
        with self.assertRaisesRegex(ValueError, "property bit"):
            self.verify(value)

    def test_direct_item_input_rewrite_is_rejected(self):
        value = self.receipt()
        value["cases"][0]["scriptBreak"]["classification"]["runs"][0]["directScriptBreak"]["inputStart"] = 1
        with self.assertRaisesRegex(ValueError, "ScriptBreak coverage"):
            self.verify(value)

    def test_duplicate_context_is_rejected(self):
        value = self.receipt()
        value["cases"][1]["Name"] = value["cases"][0]["Name"]
        with self.assertRaisesRegex(ValueError, "duplicate"):
            self.verify(value)

    def test_ctype2_original_unit_rewrite_is_rejected(self):
        value = self.receipt()
        value["cases"][0]["scriptBreak"]["classification"]["ctype2"][2]["utf16"] = 0
        with self.assertRaisesRegex(ValueError, "CTYPE2 unit identity"):
            self.verify(value)

    def test_ctype3_missing_unit_is_rejected(self):
        value = self.receipt()
        value["cases"][0]["scriptBreak"]["classification"]["ctype3"].pop()
        with self.assertRaisesRegex(ValueError, "CTYPE3 coverage"):
            self.verify(value)

    def test_ctype3_property_invention_is_rejected(self):
        value = self.receipt()
        value["cases"][0]["scriptBreak"]["classification"]["ctype3"][0]["ideograph"] = True
        with self.assertRaisesRegex(ValueError, "CTYPE3 bit interpretation"):
            self.verify(value)

    def test_ctype3_reserved_bits_are_preserved(self):
        value = self.receipt()
        item = value["cases"][0]["scriptBreak"]["classification"]["ctype3"][0]
        item["raw"] = item["reserved"] = 0x6000
        self.assertEqual(self.verify(value)["cases"], 72)
        item["reserved"] = 0
        with self.assertRaisesRegex(ValueError, "CTYPE3 reserved identity"):
            self.verify(value)

    def test_nonmatching_process_or_qualification_is_rejected(self):
        for key, replacement in (("processId", 8), ("architecture", "Arm64")):
            value = self.receipt()
            value["identity"][key] = replacement
            with self.assertRaisesRegex(ValueError, "identity mismatch"):
                self.verify(value)
        value = self.receipt()
        value["desktopQualified"] = True
        with self.assertRaisesRegex(ValueError, "incorrectly qualified"):
            self.verify(value)

    def test_unavailable_native_coordinate_cannot_have_gestures(self):
        value = self.receipt()
        value["cases"][0]["gestures"][0]["steps"] = [{"name": "invented"}]
        with self.assertRaisesRegex(ValueError, "invented gestures"):
            self.verify(value)


if __name__ == "__main__":
    unittest.main()
