"""Schema rejection controls only; these fixtures are not native EDIT evidence."""

import copy
import base64
import hashlib
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
    def receipt(self, text="a\U0001f600b "):
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

    def verify(self, receipt, mode="contexts"):
        with patch.object(Path, "is_file", return_value=True), patch.object(Path, "stat", return_value=SimpleNamespace(st_size=1)), \
                patch.object(Path, "read_text", return_value=json.dumps(receipt)), patch.object(OBSERVER, "sha", return_value="hash"):
            return OBSERVER.verify_receipt(Path("control.json"), mode, {"pid": 7}, Path("control.dll"))

    def test_schema_only_control_is_accepted(self):
        self.assertEqual(self.verify(self.receipt())["cases"], 72)

    def test_complete_source_role_schema_preserves_all_unknown_observations(self):
        value = self.receipt()
        value["cases"] = [copy.deepcopy(value["cases"][0]) for _ in range(128)]
        for index, case in enumerate(value["cases"]):
            case["Name"] = "schema-role-" + str(index)
        value["expectedCases"] = 128
        self.assertEqual(self.verify(value, "source-roles")["cases"], 128)
        value["cases"].pop()
        value["expectedCases"] = 127
        with self.assertRaisesRegex(ValueError, "Source-role inventory"):
            self.verify(value, "source-roles")

    def test_source_role_direction_cannot_change_native_analysis_flags(self):
        value = self.receipt()
        value["cases"] = [copy.deepcopy(value["cases"][0]) for _ in range(128)]
        for index, case in enumerate(value["cases"]):
            case["Name"] = "schema-role-" + str(index)
        value["expectedCases"] = 128
        value["cases"][0]["RightToLeft"] = True
        with self.assertRaisesRegex(ValueError, "source/API"):
            self.verify(value, "source-roles")

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


