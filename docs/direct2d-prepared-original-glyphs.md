# Prepared original glyphs in the native recorder

## Coverage preparation under qualification

`prepared_original_glyph_run::prepare_coverage` is an explicit, private preparation
step for disjoint convex contours. The current unpublished recorder experiment
selects it from `DrawOwnedGlyphRun` in root, binary-clip and full-viewport plain
opacity-layer scopes, with full physical viewport containment and a non-bitmap
brush. Every enclosing layer must have complete original viewport bounds, no
mask and no initialization flag. Partial layers, antialiased clips and unsupported
coverage retain the existing geometry path. The original Windows
inputs and captured pixels remain unchanged; recorder qualification is incomplete.
The current recorder also selects the bounded source-path policy described
below for grayscale glyphs that cannot use convex coverage. Its complete local
inventory now matches all 278 original Windows images on both native Metal
providers, with identical cold/warm pixels and zero warm uploads. The earlier
12 general-path failures are resolved. The paired test inventories now separate
glyph coverage from ordinary geometry coverage, as described below. All 49 local
native tests pass, and the original Windows ARM64 WARP suite passes 1,399 glyph
comparisons. The published whole producer Build and packages remain unqualified.

### Independent glyph and geometry comparisons

The original fixtures contain 266 source configurations and 12 additional
same-owner warm CFF captures, for 278 complete Microsoft glyph images. Fresh
unchanged SDK captures confirm that all 266 independent/prepared geometry pairs
are byte-identical, including the actual `GetGlyphRunOutline` controls. In 145
configurations, genuine `DrawGlyphRun` and genuine `FillGeometry` produce different
coverage despite identical positioned contours. Requiring those different APIs
to produce the same image was an invalid coverage-policy assertion.

The tests retain the complete source inventories, original font bytes, literal
independent contours, measured origins/advances and all exact pixel gates. Each
native provider now records three independent source scenes: actual
`DrawOwnedGlyphRun`, literal `FillGeometry`, and `FillGeometry` of the actual
prepared contours. The glyph image must match every byte of its original SDK
receipt. Both geometry images must match each other under the same fill policy.
All three paths run cold and warm with actual provider metrics and zero warm
vertex, index or coverage uploads. Source callback/cache and absolute-pixel
controls remain in place. No generic geometry policy changes to satisfy a glyph
comparison, and no pixel tolerance or exception list is introduced.

The shared `progpu_native_direct2d_original_glyph_reference.hpp` stores all 278
unchanged original images losslessly. It verifies every source blue/green/alpha
byte before RGBA conversion and shares only identical immutable reference data.
Its complete BGRA digest records provenance; it contains no product-rendered
pixels. The original Windows tests recheck all 278 images on each run, retain
the independent/prepared SDK geometry comparisons, and preserve actual
null-versus-design-advance and same-owner warm comparisons. The former 1,121
comparisons plus the 278 explicit receipts now pass 1,399/1,399 on ARM64 WARP.

The complete local native suite passes 49/49 on physical Apple M3 Pro, including
both wgpu-native and Dawn. The rebuilt stock native D3D12 renderer also passes
the entire WebGPU aggregate on Windows ARM64 with Microsoft Basic Render Driver,
including every original glyph image and the independent geometry controls. Its
cold run takes about 890 seconds, within the unchanged 900-second Windows
aggregate deadline; the small margin is not a performance qualification. The
actual wgpu-native DLL matches the pinned Silk.NET 2.23.0 ARM64 payload exactly.
The separate Windows default Parallels adapter run
still rejects an earlier AA Clear byte (`191` versus `192`); the explicit WARP
pass does not qualify that adapter. Exact final Windows providers, other
architectures, whole producer packages and application behavior remain gates.

### Original physical source paths

`prepare_original_path_coverage` consumes the retained, original
`CUBICS_AND_LINES` snapshot and actual target transform/DPI. It owns a new physical
line snapshot; original font bytes, glyph positions, cubic controls and source
geometry stay unchanged. It is separate from convex glyph preparation and from
ordinary geometry draws. The existing root, binary-clip and full-target plain
opacity-layer admission applies. Bitmap paint, masks and partial layers retain
their existing paths.

