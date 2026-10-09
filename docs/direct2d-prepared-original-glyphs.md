# Prepared original glyphs in the native recorder

## Coverage preparation under qualification

`prepared_original_glyph_run::prepare_coverage` is an explicit, private preparation
step for disjoint convex contours. It is not yet selected by `DrawOwnedGlyphRun`.
The original recorder and original Windows pixel comparisons remain unchanged
while this step undergoes provider and source qualification.

The prepared run now owns one segment range per original occurrence, including
zero-length ranges for no-ink glyphs. Coverage preparation retains those ranges
and the original positioned segments. It uses the existing native path
`Simplify(LINES)` implementation in `progpu_native_direct2d_path.cpp`, with a
tolerance derived from the captured physical affine frame. It then quantizes
flattened endpoints relative to each original control hull. Quantizing source
control points before flattening is a different algorithm and is not used.

The measured source cache bands start at 75 and scale by powers of two. Their
selection uses the maximum singular value of the original physical transform;
the endpoint grid is one eighth of that canonical em. This is coverage metadata,
not a changed font em, source advance, offset, outline or brush domain. Independent
DPI axes enter the physical transform once. Translation affects positions only.
Aliased contours become ordinary triangles; grayscale contours carry an inner
fan and half-physical-pixel edge ramps. Paint coordinates remain in target DIPs.
GPU triangle interpolation performs pixel coverage; there is no CPU pixel raster,
readback, new font query, shader-program inspection or foreign implementation.

Preparation publishes only a complete owned batch. Unsupported AA modes,
overlapping contour bounds, nonconvex/multiply wound contours, singular frames,
collapsed inset edges and unrepresentable arithmetic leave the previous output
untouched. The existing general geometry path remains the caller's separate
choice. A zero determinant is exact; there is no epsilon or invented inverse.
The batch is bounded by 1,048,576 vertices. For C contours and V emitted vertices,
the overlap sweep is O(C log C), and mesh assembly is O(V) with amortized growth,
in addition to the existing bounded native subdivision. Storage is O(S+C+V),
where S is the retained positioned segment count. Quantization and position/
direction projection use NEON, SSE2 or Wasm SIMD; ordered contour topology uses
dependent walks. Unsupported-intrinsics builds retain the scalar reference math.

The C++ scene builder's `draw_vertex_meshes` owns the complete existing mesh wire
payload, vertices, indices and optional brush map before publishing one command.
It shares the renderer's wire validator and preserves the original state index.
Header and module consumers use the same implementation. Both native providers
already consume this wire kind through their shared semantic mesh compiler and
canonical `Vector.wgsl`; neither the wire layout nor shader changes here.

The managed `VertexMesh2D`/`DrawVertexMesh` path already owns corresponding data.
No managed original DirectWrite source-font preparation capability exists, so
this private source capture step has no managed entry point to update. Shared
mesh rendering and source-independent text formatting keep their existing paths.
This does not qualify managed source replay or select WPF Display formatting.

