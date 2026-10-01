#!/usr/bin/env python3
"""Bounded original Windows EDIT/Uniscribe observation, never product admission."""

import hashlib
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
    schema = "native-edit-symbol-attributes-v1" if mode == "symbol-attributes" else "native-edit-word-selection-v1"
    if (receipt.get("schema") != schema or receipt.get("completed") is not True
            or receipt.get("error") is not None or receipt.get("hostShown") is not False
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
        if mode != "symbol-attributes":
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
    return {"path": path.name, "sha256": sha(path), "cases": len(cases),
            "architecture": identity["architecture"], "osBuild": identity["osBuild"],
            "framework": identity["framework"], "qualified": False}


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
        for mode in ("original24", "contexts", "symbol-attributes"):
            receipt = evidence / (mode + ".json")
            phase = run(mode, ["dotnet", str(binary), mode, str(receipt)], 60)
            manifest["receipts"].append(verify_receipt(receipt, mode, phase, binary))
        manifest["completed"] = True
    finally:
        with (evidence / "execution.json").open("x", encoding="utf-8") as output:
            json.dump(manifest, output, indent=2)


if __name__ == "__main__":
    main()
