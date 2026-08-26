#!/usr/bin/env python3
"""Generate ProGPU's neutral MIL protocol manifest from WPF MCG outputs."""

from __future__ import annotations

import argparse
import hashlib
import json
import re
import sys
from pathlib import Path


COMMAND_TYPES_RELATIVE = Path(
    "src/Microsoft.DotNet.Wpf/src/WpfGfx/include/Generated/"
    "wgx_command_types.h"
)
COMMAND_LAYOUTS_RELATIVE = Path(
    "src/Microsoft.DotNet.Wpf/src/WpfGfx/include/Generated/wgx_commands.cs"
)

FIELD_SIZES = {
    "AlignmentX": 4,
    "AlignmentY": 4,
    "BOOL": 4,
    "BitmapScalingMode": 4,
    "BrushMappingMode": 4,
    "CachingHint": 4,
    "ClearTypeHint": 4,
    "ColorInterpolationMode": 4,
    "D3DMATRIX": 64,
    "DUCE.ResourceHandle": 4,
    "EdgeMode": 4,
    "FillRule": 4,
    "GeometryCombineMode": 4,
    "GradientSpreadMethod": 4,
    "KernelType": 4,
    "MILCMD": 4,
    "MILTransparencyFlags": 4,
    "MILWindowLayerType": 4,
    "MS.Win32.NativeMethods.RECT": 16,
    "MilColorF": 16,
    "MilMatrix3x2D": 48,
    "MilPoint2F": 8,
    "MilPoint3F": 12,
    "MilQuaternionF": 16,
    "MilRenderOptions": 28,
    "PenLineCap": 4,
    "PenLineJoin": 4,
    "Point": 16,
    "Rect": 32,
    "RenderingBias": 4,
    "ShaderRenderMode": 4,
    "Size": 16,
    "Stretch": 4,
    "TileMode": 4,
    "UInt16": 2,
    "UInt32": 4,
    "UInt64": 8,
    "double": 8,
    "float": 4,
    "int": 4,
}

COMMAND_PATTERN = re.compile(
    r"/\*\s*0x[0-9a-fA-F]+\s*\*/\s*"
    r"(?P<name>MilPop|Mil(?:Cmd|Draw|Push)[A-Za-z0-9]+)\s*=\s*"
    r"(?P<value>0x[0-9a-fA-F]+)"
)
STRUCT_PATTERN = re.compile(
    r"\[StructLayout\(LayoutKind\.Explicit,\s*Pack=1\)\]\s*"
    r"internal\s+struct\s+(?P<name>MILCMD_[A-Z0-9_]+)\s*"
    r"\{(?P<body>.*?)\n\s*\};",
    re.DOTALL,
)
FIELD_PATTERN = re.compile(
    r"\[FieldOffset\((?P<offset>\d+)\)\]\s*internal\s+"
    r"(?P<type>[^;]+?)\s+(?P<name>[A-Za-z_][A-Za-z0-9_]*)\s*;"
)


def sha256(path: Path) -> str:
    return hashlib.sha256(path.read_bytes()).hexdigest()


def command_base(wpf_name: str) -> str:
    for prefix in ("MilCmd", "MilDraw", "MilPush"):
        if wpf_name.startswith(prefix):
            suffix = wpf_name[len(prefix) :]
            if prefix == "MilDraw":
                return "Draw" + suffix
            if prefix == "MilPush":
                return "Push" + suffix
            return suffix
    if wpf_name == "MilPop":
        return "Pop"
    raise ValueError(f"Unsupported WPF MIL command name: {wpf_name}")


def cpp_name(wpf_name: str) -> str:
    name = command_base(wpf_name)
    name = re.sub(r"([A-Z]+)([A-Z][a-z])", r"\1_\2", name)
    name = re.sub(r"([a-z0-9])([A-Z])", r"\1_\2", name).lower()
    name = name.replace("3_d", "3d").replace("2_d", "2d")
    name = name.replace("set3d_", "set_3d_")
    return name.replace("v_blank", "vblank")


def cpp_identifier(name: str) -> str:
    name = name.lstrip("_")
    name = re.sub(r"([A-Z]+)([A-Z][a-z])", r"\1_\2", name)
    name = re.sub(r"([a-z0-9])([A-Z])", r"\1_\2", name).lower()
    name = re.sub(r"[^a-z0-9_]+", "_", name).strip("_")
    return f"field_{name}" if name[:1].isdigit() else name