Independent Windows observations of 64 line controls and 32 curve controls
established the bounded policy: transform the original controls using binary32
source arithmetic, apply the actual DPI axes once, and round physical controls
toward positive infinity on a 1/16 grid. Cubics use dyadic intervals whose width
squared times the largest absolute component of the original second derivative
at both interval endpoints is at most 1.5. Doubling an interval requires alignment
to that dyadic boundary. Evaluated endpoints use `floor(value * 16 + .5) / 16`.
Neither a fixed subdivision count nor the existing `Simplify(.25)` policy matched
the independent inventory. Mathematically equivalent quadratic conversion is not
a substitute for the original SDK's exact cubic control values.

This is an original implementation from observed API behavior and the analytic
Bezier derivative, without consulting foreign implementation code. NEON, SSE2
and Wasm SIMD retain independent coordinate lanes. Interval selection and original
contour validation are ordered dependent walks. A separately written recursive
scalar oracle checks all emitted segment bytes for 768 transformed cases, with
negative controls and independent DPI axes. Exact threshold and rejection controls
preserve prior output, including a disconnected contour whose endpoints would
otherwise quantize to the same location.

Preparation is bounded by 1,048,576 original/emitted segments, finite physical
coordinates within +/-16,384 and a bounded atlas extent. It rejects unsupported
segment kinds, nonclosed original contours, singular transforms and invalid
frames before publication. Work and storage are O(S + V), for S original and V
derived segments. It computes no CPU pixel coverage and adds no source callback,
readback or per-segment GPU submission.

`draw_source_paths` emits command 29 using the unchanged path resource layout.
Its mandatory brush map ends in the same exact 32-byte version 1 physical frame
used by command 28. The source path requires white color, identity transform,
eight samples per axis, closed 1/16-grid line contours, complete ordered segment
ranges and no Boolean program. Header and module consumers share atomic builder
validation; raw wire reads accept unaligned storage and validate every range.
The exact frame is part of retained path identity.

Both native providers retain physical atlas coverage independently of the
target-DIP brush domain. The shared vector shader places the atlas directly in
the actual physical viewport and loads its exact pixel; it does not multiply
presentation DPI again. Replay requires the original per-axis DPI, viewport and
an integral physical state translation. Per-point guidelines and changed frames
remain unsupported. A closed path's complete atlas can cross the target edge;
binary scissoring clips it without changing paint coordinates. Convex source
triangles retain their stricter containment requirement. Both providers compare
solid/gradient paint, state and brush opacity, binary clips, anisotropic DPI,
positive/negative integral placement and failed-frame recovery against separate
ordinary mesh controls.

The actual rebuilt Windows recorder produces 278 scenes whose complete output
matches the unchanged Microsoft captures on both native Metal backends. The
production helper and command also pass all 374 frames including the 96 added
independent path controls. All cold/warm pairs and both provider byte streams
agree, with zero warm vertex/index/coverage uploads. This qualifies that bounded
local inventory only. It does not replace the whole successful producer Build,
Windows GPU execution, package validation, or source application qualification.

The managed Scene API has no original DirectWrite font preparation capability;
it must not infer this policy from ordinary geometry. Raw native replay owns the
new command and validation. Both native providers and the shared managed/native
shader source use the same coverage implementation; generic path behavior and
managed source selection are unchanged.

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
fan and half-physical-pixel edge ramps. Each edge strip uses the diagonal selected
by the original contour winding, before physical transformation. The two windings
then retain the same geometric triangles; an arbitrary diagonal changes coverage
because the four coverage vertices need not form one plane. Paint coordinates
remain in target DIPs.
An explicit GPU fragment plane performs pixel coverage from the original physical
triangle and binary endpoint coverages. There is no CPU pixel raster, readback,
new font query, shader-program inspection or foreign implementation.

Preparation publishes only a complete owned batch. Unsupported AA modes,
grayscale contours without separated physical bounds, aliased contours without
separation in either original-control or physical bounds, nonconvex/multiply
wound contours, singular frames, collapsed inset edges and unrepresentable arithmetic leave the previous output
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
consume this wire kind through their shared semantic mesh compiler. Ordinary
mesh interpolation, resource layout and vertex layout remain unchanged.

`draw_source_coverage` adds command 28 with that same mesh resource and a mandatory
brush map followed by a 32-byte version 1 frame. The frame owns both exact DPI
axes and the complete original physical viewport. Mesh positions retain original
physical pixels; paint coordinates retain the original target DIPs. Validation
requires triangles, binary coverage, white vertex RGB, the existing mesh source-in
selector and an identity mesh transform. Whole validation precedes publication,
including capacities, frame flags, brush indices and alignment-safe raw reads.
The frame participates in compiled analytic identity. Generic meshes are never
classified as source coverage from their colors or dimensions.

