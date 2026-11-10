# Native scene semantic payload port

Production source port, not execution or package qualification. No compiler,
checks, tests, browser, shader execution or benchmarks were run for this increment.

Implemented in `native_scene/semantic/{brush,text,color_glyph,layer_mask,rgb_glyph,shader_effect,hit}.rs`:

- All eleven native brush families, raw stop inventories, hatch dash records,
  Perlin table policy, path-gradient inventory and draw-brush references.
- Complete styled/material/texture glyph prefixes, reserved/sampling/coverage
  fields, color-bitmap bounds and explicit RGB outline/tile admission.
- All eight layer-mask kinds, legacy composite prefixes, ordered primitive/stream
  ranges, exact zero-byte unused analytic records, inverse-transform representability,
  rational segments and bounded postfix Boolean programs. Nested scene byte ranges
  are returned in original component order for the parent parser's recursive pass.
- Shader resource versions 1–6, source frame and bytecode admission through the
  existing owned raw `ShaderResource::read`, returning sampler/input dependencies
  and capture dimensions for earlier-picture admission in the parent parser.
- Complete hit-index byte layout, raw primitives and path slices, ordered DAG
  parents, containment and once-only primitive references, including unused records.

The parent parser owns resource/command order, error remapping, recursive limits,
whole-stream metrics, and image dependency checks. Payload admission does not prove
GPU target/provider support. RGB opaque-target/translation execution admission and
native material/style page lowering remain separate production consumers.

Scratch: brush/text/color/RGB traversal is constant-space. Layer Boolean decoding
uses one fixed 63-record program buffer; returned nested stream locations require a
bounded owned vector. Hit admission retains the original two byte-counter arrays
with fallible reservation. Shader admission currently reuses the actual owned
bytecode-to-WGSL parser and allocates program/translation storage, unlike native
scratch-only validation. Explicit fallible allocations preserve OOM diagnostics;
ordinary Rust Arc/String allocator exhaustion is not recoverable by this adapter.
No alternate or foreign shader compiler was substituted.

Original source SHA-256:

| Source | SHA-256 |
|---|---|
| `src/ProGPU.Native/src/Scene/progpu_native_semantic_brush.cpp` | `bee6831bf6b8a40a7956918d54c5c668642e8cd4078ae30f09407641e34a9b8d` |
| `src/ProGPU.Native/src/Scene/progpu_native_semantic_brush_validation.cpp` | `07fe4387eb0b4d8ffe4ccf88299767c2816d3b054314a1284fb63da5ad54a95c` |
| `src/ProGPU.Native/src/Scene/progpu_native_semantic_text_style.cpp` | `52c3a8904530a6d09ed0853afe865ffa3edb785967e58a315f85088b9b8548c6` |
| `src/ProGPU.Native/src/Scene/progpu_native_semantic_color_glyph.cpp` | `da49fe835f4101613a102950072dcc13b76f721cf90e808ee36cc1dd862a5972` |
| `src/ProGPU.Native/src/Scene/progpu_native_semantic_layer_mask.cpp` | `6c3b0b22d2e7aac8a1e9d17b4d774d1dc7c8ae090f90766653581bc1003a370a` |
| `src/ProGPU.Native/src/Scene/progpu_native_semantic_rgb_glyph.hpp` | `ad6fd9c66013c53901668f99a1ef7a2eb433fd41c82637e8bc943275230169fa` |
| `src/ProGPU.Native/src/Scene/progpu_native_shader_effect_resource.hpp` | `fda73197f0b2e6d1d0f1ea14d19824a30760fd1dc4a458f479da4645ac6e8467` |
| `src/ProGPU.Native/src/Scene/progpu_native_scene.cpp` | `a9630f05490363aeb82883a3901394187f6d36985d592faad88b1aa30975ee1c` |
| `src/ProGPU.Native/src/Scene/progpu_native_semantic_validation.cpp` | `3b49e72ee4a6d3d6d413f3e5f6eb3772d3894e730a0ec4f1926e6b0642449c32` |
| `src/ProGPU.Native/src/HitTesting/progpu_native_hit_testing_validation.hpp` | `cad1e7d60bb45df08cb96f8a093a30440dbb089817d25a83c3ea537ab299186a` |
