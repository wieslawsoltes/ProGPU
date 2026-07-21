# Text shaping showcase: research and design record

## Scope

The page is an inspection surface for the shared `CpuOpenTypeShaper`,
`ShapingRequest`, `ShapingBuffer`,
`TextShapingOptions`, `TextVisual`, font manager, and retained `DrawGlyphRun` path.
It does not own a parallel shaping or rendering implementation. The accompanying
shared-engine work adds typed item context, BOT/EOT boundary handling, and glyph
safety propagation that the page exposes directly.

The page deliberately combines two views of the same result:

- a rendered specimen, following the role of HarfBuzz `hb-view`;
- glyph IDs, source code points, UTF-16 clusters, safety flags, advances, offsets,
  and segment properties, following
  the role of HarfBuzz `hb-shape`.

## Primary sources

- [HarfBuzz utilities](https://harfbuzz.github.io/utilities.html) documents
  `hb-shape` as an inspectable shaping-result tool and `hb-view` as its rendered
  counterpart. Both expose direction, script, language, features, variations,
  cluster settings, and verification controls.
- [HarfBuzz buffer API](https://harfbuzz.github.io/harfbuzz-hb-buffer.html) defines
  the four cluster levels, output cluster meaning, default-ignorable policies, and
  glyph safety flags.
- [HarfBuzz OpenType features](https://harfbuzz.github.io/shaping-opentype-features.html)
  documents global and ranged feature settings and direction-sensitive defaults.
- [HarfBuzz shape plans and caching](https://harfbuzz.github.io/shaping-plans-and-caching.html)
  explains caching plans by face, segment properties, user features, and shaper.
- [Skia CanvasKit text shaping](https://skia.org/docs/user/modules/quickstart/)
  keeps font management, paragraph construction, layout, and drawing as explicit
  reusable stages.
- [Skia shaped-text design](https://docs.skia.org/docs/dev/design/text_shaper/)
  argues for a two-step shaped result that can be inspected, drawn, edited, or
  animated independently of the DOM or a specific renderer.
- [DirectWrite `GetGlyphPlacements`](https://learn.microsoft.com/en-us/windows/win32/api/dwrite/nf-dwrite-idwritetextanalyzer-getglyphplacements)
  exposes the cluster map, glyph indices, advances, offsets, script analysis,
  locale, direction, and feature ranges as separate shaping inputs and outputs.
- [Direct2D and DirectWrite text rendering](https://learn.microsoft.com/en-us/windows/win32/direct2d/direct2d-and-directwrite)
  recommends reusing a text-layout object because it caches glyph positions, while
  preserving `DrawGlyphRun` for custom renderers.
- [Win2D `CanvasTextLayout`](https://microsoft.github.io/Win2D/WinUI2/html/T_Microsoft_Graphics_Canvas_Text_CanvasTextLayout.htm)
  similarly exposes reusable rich-text layout and cluster metrics rather than
  recomputing layout for repeated draws.
- [WebRender glyph rasterizer source](https://searchfox.org/mozilla-central/source/gfx/wr/wr_glyph_rasterizer/src/lib.rs)
  keeps glyph rasterization as a dedicated subsystem with platform font backends;
  shaping/layout results remain distinct from cached raster output.
- [Vello source and architecture](https://github.com/linebender/vello) uses retained
  scenes and GPU compute for parallel rendering work while tracking glyph caching
  as a distinct concern.
- [Parley API](https://docs.rs/parley/latest/parley/) shares coarse-grained font and
  layout contexts, reuses scratch allocation, retains shaped layout, and permits
  repeated line breaking/alignment without reshaping unchanged text.

## Cross-engine comparison

| Concern | Production/research engines | ProGPU showcase decision |
| --- | --- | --- |
| Startup and lazy initialization | Skia/DirectWrite construct reusable font/layout resources; Parley shares `FontContext` and `LayoutContext`; WebRender separates rasterizer startup. | Page creation is navigation-lazy. Existing Inter/Noto accessors and the process font manager are reused. No private registry, graphics device, or font enumeration loop is introduced. |
| Shaping and layout reuse | HarfBuzz caches shape plans; DirectWrite/Win2D cache positioned layouts; Skia/Parley retain shaped layouts. | The live inspector shapes only on user edits. Static specimens use retained `TextVisual` commands. The preview stores its glyph and position arrays and does no shaping in `OnRender`. |
| Retained scene and visibility | Skia paragraphs, DirectWrite layouts, Vello scenes, and WebRender display/raster stages preserve reusable results. | Existing scene compilation, culling, and retained glyph runs are used unchanged. The page does not schedule frames or invalidate the root. |
| Glyph/path/texture cache keys and eviction | WebRender and Vello treat glyph raster caches separately from shaping; platform engines include face, size, transform, and rendering mode in strike/cache identity. | No cache policy changes. Existing ProGPU glyph/path atlas keys, generations, and capacity recovery remain authoritative. |
| Demand-driven upload | WebRender rasterizes requested glyphs; Vello records scene content before GPU work; Direct2D creates device-dependent resources separately. | Static text stays retained and glyph upload remains compositor-driven when content is visible. |
| Worker preparation | Parley reuses layout scratch; browser engines commonly prepare fonts/layout away from raster submission. | The Samples startup already warms system-font metadata asynchronously. The page reuses that catalog and resolves a complex-script face only when the page or preset needs it. |
| GPU batching | Vello moves parallel raster work to GPU compute; WebRender batches raster/upload; Skia and Direct2D submit glyph runs. | The page sends one retained `DrawGlyphRun` for the live result. Unicode/OpenType shaping remains the deterministic CPU reference; shared WebGPU plan contracts are described but not substituted into this diagnostic surface. |
| DPI, subpixel, and hinting | Direct2D uses DIPs and DPI-aware text rendering; Vello distinguishes cached hinted UI text from transform-heavy vector text; browser engines retain platform-specific raster modes. | Existing ProGPU physical-pixel atlas scale, quarter-pixel snapping, vector fallback phases, and final placement are untouched. |
| Fallback fonts | Skia/Parley/DirectWrite resolve fonts before shaping each run and retain the selected face in shaped output. | The live preset selects one face for its representative script. Script cards report when the platform has no matching face instead of presenting missing glyphs as successful shaping. |
| Variable fonts | HarfBuzz shape plans include variation state; DirectWrite placement consumes a font face instance; Parley retains normalized coordinates per run. | The page points to the existing Inter variable specimen and documents variation-aware plan/GPOS support without creating a parallel axis implementation. |
| Device loss and atlas invalidation | Direct2D separates device-independent layouts from device-dependent resources; WebRender/Vello rebuild GPU resources independently of shaping. | No new device-owned state is stored by the page. Existing compositor device-loss and atlas-generation contracts continue to govern rendering. |

## Adopted, adapted, and rejected

Adopted:

- `hb-view`-style rendered specimen beside `hb-shape`-style numeric output;
- explicit direction, script, language, cluster level, buffer policy, and
  HarfBuzz-like `tag[start:end]=value` feature editing;
- editable pre/post item context, paragraph-boundary flags, unsafe-concat output,
  and safe-tatweel output;
- a `Verify safe fragments` buffer-policy preset that exercises the shaper's
  exact fragment-reconstruction check;
- one immutable shaping request and one inspectable value buffer;
- retained preview arrays, no shaping or scheduling in `OnRender`;
- honest font-coverage reporting and direct access to complex-script presets.

Adapted:

- command-line switches became accessible ComboBox and TextBox controls;
- HarfBuzz numeric output became a compact, theme-aware on-page report;
- the engine matrix became curated before/after feature pairs and script cards,
  using the exact ProGPU rendering path rather than pre-rendered reference images.

Rejected:

- embedding a second HarfBuzz runtime or invoking `hb-shape` from the sample;
- rendering precomputed screenshots that would bypass current ProGPU output;
- eagerly loading every system font or rebuilding a font registry per page;
- performing shaping inside `OnRender`;
- claiming unavailable platform font coverage as a shaper success;
- changing raster quality, DPI snapping, cache invalidation, or GPU policy for a
  diagnostic page.

## Validation targets

- Release build of the Samples project with zero warnings and errors.
- Focused headless render at 1280 × 800 with a non-empty pixel assertion and PNG
  artifact.
- Existing shaping contract tests for ranged features, default ignorables,
  cluster levels, directions, normalization, vertical metrics, and CPU output.
- Visual inspection of the captured page for clipping, contrast, baseline
  placement, and retained glyph visibility.
