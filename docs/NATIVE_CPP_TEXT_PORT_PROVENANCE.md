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
quadratic controls in `O(C + P)` time with `O(1)` internal storage. `gvar`
application remains open rather than silently approximated.

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
start point `(665,-25)` and closed endpoint.
Composite record parsing ports the descriptor loop in
`TtfFont.ParseCompositeGlyphOutline` at checkpoint
`3abbec85d749466130538c8371dc772f1ef08671`. A first pass validates every
component, signed/unsigned byte/word arguments, F2Dot14 uniform, axis, or 2x2
transforms, continuation flags, and optional instruction range while reporting
the exact component count. A second pass writes fixed caller-owned component
records. Both passes are `O(K + B)` time and `O(1)` internal storage for `K`
components and `B` component/instruction bytes.

Recursive expansion ports `TtfFont.BuildCompositeGlyph` at checkpoint
`d477532a4bec274bdb47634dacab91d809c80fa2` and uses a fixed 33-glyph ancestor
stack matching ProGPU's
depth limit plus caller-owned simple-point/contour scratch and final point/path
spans. Its preflight reports exact scratch and output counts; the write pass
applies byte/word XY offsets, scaled offsets, deterministic midpoint-to-even
grid rounding, uniform/axis/2x2 transforms, and parent/component point
attachment without retaining any temporary graph. Ordinary work is linear in
visited glyph records, component records, decoded points, and path segments;
nested point attachments may require bounded child preflight, making the
worst case `O(D * (G + K + P + S))` for depth `D <= 33`, while internal storage
remains `O(1)`. Cycles and excessive depth are bounded, invalid point
attachments skip that component as in the managed implementation, and
insufficient output fails before writing. The Inter Medium U+00E9 checkpoint
resolves to composite glyph 618, exactly reproduces its two component records
(glyphs 614 and 1770), and expands to 35 points and 27 path records. Managed
and native streams share start `(630,-23)` and exact hash
`5543379682355176128` across all three contours.
Descriptor parsing and recursive outline assembly remain separate translation
units sharing only a private fixed-record reader, keeping the native text port
granular without duplicating component semantics.

The native hardware sample now connects those exact canonical records directly
to the retained glyph resource and the production glyph-atlas execution path.
When passed the repository's Inter Medium face, it decodes U+00E9 through the
standalone C++ font library, builds no intermediate geometry graph, and submits
the resulting 27-record composite outline through the shared
`GlyphRasterizer.wgsl` and `Text.wgsl` modules. The Apple M3 Pro/Metal
checkpoint renders glyph 618 at 48 logical pixels from a 64-physical-pixel
atlas raster, verifies a bounded yellow-coverage region during readback, and
retains the complete sample's eight GPU draws and 11,616 uploaded vertex bytes.
The fallback sample glyph remains available when no font path is supplied, so
packaged consumers do not acquire a source-tree asset dependency. Repository
Clang and Windows build scripts pass the font explicitly, making the real
font-to-GPU connection an integration gate on runnable targets.

Variable-font metadata starts at checkpoint
`3d1bae94c64259afbd64ed2bc630fb3430d2bc79` with a separate
`progpu_native_font_variations.cpp` translation unit that ports
`OpenTypeVariationData.ParseAxes`. The borrowed two-pass API validates the
`fvar` header, axis offset/count/stride, and complete fixed-size record range
before writing. It preserves tags, signed 16.16 minimum/default/maximum values,
flags, and name IDs without resolving provider-owned strings or allocating.
Counting is `O(1)` and decoding is `O(A)` time with `O(1)` internal storage for
`A` axes; an undersized caller span writes nothing. Synthetic hidden-axis,
truncation, and transactional-buffer cases pass under normal, LLVM named-module,
and ASan/UBSan builds. The real InterVariable checkpoint matches the managed
implementation's `opsz` 14/14/32 and `wght` 100/400/900 axes exactly. `avar`
normalization follows at checkpoint
`05ca2df1faee220bd7783611a3cf13ad72189130`: one signed 16.16 user
coordinate is clamped and normalized with the managed away-from-zero F2Dot14
rule, then passed through the selected piecewise `avar` map with managed
midpoint-to-even interpolation. The allocation-free scan validates every map
range in `O(A + M)` time and `O(1)` storage for `A` axes and `M` map pairs;
an absent/out-of-range optional table retains the base normalized coordinate,
and an axis-count mismatch is ignored exactly like the managed implementation.
Synthetic endpoints, clamping, interpolation, invalid-axis, and truncated-map
cases plus the real InterVariable checkpoints match `opsz=23 -> 8192`,
`wght=500 -> 2949`, and `wght=700 -> 8847`.

