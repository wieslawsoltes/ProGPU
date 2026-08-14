# Native C++ text-port provenance and execution plan

## Scope and policy

ProGPU's native text implementation is a parallel backend port of original,
ProGPU-owned clean-room C# and production shader code. The authoritative source
checkpoint for the first managed-picture glyph-lowering slice is
`2de7e5c618ce9515a46b915fb7ef91642b6fdfcb`. The repository policy in
`agents.md` permits this cross-language/backend work when each slice records
its in-repository source provenance, preserves public and performance
contracts, and adds matched differential tests. No third-party implementation
source is copied or translated.

WinUI controls, XAML, and media are outside this native text-port phase. The
target is the reusable font, shaping, fallback, layout, retained-scene, and GPU
text core used below those frameworks.

## Authoritative ProGPU sources

| Native responsibility | ProGPU-owned source of truth | Preserved contract |
| --- | --- | --- |
| Recorded shaped runs | `src/ProGPU.Scene/RenderCommand.cs` | Immutable glyph-index/position arrays, range ownership, transform, hinting, rendering mode, and font presentation state. |
| DPI/subpixel placement and glyph presentation | `src/ProGPU.Scene/Compositor.cs` | Maximum-singular-value raster size, 4-way physical subpixel phase, affine bases, bold/italic/font transform, style opacity, and unchanged-run reuse. |
| Font data and glyph outlines | `src/ProGPU.Text/TtfFont.cs` and its partials | Bounded SFNT/table validation, metrics, cached immutable outlines, color/bitmap metadata, and explicit ownership boundaries. |
| OpenType shaping | `src/ProGPU.Text/OpenTypeTextShaper.cs`, `src/ProGPU.Text.Shaping/` | Reusable glyph IDs, advances, offsets, clusters, script/language/direction state, GSUB/GPOS/GDEF behavior, and malformed-font failure rules. |
| Fallback and font discovery | `src/ProGPU.Text/FontManager.cs` and platform catalogs | Deterministic fallback order, cached font identity, and platform-neutral/provider seams. |
| Paragraph/line layout | `src/ProGPU.Text/TextLayout.cs` | Wrapping, trimming, alignment, positioned runs, caret/selection geometry, and hit testing. |
| Outline compilation and compute coverage | `src/ProGPU.Vector/PathAtlas.cs`, `src/ProGPU.Backend/Shaders/GlyphRasterizer.wgsl` | Exact line/quadratic/cubic outline semantics, bounded compute rasterization, winding rules, and retained atlas generations. |
| GPU text composition | `src/ProGPU.Backend/Shaders/Text.wgsl` | Physical-pixel atlas sampling, affine placement, style modes, masks, premultiplied output, and bounded shader work. |

Every C++ source file added for later phases must name the exact source file and
source checkpoint in its adjacent design note or provenance table. Structure,
ownership, and file boundaries may be redesigned for native performance; the
observable and performance contracts above remain authoritative.

## Cross-engine research gate

The architecture was checked against primary sources for
[Skia shaping/SkParagraph](https://skia.org/docs/dev/design/text_shaper/),
[DirectWrite text formatting](https://learn.microsoft.com/windows/win32/directwrite/text-formatting-and-layout),
[HarfBuzz shaping plans](https://harfbuzz.github.io/shaping-and-shape-plans.html),
[HarfBuzz's glyph/rendering boundary](https://harfbuzz.github.io/glyphs-and-rendering.html),
[Vello](https://github.com/linebender/vello),
[Parley](https://github.com/linebender/parley), and Firefox's
[WebRender architecture](https://firefox-source-docs.mozilla.org/gfx/RenderingOverview.html).
The adopted boundary keeps shaping and line layout as reusable retained results
while GPU rasterization, upload, batching, masking, and composition remain
device work. ProGPU adapts that boundary to its pointer-free scene ABI and
typed generation ownership. It rejects per-glyph native calls, character
remapping in the renderer, runtime reflection, unbounded caches, CPU texture
readback, and foreign source organization or implementation text.

## Delivered first bridge

`GpuPictureNativeSceneCompiler.Glyphs.cs` lowers already-shaped monochrome
`DrawGlyphRun` records to native outline/segment resources, positioned glyph
instances, and a deduplicated solid text-style page. Compilation is target-DPI
sensitive and explicitly records that dependency. It preserves transforms,
four-way subpixel placement, bold, italic, font stretch/skew, brush opacity,
and grayscale/aliased/ClearType selection. Color layers, embedded bitmap
glyphs, vector-fallback semantics, decorations, and text masks fail closed
until their dedicated lowering paths land.

For `G` positioned instances, `U` unique phase/raster outline variants, and `S`
outline segments, compilation is `O(G + S)` time with `O(U + G + S)` bounded
snapshot storage. Stable retained replay performs `O(G)` GPU instance work,
zero managed allocation, and zero repeat upload. The Apple M3 Pro/Metal matched
384-primitive picture now includes seven distinct outlines, 141 segments, and
18 bold positioned instances. Native/managed output differs by at most 2/255,
has zero pixels over 3/255, and has mean absolute channel difference
`0.000063175/255`; both replay paths allocate 0 B/frame.

## Parallel native implementation phases

1. Freeze bounded native byte ownership and provenance for SFNT/container,
   table-directory, metrics, cmap, and outline access.
2. Port TrueType/CFF, variation, bitmap/color, and SVG glyph data paths with
   malformed-font property/fuzz coverage.
3. Port Unicode decoding, grapheme/script/language itemization, bidi, GSUB,
   GPOS, and GDEF while differentially comparing glyph IDs, clusters,
   advances, and offsets against the authoritative C# implementation.
4. Add native fallback/provider seams for desktop, mobile, and browser without
   external runtime dependencies.
5. Port wrapping, trimming, caret/selection geometry, hit testing, and reusable
   positioned-run caches.
6. Connect native shaped runs directly to the standalone C++ retained-scene
   compiler and existing compute glyph/text pipelines.
7. Gate cold start, first interaction, sustained layout/shaping throughput,
   allocations, cache residency, malformed input, DPI/subpixel quality,
   browser AOT, and matched C#/C++ screenshots before claiming parity.
