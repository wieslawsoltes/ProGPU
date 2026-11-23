# Original static DXF text production

Implementation only: source formatting, no builds/checks/tests/fixtures, fonts,
browser/GPU execution, benchmarks or generated artifacts.

`StaticDxfBuffer::compile` and `compile_commands` now accept original archive
DrawText/DrawGlyphRun sources that resolve to retained outlines or vector/color
paths. `StaticDxfCompileOptions::text` supplies the actual immutable preparation
profile and fallback font inventory. The shared archive text descriptor producer
is reused without recording a pretend ordinary Scene text frame. Original
DrawText shape/layout policy stays distinct from metadata-free positioned arrays.

The new retained sink enters `TextPainter` at the original point: after color
layers, before bitmap lookup, vector preference, clip culling or atlas placement.
Only a solid brush plus an actual nonempty outline admits the retained route.
Original `(font instance, glyph)` identity caches one raw fill record/segment
inventory. Missing/empty outlines continue the original fallback and are not
invented no-ink successes. Raw design coordinates pass through the existing
original CompileFillPath writer, preserving source contour order, implicit
closure, control-point bounds and fill rule. The 112-byte instances retain exact
em/scale/skew/Y inversion, each bold pass, source position, active transform,
effective color, coverage gamma/source contrast and original 4/8 sample-grid
selection. A corresponding actual font owner survives every unique record.

The async `compile_with_glyph_atlas` and `compile_commands_with_glyph_atlas`
consume an actual `GlyphAtlas` exclusively. They preserve the existing original
bitmap/atlas requests, source phase, raster em, selected texture dimensions,
legacy presentation flags and capacity fallback. Planning and family admission
precede the atlas batch. Errors/cancellation cannot return a partly-mutated atlas;
successful buffers retain its exact source keys, residency and final textures,
with no mutable atlas handle published. Its mutable packing cache, producer GPU
buffers and CPU scratch retire after the final texture/residency capture; the
buffer retains only actual frozen coverage/color textures, source keys and producer
metrics. An atlas that produces no text instances retains no texture storage. This explicit immutable ownership
replaces the managed context's mutable lifetime mechanism, not its glyph policy.
The synchronous API rejects genuinely unresolved atlas requests; it never renders
bitmap glyphs as synthetic outlines or changes source choices to avoid the GPU.

Static active GPU flags retain the original identity camera, model frame and
clip bypass. Text uses ActiveCompilationContext legacy styles and original static
zoom arithmetic. All emitted atlas instances receive final `ForceTextUseMvp`;
vector paths keep final +200. Flush groups preserve the source's separate streams:
Vector first, Text second at each extension boundary. All retained analytic glyph
instances draw once after those ranges, exactly where DrawStaticDxfBuffer emits
them; they are not inserted into ordinary painter-order atlas runs. Inner clips
preserve original early-culling rules, including retained glyphs' source bypass.
Every family shares the actual target viewport/placement; text and retained glyphs
consume the original resolved outer mask and independent blend/source-alpha mode.

The canonical `GlyphCoverageTransfer` + `RetainedGlyph.wgsl` shader is included
unchanged. Static engine upload/preparation now owns all record/segment/instance/
legacy-style buffers, actual frozen atlas references, target pipeline variants and
mask bindings. Whole-source preflight checks exact device ownership, residency,
storage limits and complete retained record ranges before renderer mutation.
Prepared/submitted ownership follows the existing static queue-completion lease.
Metrics include Text/retained draw counts, actual glyph upload bytes, glyph texture
residency and exact target uniform allocation. No CPU raster surrogate is added.

Original integer-origin static glyph runs and hinted retained-DXF admission remain
rejected. Original non-static text-buffer recompilation is an independent path;
it is not substituted for retained static storage. Existing archive field/input
limits, source-only shaping contracts, unsupported external font fallback,
unrelated static extensions and source/package/pixel/performance qualification
remain explicit. Byte metrics report actual new upload/texture ownership,
not a claimed managed allocation match. Retained glyph coverage has no extra
compute pass: the original analytic fragment shader owns that work.

| Original owned source | SHA-256 |
|---|---|
| `src/ProGPU.Scene/RetainedGlyphGeometry.cs` | `cb0fdc5d981b848bd8e0778d28636ba8f124c86102d13e1db8820f90d796c44a` |
| `src/ProGPU.Scene/RetainedGlyphShaders.cs` | `ef40a685d7bd713fb2bcc83e31c8caf5069d27bfc8942625bcece152cfdc3547` |
| `src/ProGPU.Scene/Shaders/RetainedGlyph.wgsl` | `733e79ac55aa26acb2c32714e76d79003f34ea07d54a6e24aace8e769313b062` |
| `src/ProGPU.Scene/DxfStaticBuffer.cs` | `1f150883f9dfa947debc9d1ac1c615eac663c8c08bbf5fc81d770148754da4bf` |
| `src/ProGPU.Scene/StaticCompilationContext.cs` | `4b50d72ae384249b2c1ee4dd09f7062387b55a2ecd3a64046222b914335d89be` |
| `src/ProGPU.Scene/Compositor.cs` (`TryCompileRetainedGlyph`, text/positioned compilation, static flush/draw and pipeline layouts) | `a45a99f135806d248fc3792dcbdb5ebe17c094821c6aed080dcdedf4025237cd` |
