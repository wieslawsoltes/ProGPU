# Native path and glyph execution pages

Original-source implementation only; no build, compiler, test, fixture, browser,
benchmark or GPU execution was run for this increment. Source formatting only.

| Original owned source | SHA-256 |
| --- | --- |
| `src/ProGPU.Native/src/Scene/progpu_native_semantic_render_execution.cpp` | `c0325d3d72f30d79f386cfc0619f84213090b67a3a203e9e69ac4e4add4a9dae` |
| `src/ProGPU.Native/src/Backend/progpu_native_path_execution.cpp` | `98ea51e80ad1e816396dad9e6c7baf0bd57cb62a086801f7676633c95168d6b5` |
| `src/ProGPU.Native/src/Backend/progpu_native_glyph_execution.cpp` | `9b131d1891faa278bb217a4ffb5558a426185dbc9ce2c48e15c5195d8dedd51b` |
| `src/ProGPU.Native/src/Backend/progpu_native_color_glyph_resources.cpp` | `6e0fdd20a9951a857422c0099c1762cc92f9c5e697c1cc860414fa119bbcff60` |
| `src/ProGPU.Native/src/Backend/progpu_native_path_boolean_gpu.hpp` | `c1a35d79fc18b1a7391aadb3120409f6464ed5b8425ac850a2542659e7453652` |
| `src/ProGPU.Native/src/Backend/progpu_native_path_dispatch.hpp` | `174f60c2fe445d42e8ffda38a8a11ed5d20c2c3577bee448041ff00fa3cceb6d` |
| `src/ProGPU.Native/src/Backend/progpu_native_path_pixel_mapping.hpp` | `d8b0843b92e1a4483b23113f128c205b0ee3c17947322f02d82862be6b432084` |
| `src/ProGPU.Native/src/Backend/progpu_native_glyph_raster_identity.hpp` | `e5da3db8815e7eb7cd73791a1f4419e4d73af11f2f53dcf6672c64f74f03d1cb` |
| `src/ProGPU.Native/src/Scene/progpu_native_semantic_glyph_paint.cpp` | `759fa316f751c098485eb89aab0508ed059ee6c1ed64516862a9815ec9c8b6d5` |
| `src/ProGPU.Native/src/Backend/progpu_native_path_text_resources.cpp` | `85dd78f7fa95fdca3f52ea01029e98a43a1845aba45eb35bfe493873f9d87989` |

`engine_resources/path` owns the native command-to-page flow: complete raw
segment/program inventory, source physical-frame admission, actual outer-DPI
guideline snapping followed by target localization, original material mapping,
ordered disjoint-range rejection and retained command frames. Raster planning
keeps native bitwise cache keys, phase rounding, 1024-to-8192 atlas growth,
4092-pixel tile limits, exact source/ordinary material quads and original direct
pixel-mapping flags. Original postfix record construction and bounded translated
leaf classification select inline, Boolean split and signed split phases.

`NativePathPageEngine` prepares actual device buffers and atlas generations with
unchanged canonical shaders. Independent operation-zero work retains the
original 256-byte binding-aligned size grouping, while signed and Boolean work
keeps its independent phase order. `encode_coverage` returns the actual preceding
command buffers and continuation encoder: submit preceding buffers individually,
in order, before continuing the consumer submission. This preserves the native
pre-split flush and ordinary split-leaf submission boundaries.

`engine_resources/glyph` owns native complete-resource admission, exact original
resource-byte sharing, source-indexed coverage aliases, native glyph/color atlas
packing, positioned instance order, original styles/paints, source alpha and
no reconstructed font/shaping metadata. Binary scan admission reuses the
unchanged owned `semantic/glyph_scan` helper moved from the builder. Coverage
identity includes original bounds/scale/phase bits and every selected segment
byte; each source outline retains its record/uniform/raster slot even when
coverage work is shared. Color bitmaps retain raw rows and restart their own
256-to-4096 packing inventory on growth.

`NativeGlyphPageEngine` prepares canonical grayscale compute, binary horizontal
then vertical compute, or explicitly selected grayscale fragment coverage;
binary scans never select the fragment route. `NativeGlyphPaintBindings` owns
actual external-image identity, source views, original address/sampling policy,
the engine's supplied material sentinel and exact group-3 bindings. Its
`preflight_source` admits the whole paint inventory before atlas/binding mutation.
No texture source is inferred from a picture, bounds or glyph coverage.

All pages retain immutable source generations. Prepared GPU generations expose
actual buffers/views/samplers for the separately owned ordered render engine and
hold source/producer owners through `retain_submitted` completion callbacks.
Warm reuse is explicit reuse of the immutable prepared generation; these APIs do
not pretend a rebuilt page is a cache hit. Metrics report actual preparation
payloads, texture storage and encoded phase counts, separately from source costs.

Remaining independent work: original forced scalar/SIMD glyph CPU execution is
not implemented by these GPU pages and rejects explicitly; native RGB coverage,
retained clip/mask/effect execution and complete ordered scene integration have
their own source contracts. Root owns shared draw/material pipelines and final
ordered composition. No package, native provider, original Windows pixel,
browser, source-host or performance qualification follows from authored code.
