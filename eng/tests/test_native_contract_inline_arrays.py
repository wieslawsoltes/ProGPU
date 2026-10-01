"""Bounded managed contract generator checks; no native build or renderer run."""

import os
from pathlib import Path
import subprocess
import tempfile
import unittest


ROOT = Path(__file__).resolve().parents[2]
GENERATOR = ROOT / "eng/ProGPU.NativeContractGenerator/bin/Release/net10.0/ProGPU.NativeContractGenerator.dll"


class NativeContractInlineArrayTests(unittest.TestCase):
    def generate(self, declaration, success):
        self.assertTrue(GENERATOR.is_file(), "Build the managed contract generator before this focused check")
        with tempfile.TemporaryDirectory(prefix="progpu-contract-array-") as directory:
            header = Path(directory) / "array.h"
            output = Path(directory) / "array.g.cs"
            header.write_text("/* PROGPU_CSHARP_STRUCT: NativeMethods.ArrayRecord */\n"
                              "typedef struct array_record {\n" + declaration + "\n} array_record;\n")
            output.write_text("original output sentinel\n")
            result = subprocess.run([os.environ.get("DOTNET_HOST_PATH", "dotnet"), str(GENERATOR),
                                     str(header), str(output)], capture_output=True, text=True, timeout=10)
            if success:
                self.assertEqual(result.returncode, 0, result.stderr)
            else:
                self.assertNotEqual(result.returncode, 0)
                self.assertEqual(output.read_text(), "original output sentinel\n")
            return output.read_text()

    def test_literal_uint_arrays_flatten_in_original_order(self):
        for count in (1, 3, 64):
            with self.subTest(count=count):
                generated = self.generate(f"uint32_t before; uint32_t reserved[{count}]; float after;", True)
                fields = "\n".join(line.strip() for line in generated.splitlines() if "public " in line)
                self.assertEqual(fields, "public uint Before;\n" +
                                 "\n".join(f"public uint Reserved{i};" for i in range(count)) +
                                 "\npublic float After;")

    def test_invalid_array_forms_preserve_existing_output(self):
        for declaration in ("uint32_t reserved[0];", "uint32_t reserved[65];",
                            "uint32_t reserved[-1];", "uint32_t reserved[COUNT];",
                            "uint32_t reserved[999999999999999999999];",
                            "uint32_t* reserved[3];", "float reserved[3];",
                            "uint64_t reserved[3];", "uint32_t reserved[3][2];",
                            "/* PROGPU_CSHARP_TYPE: uint */ uint32_t reserved[3];"):
            with self.subTest(declaration=declaration):
                self.generate(declaration, False)


if __name__ == "__main__":
    unittest.main()