The first `gvar` slice follows at checkpoint
`9e0c63be839ad651c61f477da1f85111204eaa21`. Granular
`progpu_native_gvar.cpp` and `progpu_native_gvar_packed.cpp` translation units
port the ProGPU-owned table layout, glyph-offset, shared-tuple, packed-point,
and packed-delta contracts without retaining or allocating table data. Every
packed stream uses a validating count pass before its transactional caller-span
write pass, both `O(N)` time and `O(1)` internal storage for `N` encoded values.
Normal Clang, named C++20 module, and ASan/UBSan builds cover malformed runs,
undersized outputs, duplicate points, signed byte/word deltas, and all-point
encoding. The real InterVariable checkpoint matches 2 axes, 5 shared tuples,
2,937 glyphs, long offsets, exact shared tuple coordinates, and exact 594/60
byte glyph payloads for glyphs 397/618. Tuple regions, scalar evaluation, and
outline application were sequenced as the following granular slices.

Tuple regions and scalar evaluation follow at checkpoint
`1c31a45d271543a919b1397ba2a18f83f3f24cc1` in the granular
`progpu_native_gvar_tuples.cpp` unit. A validating count pass resolves shared
or embedded peaks, explicit or implicit start/end regions, private-point flags,
and total tuple payload bounds before the write pass touches caller-owned
header and F2Dot14 coordinate spans. Region scalar evaluation preserves the
managed invalid/zero-peak behavior and piecewise ramps in `O(A)` time and
`O(1)` storage for `A` axes. Synthetic embedded/intermediate/private-point
coverage plus real Inter shared tuples pass normal, named-module, and
ASan/UBSan gates. Packed tuple payload application and untouched-point
interpolation follow in the next checkpoints.

Allocation-free IUP interpolation follows at checkpoint
`83cc056eb2311a6ba23bdd2b003de91c15576002` in
`progpu_native_gvar_interpolate.cpp`. It validates complete contour ownership
before mutation, then scans each circular contour in `O(P)` time and `O(1)`
internal storage, preserving the managed one-touch propagation, two-touch
piecewise interpolation, equal-coordinate min/max rule, and wraparound pairs.

Simple-glyph tuple application follows at checkpoint
`ca9e9d22250b09035906c42814fafa6ef35ab898` in the granular
`progpu_native_gvar_apply.cpp` unit. It preflights all shared/private point and
X/Y delta streams before writing output, uses exact caller-owned scratch spans,
evaluates each tuple region, applies all-point or sparse deltas, and invokes the
IUP pass for untouched points. Work is `O(T * (A + P + D))` with bounded
caller storage and `O(1)` internal storage for tuples `T`, axes `A`, points
`P`, and packed deltas `D`. Fractional varied points lower directly to the
canonical line/quadratic path ABI without rounding. The managed and native
InterVariable `opsz=23` glyph-397 checkpoint matches start `(648.5,-25)`, 39
segments, and exact full-stream hash `12343280691057163238` under normal,
named-module, ASan/UBSan, and focused managed gates.

