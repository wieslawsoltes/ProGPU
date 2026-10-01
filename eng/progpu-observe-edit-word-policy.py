#!/usr/bin/env python3
"""Bounded original Windows EDIT/Uniscribe observation, never product admission."""

import hashlib
import base64
import json
import os
from pathlib import Path
import subprocess
import sys

ORIGINAL_CASES = ["spaces", "punctuation", "tabs-single", "tabs-multiline", "crlf-single",
                  "crlf-multiline", "surrogate-combining", "bidi-ltr", "bidi-rtl", "password",
                  "read-only", "wrapped-multiline", "unicode-spaces", "symbols",
                  "isolated-breaks-single", "isolated-breaks-multiline", "crcrlf-multiline",
                  "wrapped-longword", "supplementary-scripts", "emoji-context", "thai-adjacent",
                  "thai-spaced", "lao-adjacent", "khmer-adjacent"]
ORIGINAL_CASE_SOURCE_SHA256 = "71a12a126cb712b7177094d26109c1f4c7950b1dfaa8ca9240b5b4582fe51e29"
GEOMETRY_INPUTS = {
    "emoji-context": ("a\u2603\ufe0fb\U0001f469\u200d\U0001f4bbc ", [4, 7, 9]),
    "supplementary-emoji-zwj": ("x\U0001f469\u200d\U0001f4bby ", [1, 4, 6]),
    "surrogate-combining": ("go A\U0001f600 e\u0301 fin ", [7, 8, 9]),
    "bidi-ltr": ("abc \u05d0\u05d1\u05d2, \u0639\u0631\u0628\u0649 end ", [4, 5, 7]),
    "bidi-rtl": ("abc \u05d0\u05d1\u05d2, \u0639\u0631\u0628\u0649 end ", [4, 5, 7]),
}