def layout_key_from_command(wpf_name: str) -> str:
    return re.sub(r"[^A-Za-z0-9]", "", command_base(wpf_name)).upper()


def layout_key_from_struct(struct_name: str) -> str:
    return re.sub(r"[^A-Za-z0-9]", "", struct_name.removeprefix("MILCMD_"))


def parse_layouts(source: str) -> tuple[list[dict[str, object]], dict[str, str]]:
    layouts: list[dict[str, object]] = []
    keys: dict[str, str] = {}
    for match in STRUCT_PATTERN.finditer(source):
        name = match.group("name")
        fields: list[dict[str, object]] = []
        fixed_size = 0
        for field_match in FIELD_PATTERN.finditer(match.group("body")):
            field_type = field_match.group("type").strip()
            if field_type not in FIELD_SIZES:
                raise ValueError(
                    f"No fixed-width mapping for {field_type!r} in {name}."
                )
            offset = int(field_match.group("offset"))
            size = FIELD_SIZES[field_type]
            fixed_size = max(fixed_size, offset + size)
            fields.append(
                {
                    "name": field_match.group("name"),
                    "type": field_type,
                    "offset": offset,
                    "size": size,
                }
            )
        if not fields:
            raise ValueError(f"No fields parsed for {name}.")
        layout = {"name": name, "fixedSize": fixed_size, "fields": fields}
        layouts.append(layout)
        key = layout_key_from_struct(name)
        if key in keys:
            raise ValueError(f"Duplicate normalized layout key {key}.")
        keys[key] = name
    if not layouts:
        raise ValueError("No WPF MIL packet layouts were parsed.")
    return layouts, keys


def build_manifest(wpf_root: Path) -> dict[str, object]:
    command_types_path = wpf_root / COMMAND_TYPES_RELATIVE
    command_layouts_path = wpf_root / COMMAND_LAYOUTS_RELATIVE
    if not command_types_path.is_file() or not command_layouts_path.is_file():
        raise FileNotFoundError(
            "The WPF root does not contain the checked-in MCG command outputs."
        )
    command_source = command_types_path.read_text(encoding="utf-8-sig")
    layout_source = command_layouts_path.read_text(encoding="utf-8-sig")
    layouts, layout_keys = parse_layouts(layout_source)
    commands: list[dict[str, object]] = []
    values: set[int] = set()
    names: set[str] = set()
    for match in COMMAND_PATTERN.finditer(command_source):
        wpf_name = match.group("name")
        value = int(match.group("value"), 16)
        name = cpp_name(wpf_name)
        if value in values or name in names:
            raise ValueError(f"Duplicate MIL command {wpf_name} ({value:#x}).")
        values.add(value)
        names.add(name)
        entry: dict[str, object] = {
            "name": name,
            "wpfName": wpf_name,
            "value": value,
        }
        layout = layout_keys.get(layout_key_from_command(wpf_name))
        if layout is not None:
            entry["layout"] = layout
        commands.append(entry)
    commands.sort(key=lambda entry: int(entry["value"]))
    expected_values = list(range(commands[-1]["value"] + 1))
    actual_values = [entry["value"] for entry in commands]
    if actual_values != expected_values:
        raise ValueError("WPF MIL command values are no longer contiguous.")
    return {
        "schemaVersion": 1,
        "wireContract": {
            "byteOrder": "little-endian",
            "commandWidth": 4,
            "managedLayoutPack": 1,
        },
        "source": {
            "commandTypes": {
                "path": COMMAND_TYPES_RELATIVE.as_posix(),
                "sha256": sha256(command_types_path),
            },
            "commandLayouts": {
                "path": COMMAND_LAYOUTS_RELATIVE.as_posix(),
                "sha256": sha256(command_layouts_path),
            },
        },
        "commands": commands,
        "layouts": layouts,
    }


def render_manifest(manifest: dict[str, object]) -> str:
    return json.dumps(manifest, indent=2, ensure_ascii=False) + "\n"


