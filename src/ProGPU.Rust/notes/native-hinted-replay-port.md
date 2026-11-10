# Original captured-font replay increment

Implemented the original prepared physical-outline route from
`FontReplayGeometry` through `Scene::draw_hinted_text`, shared glyph atlas and
actual Renderer submissions. No shaping metadata or font execution is invented.
`HintedReplayDraw` retains the selected original descriptor/no-ink slots, source
ink/brush domain, exact prepared DPI, coverage transfer and Auto/IntegerPixel
policies. Whole-range admission precedes atlas work; integer origins include
the complete target translation before half-toward-positive-infinity rounding.
Ink bounds retain division, storage bounds retain their original reciprocal
multiplication, and private raster bounds retain the original grouping.

Atlas keys retain the exact captured raster generation across selected views;
distinct captures do not alias. Native physical records upload without coordinate,
bound, contour or scan-word changes. Original four-pixel checked padding, fixed
scale/phase, bounded binary admission, physical dictionary slots/LRU ties,
best-fit failed-region reuse, cancellation rollback, outline trim invalidation,
growth/residency and actual submission ownership use the existing shared atlas.

Solid paint uses canonical `Text.wgsl`. All registered spatial brush families and
the existing original `ImageBrush` mapping use canonical `HintedGlyphPaint.wgsl`
with its unchanged 96-byte paint/instance ABI, original per-occurrence order,
six-/twelve-vertex extended/bounded image route, exact source mapping, original
sampling/addressing, alpha interpretation and four-mask shader path. Texture
uploads and mip owners reuse the actual renderer image cache. No white coverage
union, surrogate geometry, CPU pixels or image resampling is introduced. Selected
views retain their captured generation through residency and replay owners.

| Original ProGPU source | SHA-256 |
| --- | --- |
| `src/ProGPU.Scene/DrawingContext.HintedGlyphs.cs` | `efde36d1a824ceff5df764c7cda0d5921ad9caae3389159f4fbc4d39bc103b90` |
| `src/ProGPU.Scene/HintedGlyphCommandGeometry.cs` | `72f74f8af8ea407822f7d3a9200850d7fb8c7c5694a84ece8ab2e5ddb9a629d0` |
| `src/ProGPU.Scene/IntegerGlyphRasterOrigin.cs` | `31977e52b6250b3d02fe3270d4c18593d31f532a4f39e1a68f564c282e981aa4` |
| `src/ProGPU.Scene/Compositor.HintedGlyphs.cs` | `237b5c67c1c23c88b3f8abc739f58187fb93e86e2a6996b825a7ad59173a484b` |
| `src/ProGPU.Scene/Compositor.HintedGlyphPaint.cs` | `6e0bdc5b62a950f9ac1ead890dee2a554191005f31e28c113a30cf75cb8a41b3` |
| `src/ProGPU.Scene/GpuHintedGlyphPaint.cs` | `925fc8999d43e995c7eb976b96f469a32b67fed0fcdfd377a46f0598d45f5455` |
| `src/ProGPU.Text/GlyphAtlas.HintedGeometry.cs` | `fab0c90753583197b3f057ea8eb9ea98e952e22d78e1576b557d7023dcd8adcc` |
| `src/ProGPU.Backend/Shaders/HintedGlyphPaint.wgsl` | `1afaa272899c9f498602a9e21e9101540497afa37a8ac0cd44343b1f050014d2` |

The shared shader composition is copied from the original `Shaders.cs` include
order. Existing original ImageBrush/registered-material implementations are reused;
no foreign implementation was consulted. DESIGN.md architecture references remain
applicable. Vec staging uses fallible reserves; Arc/Box allocation follows the
crate's existing allocation contract. Rust exclusive async atlas ownership replaces
the original mutable compositor lifetime; cancelled work cannot publish residency.

Independent remaining scope: actual safe FreeType backend and dependency/loaded
image verification, shaped-paragraph-to-captured-geometry source adapters, original
static DXF/late-camera replay, direct per-glyph destination-sampling advanced blend
integration and R8 mask-target paint pipeline variants. These source paths are not
relabeled as the connected source-over Scene route. Ordinary rendering policies
are unchanged; source GDI/GDI+, native packages, browser producers, original pixels,
performance and provider/application qualification remain unproven.

No compiler, lint, tests, browser, benchmarks or new fixtures were run/authored,
per the mandatory port-first instruction. Only owned source formatting was used;
no build, trace or result artifacts were generated.
