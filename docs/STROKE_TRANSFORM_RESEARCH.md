# Stroke-transform provenance and rendering research

## Scope and clean-room boundary

This record defines how a retained two-dimensional pen width composes with a
command transform, picture or visual replay transform, a late-bound static or
GPU transform, and the final render transform. It covers solid and dashed
rectangles, ellipses, rounded rectangles, paths, lines, quadratic and cubic
curves, polylines, splines, opacity masks, hit-test geometry, and Skia's
explicit one-device-pixel hairline. Text shaping, font selection, and
three-dimensional line width are outside this change.

The follow-up DXF correction also covers an explicit positive fixed-device
width. DXF linework opts into that mode because its cached 1.0/1.2/1.5 widths
are cosmetic screen widths: zoom transforms the retained CAD centerline but
must not magnify those widths. Ordinary positive-width pens remain in normal
source-space mode.

The implementation is clean-room. The primary sources below informed only
observable contracts, coordinate-space separation, retained-scene
architecture, and validation cases. No foreign implementation text, helper
structure, shader, data layout, naming, or control flow was copied, translated,
or adapted.

## Primary-source comparison

| Engine or specification | Public or architectural contract examined | ProGPU decision |
| --- | --- | --- |
| [Skia `SkCanvas`](https://api.skia.org/classSkCanvas.html) and [`SkPaint`](https://api.skia.org/classSkPaint.html) | Canvas draw calls apply the current matrix to geometry and use paint for stroke width. A zero-width Skia stroke is an explicit one-device-pixel hairline whose width does not scale. | Retain ordinary width in the command's source coordinate space and compose it with the complete transform exactly once. Keep hairline behavior separate; do not infer a non-scaling stroke from an ordinary positive width. |
| [Direct2D transforms](https://learn.microsoft.com/en-us/windows/win32/direct2d/direct2d-transforms-overview) and [`D2D1_STROKE_TRANSFORM_TYPE`](https://learn.microsoft.com/en-us/windows/win32/api/d2d1_1/ne-d2d1_1-d2d1_stroke_transform_type) | A render-target world transform generally affects both fill and stroke. Geometry transforms happen before stroking, while fixed and hairline transform types are explicit exceptions. | Preserve whether retained width is local or was already changed by a legacy recorder. Normal visual transforms scale the pen; WinUI's fixed-width contract is lowered explicitly in device space, not simulated by an inverse scalar. |
| [Win2D `CanvasDrawingSession`](https://microsoft.github.io/Win2D/WinUI3/html/T_Microsoft_Graphics_Canvas_CanvasDrawingSession.htm) and [`DrawLine`](https://microsoft.github.io/Win2D/WinUI2/html/M_Microsoft_Graphics_Canvas_CanvasDrawingSession_DrawLine_2.htm) | A drawing session applies its `Transform` to subsequent operations, while each draw supplies an independent stroke width and style. | Keep transform and pen as separate typed retained state. Bridges record provenance rather than destructively rewriting the width during picture replay. |
| [WebRender rendering overview](https://searchfox.org/mozilla-central/source/gfx/docs/RenderingOverview.rst) | A retained display list becomes a scene and then a culled frame. A spatial tree preserves local-to-world and world-to-device transforms, and non-simple transforms form distinct coordinate systems. | Retain one source command and compose coordinate transforms at compilation. Do not create scale-specific picture copies or make width identity depend on replay position. |
| [Vello](https://github.com/linebender/vello) and its [`Scene`](https://github.com/linebender/vello/blob/main/vello/src/scene.rs) | A retained scene records path, stroke style, brush, and affine state, then performs parallel vector work on a WebGPU-capable renderer. | Preserve the same separation at ProGPU's typed boundary and use GPU expansion only where it is mathematically equivalent. ProGPU's provenance flag and affine outline path are original designs rather than ports of Vello encoding. |
| [SkParagraph](https://skia.googlesource.com/skia/+/refs/heads/main/modules/skparagraph/) | Shaping and paragraph layout are reusable CPU results consumed later by rendering. | Examined because text is a mandatory rendering-research boundary, but rejected as an implementation model for primitive pen transforms. This change does not reshape, relayout, or re-key glyphs. |
| [DirectWrite glyph-run analysis](https://learn.microsoft.com/en-us/windows/win32/api/dwrite/nn-dwrite-idwriteglyphrunanalysis) | DirectWrite derives device-pixel coverage from a reusable positioned glyph run and an explicit transform at the rendering boundary. | Preserve ProGPU's existing separation between retained text layout and transformed coverage. No pen-transform state is added to text or glyph caches. |
| [Parley](https://docs.rs/parley/latest/parley/) | Shared font and layout contexts produce reusable positioned glyph runs; rendering is a later concern. | Rejected for stroke implementation. Its separation reinforces that a primitive-stroke correction must not invalidate or rebuild text layout. |
| [HarfBuzz glyph rendering boundary](https://harfbuzz.github.io/glyphs-and-rendering.html) | HarfBuzz primarily returns positioned glyphs; outline extraction and rasterization follow shaping. | Rejected for stroke implementation. Unicode/OpenType shaping, fallback, glyph IDs, and variation state remain unchanged. |
| [SVG 2 painting and vector effects](https://www.w3.org/TR/SVG2/painting.html#VectorEffects) | Normal positive-width strokes participate in transforms. `non-scaling-stroke` is a distinct opt-in vector effect. | Ordinary ProGPU pens scale with their visual. A non-scaling pen must become an explicit public contract; it must not be simulated by ambiguously pre-scaling every retained width. |
| [WinUI `CompositionSpriteShape.IsStrokeNonScaling`](https://learn.microsoft.com/en-us/uwp/api/windows.ui.composition.compositionspriteshape.isstrokenonscaling) | Enabling the property keeps the shape outline from scaling with the shape transform. | Lower the retained centerline through the complete Composition transform once, cache that transformed path, and apply the original width, dash distances, caps, and joins in device space. A scalar inverse is rejected because it cannot be exact for anisotropic scale or shear. |
| [WGSL derivative built-ins](https://www.w3.org/TR/WGSL/#derivative-builtin-functions) | Screen-space derivatives, including the Manhattan-width `fwidth`, are defined only in fragment stages and must be evaluated in uniform control flow. | Late affine stroke modes compute coordinate derivatives at fragment entry, then select the winning edge or distance gradient without placing derivative calls in divergent primitive branches. |

## Retained provenance contract

Let `C` be a command-local transform, `V` the enclosing visual or picture
transform, and `F = C * V` the complete row-vector transform used by
`System.Numerics`. A retained command has one of two width representations:

- `IsPenThicknessLocal == true` means `Pen.Thickness` is the source-space
  width. All new Scene transform overloads and bridges that retain a raw pen
  must set this state.
- `IsPenThicknessLocal == false` is the compatibility representation used by
  older or external recorders that pre-scaled the width by `C`. The compiler
  recovers the source width by dividing by the same finite, positive matrix
  metric that recorder used. Current WPF, System.Drawing, Avalonia, Skia, and
  Scene transform overloads all retain the raw width and mark it local, so
  anisotropic and sheared geometry never has to reconstruct a lost direction.

Picture replay may transform brush coordinates, but it must not multiply pen
width, mutate a dash array, or change dash offset. Replaying one retained
picture beneath scales `s1`, `s2`, ... must compile each instance from the same
source width. In particular, a scale `s` must produce `s`, never `s^2`, width
composition. Solid brushes reuse their existing pen object when no brush-space
rewrite is required.

`PenStrokeTransformMode.Fixed` is a distinct positive-width contract. It
retains the requested width in typed pen state, transforms the centerline by
the complete affine matrix, and expands the body, caps, and joins in device
space. The mode is preserved through pen clones, geometry caches, append and
picture replay, and the versioned picture archive. It is not represented as a
hairline, an inverse zoom scalar, or a scale-keyed CPU geometry copy. DXF uses
this mode for every cached entity pen and for its specialized uncached
outlines.

Dash lengths, offsets, caps, joins, and miter limits are resolved from the same
source-space thickness. A dashed command lowered to an undashed outline records
that derived pen as local, preventing the compatibility recovery from running a
second time. Open dashed contours retain the source start and end caps only
when the first or last painted interval reaches that endpoint; interior dash
intervals use the dash cap. A painted interval that crosses a closed contour's
seam is merged into one cyclic run, so it receives a join instead of two
coincident caps. These per-figure decisions are retained typed metadata and are
part of clone, transform, path-operation, font-outline, and picture archive
ownership. Opacity-mask compilation and CPU/GPU hit testing use the same
provenance and cap resolver as visible rendering.

Non-finite, zero, collapsed, or non-invertible width state fails closed for the
stroke. A valid fill on the same command remains renderable. The compatibility
path is bounded to known legacy commands; it is not a general inference that a
pen with an arbitrary transform has already been scaled.

## Conformal fast path and affine fallback

For the linear two-by-two part `A = [[a,b],[c,d]]`, the existing matrix metric
is its largest singular value:

`scale(A) = sqrt((q + sqrt(q^2 - 4 det(A)^2)) / 2)`, where
`q = a^2 + b^2 + c^2 + d^2`.

The calculation is fixed `O(1)` work and allocation-free. For a conformal
transform--orthogonal basis vectors of equal length--this scalar is the exact
uniform scale. Lines and analytic curves whose centerlines are already in
render space may therefore use
`deviceThickness = localThickness * scale(F)` without changing their existing
constant-size GPU records.

No scalar is exact for an anisotropic scale or shear: the transformed width
depends on the local tangent. Those transforms use the affine fallback. ProGPU
constructs the stroked outline in source space using the source thickness,
dash, cap, join, and miter state, then transforms the resulting geometry by
`F`. Rectangles, ellipses, and rounded rectangles whose shader distance fields
already interpolate local coordinates likewise keep local thickness. Applying
the largest singular value as a universal width is rejected because it makes
some orientations too thick and cannot reproduce a sheared outline.

The conformal classification compares the lengths and dot product of the two
basis vectors using a small scale-relative tolerance. Rotation, reflection,
translation, and uniform scale stay on the scalar path. Non-uniform scale and
shear take the outline path. Perspective and invalid transforms are not
silently approximated as affine strokes.

## Static and late-bound GPU transforms

Static buffers and commands with `UseGpuTransforms` retain source-space width;
their final matrix is intentionally applied after CPU scene compilation. A
conformal late-bound matrix may supply its exact scalar to the vector vertex
shader. A static viewport may cache that `O(1)` metric in reserved uniform
storage without changing the shared 224-byte `GpuUniforms` ABI; a dynamic view
derives it from the current matrix.

The explicit multiplication is scoped only to direct two-dimensional stroke
primitives whose centerline is transformed before screen-space expansion.
Local-coordinate distance-field shapes already inherit scale from their
coordinates, and an explicit hairline stays one device pixel. Text, textures,
three-dimensional lines, and unrelated vector payloads must not consume the
stroke scale.

A non-conformal late-bound matrix must preserve the same affine-outline result
as normal scene compilation. It must either transform source-space outline
offsets in the GPU path or materialize the local outline before the late-bound
draw. A singular-value-only shader fallback for anisotropic scale or shear is
not acceptable. Static and dynamic GPU paths are required to match the CPU
path's orientation-sensitive result. For quadratic and cubic commands, the
affine ribbon uses at least 24 sections and increases the bounded section count
from transformed second-difference curvature until its conservative chord
error is at most 0.25 device pixel (capped at 1,024 sections). It does not claim
an analytic nearest-distance solution for an arbitrary cubic curve.

Retained hairline caps and joins use fixed vector ABI shape types `22` and
`23`, with one quad per adornment. The vertex stage reconstructs transformed
directions, turns, and miter intersections in framebuffer space; the fragment
stage evaluates only the exterior signed-distance region while the adjoining
stroke bodies own their shared seams. This preserves exact one-device-pixel
behavior under scale, reflection, anisotropy, and shear without a CPU-baked
classifier or per-frame outline allocation.

Arbitrary positive fixed-device widths reuse the same constant-size GPU
centerline records and cap/join descriptors. A reserved negative vertex-width
encoding distinguishes the positive fixed width from the `-1` hairline
sentinel; the vertex shader decodes it only for analytic and direct-stroke
shape types. Direct line, quadratic, cubic, and arc bodies expand by the
decoded width after the late transform. Analytic rectangle, ellipse, rounded
rectangle, and circle distance fields convert that device width into local
distance units from fragment derivatives evaluated in uniform control flow.
This keeps DXF static-buffer zoom entirely on WebGPU and adds no per-frame CPU
geometry rebuild, managed allocation, or GPU readback.

WinUI's explicit positive-width non-scaling stroke is lowered at the retained
Composition boundary. The source centerline is transformed once and cached by
source-path identity and the complete affine matrix; the unchanged positive
width is then stroked with an identity command transform. This makes width,
dash arc length, caps, joins, visible rendering, opacity masks, and GPU hit
testing share one exact device-space representation. Geometry or transform
changes replace the bounded cache; steady replay performs no path conversion.
This is deliberately separate from Skia's zero-width hairline sentinel.

Special Skia picture, image, composed, and color-filter shader strokes use the
same retained hairline contract. A zero-width Stroke records a typed path
opacity mask carrying the complete canvas transform, cap/join state, and dash
state, then shades a tightly bounded fill through that mask. StrokeAndFill
records its fill and its one-device-pixel outline as two ordered operations.
Materializing a local fill outline is rejected because a device hairline has no
transform-independent local outline.

The experimental Wavefront engine currently supports only a narrow subset of
opaque, nonzero-winding, closed solid fills. Because it composites one compute
layer after the ordered Atlas pass, mixing a Wavefront instance with a stroke,
mask, text run, image, gradient, effect, or other Atlas command would reorder
content. ProGPU therefore makes one transactional engine choice per frame: a
pure supported-fill frame remains Wavefront, while any unsupported or mixed
content retries the complete frame through Atlas. Per-command stroke fallback
was rejected because it cannot preserve z order, and mask/offscreen work stays
on Atlas until Wavefront gains ordered render-target batches. The current
64-instance frame bound also prevents the fixed per-cell Wavefront index table
from silently dropping overlapping shapes; larger fill scenes retry Atlas.
Wavefront's current signed-distance conversion uses one transform-axis length,
so its eligibility gate also rejects anisotropic scale and shear. Those
non-conformal fills use Atlas until Wavefront evaluates distance with the full
inverse-transpose/Jacobian rather than a scalar approximation.

## Complexity, ownership, and performance contract

- Provenance recovery, conformal classification, and matrix-scale resolution
  are allocation-free `O(1)` operations per stroke command.
- A conformal line retains constant vertex and index counts. A conformal curve
  retains its existing bounded sampling cost; only one scalar multiplication is
  added per applicable GPU vertex.
- Affine outline construction is `O(S + D)` time and storage for generated
  stroke segments `S` and dash boundaries `D`. Scratch buffers are reserved or
  pooled and must not allocate per source segment after warmup.
- Device-hairline arc hit geometry selects its chord count from transformed
  ellipse radius and sweep with at most 0.25-pixel sagitta error, retaining a
  32-section floor and a 4,096-section safety bound. The bound covers radii far
  beyond supported framebuffer dimensions without allowing adversarial paths
  to request unbounded CPU memory.
- Picture replay is `O(C)` for retained commands `C`. Stable solid-color replay
  does not clone pens or dash arrays and does not create variants keyed by
  visual scale. Span-based polyline and spline recording, append translation,
  and portable picture deserialization do not materialize managed path graphs.
  Connected solid polylines compile directly into pre-reserved bounded vertex
  and index spans. Splines use a transform-adaptive 10/25/50/100-segment stack
  sample at compilation rather than an unconditional 100-segment retained
  graph. Opaque transform-backed payloads (pictures,
  visuals, hatches, dot grids, 3D lines, ACIS, and static DXF) compose the
  translation without rewriting shared retained geometry or GPU buffers.
  Extension compilation receives that already-composed transform exactly once;
  static hatch/3D-line vertices bake it, while transform-consuming retained DXF
  draw calls carry it to rendering without a second multiplication.
  Static image, ShaderToy, WPF shader-effect, and backdrop quads likewise bake
  their active transform once; deferred-transform extensions keep their
  render-time transform contract.
- A warmed dashed-stroke cache compares the bounded dash interval list in
  `O(D)` time and performs no geometry or paint allocation. Geometry identity
  includes width, offset, interval values, and all endpoint/dash caps; derived
  paint identity additionally includes brush reference, join, and miter state.
  Rectangles, ellipses, circles, and rounded rectangles retain their analytic
  fill quad, but route a dashed outline through the same cached path for visible
  rendering and GPU hit testing. Replay, append, and picture archives preserve
  or rebuild that source path once; solid analytic primitives keep the compact
  allocation-free recorder path.
- Static conformal scale is computed once per viewport-uniform update, not once
  per command. Late-bound affine expansion remains linear in emitted outline
  vertices and does not require CPU readback.
- Fixed-device positive strokes retain the normal constant vertex/index count.
  Their late zoom path adds only bounded matrix, direction, and derivative
  arithmetic per existing vertex or fragment; steady DXF zoom neither
  recompiles the static buffer nor allocates scale-specific pen/geometry state.
- Wavefront eligibility is checked only when that experimental engine is
  selected. A supported path scan is `O(S)` for retained segments `S`; an
  unsupported or mixed frame performs one bounded transactional recompilation
  through Atlas and never interleaves incorrectly ordered engine output.
- Width provenance is scalar typed state on `RenderCommand`; it introduces no
  reflection, object dictionary, native dependency, or per-frame adapter.
- A WinUI non-scaling shape path is rebuilt in `O(S)` time and storage for `S`
  source segments only when its source geometry or complete transform changes;
  steady recording and replay reuse the cached transformed path. Special-shader
  hairlines add one bounded opacity-mask pass and retain one-device-pixel width
  under every finite invertible affine transform.

Correctness is not traded for a lower command count. An optimization that
restores the old width by skipping visual invalidation, dropping caps or dashes,
using a conservative scalar for affine geometry, or turning an ordinary stroke
into a hairline is a regression.

## Validation contract

The focused tests and existing rendering suites jointly exercise:

- raw-local Scene, Avalonia, WPF, System.Drawing, and Skia commands plus
  synthetic legacy pre-scaled compatibility commands;
- nested visual and picture scales below and above one, proving exactly one
  width composition and immutable picture metadata;
- rectangles, ellipses, rounded rectangles, paths, lines, quadratic and cubic
  curves, polylines, splines, dashes, open and closed dash seams, every cap and
  join family, masks, and hit-test bounds;
- horizontal, vertical, diagonal, rotated, reflected, anisotropically scaled,
  and sheared strokes, comparing the conformal path with transformed local
  outline expectations;
- static-buffer and dynamic GPU-transform output for both conformal and
  non-conformal matrices, with explicit one-device-pixel hairline and positive
  fixed-device-width coverage;
- a DXF-rendered static line plus analytic fixed-width rectangle, ellipse, and
  rounded rectangle at zoom factors below and above one, proving unchanged
  framebuffer thickness without static-buffer recompilation;
- invalid and collapsed render and hit-test transforms, ensuring a bad stroke
  emits no invalid vertices or ghost hit while an independent fill survives;
- fixed `GpuUniforms` layout, scoped vector-shader use, retained-scene reuse,
  and zero managed allocation in the warmed provenance/solid-picture hot path.
- WinUI positive non-scaling strokes under anisotropic scale and Skia special-
  shader hairlines under non-uniform transforms, including StrokeAndFill.

Release qualification runs the focused pixel and provenance tests, the full
Release core and headless suites, Avalonia contract tests, static/GPU rendering
tests, `ShaderResourceTests`, documentation gates, and package builds on all CI
platforms. Any performance claim requires matched Release workloads and
correlated EventPipe plus macOS Time Profiler, Allocations/VM Tracker, and Metal
System Trace evidence. Raw profiling traces and temporary launch artifacts are
removed after retaining compact measurements and exact reproduction metadata.