Research separates retained layout from coverage: [SkParagraph's public model](https://skia.org/docs/user/modules/quickstart/),
[Parley's retained layout](https://docs.rs/parley/latest/parley/layout/struct.Layout.html)
and [HarfBuzz shape-plan ownership](https://harfbuzz.github.io/shaping-plans-and-caching.html)
support retaining original shaping inputs without re-shaping during raster work.
[Direct2D realizations](https://learn.microsoft.com/en-us/windows/win32/direct2d/geometry-realizations-overview)
and [Win2D geometry](https://microsoft.github.io/Win2D/WinUI3/html/T_Microsoft_Graphics_Canvas_Geometry_CanvasGeometry.htm)
inform reusable geometry, but do not promise DrawGlyphRun pixel equivalence.
[WebRender's retained scene model](https://firefox-source-docs.mozilla.org/gfx/RenderingOverview.html)
supports keeping existing scene ownership and demand-driven upload. [Skia's raster discussion](https://skia.org/docs/dev/design/raster_tragedy/)
and [Vello's explicit AA choices](https://docs.rs/vello/latest/vello/enum.AaConfig.html)
reinforce keeping source coverage, transfer and hinting separate; substituting
area/MSAA results or guessed gamma is rejected. No implementation text from
these projects is used.

The independently authored diagnostic with ProGPU's existing flattener passes
all 1,121 original comparisons while preserving all 1,351 original source
capture hashes. Only 306 comparison frames use its independent GPU triangles;
overlap/layer cases still use SDK geometry. These are diagnostic results, not
qualification of the new preparation API or the actual recorder. Remaining
gates include complete source/provider bytes, general contour topology,
halfway quantization policy, physical-frame replay, brushes/clips/layers,
cross-architecture packages, warm counters and application performance.

The preparation API subsequently supplied 157 comparison frames to the Windows
diagnostic, still with 1,121 passing comparisons and all original hashes
unchanged. Those exact serialized scenes were also replayed cold/warm through
both native providers on physical Apple M3 Pro hardware: 314 frames per provider,
identical provider/cold/warm bytes, and zero warm vertex/index/coverage uploads.
156 of the 157 distinct frames match the Windows capture exactly. The remaining
variable-font frame differs by one red byte at pixel (12,25): native 127 versus
original 128. This is an open qualification failure, with no added tolerance.
The explicit paired-provider regression also checks source-observed 149/75 edge
bytes after all source owners end. It caught and now guards the distinct mesh
source-in selector (5), which must not use the layer-composite enum value (3).

The current local native suite passes all 49 tests; the C++ import consumer and
native contract checks pass. These results cover the additive preparation and
serialization seam. They neither enable recorder selection nor clear the
existing whole-producer Windows reference failures.

## Original font and recorder ownership

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

Horizontal design coordinates use the original glyph's unhinted phantom origin,
`xMin - leftSideBearing`, from its owned `glyf`/`hmtx` records. That subtraction
occurs in design units before em scaling, for both point pairs and quadratic
tails. It neither substitutes nominal advances for caller advances nor changes
ascender offsets. The context retains the origin alongside the decoded outline;
repeated draws make no source font callbacks or metric crossings. Empty glyphs
have no fabricated bounds and still consume their supplied or nominal advance.

Absent advances use the original unsigned 16-bit horizontal `hmtx` width cached
alongside that origin: `width * (em / unitsPerEm)`, in the same float placement
arithmetic as explicit advances. This is an unhinted design metric, not a device
width or a new source array. The retained request keeps its original null pointer;
explicit zero and negative advances continue to take precedence. The no-ink
glyph participates, including the repeated-width compact metric tail. The
original [GetDesignGlyphAdvances](https://learn.microsoft.com/en-us/windows/win32/api/dwrite_1/nf-dwrite_1-idwritefontface1-getdesignglyphadvances)
contract distinguishes these horizontal design widths from sideways advances.

The capability preflights the complete original horizontal metric inventory,
including every bearing in the compact repeated-advance tail, before publishing
a context. A missing bearing must not become zero through the shared raw reader's
legacy permissive behavior; that reader and its other callers are unchanged.
The source is the primary [OpenType hmtx contract](https://learn.microsoft.com/en-us/typography/opentype/spec/hmtx),
which explicitly distinguishes the stored outline bounds from the left bearing.

## Exact current source family

The connected family is one original TrueType file/collection face,
no simulations, caller-supplied rendering mode **OUTLINE**, natural measurement,
horizontal placement and explicit or absent original advances. Null offsets
mean the documented absence of a position adjustment. Original bidi levels
are retained, not normalized to zero; see [horizontal direction](direct2d-horizontal-glyph-direction.md).
[Owned variable coordinates](direct2d-variable-prepared-glyphs.md) use the paired
native contour/origin/advance implementation. Unknown/default and modern raster
modes, sideways/vertical metrics, CFF/type1/multi-file faces and simulations
remain explicit unimplemented contracts.
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
Horizontal direction and future vertical-metric implementation require their own original
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
The original four authored pixel cases cover aliased/grayscale coverage, fractional placement,
affine transforms, capture-time clips and half-opacity layers. Both native
providers compare every cold/warm output byte against independently specified
rectangle geometry; absolute ink, no-ink-advance gap and background pixels remain
separate assertions. Source target/brush owners end before native replay, and
repeated runs assert no additional font callbacks and exactly three cached glyphs.

The paired original Windows controls register a genuine in-memory font loader,
create a real original face from the same complete authored bytes, and capture
that face through the source API. Real `DrawGlyphRun` pixels are compared with
both independent rectangle coordinates and prepared contours using the original
rasterizer. This isolates source decoding/placement from coverage differences;
it is not a replacement for the separate native-provider full-byte gate. The
original loader registration outlives all file/face/context owners. The earlier
54-case modern RGB corpus is unchanged. All these controls are authored only;
no original or native pixel result is claimed at this checkpoint.

Two additional original-font variants retain identical contours but use positive
and negative bearings unequal to `xMin`; one uses the compact `hmtx` tail. Each
runs all four original Windows and paired-provider pixel cases, bringing this
family to twelve without removing the original four. Independent expected
coordinates shift each glyph differently, so decoder self-equivalence cannot
hide ignored bearing metadata. Source controls additionally reject zero/excess
metric counts and one-/two-byte truncated final bearings atomically while
preserving an already-owned context/cache. The authored font builder's default
arguments preserve all earlier fault-font bytes and hint programs.

A separate twelve-case nominal family retains those same three fonts and four
coverage/transform/scope variants without changing the twelve explicit-advance
cases. At em 31.25 and UPM 1000, each original width 500 consumes exactly 15.625
DIPs, including the empty middle glyph. Independently specified rectangle
coordinates and absolute ink/gap pixels do not query the product metrics. Both
native providers retain every cold/warm byte comparison and the no-font-callback
checks. Original Windows queries the actual face's `GetDesignGlyphAdvances`,
asserts the authored design metrics and compares genuine null-advance
`DrawGlyphRun` against explicit original design advances, independent rectangles
and prepared outlines through the original rasterizer. This is authored
acceptance coverage, not an executed Windows result or modern-mode admission.

Additional source controls reject missing/one-byte nominal widths, invalid later
glyph IDs and unrepresentable nominal placement without changing an earlier
request, prepared result or cache. The overflow case contains only uncached
no-ink occurrences, so it exercises pen accumulation rather than overflowing
contour coordinates; it also passes through the actual recorder and requires no
published draw. A positive em whose divided scale underflows is rejected, not
snapped upward. Successful recovery, complete/compact cached widths, null-offset
identity and explicit zero/negative precedence are retained independently.

No validation is executed at this checkpoint: all compile, source, original
Windows, GPU, package and platform gates remain deferred to the final integrated
tip. This is a reusable source ownership/recording connection, not an application
or numerical parity result.
