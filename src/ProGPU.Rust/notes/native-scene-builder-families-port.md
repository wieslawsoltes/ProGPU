# Native scene builder drawing families

`native_scene/builder/{geometry,path,three_d,glyph,text}.rs` and their private
mesh/stroke/scan helpers port the original native producers over owned wire
records. These methods join the independently ported recorder core; they do not
route through managed Scene constructors or infer source metadata from bounds.

The public producer inventory is `draw_geometry`, `draw_vertex_meshes`,
`draw_source_coverage`, `draw_strokes`, `draw_paths`, `draw_source_paths`,
`draw_lines_3d`, `draw_meshes_3d`, `draw_meshes_3d_with_lights`,
`draw_meshes_3d_with_materials`, `add_text_style`, `add_glyph_outlines`,
`add_color_glyph_bitmaps`, `draw_rgb_glyph_run`, `draw_glyph_run`, and
`draw_shaped_text_run`. `NativeShapedTextSceneOptions` carries the original
basis/color/atlas scale/bold/skew defaults. Shaped conversion consumes the actual
native `ShapingGlyph` and `NativePositionedTextGlyph` records and caller-owned
scratch; it does not fabricate shaping records.

The port retains gap-free and overlapping outline/segment inventories, raw
glyph-only binary scan descriptors, bit-exact text-style deduplication, original
RGB restrictions, source hit-ink metadata only under an actual selected owner,
independent explicit source paths/coverage, packed mesh/stroke layout rules,
and source camera/light/material policy. 3D packed inputs discard only
unreferenced trailing elements, preserving the source section-length inference.
Record appends reserve geometric capacity before publication.

Failure behavior remains source-specific: the shaped-text bridge may write a
valid prefix of caller scratch before rejecting a later occurrence. Each
successful 3D material `add_brush` remains retained if a later material append or
final mesh allocation fails, exactly as the original sequential producer does;
the final mesh resource/command pair is still published together. The port does
not silently turn that source behavior into a transaction. Rust checked sizes
and bounded slice access keep invalid extents memory-safe.

Original sources and SHA-256:

| Source | SHA-256 |
|---|---|
| `Scene/Builder/progpu_native_scene_builder_geometry.cpp` | `30eeac22e69ed3cf20325909af426bf18796e7f82f4685dad129717d750fa2c1` |
| `Scene/Builder/progpu_native_scene_builder_path.cpp` | `fae34a31c7179600d4834896107e8859bbcd208d2f54ebe030cede08bcc8df66` |
| `Scene/Builder/progpu_native_scene_builder_3d.cpp` | `33e575b6378b4e29b81e3f5b0b2aa1bc6e24a703d26e07aac557fdebaad8d7ef` |
| `Scene/Builder/progpu_native_scene_builder_glyph.cpp` | `e0a682de64ee1be87fb72a01aa3b00cc956ef4c069b568cd7fa2392ffd8226b3` |
| `Scene/Builder/progpu_native_scene_builder_text.cpp` | `a9b8658d35ea51e0f217a7f15093384498a3ef5dee248fbbd0f742dfb3b31c88` |
| `Backend/progpu_native_vertex_mesh.hpp` | `ec6cd5443cfbff74b1261e026e030a0b42728b0bc3199d5a495bfaa09abb3861` |
| `Scene/progpu_native_semantic_stroke.hpp` | `65137f961b2250456dcf0d5a61fb1896f657d7c9f52c8f9f6f35a610933ca082` |
| `Text/Font/progpu_native_binary_glyph_contract.hpp` | `7f6d3c2a1c003ef60c9282371db2b262ff813d1cc4fa1ec5cfc123a4e6b14de4` |

Paths above are relative to `src/ProGPU.Native/src`. Shared scalar and source
coverage helpers retain their separate provenance notes. The existing DESIGN.md
architecture reference section remains applicable; no foreign implementation is
copied. Only source formatting was performed. Compilation, complete original
wire equality/failure controls, GPU execution and source/package/browser
qualification remain deferred until implementation is complete.