def sha(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def utf16(text):
    data = text.encode("utf-16-le", errors="surrogatepass")
    return [int.from_bytes(data[index:index + 2], "little") for index in range(0, len(data), 2)]


def verify_receipt(path, mode, phase, binary):
    limit = 16 * 1024 * 1024 if mode == "original24" else 128 * 1024 * 1024
    if not path.is_file() or not 1 <= path.stat().st_size <= limit:
        raise ValueError("Missing/oversized observation receipt")
    receipt = json.loads(path.read_text(encoding="utf-8"))
    schema = ("native-edit-symbol-attributes-v1" if mode == "symbol-attributes" else
              "native-edit-selection-geometry-v1" if mode == "selection-geometry" else "native-edit-word-selection-v1")
    if (receipt.get("schema") != schema or receipt.get("completed") is not True
            or receipt.get("error") is not None or receipt.get("hostShown") is not (mode == "selection-geometry")
            or receipt.get("desktopQualified") is not False or receipt.get("physicalInputQualified") is not False):
        raise ValueError("Incomplete or incorrectly qualified native observation")
    identity = receipt["identity"]
    if (identity.get("architecture") != "X64" or identity.get("processId") != phase["pid"]
            or "Microsoft.WindowsDesktop.App" not in identity.get("formsPath", "")
            or identity.get("probeSha256") != sha(binary)
            or not all(isinstance(identity.get(key), str) and identity[key]
                       for key in ("framework", "os", "osBuild", "locale", "uiLocale"))):
        raise ValueError("Original reference process/source/platform identity mismatch")
    forms = Path(identity["formsPath"])
    if sha(forms) != identity["formsSha256"]:
        raise ValueError("Loaded original Forms binary changed")
    modules = identity["nativeTextModules"]
    names = [item["name"].lower() for item in modules]
    if "usp10.dll" not in names or len(set(names)) != len(names):
        raise ValueError("Actual Uniscribe module ownership is absent or duplicated")
    for item in modules:
        if (Path(item["path"]).name.lower() != item["name"].lower()
                or sha(Path(item["path"])) != item["fileSha256"] or not item["fileVersion"]):
            raise ValueError("Loaded native module/version/hash mismatch")
    cases = receipt["cases"]
    if len(cases) != receipt["expectedCases"]:
        raise ValueError("Observation inventory incomplete")
    names = [case["name" if mode == "symbol-attributes" else "Name"] for case in cases]
    if len(set(names)) != len(names):
        raise ValueError("Observation inventory contains duplicate inputs")
    if mode == "original24" and [case["Name"] for case in cases] != ORIGINAL_CASES:
        raise ValueError("Original24 controls changed or disappeared")
    if mode == "contexts" and len(cases) != 72:
        raise ValueError("Context inventory changed")
    if mode == "item-contexts" and len(cases) != 128:
        raise ValueError("Item-context inventory changed")
    if mode == "source-roles" and len(cases) != 128:
        raise ValueError("Source-role inventory changed")
    if mode == "symbol-attributes":
        if (receipt.get("editGesturesObserved") is not False or not 1 <= receipt["symbolCount"] <= 4096
                or len(cases) != receipt["symbolCount"] * 3):
            raise ValueError("Attribute sweep claimed EDIT gestures or exceeded its bound")
    for case in cases:
        source = case["requestedUtf16"]
        if utf16(case["requestedText"]) != source:
            raise ValueError("Requested source UTF16 mismatch")
        observed = case["scriptBreak"]
        if (observed.get("completed") is not True or observed.get("actualUtf16") != source
                or observed.get("actualText") != case["requestedText"] or observed.get("flags") !=
                (0x140 if case.get("RightToLeft") else 0x40)
                or observed.get("hdc") != 0 or observed.get("charset") != -1
                or observed.get("attributeByteSize") != 1 or observed.get("analyseHResult") != 0
                or observed.get("freeHResult") != 0):
            raise ValueError("Complete original source/API/cleanup mismatch")
        raw = observed["rawBytes"]
        attributes = observed["attributes"]
        if len(raw) != len(source) or len(attributes) != len(source):
            raise ValueError("Raw UTF16 log-attribute coverage mismatch")
        for index, (byte, attribute) in enumerate(zip(raw, attributes)):
            if (type(byte) is not int or not 0 <= byte <= 255 or attribute["index"] != index
                    or attribute["utf16"] != source[index] or attribute["raw"] != byte
                    or attribute["reserved"] != byte >> 5):
                raise ValueError("Raw log-attribute identity mismatch")
            for name, bit in (("softBreak", 1), ("whiteSpace", 2), ("charStop", 4), ("wordStop", 8), ("invalid", 16)):
                if attribute[name] is not bool(byte & bit):
                    raise ValueError("Native attribute bitfield interpretation changed")
        classification = observed["classification"]
        if (classification.get("completed") is not True or classification["terminalPosition"] != len(source)
                or classification.get("itemByteSize") != 8 or classification.get("characterTypesSucceeded") is not True):
            raise ValueError("Original script partition incomplete")
        character_types = classification["ctype1"]
        if len(character_types) != len(source):
            raise ValueError("Original CTYPE1 coverage incomplete")
        for index, value in enumerate(character_types):
            if (value["index"] != index or value["utf16"] != source[index]
                    or type(value["raw"]) is not int or not 0 <= value["raw"] <= 65535):
                raise ValueError("Original CTYPE1 unit identity changed")
            for name, bit in (("space", 8), ("punctuation", 16), ("control", 32), ("blank", 64), ("alphabetic", 256)):
                if value[name] is not bool(value["raw"] & bit):
                    raise ValueError("Original CTYPE1 bit interpretation changed")
        for name in ("ctype2", "ctype3"):
            values = classification[name]
            if (classification.get(name + "Succeeded") is not True or classification[name + "Error"] != 0
                    or len(values) != len(source)):
                raise ValueError("Original " + name.upper() + " coverage incomplete")
            for index, value in enumerate(values):
                if (value["index"] != index or value["utf16"] != source[index]
                        or type(value["raw"]) is not int or not 0 <= value["raw"] <= 65535):
                    raise ValueError("Original " + name.upper() + " unit identity changed")
                if name == "ctype3":
                    for flag, bit in (("nonspacing", 1), ("diacritic", 2), ("vowelMark", 4),
                                      ("symbol", 8), ("katakana", 16), ("hiragana", 32),
                                      ("halfWidth", 64), ("fullWidth", 128), ("ideograph", 256),
                                      ("kashida", 512), ("lexical", 1024), ("highSurrogate", 2048),
                                      ("lowSurrogate", 4096), ("alphabetic", 32768)):
                        if value[flag] is not bool(value["raw"] & bit):
                            raise ValueError("Original CTYPE3 bit interpretation changed")
                    if value["reserved"] != value["raw"] & 0x6000:
                        raise ValueError("Original CTYPE3 reserved identity changed")
        end = 0
        for run in classification["runs"]:
            if run["start"] != end or not end < run["end"] <= len(source):
                raise ValueError("Original script partition changed")
            end = run["end"]
            if (run["script"] != run["rawAnalysis"] & 0x3FF or
                    run["needsWordBreaking"] is not bool(run["rawPropertiesFirst"] & (1 << 18))):
                raise ValueError("Raw script ID/property bit interpretation changed")
            direct = run["directScriptBreak"]
            if (direct["hResult"] != 0 or direct["inputStart"] != run["start"]
                    or direct["inputUtf16"] != source[run["start"]:end]
                    or len(direct["rawBytes"]) != end - run["start"]
                    or any(type(value) is not int or not 0 <= value <= 255 for value in direct["rawBytes"])):
                raise ValueError("Direct original-item ScriptBreak coverage mismatch")
        if end != len(source):
            raise ValueError("Missing original terminal script boundary")
        if mode not in ("symbol-attributes", "selection-geometry"):
            gestures = case["gestures"]
            if len(gestures) != len(case["Indices"]) * 2:
                raise ValueError("Missing requested EDIT coordinates")
            for gesture in gestures:
                if gesture["actualUtf16"] != source or gesture["actualText"] != case["requestedText"]:
                    raise ValueError("EDIT rewrote original source")
                unavailable = gesture["coordinateUnavailable"]
                if type(unavailable) is not bool or unavailable != (gesture["anchor"] is None):
                    raise ValueError("Unavailable-coordinate identity changed")
                if unavailable and gesture["steps"]:
                    raise ValueError("Unavailable native coordinate acquired invented gestures")
    if mode == "selection-geometry":
        verify_geometry(receipt)
    return {"path": path.name, "sha256": sha(path), "cases": len(cases),
            "architecture": identity["architecture"], "osBuild": identity["osBuild"],
            "framework": identity["framework"], "qualified": False}


def verify_geometry(receipt):
    """Validate original evidence structure/bytes; never manufacture geometry."""
    cases = receipt["cases"]
    if ([case["Name"] for case in cases] != list(GEOMETRY_INPUTS)
            or receipt.get("ownedWindowOnly") is not True
            or receipt.get("fallbackFontIdentityQualified") is not False):
        raise ValueError("Owned geometry case/identity inventory changed")
    total, unavailable = 0, 0
    for case in cases:
        source, seams = GEOMETRY_INPUTS[case["Name"]]
        length = len(utf16(source))
        if (case["requestedText"] != source or case["requestedUtf16"] != utf16(source)
                or case["requestedSeams"] != seams or case["RightToLeft"] is not (case["Name"] == "bidi-rtl")):
            raise ValueError("Geometry original source/seams/direction changed")
        metadata = case["metadata"]
        handle = metadata["handle"]
        font = metadata["baseFont"]
        descriptor = font["descriptor"]
        signed_fields = ("Height", "Width", "Escapement", "Orientation", "Weight")
        byte_fields = ("Italic", "Underline", "StrikeOut", "CharSet", "OutPrecision", "ClipPrecision", "Quality", "PitchAndFamily")
        if (type(handle) is not int or handle == 0 or metadata.get("fallbackFontIdentityQualified") is not False
                or font.get("borrowed") is not True or font.get("fallbackIdentityQualified") is not False
                or type(font["handle"]) is not int or font["handle"] == 0
                or not 1 <= font["byteCount"] <= 32 * 1024 * 1024 or not hash_text(font["sha256"])
                or not isinstance(descriptor.get("FaceName"), str) or not 1 <= len(descriptor["FaceName"]) <= 31
                or any(type(descriptor.get(key)) is not int or not -(2 ** 31) <= descriptor[key] < 2 ** 31 for key in signed_fields)
                or any(type(descriptor.get(key)) is not int or not 0 <= descriptor[key] <= 255 for key in byte_fields)
                or not 1 <= metadata["deviceDpi"] <= 960
                or font["DeviceDpi"] != metadata["deviceDpi"]):
            raise ValueError("Original owned base-font/DPI metadata incomplete")
        if ([color["index"] for color in metadata["systemColors"]] != [5, 8, 13, 14, 15, 18]
                or any(type(color.get("colorRef")) is not int or not 0 <= color["colorRef"] <= 0xFFFFFF
                       for color in metadata["systemColors"])):
            raise ValueError("Original system-color inventory changed")
        a, b, c = seams
        requested = [(f"collapsed-{position}", position, position) for position in [0, a, b, c, length]]
        for start, end in ((a, b), (b, c), (a, c)):
            requested += [(f"forward-{start}-{end}", start, end), (f"reverse-{end}-{start}", end, start)]
        gestures = ["first-down", "first-up", "double-down", "drag-last", "drag-first-reversal",
                    "drag-anchor-reversal", "drag-last-again", "final-up"]
        observations = case["observations"]
        if [item["name"] for item in observations] != [item[0] for item in requested] + gestures:
            raise ValueError("Directed selection/gesture geometry inventory changed")
        caret_count = 0
        for index, observation in enumerate(observations):
            request = observation["request"]
            if index < len(requested):
                _, anchor, active = requested[index]
                if request != {"kind": "set-selection", "requestedAnchor": anchor,
                               "requestedActive": active, "result": request.get("result")}:
                    raise ValueError("Original directed selection identity changed")
            else:
                messages = [0x201, 0x202, 0x203, 0x200, 0x200, 0x200, 0x200, 0x202]
                callbacks = request.get("callbacks", [])
                if (request.get("kind") != "gesture" or request.get("message") != messages[index - len(requested)]
                        or len(callbacks) < 2 or callbacks[0].get("name") != "WndProc-enter"
                        or callbacks[-1].get("name") != "WndProc-return"
                        or any(callback.get("step") != observation["name"] for callback in callbacks)):
                    raise ValueError("Original native gesture delivery is missing")
            if type(observation.get("scrollCaretResult")) is not int:
                raise ValueError("Original EM_SCROLLCARET result is missing")
            for state_name in ("beforeScroll", "afterScroll"):
                state = observation[state_name]
                selection = state["Selection"]
                start, end = selection["Start"], selection["End"]
                if (not 0 <= start <= end <= length or selection["ManagedStart"] != start
                        or selection["ManagedLength"] != end - start or selection.get("Focused") is not True
                        or utf16(selection["SelectedText"]) != utf16(source)[start:end]):
                    raise ValueError("Original selected UTF16/focus identity changed")
                if index < len(requested) and (start, end) != tuple(sorted(requested[index][1:])):
                    raise ValueError("Original directed selection endpoints were snapped")
                caret = state["Caret"]
                total += 1
                if caret["Available"] is True:
                    if (caret.get("UnavailableReason") is not None or caret.get("GuiQuerySucceeded") is not True
                            or caret.get("PointQuerySucceeded") is not True or caret["GuiError"] != 0 or caret["PointError"] != 0
                            or caret["CaretOwner"] != handle or caret["FocusOwner"] != handle
                            or not positive_rect(caret["Rectangle"]) or not native_point(caret["Point"])):
                        raise ValueError("Owned native caret evidence is invalid")
                    caret_count += 1
                elif caret["Available"] is False:
                    unavailable += 1
                    if not caret["UnavailableReason"] or caret["Point"] is not None or caret["Rectangle"] is not None:
                        raise ValueError("Unavailable native caret acquired invented coordinates")
                else:
                    raise ValueError("Native caret availability is not explicit")
                client = state["Client"]
                if (not positive_rect(client) or client["Left"] != 0 or client["Top"] != 0
                        or client["Right"] > 512 or client["Bottom"] > 160 or not positive_rect(state["Format"])):
                    raise ValueError("Original bounded client/format frame is invalid")
                if type(state.get("FirstVisibleRaw")) is not int:
                    raise ValueError("Original first-visible position is missing")
                for key in ("HorizontalScroll", "VerticalScroll"):
                    scroll = state[key]
                    if (type(scroll.get("Available")) is not bool
                            or any(type(scroll.get(field)) is not int for field in
                                   ("Error", "Minimum", "Maximum", "Page", "Position", "TrackPosition"))
                            or not 0 <= scroll["Page"] <= 0xFFFFFFFF
                            or (scroll["Available"] and scroll["Error"] != 0)):
                        raise ValueError("Original scroll status/metrics are incomplete")
                positions = state["Positions"]
                if [p["Index"] for p in positions] != list(range(length + 1)):
                    raise ValueError("Original EM_POSFROMCHAR inventory was filtered")
                rows = set()
                for position in positions:
                    raw = position["Raw"] & 0xFFFFFFFF
                    point = position["Point"]
                    if raw == 0xFFFFFFFF:
                        if point is not None: raise ValueError("Unavailable source position was invented")
                    else:
                        x, y = signed_short(raw), signed_short(raw >> 16)
                        if point is None or (point["X"], point["Y"]) != (x, y):
                            raise ValueError("Raw EM_POSFROMCHAR decoding changed")
                        if 0 <= y < client["Bottom"]: rows.add(y)
                expected_hits = [(x, y) for y in sorted(rows) for x in range(client["Right"])]
                hits = state["Hits"]
                if not 1 <= len(rows) <= 8 or [(hit["X"], hit["Y"]) for hit in hits] != expected_hits:
                    raise ValueError("Integer EM_CHARFROMPOS scan is incomplete")
                for hit in hits:
                    raw = hit["Raw"] & 0xFFFFFFFF
                    decoded = (None, None) if raw == 0xFFFFFFFF else (raw & 0xFFFF, raw >> 16)
                    if (hit["Character"], hit["Line"]) != decoded:
                        raise ValueError("Original raw hit identity changed")
            pixels = observation["pixels"]
            width, height, stride = pixels["Width"], pixels["Height"], pixels["Stride"]
            if (pixels.get("Format") != "BGRX8-top-down-original-bytes" or not 1 <= width <= 512
                    or not 1 <= height <= 160 or stride != width * 4
                    or (width, height) != (observation["afterScroll"]["Client"]["Right"], observation["afterScroll"]["Client"]["Bottom"])
                    or pixels.get("RgbIndependentOfClear") is not True or pixels["RgbClearDifferences"] != 0
                    or not hash_text(pixels["ClearControlSha256"])):
                raise ValueError("Original WM_PRINTCLIENT RGB evidence is incomplete")
            buffers = []
            for data_key, hash_key in (("Bytes", "Sha256"), ("ClearControlBytes", "ClearControlSha256")):
                raw = base64.b64decode(pixels[data_key], validate=True)
                if len(raw) != stride * height or hashlib.sha256(raw).hexdigest() != pixels[hash_key]:
                    raise ValueError("Original print pixels changed")
                buffers.append(raw)
            if any(buffers[0][channel::4] != buffers[1][channel::4] for channel in range(3)):
                raise ValueError("Original print RGB depends on unpainted clear bytes")
            if any(type(pixels.get(key)) is not int for key in ("FirstPrintResult", "SecondPrintResult")):
                raise ValueError("Original print message results are missing")
        if caret_count == 0 or metadata["ownedCaretSamples"] != caret_count:
            raise ValueError("Actual owned caret evidence is missing")
    if (receipt["caretSamples"] != total or receipt["unavailableCaretSamples"] != unavailable
            or receipt["geometryComplete"] is not (unavailable == 0)):
        raise ValueError("Caret availability/geometry completeness changed")


def hash_text(value):
    return isinstance(value, str) and len(value) == 64 and all(c in "0123456789abcdef" for c in value)


def positive_rect(value):
    return (isinstance(value, dict) and all(type(value.get(key)) is int for key in ("Left", "Top", "Right", "Bottom"))
            and value["Right"] > value["Left"] and value["Bottom"] > value["Top"])


def native_point(value):
    return isinstance(value, dict) and all(type(value.get(key)) is int for key in ("X", "Y"))


def signed_short(value):
    value &= 0xFFFF
    return value - 0x10000 if value >= 0x8000 else value


def main():
    if os.name != "nt" or len(sys.argv) != 2:
        raise RuntimeError("Expected Windows and an exact source head")
    root = Path(__file__).resolve().parent.parent
    actual_head = subprocess.check_output(["git", "rev-parse", "HEAD"], cwd=root, text=True).strip()
    if actual_head != sys.argv[1]:
        raise RuntimeError("Checked-out source differs from the requested exact head")
    project = root / "eng/NativeEditWordPolicyReference"
    copied = (project / "WordSelectionReference.cs").read_text(encoding="utf-8")
    start = copied.index("    private static readonly Case[] Cases =")
    original_case_source = copied[start:copied.index("    ];", start) + 7]
    if hashlib.sha256(original_case_source.encode()).hexdigest() != ORIGINAL_CASE_SOURCE_SHA256:
        raise RuntimeError("Original24 input/coordinate/flag source inventory changed")
    evidence = root / "artifacts/native-edit-word-policy"
    evidence.mkdir(parents=True, exist_ok=False)
    environment = os.environ.copy()
    environment.update(DOTNET_CLI_HOME=str(evidence / "cli-home"), DOTNET_GENERATE_ASPNET_CERTIFICATE="false",
                       DOTNET_ADD_GLOBAL_TOOLS_TO_PATH="false", DOTNET_CLI_TELEMETRY_OPTOUT="1")
    phases = []
    manifest = {"schema": "native-edit-word-policy-execution-v1", "sourceHead": actual_head,
                "completed": False, "qualified": False, "phases": phases, "receipts": [],
                "originalReferenceSource": {"repository": "wieslawsoltes/LibreWinForms",
                    "commit": "def9a31a9192bd560da34c49787089fec848b4ab",
                    "sha256": "09a505121cb1cbf1576f104b0084d456688fae8e9de845ca1f5d52de84c8a337",
                    "caseSourceSha256": ORIGINAL_CASE_SOURCE_SHA256},
                "sources": {str(path.relative_to(root)): sha(path) for path in sorted(project.glob("*")) if path.is_file()}}

    def run(name, command, timeout):
        phase = {"name": name, "command": command, "timeoutSeconds": timeout}
        phases.append(phase)
        with (evidence / (name + ".log")).open("xb") as log:
            child = subprocess.Popen(command, cwd=project, env=environment, stdout=log, stderr=subprocess.STDOUT)
            phase["pid"] = child.pid
            try:
                phase["exitCode"] = child.wait(timeout=timeout)
            except subprocess.TimeoutExpired:
                phase["timedOut"] = True
                subprocess.run(["taskkill", "/PID", str(child.pid), "/T", "/F"], stdout=log,
                               stderr=subprocess.STDOUT, timeout=10, check=False)
                phase["exitCode"] = child.wait(timeout=10)
                raise
        if phase["exitCode"] != 0:
            raise RuntimeError(name + " exited " + str(phase["exitCode"]))
        return phase

    try:
        run("sdk", ["dotnet", "--info"], 30)
        run("build", ["dotnet", "build", "NativeEditWordPolicyReference.csproj", "-c", "Release",
                      "-o", str(evidence / "bin"), "-m:1", "-nodeReuse:false", "-p:UseSharedCompilation=false",
                      "-p:BaseIntermediateOutputPath=" + str(evidence / "obj") + os.sep], 180)
        binary = evidence / "bin/NativeEditWordPolicyReference.dll"
        for mode in ("original24", "contexts", "symbol-attributes", "item-contexts", "source-roles", "selection-geometry"):
            receipt = evidence / (mode + ".json")
            phase = run(mode, ["dotnet", str(binary), mode, str(receipt)], 60)
            manifest["receipts"].append(verify_receipt(receipt, mode, phase, binary))
        manifest["completed"] = True
    finally:
        with (evidence / "execution.json").open("x", encoding="utf-8") as output:
            json.dump(manifest, output, indent=2)


if __name__ == "__main__":
    main()
