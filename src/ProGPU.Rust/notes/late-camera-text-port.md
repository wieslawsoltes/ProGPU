# Original ordinary late-camera text

Implementation only. No compilation, tests, fixtures, browser execution,
benchmarks, font execution, GPU execution or generated artifacts were performed.

`Scene::draw_text_late_camera(TextDraw, TextPaintContext, Matrix4)` retains the
actual paragraph/positioned-font owner, model frame and separate command camera.
Archive `DrawText` and `DrawGlyphRun` now enter this route when the original
`UseGpuTransforms` flag is set. Missing source fonts or empty selected runs still
retain the original last-camera selection through an empty command with its camera.
The ordinary archive layout environment and existing original text policies stay
explicit; this does not invent source shaping metadata for positioned glyphs.

Text planning retains original model-only atlas size, placement, integer origins,
source phases, selected glyph ranges, bold/italic/font transforms, color layers,
bitmap selection and capacity fallback. The command camera participates in the
original path-coverage policy and disables the ordinary CPU clip shortcut. Shared
text styles are chosen before the post-command flag rewrite: ordinary late camera
is not an `ActiveCompilationContext` or static text-buffer producer. Resolved
atlas glyphs receive the exact scalar `ForceTextUseMvp` decode/re-encode, preserving
independent color and source grayscale transfer. Resolved vector/color/fallback
paths keep the existing model coordinates, camera raster scale/phase and +100
shape offset. Resolution and publication retain the existing whole-atlas
transaction and final texture/residency generation.

Every marked command retains its own camera for preparation; the final marked
command selects the common compiled frame camera, including no-draw commands.
Source placement composes that camera with the actual capture placement, leaving
model geometry unchanged. Cached ClearType suppression and source opacity remain
independent captured metadata. Text uploads now bind the same compiled camera
frame as vector uploads. This deliberately preserves a source distinction:
ordinary late-camera uniforms contain identity `Mvp` and the last camera in
`View`, while canonical `TextGlyphGeometryCommon.wgsl` uses `Mvp` for flagged
glyphs. No camera is substituted into text MVP and no glyph positions are moved
to compensate. The canonical shaders are unchanged.

The adjacent actual hinted paths already retain style-group paragraph owners,
spatial material/texture paints, per-occurrence advanced texture composites and
the original direct-R8 Text/Hinted painter families. This increment does not
duplicate those producers. `CompileHintedGlyphCommand` explicitly rejects active
GPU transforms, incompatible DPI/basis/static zoom, retained static-DXF glyph
storage and static spatial paint contexts. Those are original source admission
rules, not missing inferred late-camera hinted producers. Static DXF ordinary
text retains a separate retained-glyph/cache/recompilation contract; +100 ordinary
text does not qualify its +200 static route. Native text transports, arbitrary
non-affine model frames and lossless serialization of the private lowered Scene
wrapper retain their existing explicit boundaries.

No original source, native/browser package, pixel or performance qualification
follows from this source implementation. Original producer and frame gates remain.

| Original owned source | SHA-256 |
|---|---|
| `src/ProGPU.Scene/Compositor.cs` (`ForceTextUseMvp`, ordinary compilation, text/glyph compilation, root/offscreen uniforms) | `a45a99f135806d248fc3792dcbdb5ebe17c094821c6aed080dcdedf4025237cd` |
| `src/ProGPU.Scene/Compositor.HintedGlyphs.cs` (original independent hinted admission and paint) | `237b5c67c1c23c88b3f8abc739f58187fb93e86e2a6996b825a7ad59173a484b` |
| `src/ProGPU.Scene/IntegerGlyphRasterOrigin.cs` (model-frame and original selected-array admission) | `31977e52b6250b3d02fe3270d4c18593d31f532a4f39e1a68f564c282e981aa4` |
| `src/ProGPU.Backend/Shaders/TextGlyphGeometryCommon.wgsl` (unchanged flag/MVP consumer) | `321a1eed489c77c51c04fb89b5fa96769ad96afd142b4edcef1a492d93f19f25` |