def render_header(manifest: dict[str, object]) -> str:
    commands = manifest["commands"]
    layouts = manifest["layouts"]
    assert isinstance(commands, list)
    assert isinstance(layouts, list)
    layouts_by_name = {
        layout["name"]: layout for layout in layouts if isinstance(layout, dict)
    }
    lines = [
        "#ifndef PROGPU_NATIVE_MIL_COMMANDS_GENERATED_HPP",
        "#define PROGPU_NATIVE_MIL_COMMANDS_GENERATED_HPP",
        "",
        "// Generated by eng/progpu-generate-mil-protocol.py from the",
        "// checked-in neutral WPF MCG protocol manifest. Do not edit.",
        "#include <cstdint>",
        "",
        "namespace progpu::native::mil {",
        "",
        "enum class command : std::uint32_t {",
    ]
    for entry in commands:
        assert isinstance(entry, dict)
        lines.append(f"    {entry['name']} = 0x{int(entry['value']):02x},")
    lines.extend(
        [
            "};",
            "",
            "namespace command_layouts {",
            "",
        ]
    )
    layout_commands = [
        entry for entry in commands
        if isinstance(entry, dict) and "layout" in entry
    ]
    for entry in layout_commands:
        layout = layouts_by_name[entry["layout"]]
        fields = layout["fields"]
        assert isinstance(fields, list)
        lines.extend(
            [
                f"struct {entry['name']} final {{",
                f"    static constexpr command kind = command::{entry['name']};",
                "    static constexpr std::uint32_t fixed_size = "
                f"{int(layout['fixedSize'])}U;",
            ]
        )
        for field in fields:
            assert isinstance(field, dict)
            field_name = cpp_identifier(str(field["name"]))
            lines.append(
                f"    static constexpr std::uint32_t {field_name}_offset = "
                f"{int(field['offset'])}U;"
            )
            lines.append(
                f"    static constexpr std::uint32_t {field_name}_size = "
                f"{int(field['size'])}U;"
            )
        lines.extend(["};", ""])
    lines.extend(
        [
            "constexpr std::uint32_t fixed_header_size(command value) noexcept {",
            "    switch (value) {",
        ]
    )
    for entry in layout_commands:
        lines.extend(
            [
                f"    case command::{entry['name']}:",
                f"        return {entry['name']}::fixed_size;",
            ]
        )
    lines.extend(
        [
            "    default:",
            "        return 0U;",
            "    }",
            "}",
            "",
            f"inline constexpr std::uint32_t count = {len(layout_commands)}U;",
            "",
            "} // namespace command_layouts",
            "",
            "} // namespace progpu::native::mil",
            "",
            "#endif",
            "",
        ]
    )
    return "\n".join(lines)


def require_equal(path: Path, expected: str) -> None:
    actual = path.read_text(encoding="utf-8") if path.is_file() else None
    if actual != expected:
        raise ValueError(f"Generated MIL protocol artifact is stale: {path}")


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--wpf-root", type=Path)
    parser.add_argument("--check", action="store_true")
    parser.add_argument(
        "--manifest",
        type=Path,
        default=Path("eng/mil/wpf-mil-protocol.json"),
    )
    parser.add_argument(
        "--header",
        type=Path,
        default=Path(
            "src/ProGPU.Native/include/"
            "progpu_native_mil_commands.generated.hpp"
        ),
    )
    args = parser.parse_args()
    manifest = (
        build_manifest(args.wpf_root.resolve())
        if args.wpf_root is not None
        else json.loads(args.manifest.read_text(encoding="utf-8"))
    )
    manifest_text = render_manifest(manifest)
    header_text = render_header(manifest)
    if args.check:
        require_equal(args.manifest, manifest_text)
        require_equal(args.header, header_text)
        print(
            f"MIL protocol artifacts are current: "
            f"{len(manifest['commands'])} commands, "
            f"{len(manifest['layouts'])} managed packet layouts."
        )
        return 0
    args.manifest.parent.mkdir(parents=True, exist_ok=True)
    args.header.parent.mkdir(parents=True, exist_ok=True)
    args.manifest.write_text(manifest_text, encoding="utf-8")
    args.header.write_text(header_text, encoding="utf-8")
    print(
        f"Generated {args.manifest} and {args.header}: "
        f"{len(manifest['commands'])} commands, "
        f"{len(manifest['layouts'])} managed packet layouts."
    )
    return 0


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except (FileNotFoundError, ValueError, KeyError, TypeError) as error:
        print(f"error: {error}", file=sys.stderr)
        raise SystemExit(1)