Both native providers pack each original triangle's three physical corners and
coverage bits into their shared 56-byte vector layout. Point translation uses
NEON/SSE2/Wasm SIMD with the bounded scalar reference on other architectures.
The canonical `Vector.wgsl` source-coverage branch receives flat metadata and
evaluates the plane at the actual fragment position after nearest-even snapping
to 1/256 physical pixels. Native pass uniforms carry the actual viewport extent;
independent target-DIP brush coordinates and state opacity remain separate.
This follows the public [D3D11.3 coordinate snapping and fixed-point rules,
sections 3.2.4 and 3.4.1](https://microsoft.github.io/DirectX-Specs/d3d/archive/D3D11_3_FunctionalSpec.htm).
[WGSL interpolation qualifiers](https://www.w3.org/TR/WGSL/#interpolation)
do not specify that source lattice: changing only perspective to linear did not
fix the measured difference. Generic vertex-color interpolation is unchanged.

Replay requires the captured viewport/DPI and exact integral physical state
translation. Every original triangle must fit the actual target before drawing;
changed DPI, fractional placement, per-point guidelines and target-created
clipping decline before GPU submission. Original binary scissors still apply.
Offscreen clipping and changed-frame replay need their own source qualification.
There is no identity inverse, tolerance, added wait or per-triangle submission.

The managed `VertexMesh2D`/`DrawVertexMesh` path already owns corresponding data.
No managed original DirectWrite source-font preparation capability exists, so
this private capture has no corresponding managed recorder to select it. Managed
interop declares the exact command value and generated frame layout for raw
native transport; managed `Scene` does not infer or emit this policy. The canonical
shader is shared by both native providers and managed rendering, while managed
ordinary meshes retain their existing interpretation and uniforms.
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
general physical-frame replay, source brush/layer integration,
cross-architecture packages and application performance.

The preparation API subsequently supplied 157 comparison frames to the Windows
diagnostic, still with 1,121 passing comparisons and all original hashes
unchanged. Those exact serialized scenes were also replayed cold/warm through
both native providers on physical Apple M3 Pro hardware: 314 frames per provider,
identical provider/cold/warm bytes, and zero warm vertex/index/coverage uploads.
The original generic interpolation matched 156/157 distinct frames: one variable
frame produced red 127 instead of 128 at (12,25). Explicit retained source coverage
now matches **157/157 complete original frames** on both native Metal providers,
including that pixel, with identical cold/warm bytes and zero warm vertex/index/
coverage uploads. Original vertices, clips and Windows capture bytes are unchanged;
no comparison tolerance was added. The combined original capture SHA-256 is
`d03c5985f95680e97f89256d64ddfdaad73d5aca3f6e113207eb03892f549e9d`.
Both providers' cold/warm aggregate SHA-256 is
`531c0b1e70817dd668f5d9e03d6c9d60aeb844b27d07fc5efa2ddf8a82140eca`.
The explicit paired-provider regression also checks source-observed 149/75 edge
bytes after all source owners end. It caught and now guards the distinct mesh
source-in selector (5), which must not use the layer-composite enum value (3).

Before recorder selection, the local native suite passed all 49 tests; the C++ import consumer and
native contract checks pass. Both native providers additionally exercise exact
anisotropic DPI, independent gradient paint coordinates, brush/state opacity,
binary clips and integral placement against an ordinary-mesh control. Changed
DPI, viewport, fractional translation and incomplete target containment reject
before submission/upload; a later original frame still succeeds. Raw tests check
unaligned caller reads, sixteen invalid wire/builder inputs, immutable frame
ownership, SIMD packing and frame-sensitive retained identity. The managed shader
and interop selection passes 166 tests on recheck (an initial allocation-count test
failed with 5,288 bytes versus 0). These results cover the additive preparation and
serialization seam. They do not clear the existing whole-producer Windows
reference failures.

The actual recorder experiment exports 278 scenes from the original Windows
requests, including original font bytes, glyph arrays, brush properties,
transforms, clips and layers. Both native Metal providers exactly match all 225
complete original captures that select source coverage, cold and warm. This
includes all 159 previously admitted frames and 66 newly admitted layer frames.
The 53 geometry fallbacks retain 12 mismatching frames (399 differing bytes per
provider, maximum difference 24); every cold/warm pair is identical. Both
providers produce identical bytes throughout the complete inventory.
An initial diagnostic omitted clips in five families; its results are retained
as invalid evidence, and the corrected run preserves all 1,351 original capture
hashes. No source input, expected byte or comparison tolerance changed.

A separate diagnostic identified a general-path filtering difference: rasterizing
in local coordinates and subsequently shearing the coverage texture alters the
original physical edge. The recorder now projects its private filled-path
snapshot through the actual draw transform before coverage rasterization, using
target DIPs and an identity path placement. Lines and cubic controls retain their
original topology; this does not introduce curve flattening or pixel sampling on
the CPU. NEON/SSE2/Wasm transform independent point pairs in O(S) time and O(1)
additional space, with the explicit scalar reference on unsupported targets.
Source path objects are untouched. Original draw/brush transforms still determine
gradient coordinates and opacity masks; bitmap-brush masks retain their separate
representation. Exactly singular transforms preserve the existing rejection path.
Independent DPI presentation and later picture transforms remain separate from
this capture-time target mapping.

This fixes all six remaining static-font frames, preserving every previously
matching frame and all 225 source-coverage scene bytes. Complete original Microsoft
references for those six frames fail before the projection change and pass on
both providers afterward, cold and warm with source owners retired. Separate
literal world-geometry/brush controls preserve spatial gradients, brush/layer
opacity, binary clips and independent DPI axes. All 278 frames retain zero warm
vertex/index/coverage uploads. The remaining failures are eight variable-font
overlap frames and four curved CFF frames; no sampling-phase workaround is used.
The actual recorder currently passes 47 of 49 local native tests: both
provider suites still reject the original full-byte glyph/independent-geometry
comparison. Those assertions remain intact. Added compatibility controls cover
root/binary-clip/full-layer selection, partial-layer/AA-clip/incomplete-viewport fallback, immutable
brush/frame snapshots, warm source-cache reuse and reentrant DPI invalidation.
They pass on macOS ARM64 and Windows ARM64; Windows internal controls also pass.

The aliased contour sweep accepts separation in either the original control-hull
basis or the physical basis, after exact singular rejection. Grayscale requires
separated physical bounds: two sheared source-disjoint controls whose physical
bounds overlap use the original whole-path policy instead. This retains every
previously matching frame and admits two additional sheared aliased cases.
It preserves independent contour draws when their antialiasing fringes overlap:
rejecting emitted-fringe overlap incorrectly lost seven original matching frames,
and that experiment was removed. Original overlapping outlines still decline.
The 225 selected frames retain zero warm vertex/index/coverage uploads on both
providers. The two original native glyph/geometry assertions still fail; they
have not been replaced or relaxed.

Independent Windows layer controls retain each original request and capture it
at 25%, 50%, 75% and 100% layer opacity, plus 100% without a layer. All 80 original
half-opacity frames are byte-identical to the added 50% controls. All 80 opaque
layer captures are byte-identical to the corresponding no-layer captures.
Quarter- and half-opacity colors exactly follow byte rounding of the original
opaque captures. The three-quarter controls differ by one byte at some rounding
ties and do not establish a general composition policy. The winding-selected
edge-strip diagonal matches all 66 opaque layer controls on the independent GPU
diagnostic and removes its remaining failures: 1,121 comparisons pass, with 434
comparison frames using independent GPU triangles. Other comparisons still use
SDK geometry. Reversed-contour controls preserve the same result and all 1,351
original capture hashes. This is diagnostic evidence, not a reduction of the
unchanged published 1,121/128 source reference gate.

The paired native-provider regression retains the complete original Microsoft
opaque and half-opacity captures as lossless run-length encoded reference data.
It compares every byte for both original contour windings, cold and warm, after
the font/preparation/builder owners end. The new regression failed before the
diagonal correction and now passes in both providers. Plain full-frame layers
reuse the existing linear-byte-opacity compositor without changing shader
rounding. Recorder controls also cover nested full-frame layers and reject
partial layers and opacity-brush masks. The later paired coverage-policy controls
above resolve the two glyph/geometry assertions while retaining independent
geometry and original glyph images; whole-product qualification remains open.

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