Composite-component tuple application follows at checkpoint
`6ca97ba7fc9def783fc511c8918e1743de701414`. The shared/private point and
packed-delta walker is isolated in `progpu_native_gvar_payload.cpp` and reused
by both simple and composite glyphs. `progpu_native_gvar_composite.cpp`
preflights every tuple, evaluates the same normalized regions, and writes
caller-owned component offsets transactionally with no internal allocation.
The native InterVariable `opsz=23` glyph-618 checkpoint exactly resolves its
two component offsets to `(0,0)` and `(15,0)`; the authoritative managed
outline independently matches start `(595,-24)`, 2 figures, 36 segments, and
hash `12064242707506207632`.

Recursive varied-composite expansion follows at checkpoint
`7c045a805a3f4d7028d2f503660c402ff3ead09d`. The dedicated
`progpu_native_true_type_varied_requirements.cpp` pass measures exact maximum
simple-tuple, composite-tuple, varied-point, and active recursion-path offset
storage. `progpu_native_true_type_varied_outline.cpp` then varies every simple
child, applies parent component deltas before scaled-offset transformation,
preserves point attachment and midpoint-to-even grid rounding, and emits the
canonical path ABI directly. Both passes use a bounded 33-glyph ancestor stack
and caller-owned spans with no internal heap allocation. Native InterVariable
glyph 618 now matches the full managed `opsz=23` checkpoint: start
`(595,-24)`, 36 segments, and exact hash `12064242707506207632`. Normal,
named C++20 module, ASan/UBSan, short-scratch transactionality, and focused
managed differential gates pass.

Phantom-point advance fallback follows at checkpoint
`868906b56aba0e79d918555de2482e351b57e125` in the granular
`progpu_native_gvar_phantom.cpp` unit. It reuses the shared tuple payload
walker, accumulates left/right phantom X deltas for all-point or sparse tuples,
and publishes their difference only after complete validation. Work is
`O(T * (A + D))` with caller-owned `O(T * A + I)` scratch for tuples `T`, axes
`A`, packed deltas `D`, and glyph item count `I`. A synthetic half-scaled tuple
produces exact left/right deltas `2/5` and advance delta `3`; short scratch and
item-count-under-four behavior pass normal, named-module, and ASan/UBSan
gates. This raw `gvar` fallback remains deliberately separate from the HVAR
precedence and item-variation-store slice.

Borrowed item-variation stores and HVAR advance precedence follow at checkpoint
`920df3b0a2971232f84720ab384a3c2a737dd15c`. The granular
`progpu_native_item_variation_store.cpp` unit validates format-1 region lists,
subtable offsets, word/long-word delta rows, region indices, and format-0/1
delta-set maps before exposing a borrowed view. Lookup scans only the selected
row and its referenced regions in `O(R * A)` time with `O(1)` storage for `R`
regions and `A` axes. `progpu_native_hvar.cpp` applies the optional advance map
and reports whether HVAR owns advance variation so callers use phantom points
only when the managed implementation would. InterVariable glyph 397 at
`opsz=23` resolves exact native delta `-28`, matching the managed 2048-em
advance `1314 -> 1286`. Synthetic map clamping, row selection, missing-HVAR,
truncation, normal, named-module, ASan/UBSan, and 29/29 managed Inter gates
pass.

MVAR and GDEF layout-store consumers follow at checkpoint
`aed98a99996447a5b2048d5b1f7df6c14c9d1eb4`. The granular MVAR unit validates
record stride/count/store bounds, preserves the managed last-duplicate-tag
rule, and evaluates each metric record through its own borrowed store. The
GDEF 1.3 unit resolves layout variation indices through the same parser and
returns an explicit absent-store state for earlier table versions. Native
InterVariable `opsz=23` resolves exact `xhgt=-31`, matching managed X-height
`1118 -> 1087`, and successfully evaluates its real GDEF layout store. Normal,
named-module, ASan/UBSan, and 29/29 managed Inter gates pass. Wiring these
layout deltas into the later native GPOS port remains sequenced with shaping.