class SelectionGeometryReceiptTests(unittest.TestCase):
    """Synthetic schema controls, not measured Windows coordinates or pixels."""

    def receipt(self):
        value = EditPolicyReceiptTests().receipt()
        value.update(schema="native-edit-selection-geometry-v1", hostShown=True, ownedWindowOnly=True,
                     fallbackFontIdentityQualified=False, expectedCases=5, geometryComplete=True,
                     caretSamples=190, unavailableCaretSamples=0, cases=[])
        descriptor = dict(Height=-11, Width=0, Escapement=0, Orientation=0, Weight=400,
                          Italic=0, Underline=0, StrikeOut=0, CharSet=1, OutPrecision=0,
                          ClipPrecision=0, Quality=0, PitchAndFamily=0, FaceName="schema-only")
        for name, (source, seams) in OBSERVER.GEOMETRY_INPUTS.items():
            units = OBSERVER.utf16(source)
            length = len(units)
            case = EditPolicyReceiptTests().receipt(source)["cases"][0]
            case.update(Name=name, RightToLeft=(name == "bidi-rtl"), requestedSeams=seams,
                        observations=[], metadata={"handle": 17, "nativeClass": "EDIT", "deviceDpi": 96,
                            "clientSize": {"Width": 2, "Height": 2}, "fallbackFontIdentityQualified": False,
                            "ownedCaretSamples": 38, "systemColors": [{"index": i, "colorRef": 0x112233}
                                for i in (5, 8, 13, 14, 15, 18)],
                            "baseFont": {"handle": 23, "borrowed": True, "fallbackIdentityQualified": False,
                                "descriptor": copy.deepcopy(descriptor), "byteCount": 128, "sha256": "a" * 64,
                                "DeviceDpi": 96}})
            if name == "bidi-rtl":
                case["scriptBreak"]["flags"] |= 0x100
            a, b, c = seams
            requested = [(f"collapsed-{p}", p, p) for p in (0, a, b, c, length)]
            for start, end in ((a, b), (b, c), (a, c)):
                requested += [(f"forward-{start}-{end}", start, end), (f"reverse-{end}-{start}", end, start)]
            for index, (label, anchor, active) in enumerate(requested + [(label, 0, 0) for label in (
                    "first-down", "first-up", "double-down", "drag-last", "drag-first-reversal",
                    "drag-anchor-reversal", "drag-last-again", "final-up")]):
                start, end = sorted((anchor, active))
                text = b"".join(unit.to_bytes(2, "little") for unit in units[start:end]).decode("utf-16-le", "surrogatepass")
                state = {"Selection": {"Start": start, "End": end, "ManagedStart": start,
                            "ManagedLength": end - start, "SelectedText": text, "Focused": True},
                    "Caret": {"Available": True, "UnavailableReason": None, "GuiQuerySucceeded": True,
                        "GuiError": 0, "CaretOwner": 17, "FocusOwner": 17, "Flags": 0,
                        "Rectangle": {"Left": 0, "Top": 0, "Right": 1, "Bottom": 2},
                        "PointQuerySucceeded": True, "PointError": 0, "Point": {"X": 0, "Y": 0}},
                    "Client": {"Left": 0, "Top": 0, "Right": 2, "Bottom": 2},
                    "Format": {"Left": 0, "Top": 0, "Right": 2, "Bottom": 2}, "FirstVisibleRaw": 0,
                    "Positions": [{"Index": p, "Raw": 0 if p < length else -1,
                        "Point": {"X": 0, "Y": 0} if p < length else None} for p in range(length + 1)],
                    "Hits": [{"X": x, "Y": 0, "Raw": x, "Character": x, "Line": 0} for x in range(2)]}
                for key in ("HorizontalScroll", "VerticalScroll"):
                    state[key] = {"Available": False, "Error": 0, "Minimum": 0, "Maximum": 0,
                                  "Page": 0, "Position": 0, "TrackPosition": 0}
                request = {"kind": "set-selection", "requestedAnchor": anchor, "requestedActive": active, "result": 0}
                if index >= 11:
                    request = {"kind": "gesture", "message": [0x201, 0x202, 0x203, 0x200, 0x200, 0x200, 0x200, 0x202][index - 11],
                               "callbacks": [{"name": callback, "step": label} for callback in ("WndProc-enter", "WndProc-return")]}
                raw = bytes((0x12, 0x34, 0x56, 0xA5)) * 4
                pixels = {"Width": 2, "Height": 2, "Stride": 8, "Format": "BGRX8-top-down-original-bytes",
                          "Bytes": base64.b64encode(raw).decode(), "Sha256": hashlib.sha256(raw).hexdigest(),
                          "ClearControlBytes": base64.b64encode(raw).decode(), "ClearControlSha256": hashlib.sha256(raw).hexdigest(),
                          "RgbIndependentOfClear": True, "RgbClearDifferences": 0, "FirstPrintResult": 0, "SecondPrintResult": 0}
                case["observations"].append({"name": label, "request": request, "beforeScroll": copy.deepcopy(state),
                    "scrollCaretResult": 0, "afterScroll": copy.deepcopy(state), "pixels": pixels})
            value["cases"].append(case)
        return value

    def sample(self, value, index=0):
        return value["cases"][0]["observations"][index]

    def test_complete_geometry_schema_is_accepted_through_actual_phase_verifier(self):
        self.assertEqual(EditPolicyReceiptTests().verify(self.receipt(), "selection-geometry")["cases"], 5)

    def test_unavailable_caret_is_explicit_and_does_not_claim_complete_geometry(self):
        value = self.receipt()
        caret = self.sample(value)["afterScroll"]["Caret"]
        caret.update(Available=False, UnavailableReason="No owned caret", Rectangle=None, Point=None, CaretOwner=0)
        value["unavailableCaretSamples"] = 1
        value["geometryComplete"] = False
        value["cases"][0]["metadata"]["ownedCaretSamples"] -= 1
        OBSERVER.verify_geometry(value)
        value["geometryComplete"] = True
        with self.assertRaisesRegex(ValueError, "completeness"):
            OBSERVER.verify_geometry(value)

    def test_unavailable_caret_cannot_publish_point(self):
        value = self.receipt()
        self.sample(value)["afterScroll"]["Caret"].update(Available=False, UnavailableReason="missing")
        with self.assertRaisesRegex(ValueError, "invented coordinates"):
            OBSERVER.verify_geometry(value)

    def test_foreign_caret_is_not_owned_evidence(self):
        value = self.receipt()
        self.sample(value)["afterScroll"]["Caret"]["CaretOwner"] = 99
        with self.assertRaisesRegex(ValueError, "Owned native caret"):
            OBSERVER.verify_geometry(value)

    def test_source_units_and_direction_are_exact(self):
        for field, replacement in (("requestedUtf16", [1]), ("RightToLeft", True), ("requestedSeams", [4, 6, 9])):
            value = self.receipt()
            value["cases"][0][field] = replacement
            with self.assertRaisesRegex(ValueError, "source/seams/direction"):
                OBSERVER.verify_geometry(value)

    def test_reverse_selection_request_is_not_normalized(self):
        value = self.receipt()
        self.sample(value, 6)["request"].update(requestedAnchor=4, requestedActive=7)
        with self.assertRaisesRegex(ValueError, "directed selection identity"):
            OBSERVER.verify_geometry(value)

    def test_changed_original_response_is_preserved_without_rewriting_request(self):
        value = self.receipt()
        sample = self.sample(value, 2)
        requested = copy.deepcopy(sample["request"])
        for key in ("beforeScroll", "afterScroll"):
            sample[key]["Selection"].update(Start=8, End=8, ManagedStart=8)
        OBSERVER.verify_geometry(value)
        self.assertEqual(sample["request"], requested)
        self.assertEqual(sample["request"]["requestedAnchor"], 7)
        self.assertEqual(sample["request"]["requestedActive"], 7)
        self.assertEqual(sample["afterScroll"]["Selection"]["Start"], 8)
        sample["afterScroll"]["Selection"]["ManagedStart"] = 7
        with self.assertRaisesRegex(ValueError, "selected UTF16/focus identity"):
            OBSERVER.verify_geometry(value)

    def test_every_em_posfromchar_slot_including_unavailable_terminal_is_required(self):
        value = self.receipt()
        self.sample(value)["afterScroll"]["Positions"].pop()
        with self.assertRaisesRegex(ValueError, "inventory was filtered"):
            OBSERVER.verify_geometry(value)

    def test_raw_source_point_decoding_preserves_signed_coordinates(self):
        value = self.receipt()
        p = self.sample(value)["afterScroll"]["Positions"][0]
        p.update(Raw=0x0000FFFF, Point={"X": -1, "Y": 0})
        OBSERVER.verify_geometry(value)
        p["Point"]["X"] = 65535
        with self.assertRaisesRegex(ValueError, "decoding changed"):
            OBSERVER.verify_geometry(value)

    def test_integer_scan_cannot_drop_a_pixel_or_change_raw_hit(self):
        value = self.receipt()
        self.sample(value)["afterScroll"]["Hits"].pop()
        with self.assertRaisesRegex(ValueError, "scan is incomplete"):
            OBSERVER.verify_geometry(value)
        value = self.receipt()
        self.sample(value)["afterScroll"]["Hits"][0]["Character"] = 7
        with self.assertRaisesRegex(ValueError, "raw hit identity"):
            OBSERVER.verify_geometry(value)

    def test_scroll_status_and_message_results_are_not_optional(self):
        for where, key in (("afterScroll", "FirstVisibleRaw"), (None, "scrollCaretResult")):
            value = self.receipt()
            target = self.sample(value)[where] if where else self.sample(value)
            del target[key]
            with self.assertRaisesRegex(ValueError, "missing"):
                OBSERVER.verify_geometry(value)
        value = self.receipt()
        self.sample(value)["afterScroll"]["HorizontalScroll"].update(Available=True, Error=5)
        with self.assertRaisesRegex(ValueError, "scroll status"):
            OBSERVER.verify_geometry(value)

    def test_logfont_fields_and_os_colors_are_not_invented_or_omitted(self):
        value = self.receipt()
        del value["cases"][0]["metadata"]["baseFont"]["descriptor"]["CharSet"]
        with self.assertRaisesRegex(ValueError, "base-font"):
            OBSERVER.verify_geometry(value)
        value = self.receipt()
        value["cases"][0]["metadata"]["systemColors"][0]["colorRef"] = 0x1000000
        with self.assertRaisesRegex(ValueError, "system-color"):
            OBSERVER.verify_geometry(value)

    def test_fallback_identity_is_not_inferred_from_basefont_hash(self):
        value = self.receipt()
        value["cases"][0]["metadata"]["baseFont"]["fallbackIdentityQualified"] = True
        with self.assertRaisesRegex(ValueError, "base-font"):
            OBSERVER.verify_geometry(value)

    def test_clear_independence_is_verified_from_both_original_buffers(self):
        value = self.receipt()
        pixels = self.sample(value)["pixels"]
        raw = bytearray(base64.b64decode(pixels["ClearControlBytes"]))
        raw[0] ^= 1
        pixels.update(ClearControlBytes=base64.b64encode(raw).decode(), ClearControlSha256=hashlib.sha256(raw).hexdigest())
        with self.assertRaisesRegex(ValueError, "unpainted clear"):
            OBSERVER.verify_geometry(value)

    def test_original_bgrx_alpha_is_preserved_not_normalized_or_compared_as_rgba(self):
        value = self.receipt()
        pixels = self.sample(value)["pixels"]
        raw = bytearray(base64.b64decode(pixels["ClearControlBytes"]))
        raw[3] = 0
        pixels.update(ClearControlBytes=base64.b64encode(raw).decode(), ClearControlSha256=hashlib.sha256(raw).hexdigest())
        OBSERVER.verify_geometry(value)
        pixels["ClearControlSha256"] = "0" * 64
        with self.assertRaisesRegex(ValueError, "pixels changed"):
            OBSERVER.verify_geometry(value)

    def test_gesture_delivery_and_original_capture_count_cannot_disappear(self):
        value = self.receipt()
        self.sample(value, 13)["request"]["message"] = 0x201
        with self.assertRaisesRegex(ValueError, "gesture delivery"):
            OBSERVER.verify_geometry(value)
        value = self.receipt()
        value["cases"][0]["observations"].pop()
        with self.assertRaisesRegex(ValueError, "geometry inventory"):
            OBSERVER.verify_geometry(value)

    def test_original_case_inventory_hash_and_deadlines_are_preserved(self):
        root = Path(__file__).parents[1]
        source = (root / "NativeEditWordPolicyReference/WordSelectionReference.cs").read_text()
        start = source.index("    private static readonly Case[] Cases =")
        inventory = source[start:source.index("    ];", start) + 7]
        self.assertEqual(hashlib.sha256(inventory.encode()).hexdigest(), OBSERVER.ORIGINAL_CASE_SOURCE_SHA256)
        self.assertTrue("budget.Elapsed > TimeSpan.FromSeconds(30)" in source)
        driver = (root / "progpu-observe-edit-word-policy.py").read_text()
        self.assertIn('mode, str(receipt)], 60)', driver)
        geometry = (root / "NativeEditWordPolicyReference/SelectionGeometryReference.cs").read_text()
        self.assertIn("128L * 1024 * 1024", geometry)


if __name__ == "__main__":
    unittest.main()
