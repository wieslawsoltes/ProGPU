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
MANAGED_LAYOUTS_RELATIVE = Path(
    "src/Microsoft.DotNet.Wpf/src/WpfGfx/include/Generated/wgx_commands.cs"
)
NATIVE_LAYOUTS_RELATIVE = Path(
    "src/Microsoft.DotNet.Wpf/src/WpfGfx/include/Generated/wgx_commands.h"
)
RENDER_DATA_LAYOUTS_RELATIVE = Path(
    "src/Microsoft.DotNet.Wpf/src/WpfGfx/include/Generated/"
    "wgx_renderdata_commands.h"
)
PROGPU_ROOT = Path(__file__).resolve().parent.parent

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
    "byte": 1,
    "double": 8,
    "float": 4,
    "int": 4,
}

C_FIELD_LAYOUTS = {
    "BOOL": (4, 4),
    "D3DMATRIX": (64, 4),
    "DOUBLE": (8, 8),
    "FLOAT": (4, 4),
    "HMIL_CHANNEL": (4, 4),
    "HMIL_RESOURCE": (4, 4),
    "INT": (4, 4),
    "IWICBitmapSource*": (8, 8),
    "MILCMD": (4, 4),
    "MIL_RESOURCE_TYPE": (4, 4),
    "MilBitmapScalingMode::Enum": (4, 4),
    "MilBrushMappingMode::Enum": (4, 4),
    "MilCachingHint::Enum": (4, 4),
    "MilClearTypeHint::Enum": (4, 4),
    "MilColorF": (16, 4),
    "MilColorInterpolationMode::Enum": (4, 4),
    "MilCombineMode::Enum": (4, 4),
    "MilEdgeMode::Enum": (4, 4),
    "MilEffectRenderingBias::Enum": (4, 4),
    "MilFillMode::Enum": (4, 4),
    "MilGradientSpreadMethod::Enum": (4, 4),
    "MilHorizontalAlignment::Enum": (4, 4),
    "MilKernelType::Enum": (4, 4),
    "MilMatrix3x2D": (48, 8),
    "MilPenCap::Enum": (4, 4),
    "MilPenJoin::Enum": (4, 4),
    "MilPixelFormat::Enum": (4, 4),
    "MilPoint2D": (16, 8),
    "MilPoint2F": (8, 4),
    "MilPoint3F": (12, 4),
    "MilPointAndSizeD": (32, 8),
    "MilQuaternionF": (16, 4),
    "MilRectF": (16, 4),
    "MilRenderOptions": (28, 4),
    "MilSizeD": (16, 8),
    "MilStretch::Enum": (4, 4),
    "MilTileMode::Enum": (4, 4),
    "MilTransparency::Flags": (4, 4),
    "MilVerticalAlignment::Enum": (4, 4),
    "MilWindowLayerType::Enum": (4, 4),
    "RECT": (16, 4),
    "ShaderEffectShaderRenderMode::Enum": (4, 4),
    "UINT": (4, 4),
    "UINT16": (2, 2),
    "UINT32": (4, 4),
    "UINT64": (8, 8),
    "WORD": (2, 2),
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
    r"\[FieldOffset\((?P<offset>\d+)\)\]\s*(?:internal|private)\s+"
    r"(?P<type>[^;]+?)\s+(?P<name>[A-Za-z_][A-Za-z0-9_]*)\s*;"
)
C_TYPEDEF_STRUCT_PATTERN = re.compile(
    r"typedef\s+struct\s*\{(?P<body>.*?)\}\s*"
    r"(?P<name>MILCMD_[A-Z0-9_]+)\s*;",
    re.DOTALL,
)
C_NAMED_STRUCT_PATTERN = re.compile(
    r"struct\s+(?P<name>MILCMD_[A-Z0-9_]+)\s*"
    r"\{(?P<body>.*?)\}\s*;",
    re.DOTALL,
)
C_FIELD_PATTERN = re.compile(
    r"^\s*(?P<type>[A-Za-z_][A-Za-z0-9_:]*(?:\s*\*)?)\s+"
    r"(?P<name>[A-Za-z_][A-Za-z0-9_]*)\s*;\s*$",
    re.MULTILINE,
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


def parse_managed_layouts(
    source: str,
) -> tuple[list[dict[str, object]], dict[str, str]]:
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
        if (
            fields[0]["name"] != "Type"
            or fields[0]["offset"] != 0
            or fields[0]["size"] != 4
            or fixed_size % 4 != 0
        ):
            raise ValueError(f"Unexpected packed command framing for {name}.")
        layout = {"name": name, "fixedSize": fixed_size, "fields": fields}
        layouts.append(layout)
        key = layout_key_from_struct(name)
        if key in keys:
            raise ValueError(f"Duplicate normalized layout key {key}.")
        keys[key] = name
    if not layouts:
        raise ValueError("No WPF MIL packet layouts were parsed.")
    return layouts, keys


def parse_native_layouts(
    source: str,
    pattern: re.Pattern[str],
    source_kind: str,
) -> tuple[list[dict[str, object]], dict[str, str]]:
    layouts: list[dict[str, object]] = []
    keys: dict[str, str] = {}
    for match in pattern.finditer(source):
        name = match.group("name")
        fields: list[dict[str, object]] = []
        offset = 0
        body = match.group("body")
        for field_match in C_FIELD_PATTERN.finditer(body):
            field_type = re.sub(r"\s*\*\s*$", "*", field_match.group("type"))
            if field_type not in C_FIELD_LAYOUTS:
                raise ValueError(
                    f"No native wire layout for {field_type!r} in {name}."
                )
            size, alignment = C_FIELD_LAYOUTS[field_type]
            # WPF includes these generated command declarations under its
            # packed wire-structure boundary. Explicit QuadWord/BYTE padding
            # fields in the MCG output are therefore protocol bytes, while
            # host ABI alignment must never change an offset.
            del alignment
            fields.append(
                {
                    "name": field_match.group("name"),
                    "type": field_type,
                    "offset": offset,
                    "size": size,
                }
            )
            offset += size
        if not fields:
            raise ValueError(f"No native fields parsed for {name}.")
        fixed_size = offset
        if (
            str(fields[0]["name"]).lower() != "type"
            or fields[0]["offset"] != 0
            or fields[0]["size"] != 4
            or fixed_size % 4 != 0
        ):
            raise ValueError(f"Unexpected native command framing for {name}.")
        layout = {
            "name": name,
            "fixedSize": fixed_size,
            "sourceKind": source_kind,
            "fields": fields,
        }
        layouts.append(layout)
        key = layout_key_from_struct(name)
        if key in keys:
            raise ValueError(f"Duplicate normalized native layout key {key}.")
        keys[key] = name
    if not layouts:
        raise ValueError(f"No {source_kind} MIL packet layouts were parsed.")
    return layouts, keys


def validate_managed_overlap(
    managed_layouts: list[dict[str, object]],
    native_layouts: list[dict[str, object]],
) -> None:
    native_by_key = {
        layout_key_from_struct(str(layout["name"])): layout
        for layout in native_layouts
    }
    for managed in managed_layouts:
        key = layout_key_from_struct(str(managed["name"]))
        native = native_by_key.get(key)
        if native is None:
            raise ValueError(
                f"Managed MIL layout {managed['name']} has no native peer."
            )
        if managed["fixedSize"] != native["fixedSize"]:
            raise ValueError(
                f"Managed/native size mismatch for {managed['name']}: "
                f"{managed['fixedSize']} != {native['fixedSize']}."
            )
        native_fields = {
            cpp_identifier(str(field["name"])): field
            for field in native["fields"]
        }
        for managed_field in managed["fields"]:
            field_name = str(managed_field["name"])
            if field_name.startswith("BYTEPacking"):
                continue
            native_field = native_fields.get(cpp_identifier(field_name))
            if native_field is None:
                raise ValueError(
                    f"Managed field {managed['name']}.{field_name} has no "
                    "native peer."
                )
            if (
                managed_field["offset"] != native_field["offset"]
                or managed_field["size"] != native_field["size"]
            ):
                raise ValueError(
                    f"Managed/native field mismatch for "
                    f"{managed['name']}.{field_name}."
                )


def build_manifest(wpf_root: Path) -> dict[str, object]:
    command_types_path = wpf_root / COMMAND_TYPES_RELATIVE
    managed_layouts_path = wpf_root / MANAGED_LAYOUTS_RELATIVE
    native_layouts_path = wpf_root / NATIVE_LAYOUTS_RELATIVE
    render_data_layouts_path = wpf_root / RENDER_DATA_LAYOUTS_RELATIVE
    source_paths = (
        command_types_path,
        managed_layouts_path,
        native_layouts_path,
        render_data_layouts_path,
    )
    if not all(path.is_file() for path in source_paths):
        raise FileNotFoundError(
            "The WPF root does not contain the checked-in MCG command outputs."
        )
    command_source = command_types_path.read_text(encoding="utf-8-sig")
    managed_source = managed_layouts_path.read_text(encoding="utf-8-sig")
    native_source = native_layouts_path.read_text(encoding="utf-8-sig")
    render_data_source = render_data_layouts_path.read_text(encoding="utf-8-sig")
    managed_layouts, _ = parse_managed_layouts(managed_source)
    native_layouts, native_keys = parse_native_layouts(
        native_source,
        C_TYPEDEF_STRUCT_PATTERN,
        "native-command",
    )
    render_data_layouts, render_data_keys = parse_native_layouts(
        render_data_source,
        C_NAMED_STRUCT_PATTERN,
        "render-data",
    )
    duplicate_keys = set(native_keys) & set(render_data_keys)
    if duplicate_keys:
        raise ValueError(
            f"Duplicate native/render-data layout keys: {sorted(duplicate_keys)}"
        )
    layouts = native_layouts + render_data_layouts
    layout_keys = native_keys | render_data_keys
    validate_managed_overlap(managed_layouts, layouts)
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
    linked_layouts = {
        str(entry["layout"]) for entry in commands if "layout" in entry
    }
    expected_layouts = {str(layout["name"]) for layout in layouts}
    if linked_layouts != expected_layouts:
        raise ValueError("Not every native MIL packet layout maps to a command.")
    retail_commands = {
        str(entry["wpfName"])
        for entry in commands
        if 0 < int(entry["value"]) < int(commands[-1]["value"])
    }
    linked_commands = {
        str(entry["wpfName"])
        for entry in commands
        if "layout" in entry
    }
    if linked_commands != retail_commands:
        raise ValueError("Not every retail MIL command has a packet layout.")
    return {
        "schemaVersion": 1,
        "wireContract": {
            "byteOrder": "little-endian",
            "commandWidth": 4,
            "managedLayoutPack": 1,
            "nativeLayoutPack": 1,
        },
        "source": {
            "commandTypes": {
                "path": COMMAND_TYPES_RELATIVE.as_posix(),
                "sha256": sha256(command_types_path),
            },
            "managedLayouts": {
                "path": MANAGED_LAYOUTS_RELATIVE.as_posix(),
                "sha256": sha256(managed_layouts_path),
                "overlapCount": len(managed_layouts),
            },
            "nativeLayouts": {
                "path": NATIVE_LAYOUTS_RELATIVE.as_posix(),
                "sha256": sha256(native_layouts_path),
            },
            "renderDataLayouts": {
                "path": RENDER_DATA_LAYOUTS_RELATIVE.as_posix(),
                "sha256": sha256(render_data_layouts_path),
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
        default=PROGPU_ROOT / "eng/mil/wpf-mil-protocol.json",
    )
    parser.add_argument(
        "--header",
        type=Path,
        default=(
            PROGPU_ROOT
            / "src/ProGPU.Native/include/"
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
            f"{len(manifest['layouts'])} complete packet layouts."
        )
        return 0
    args.manifest.parent.mkdir(parents=True, exist_ok=True)
    args.header.parent.mkdir(parents=True, exist_ok=True)
    args.manifest.write_text(manifest_text, encoding="utf-8")
    args.header.write_text(header_text, encoding="utf-8")
    print(
        f"Generated {args.manifest} and {args.header}: "
        f"{len(manifest['commands'])} commands, "
        f"{len(manifest['layouts'])} complete packet layouts."
    )
    return 0


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except (FileNotFoundError, ValueError, KeyError, TypeError) as error:
        print(f"error: {error}", file=sys.stderr)
        raise SystemExit(1)