CFF1 container and Type 2 outline parity follows at checkpoint
`fdb47fb7973c844cb3db27b8ab968318d22b4a7b`, porting the ProGPU-owned
`Cff1OutlineSource.cs` implementation. CFF INDEX offsets, top/private
dictionaries, FDSelect formats 0/3/4, global/local subroutines, and selected
charstrings remain borrowed table views. Format-0 FD lookup is `O(1)` and
range formats are `O(log R)` without the managed range arrays. The complete
Type 2 operator set uses fixed 513-value operand and 32-value transient stacks,
bounds subroutine depth at 10, and emits line/cubic records directly into the
shared renderer ABI. A count pass makes caller-buffer writes transactional;
execution is `O(B + S)` time and `O(1)` internal heap storage for executed
bytes `B` and emitted segments `S`. Managed and native Noto CJK checkpoints
match exactly: `A` is glyph 34 with 14 canonical segments and hash
`1714381338565491643`; `日` is glyph 20220 with 16 segments and hash
`5620540281806238275`. Normal Clang, LLVM named modules, ASan/UBSan, 20/20
managed Noto tests, and Emscripten/Emdawnwebgpu/Chromium pass.
The DICT real-number reader uses a bounded locale-independent decimal/exponent
parser rather than optional floating-point `std::from_chars`, preserving C++20
compatibility with Xcode 16.4 libc++ as well as current Clang, GCC, and MSVC.

Borrowed color-font data begins with `sbix` at checkpoint
`ad2d2c43b62d5814066b2b7316241d4204aba7dd`. Strike selection preserves the
managed closest-ppem rule and higher-ppem tie break; PNG/JPEG/TIFF payloads
remain borrowed, and `dupe` chains preserve the referencing glyph's origin
while bounding depth at 16. Lookup is `O(S + D)` time and `O(1)` storage for
strikes `S` and duplicate depth `D`. OpenType SVG table/range lookup follows at
`ca025130da3201972a9d11c07bcbf91f6d925e76`: a validated glyph record returns
its borrowed encoded document, covered glyph interval, and explicit gzip flag,
with a 16 MiB encoded-document bound. Gzip decoding and XML-to-color-layer
conversion remain separate bounded slices.

