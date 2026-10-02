# Prepared original glyphs in the native recorder

The private `prepared_glyph_target` capability connects an owned original font
request to the actual portable Direct2D recorder. It is an explicit same-build
C++ source path, not a changed `ID2D1RenderTarget` vtable, installed COM API, C ABI
for STL owners, or automatic replacement for ordinary `DrawGlyphRun`.

Prepare a font once from [owned original source files](direct2d-original-font-capture.md).
The prepared context borrows the exact immutable bytes/index under that owner,
parses the shared `sfnt_font_view` once and lazily caches original design contours
by glyph ID. It uses the existing ProGPU `try_get_expanded_glyph_requirements` and
`try_decode_glyph_outline`; these are the same native TrueType decoder used by
MIL, not a source font-object callback or another parser. Cache identity includes
the owning original font capture. Distinct captures are not merged by address,
name, glyph count or a digest alone. Cache creation does not initialize a GPU,
execute hint bytecode, shape text or look up characters.

The recorder's `DrawOwnedGlyphRun` captures the supplied arrays and its actual
transform, independent DPI, target extent/format, tags, text AA and rendering
parameters under the existing glyph-generation lease. The prepared-specific
guard keeps the original parameter object alive while AddRef/getters run outside
the recorder lock. Reentrant target mutation latches its original failure;
parameter replacement during this prepared operation likewise invalidates it.
Ordinary `DrawGlyphRun` and its setter behavior remain unchanged.

The context produces one owned run path from the supplied original IDs,
advances, offsets and baseline. No-ink glyphs still advance the pen. Retained
source data and exact target identity accompany the result. The actual native
recorder consumes that path through the existing geometry/brush compiler with
the original transform and clip/layer scopes still held by the same lease.
There is no second source layout, late font callback, dropped scope, target-DPI
reset, CPU rasterization or destination readback. Repeated requests reuse decoded
design contours; each changed run still gets its own positioned path and source
identity. Native renderer replay uses the ordinary retained path pipeline in both
providers, not the internal historical RGB coverage model.

## Exact current source family

The connected family is one original non-variable TrueType file/collection face,
no simulations, caller-supplied rendering mode **OUTLINE**, natural measurement,
horizontal left-to-right placement and explicit original advances. Null offsets
mean the documented absence of a position adjustment. Original even bidi levels
are retained, not normalized to zero. Unknown/default and modern raster modes,
RTL, sideways/vertical metrics, absent advances, CFF/type1/multi-file faces,
simulations and variable coordinates remain explicit unimplemented contracts.
These are real remaining tasks, not a claim of full DirectWrite or ClearType
support. In particular no TT35/40 interpreter is selected to impersonate a modern
DirectWrite mode, and OUTLINE is not relabeled as RGB coverage.

The primary source contracts are
[GetGlyphRunOutline](https://learn.microsoft.com/en-us/windows/win32/api/dwrite/nf-dwrite-idwritefontface-getglyphrunoutline),
[DWRITE_GLYPH_RUN](https://learn.microsoft.com/en-us/windows/win32/api/dwrite/ns-dwrite-dwrite_glyph_run)
and [DWRITE_GLYPH_OFFSET](https://learn.microsoft.com/en-us/windows/win32/api/dwrite/ns-dwrite-dwrite_glyph_offset).
They separate DIP em/advances, logical glyph order, advance-direction offsets,
ascender-direction offsets and sideways vertical metrics. This implementation
uses original ProGPU decoder/geometry code and those public contracts only.
RTL origin/vertical-metric implementation still requires its own original
placement controls; no cursor-sign heuristic is accepted as coverage.

## Ownership, bounds and authored controls

For U uncached glyphs, decoded contour work follows the shared decoder's bounded
simple/composite algorithm; retained source storage is O(font bytes + cached
segments). One cache and one prepared run each admit at most 1,048,576 segments,
with the same bound for point scratch. At most the original 16-bit glyph domain
can enter the lazy cache. A request is O(G + S) for G occurrences and S positioned
segments, with O(G + S) temporary/owned storage. Independent point pairs use
NEON/SSE2/Wasm SIMD where available, with a fixed third-point scalar tail for a
quadratic; scalar point conversion remains the portable unsupported-intrinsics
path. This is source geometry work, not CPU pixel coverage. Original float
placement arithmetic is retained without fitting, phase quantization or rounding.
New glyphs and the output publish only after full run preparation; later failures
preserve earlier cached glyphs and the caller's previous output.

Authored source controls use original repository-built TrueType bytes with known
rectangle contours, repeated/no-ink glyphs and deliberately invalid hint bytecode
that OUTLINE must never execute. They exercise the actual target under captured
clip/layer/transform/DPI state, confirm zero additional source-font reads or
outline callbacks, inspect exact independently calculated source coordinates,
and retain explicit unsupported-family and reentrant invalidation controls.
Independent original Windows and paired native-provider full-byte rendering
controls are required alongside these source cases before qualification.

No validation is executed at this checkpoint: all compile, source, original
Windows, GPU, package and platform gates remain deferred to the final integrated
tip. This is a reusable source ownership/recording connection, not an application
or numerical parity result.
