#!/usr/bin/env python3
"""Guard coverage of directly declared buffer/texture ownership fields.

This source guard supplements runtime accounting tests; it is not an allocation
tracker or proof of driver residency. New ownership containers require review.
"""
from pathlib import Path
import re

root = Path(__file__).resolve().parents[1] / "src/ProGPU.Native/src"


def source(path):
    return re.sub(r'//[^\n]*|/\*.*?\*/|"(?:\\.|[^"\\])*"', " ", path.read_text(), flags=re.S)


def body(text, start):
    opening = text.index("{", start)
    depth = 1
    for end in range(opening + 1, len(text)):
        depth += (text[end] == "{") - (text[end] == "}")
        if depth == 0:
            return text[opening + 1:end]
    raise ValueError("Unterminated C++ declaration")


def fields(text, name):
    match = re.search(r"\bstruct\s+" + name + r"\s*\{", text)
    if not match:
        raise ValueError(f"Missing ownership struct {name}")
    declaration = body(text, match.start())
    depth = 0
    top = []
    for char in declaration:
        if char == "{":
            depth += 1
        elif char == "}":
            depth -= 1
        elif depth == 0:
            top.append(char)
    top = "".join(top)
    handles = r"WGPU(?:Buffer|Texture)\b"
    direct = re.findall(handles + r"\s+(\w+)\s*(?:=|;)", top)
    containers = re.findall(r"std::(?:array|vector)<\s*" + handles + r"[^>]*>\s*(\w+)\s*(?:=|;)", top)
    return set(direct + containers)


collector = source(root / "Backend/progpu_native_engine_memory.hpp")
# Shader bindings retain picture backings even after the optional picture cache
# evicts them. Enumerating only the cache misses these in-flight owned textures.
shader_binding = source(root / "Scene/progpu_native_shader_effect_execution.hpp")
if not re.search(r"std::shared_ptr<semantic_picture_backing>\s+sampler_picture\s*;", shader_binding):
    raise ValueError("Shader sampler picture ownership declaration needs review")
if not re.search(r"inventory\.texture\(value\.sampler_picture->texture\)", collector):
    raise ValueError("Shader sampler picture leases are missing from memory inventory")
owners = [
    ("Scene/progpu_native_shader_effect_execution.hpp", "semantic_shader_binding", "value", "semantic_shader_binding"),
    ("Backend/progpu_native_engine.hpp", "progpu_native_engine", "engine", None),
    ("Backend/progpu_native_webgpu_resources.hpp", "path_raster_resources", "value", "path_raster_resources"),
    ("Scene/progpu_native_semantic_replay.hpp", "semantic_layer_slot", "value", "semantic_layer_slot"),
    ("Scene/progpu_native_semantic_replay.hpp", "semantic_image_draw", "value", "semantic_image_draw"),
    ("Scene/progpu_native_semantic_replay.hpp", "semantic_analytic_page", "semantic_analytic_cache", None),
    ("Scene/progpu_native_semantic_replay.hpp", "semantic_image_page", "semantic_image_cache", None),
    ("Scene/progpu_native_semantic_replay.hpp", "semantic_3d_page", "semantic_3d_cache", None),
    ("Scene/progpu_native_semantic_replay.hpp", "semantic_picture_backing", "picture", None),
    ("Scene/progpu_native_semantic_replay.hpp", "semantic_render_bundle_span", "span", None),
    ("Scene/progpu_native_semantic_replay.hpp", "semantic_glyph_paint_uniform_binding", "binding", None),
]
aliases = {"bound_analytic_brush_buffer", "bound_analytic_gradient_buffer", "bound_text_style_buffer"}
count = 0
for path, name, prefix, visitor in owners:
    declared = fields(source(root / path), name)
    if name == "semantic_layer_slot":
        if not aliases <= declared:
            raise ValueError("Binding-cache alias exemptions need review")
        declared -= aliases
    if name == "semantic_glyph_paint_uniform_binding":
        paint_keys = {"uniform", "brushes", "stops", "paints"}
        if declared != paint_keys:
            raise ValueError("Original hinted-paint binding-cache keys need review")
        # Bind groups retain the actual resources. These fields are identity
        # comparisons only and can outlive source-container invalidation.
        declared -= paint_keys
    scope = collector
    if visitor:
        match = re.search(r"const\s+" + visitor + r"&\s+value\)", collector)
        if not match:
            raise ValueError(f"Missing visitor for {visitor}")
        scope = body(collector, match.start())
    for field in declared:
        access = rf"\b{prefix}(?:\.|->){field}\b"
        macro = rf"\b[BT]\({field}\)" if prefix == "engine" else r"(?!)"
        if not re.search(access + "|" + macro, scope):
            raise ValueError(f"Native memory inventory omits {name}.{field}")
        count += 1
print(f"Native memory inventory covers {count} declared owned buffer/texture fields; 7 non-owning cache identities excluded.")