CBLC/CBDT lookup ports the ProGPU-owned `TtfFont.cs` implementation at source
checkpoint `873593a7c56ced46c2480ea72de017cd7f4e5cc3`. It follows the
[OpenType 1.9.1 CBLC location contract](https://learn.microsoft.com/en-us/typography/opentype/spec/cblc),
the inherited [EBLC index-subtable formats](https://learn.microsoft.com/en-us/typography/opentype/spec/eblc),
and the [CBDT formats 17-19 contract](https://learn.microsoft.com/en-us/typography/opentype/spec/cbdt).
All five dense/sparse index formats are implemented in a dedicated resolver;
small, big, and index-owned metrics resolve in a separate CBDT reader. Strike
selection preserves closest ppem and the managed higher-ppem tie break, and
the result borrows the exact PNG payload with horizontal bearings rather than
allocating or decoding it. Work is `O(S + R + N)` and `O(1)` storage for
strikes `S`, subtable records `R`, and sparse records `N`. Apple Clang, LLVM
named-module, ASan/UBSan, Emscripten/Emdawnwebgpu/Chromium, and six focused
managed CBLC/CBDT tests pass.

Layered COLR/CPAL lookup ports the ProGPU-owned `TtfFont.cs` implementation at
source checkpoint `30e9ebe57ef7e4a7e575dd57c9cf6540687b87ae`. It follows the
[OpenType 1.9.1 COLR contract](https://learn.microsoft.com/en-us/typography/opentype/spec/colr)
and the corresponding
[CPAL palette contract](https://learn.microsoft.com/en-us/typography/opentype/spec/cpal).
The current slice decodes the version-0 base/layer record model, including the
compatible prefix in a version-1 table; full COLR version-1 paint graphs remain
future work. Base-glyph lookup is `O(log B)`, layer decoding is `O(L)`, palette
resolution is `O(1)` per layer, and all table storage stays borrowed. The caller
supplies the exact output span, malformed tables fail before any output is
written, invalid palette selections preserve the managed palette-0 fallback,
and palette entry `0xFFFF` is retained as an explicit foreground-color flag.
Normal Apple Clang, LLVM named modules, ASan/UBSan, Emscripten compilation plus
the shared Emdawnwebgpu/Chromium runtime contract, and five focused managed
color-glyph renderer/compiler tests pass.

The dependency-free compressed-glyph foundation starts with a standalone
zlib/DEFLATE C++20 module. ProGPU's managed implementation delegates this seam
to `System.IO.Compression`, so the native implementation is original work from
[RFC 1950](https://www.rfc-editor.org/info/rfc1950/) and
[RFC 1951](https://www.rfc-editor.org/info/rfc1951/), with the eventual PNG
consumer constrained by the [W3C PNG specification](https://www.w3.org/TR/png-3/).
Stored, fixed-Huffman, and dynamic-Huffman blocks decode into a caller-owned
output span, including overlapping history copies up to 32 KiB. Header,
dictionary, canonical-tree, length/distance, trailing-byte, capacity, and
Adler-32 failures are explicit. Decoding is `O(I + 15S + O)` worst-case time
for input bytes `I`, Huffman symbols `S`, and output bytes `O`; scratch remains
fixed `O(1)` stack storage with no heap allocation or native dependency. Normal
Apple Clang, LLVM named-module, ASan/UBSan, and Emscripten compilation plus the
shared Emdawnwebgpu/Chromium runtime contract cover all three block types,
history overlap, malformed headers, trailing data, short output, and checksum
failure. PNG chunk/filter conversion and SVG gzip framing remain separate
bounded consumers.

The compression module also implements the
[RFC 1952](https://www.rfc-editor.org/info/rfc1952/) single-member gzip framing
required by OpenType SVG glyph documents. It validates optional extra, name,
comment, and header-CRC fields, then checks payload CRC-32 and `ISIZE` around
the same caller-buffer DEFLATE engine. `sfnt_svg_glyph_document_view` stays
borrowed; its typed decoder either copies plain XML or inflates gzip into the
caller span without a heap allocation or platform codec dependency. Framing is
`O(I + O)` time and `O(1)` internal storage, with short output, malformed
headers, DEFLATE errors, and checksum failures reported explicitly.

The next bounded slice ports ProGPU's managed PNG ownership and pixel contract
into the standalone `progpu_native_image` C++20 library while deriving the wire
format exclusively from [W3C PNG Third Edition](https://www.w3.org/TR/png-3/).
It validates signature, critical-chunk order, CRC-32, palette/transparency
metadata, consecutive `IDAT` payloads, and the exact zlib result before writing
straight RGBA8. The completed profile covers every legal grayscale, RGB,
indexed, grayscale-alpha, and RGBA depth, including packed 1/2/4-bit samples,
16-bit full-range normalization, sequential scanlines, Adam7, and filters 0–4.
Parsing and decode are `O(C + B + W*H)` time with caller-owned compressed,
filtered, and RGBA spans and `O(1)` internal state. Adam7 uses a fixed seven-pass
layout and scatters directly into the destination without a temporary frame.
Unknown critical chunks, malformed palette indices, illegal depth/interlace,
CRC/Adler failures, and short
buffers fail transactionally. Normal Apple Clang warnings-as-errors, LLVM named
modules, ASan/UBSan, and a real Emscripten/Emdawnwebgpu/Chromium execution gate
exercise this same library.

WOFF1 and WOFF2 are rejected explicitly rather than being interpreted as SFNT;
container normalization, compressed ownership, legacy symbol-page tables,
COLR version-1 paint graphs, SVG vector lowering, and CFF2 remain later
phase-1/2 work.

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
   compiler and existing compute glyph/text pipelines. The first direct
   decoded-outline-to-GPU connection is complete; native shaping and run
   assembly remain open.
7. Gate cold start, first interaction, sustained layout/shaping throughput,
   allocations, cache residency, malformed input, DPI/subpixel quality,
   browser AOT, and matched C#/C++ screenshots before claiming parity.
