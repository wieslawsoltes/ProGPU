# Native C++ text-port provenance and execution plan

## Scope and policy

ProGPU's native text implementation is a full parallel backend port of the proven,
original ProGPU-owned clean-room C# implementation. Managed and native builds share
the same canonical production shader files rather than maintaining shader forks. The authoritative source
checkpoint for the first managed-picture glyph-lowering slice is
`d5a41e169f19f2da103a7cd8001f35f3b250198d`. The repository policy in
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
source checkpoint in its adjacent design note or provenance table. Native data layout,
ownership, and file boundaries may be optimized for native performance, but the applicable
algorithms and observable, quality, complexity, and performance contracts are ported in
full. Shader algorithms use the same canonical ProGPU resource files in both managed and
native builds; only generated embedding or binding wrappers may differ.

Blittable font, shaping, layout, and scene transport records added by this port use the
same header-driven C# generation lane as existing native scene records. This removes
parallel handwritten field layouts while keeping managed ownership and ergonomic APIs
outside the wire contract. Every new eligible record must add its generator marker and
pass the stale-output plus native/C# size-and-offset gates in the same slice.

## Delivered borrowed SFNT/TTC foundation

The first text-core slice ports the ProGPU-owned `SfntFontFace.cs` contracts at
checkpoint `2f2a92c4286da763d4e4be0908b0f6b706a86c3f` into the standalone
`progpu_native_text` C++20 library. `sfnt_font_view` borrows a caller-owned byte
span and retains no file, mapping, decoder, or heap ownership. It validates the
SFNT/TTC header and directory bounds once, preserves TTC absolute table offsets
and last-record-wins duplicate-tag behavior, and skips an individually invalid
table record as the managed implementation does. Table lookup and construction
are `O(T)` time with `O(1)` storage for `T` table records.

The same view reads `head`, `hhea`, `hmtx`, and `maxp` metrics without copying
and selects cmap format 4, 12, and 13 subtables using the managed Unicode and
Microsoft-symbol precedence. Format 12/13 lookup is `O(log G)` for `G` groups;
format 4 is `O(S)` for `S` segments. All paths are allocation-free and CPU-only.
Short and long `loca` tables resolve into borrowed `glyf` byte spans in `O(1)`
time and storage per glyph; empty glyphs preserve an empty successful result,
and non-empty records expose their contour count and exact font-unit bounds
without parsing or allocating an outline graph.
The simple decoder and contour lowerer port `src/ProGPU.Text/TtfFont.cs` at
checkpoint `ba6b5588afff85203b64d48c534c4780afb8d75c`. Simple TrueType
records continue through an allocation-free two-pass
decoder. Pass one validates strictly increasing contour endpoints, instruction
ranges, repeated-flag expansion, and the complete X/Y delta byte budget while
reporting exact caller-buffer requirements. Pass two writes raw flags and
signed accumulated coordinates directly into caller spans. Its complexity is
`O(C + P + B)` time and `O(1)` internal storage for `C` contours, `P` points,
and `B` encoded bytes. Empty, simple, and composite glyphs are distinct typed
results. A second allocation-free count/write pair ports
`TtfFont.DecodeContourToFigure` directly into the canonical
`progpu_native_path_segment` ABI. It preserves line closure, explicit
on-curve points, implied midpoints between consecutive off-curve points, and
quadratic controls in `O(C + P)` time with `O(1)` internal storage. Composite
expansion and `gvar` application remain open rather than silently approximated.

The managed and native tests share the repository's exact `Inter-Medium.ttf`
asset as a differential checkpoint. Both assert 2,048 units per em, 2,937
glyphs, scalar U+0053 to glyph 397, advance/side-bearing `1323/106`, and glyph
bounds `(106,-25)-(1217,1510)`. The native test additionally verifies the
same glyph's one contour, 46 decoded points, 59 instruction bytes, mixed
on/off-curve flags, repeated-flag behavior, insufficient caller buffers,
truncated coordinates, excessive repeats, decreasing endpoints, and explicit
composite classification. Matched final path evidence covers all 34
line/quadratic records for Inter Medium glyph 397 with an exact shared 64-bit
hash of `13245664145576799719`, including the
start point `(665,-25)` and closed endpoint. A GPU-rendered native-font
screenshot waits for composite expansion and the font-to-scene connection.
WOFF1 and WOFF2 are rejected explicitly rather than being interpreted as SFNT;
container normalization, compressed ownership, legacy symbol-page tables,
outlines, variations, and color glyph data remain later phase-1/2 work.

The header-compatible library compiles under the normal Clang/MSVC/GCC matrix,
is part of the Emscripten all-target build, and adds a real
`import progpu.native.text;` consumer to the LLVM Clang/Ninja named-module gate.
Focused synthetic tests cover SFNT metrics, BMP and supplementary cmap lookup,
TTC face selection, borrowed identity, invalid face indices, truncated
directories, invalid collection counts, and explicit WOFF rejection.

## Cross-engine research gate

The architecture was checked against primary sources for
[OpenType file organization and table-directory contracts](https://learn.microsoft.com/typography/opentype/spec/otff),
[OpenType character mapping](https://learn.microsoft.com/typography/opentype/spec/cmap),
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

## Delivered managed-picture bridge

`GpuPictureNativeSceneCompiler.Glyphs.cs` lowers already-shaped monochrome
`DrawGlyphRun` records to native outline/segment resources, positioned glyph
instances, and a deduplicated solid text-style page. Compilation is target-DPI
sensitive and explicitly records that dependency. It preserves transforms,
four-way subpixel placement, bold, italic, font stretch/skew, brush opacity,
and grayscale/aliased/ClearType selection. Color layers, embedded bitmap
glyphs, and vector fallback now lower at the same one-time revision boundary:
COLR/OpenType-SVG vector layers and explicit/CFF glyphs reuse retained native
paths/materials, while sbix/CBDT payloads reuse the managed decoder and metric
resolver before transferring tightly packed decoded RGBA8 records into the
native color atlas. Mixed presentation families preserve source order, and
repeated bitmap instances share one decoded resource. Compressed font bytes,
decoder state, path objects, and per-glyph calls never cross the native ABI.
Decorations and text-specific masks remain fail closed until their dedicated
lowering paths land.

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
