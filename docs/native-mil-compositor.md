# Native MIL compositor

## Goal

ProGPU will provide a reflection-free C++ composition endpoint that can consume
LibreWPF's canonical DUCE/MIL command batches, retain the WPF resource graph,
lower it to the existing ProGPU semantic scene ABI, and execute that scene on
the same C++ renderer through either wgpu-native or provider-resolved Dawn.
The existing managed `ProGPU.Scene` compositor remains available as an
independent compatibility lane while parity is established.

This is a clean source-integrated replacement, not a binary shim that scrapes
managed WPF objects. Protocol structs are derived from the MIT-licensed WPF MCG
model and are consumed as typed byte records with explicit bounds checks.

## Protocol findings

WPF's client channel writes each command as:

```text
uint32 item_size_including_header
uint32 MILCMD
byte[] packed_command_fields_and_optional_payload
byte[0..3] dword_alignment_padding
```

`item_size` is at least eight, is divisible by four, and must fit entirely in
the submitted batch. The command packet returned by WPF's reader begins at the
`MILCMD` field and has `item_size - 4` bytes. Resource handles are 32-bit and
belong to a channel-local namespace. The current retail protocol contains 141
commands:

| Range | Area | Count |
| --- | --- | ---: |
| `0x01`–`0x3d` | transport, resources, visuals, targets, glyphs | 61 |
| `0x3e`–`0x56` | nested render-data instruction stream | 25 |
| `0x57`–`0x8d` | retained media, 3D, effect, geometry, brush, drawing resources | 55 |

The transport and render-data streams use the same item framing. The outer
`MilCmdRenderData` packet carries the nested stream byte count and then the
nested command bytes. Commands and structures are packed, so the decoder uses
bounded byte copies and never casts an untrusted address to a command struct.

## Architecture

```text
LibreWPF source-built PresentationCore
  DUCE command producer / typed portable producer
             |
             | canonical MIL batch bytes
             v
ProGPU.Native.MIL (C++20)
  framing validator -> channel handle table -> retained resource graph
      -> render-data decoder -> semantic scene lowering -> damage tracking
             |
             | ProGPU semantic scene stream ABI
             v
ProGPU.Native C++ compositor
  wgpu-native adapter       provider-resolved Dawn adapter
                                      |
                           Windows Dawn D3D12 / DXGI path
```

The native MIL layer is backend-neutral. It produces the same semantic scene
stream used by current C++ samples and the managed `ProGPU.Scene.Native`
compiler. Backend selection, device loss, external-image binding, submission
lifetime, hit testing, and render-target ownership remain in the native
compositor.

## Delivery stages and gates

### Stage 0 — protocol foundation (implemented)

- Complete command-ID namespace (`0x01`–`0x8d`).
- Zero-copy, bounds-checked batch reader.
- Transactional channel state: a rejected batch cannot partially mutate the
  live graph.
- Size-versioned C ABI exported by both native renderer modules and a typed
  allocation-free .NET batch submission owner in `ProGPU.Backend.Native`.
- Initial typed resource, visual, generic-target, and opaque render-data state.
- Strict failure for unknown commands, unsupported commands, invalid handles,
  type mismatches, invalid graph operations, and malformed sizes.

### Stage 1 — complete retained 2D resources

The first Stage 1 vertical slice is implemented in the typed C++ API, the
size-versioned C ABI, and `NativeMilChannel.CompileScene(...)`. It
decodes the exact WPF `MILCMD_SOLIDCOLORBRUSH`, nested
`MILCMD_DRAW_RECTANGLE`, and nested `MILCMD_DRAW_ELLIPSE` records, applies
retained visual offsets and opacity, supports balanced nested
`MILCMD_PUSH_OPACITY`/`MILCMD_POP` scopes, walks the target's visual tree with
cycle/depth validation, and emits the shared pointer-free ProGPU semantic scene
stream. `MILCMD_DRAW_ROUNDED_RECTANGLE` is also lowered with either the
uniform analytic primitive or the exact positive non-uniform vector-path lane.
Ellipse centers/radii are converted exactly to native analytic
bounds, and every primitive kind is reported separately in typed scene metrics.
Scope opacity is composed with retained visual opacity in native semantic
state; malformed opacity and over/underflowed scope stacks fail closed.
Typed `MILCMD_MATRIXTRANSFORM`, `MILCMD_TRANSLATETRANSFORM`,
`MILCMD_SCALETRANSFORM`, `MILCMD_SKEWTRANSFORM`,
`MILCMD_ROTATETRANSFORM`, variable-size `MILCMD_TRANSFORMGROUP`,
`MILCMD_VISUAL_SETTRANSFORM`, and nested `MILCMD_PUSH_TRANSFORM` are also
implemented. `MILCMD_DOUBLERESOURCE` and `MILCMD_MATRIXRESOURCE` supply current
animated field values for those transform packets; a nonzero animation handle
replaces its corresponding base packet value exactly. Leaf values are
range-checked and quantized to the same float matrix state used by WPF MIL.
Transform groups retain ordered child handles and resolve them on demand in WPF
row-vector collection order, so child or animation-resource updates are visible
without flattening or rebuilding the group. Cycles, deletion of a live child or
animation dependency, excessive recursion, missing/wrong-type nonzero handles,
nonzero packet padding, and unbalanced scopes fail closed transactionally.
Resolved transforms compose as local visual transform, visual offset, parent
transform, and then nested drawing scopes; draw culling bounds are the
axis-aligned bounds of all four transformed primitive corners.
Transform handle zero retains WPF's defined balanced no-op scope. Animated
transform fields on linear and radial gradient brushes are resolved through
the same retained transform graph. Other animated brushes and pens and other
nested commands deliberately fail closed until their typed resources are
implemented.
The slice is covered by byte-level fixtures that check semantic
brush, transform/opacity state, rectangle, ellipse and rounded-rectangle
primitives, transformed bounds, nested scope, rollback, scene identity,
generation, and tree metrics. The C ABI supports an explicit required-size
query, preserves the original 32-byte metrics caller contract when appending
new metrics, and writes into caller-owned storage; the managed owner returns
the completed semantic stream with typed compilation metrics for direct native
compositor submission.

The current Stage 1 slice implements the exact 52-byte `MILCMD_PEN` resource,
variable-size `MILCMD_DASHSTYLE` resource, and 44-byte nested
`MILCMD_DRAW_LINE` packet for unanimated solid-brush pens. Flat,
square, round, and triangle start/end caps map directly to the reusable ProGPU
geometry-stroke flags; pen brushes share the semantic brush table, and line
width/cap-conservative local bounds are transformed through the same four-
corner affine path. Nonempty dash resources route through ProGPU's reusable
semantic connected-stroke engine, preserving thickness-relative intervals,
offset, dash cap, odd-pattern repetition, and backend-independent execution.
Null pen handles and zero-width/null-brush pens are no-op draws. Thickness and
dash-offset animation, invalid dash values/enums, unresolved handles, nonzero
padding, and zero-total-length dash cycles fail closed transactionally.
Zero-length lines use WPF's horizontal shape-space direction and compose the
configured non-Flat start/end point caps from native cap primitives. For finite
nonzero dash patterns, the offset selects the initial dash/gap exactly, with an
offset on a boundary belonging to the preceding interval as in WPF's
`CDashSequence::Initialize`. Flat/Flat and initial-gap cases are exact no-ops.
The size-stable MIL scene metrics ABI now publishes `line_count` in its former
reserved tail field.

Canonical `MILCMD_LINEARGRADIENTBRUSH` and `MILCMD_RADIALGRADIENTBRUSH`
resources are retained as typed native state. The decoder validates their
84-byte and 108-byte fixed packet prefixes, respectively, plus the exact
24-byte `MilGradientStop` payload stride. `MILCMD_POINTRESOURCE` and
`MILCMD_DOUBLERESOURCE` replace current point, opacity, and radial-radius values
on every scene compilation; referenced transform resources remain live without
retransmitting the brush packet. Deletion of a referenced animation or
transform is rejected transactionally.

Each brush use resolves WPF's ordering in geometry space: relative coordinates
are mapped through the drawing bounds, `RelativeTransform` is conjugated by
those bounds, absolute `Transform` is appended, and the inverse draw and brush
matrices become the shared shader coordinate transform. Fill and ordinary
nondegenerate pen paths therefore share one bounds-correct material across
analytic rectangles/ellipses, retained paths, geometry groups, combined
geometry, lines, and rounded rectangles. Pad, Reflect, Repeat, sRGB, scRGB,
anisotropic radii, and focal origins lower to the existing backend-neutral
ProGPU brush ABI and execute in the same `Vector.wgsl` path for wgpu-native and
Dawn/DirectX.

Gradient stops are stably sorted after WPF double-to-float position
quantization. Out-of-range endpoints are clamped or interpolated in the selected
WPF color space, scRGB packet colors are converted to the shader's sRGB storage
contract, zero stops become an empty material, and one stop becomes a solid
material. Internal exact duplicate stops remain ordered for hard transitions.
WPF's epsilon-based near-coincident consolidation and its distinct Pad outside
color at duplicate 0/1 endpoints are not yet represented by the semantic brush
ABI; those cases remain an explicit differential-parity task rather than an
approximate claim. Gradient brushes on cap-only degenerate pen strokes also
fail closed until the cap path exposes its exact brush-sizing bounds.

Canonical `MILCMD_GEOMETRYDRAWING` resource `87` and nested
`MILCMD_DRAW_DRAWING` command `0x4a` are retained as typed native state. A
geometry drawing keeps nullable brush, pen, and geometry handles and resolves
them through the same native geometry lowering used by `DrawGeometry`; a null
geometry is the WPF-defined no-op. The channel rejects wrong resource types and
deletion of a live brush, pen, or geometry dependency transactionally. Other
drawing resource kinds remain unsupported and fail closed rather than being
adapted through managed object inspection.

Axis-aligned `MILCMD_DRAW_RECTANGLE` records now accept independent fill and
pen handles. Rectangle pens lower to closed four-point semantic polylines, so
solid and dashed outlines share ProGPU's native join, miter-limit, dash-cap,
offset, odd-pattern, transform, and backend execution rules. Fill-only,
stroke-only, and fill-plus-stroke records remain distinct draws with one shared
brush table; stroke culling expands the local rectangle by half the pen width
before the four-corner affine bounds transform. A solid zero-width or
zero-height rectangle uses WPF's optimized widened-shape result directly: the
degenerate contour has no inner boundary, so ProGPU emits the one exact outer
vector fill. Miter and Bevel joins preserve `Get90DegreeBevelOffset()` and its
miter-limit clamp; Round joins preserve the rounded outer path. Nonempty dash
patterns on degenerate rectangles remain fail closed pending exact collapsed
dash traversal.

`MILCMD_DRAW_ELLIPSE` records likewise accept independent fill and pen handles.
Solid ellipse pens lower to ProGPU's exact analytic full-ellipse arc primitive,
including non-uniform radii and affine semantic-state execution. Fill-only,
stroke-only, and fill-plus-stroke records share the native brush table; stroke
culling expands the local ellipse bounds by half the pen width before the
four-corner affine bounds transform. A nonempty dash pattern on an ellipse
fails closed until the native curve path can preserve phase continuously around
the full circumference or along a one-axis collapse. Degenerate ellipse fills
produce no coverage. A solid one-axis ellipse lowers to the exact round-ended
capsule implied by WPF's four SmoothJoin cubic segments; a fully collapsed
ellipse uses the same Round/Round point-disk path as the native widener. Both
retain their geometry-local affine transform without curve flattening.

Uniform-radius `MILCMD_DRAW_ROUNDED_RECTANGLE` records now accept independent
fill and pen handles. Positive-radius solid outlines lower to ProGPU's exact
rounded-rectangle analytic primitive with native stroke thickness, including
affine semantic-state execution and bounds expanded by half the pen width.
Fill-only, stroke-only, and fill-plus-stroke records share the native brush
table. If either radius is zero on a positive-area rectangle, WPF's
`CShape::AddRoundedRectangle` normalizes the shape to a sharp rectangle before
widening; native fill therefore uses the analytic rectangle and stroke keeps
the closed-polyline rectangle path so WPF join and dash metadata are preserved.
Degenerate uniform-radius solid outlines use the same WPF outer widened path
with separately clamped X/Y corner radii, retaining
analytic quarter arcs under affine transforms. Positive independent X/Y radii
reuse the shared vector path and connected-curve stroke lanes. Nonempty dash
patterns on curved corners fail closed until their exact curve semantics are
available. Degenerate asymmetric records with either radius zero remain fail
closed until their general-widener collapse semantics are proved.

The retained fixed-geometry slice implements the exact fixed-size
`MILCMD_LINEGEOMETRY`, `MILCMD_RECTANGLEGEOMETRY`, and
`MILCMD_ELLIPSEGEOMETRY` updates plus nested `MILCMD_DRAW_GEOMETRY`. Each
resource retains its primitive state and optional typed matrix-transform
handle. Line fills remain empty while solid and dashed pens reuse the same
stroke path as `MILCMD_DRAW_LINE`. Rectangle and ellipse resources reuse the
native analytic fill/stroke lowering used by their immediate draw commands,
including uniform rounded rectangles and geometry-local affine transforms.
Positive non-uniform rounded rectangles reuse the same path fill and connected
curve stroke lane. Positive-area zero-axis asymmetric records normalize to the
same sharp rectangle fill/stroke lane as immediate draws. Animated fields,
degenerate zero-axis asymmetric radii, uninitialized or wrong-type resources
fail closed transactionally.

The first retained general-path slice implements canonical variable-size
`MILCMD_PATHGEOMETRY` updates and nested fill-only `MILCMD_DRAW_GEOMETRY`.
The native channel validates WPF's `MIL_PATHGEOMETRY`, `MIL_PATHFIGURE`, fixed
line/quadratic/cubic/arc segments, and poly-line/poly-quadratic/poly-cubic
record links and sizes before committing retained state. Filled contours lower to one
backend-independent ProGPU semantic path batch with EvenOdd/Nonzero fill,
geometry-local affine transforms, exact validated cached local bounds when
present, conservative native bounds when WPF leaves `BoundsValid` clear, and
WPF's implicit fill closure for open figures. Malformed back-links, sizes, padding,
flags, counts, bounds, transforms, and handles roll back transactionally.
Endpoint arcs reuse the neutral native arc resolver shared with SVG glyph
paths, retain center/radii/sweep data in ProGPU's semantic arc record, and
preserve the degenerate-to-line rule. Fill compilation remains independent of
the separately retained stroke topology described below.

The first retained path-pen slice now keeps WPF stroke topology separately
from fill topology while decoding the same canonical figure stream. Line and
poly-line segments lower to ProGPU semantic polylines with the retained pen's
thickness, miter limit, start/end/dash caps, join, thickness-relative dash
intervals/offset, brush, and geometry-local affine transform. `SegIsAGap`
splits open figures into independent runs, using the pen's dash cap at each
gap boundary and preserving the figure start/end caps only at true open-figure
endpoints. Each run restarts the dash sequence as WPF's `CDasher::StartFigure`
does after a geometry gap. For closed figures the
decoder rotates after a gap and joins the stroked tail, implicit closing edge,
and stroked head into one open run, avoiding a false cap at the original figure
start while retaining dash caps at the actual gap. Fully stroked closed figures
remain one closed contour, and open figures
are never implicitly closed for stroking even though WPF fill closure remains
unchanged. A nonempty dash pattern on a closed gapped figure whose continuous
run crosses the original figure start fails closed: WPF resets dash phase at
that start yet joins the two widened pieces, which cannot be represented by
one current semantic polyline without changing phase or join shape. The native
curved-stroke slice retains each exact segment record beside the line topology
and lowers solid line/quadratic/cubic/analytic-arc contours to reusable ProGPU
geometry primitives. Multi-segment and closed contours compose those
primitives with native path-join records. Join tangents come from the exact
segment derivative at each shared endpoint: line direction,
quadratic/cubic endpoint-control fallbacks, or the resolved analytic arc axes
and sweep. A closed contour also emits the final-to-first join, while an open
contour emits native path-cap records for Square, Round, or Triangle start/end
caps using those same exact endpoint tangents; Flat caps remain implicit.
Geometry-gap boundaries use the pen's typed dash-cap value even when the dash
interval list is empty. Geometry-local affine transforms stay on every
primitive, cap, and join; arcs map their resolved center/radii/rotation/angles
into the reusable two-axis analytic arc contract. Discontinuous endpoints or
degenerate tangents adjacent to nondegenerate cap/join composition fail closed
transactionally. A wholly degenerate solid open contour instead composes its
typed start/end (or gap-boundary DashCap) pair around WPF's horizontal
shape-space direction. A wholly degenerate solid closed contour forces
Round/Round caps, matching `CWidener::WidenClosedFigure` and yielding an exact
point disk independent of the pen's line caps. `SegSmoothJoin` is retained per
incoming segment and forces only that endpoint's join to Round, matching WPF
`CWidener::DoSegment`/`CSimplePen::DoCorner`; the closing join uses the last
segment's flag. Finite nonzero dash patterns on wholly degenerate contours use
the same exact initial dash/gap selection as immediate lines. Dashed curves,
dashed smooth joins, and zero-total-length dash cycles remain unsupported until
their exact composition is available. Unstroked curves remain valid topology
gaps and do not prevent neighboring line runs from using the native path-pen
lane.

The first retained `MILCMD_GEOMETRYGROUP` slice validates the canonical
variable child-handle payload, group fill rule, optional matrix transform,
typed geometry dependencies, and cycles transactionally. At execution, groups
whose children are identity-local retained `PathGeometry` resources aggregate
their contours into one semantic path batch, so the group's EvenOdd/Nonzero
rule is applied across child overlap exactly as WPF's `CShape` aggregation
does. Affine-transformed line/quadratic/cubic path children are baked into that
shared coordinate space exactly, including WPF's implicit closing fill edge;
their cached local bounds are conservatively transformed once. Fixed rectangle
and ellipse children join that same batch, including
geometry-local affine transforms and non-uniform rounded rectangles. Rectangle,
rounded-corner, and ellipse contours use WPF's `CFigureData` point order,
radius clamping, and `ARC_AS_BEZIER` cubic constant before applying the child
matrix; line children correctly contribute no fill. The group transform remains
one native path transform. Nested groups recurse through the same bounded
lowerer, compose each group/leaf transform in WPF order, and intentionally
ignore nested fill rules before applying the outer root fill rule. This matches
`CMilGeometryGroupDuce::GetShapeDataCore`, which copies every child figure with
`CShape::AddShapeData` and calls `SetFillMode` only on the resulting outer
shape. Nonzero groups keep that shared contour batch because cross-child winding
cancellation is significant. EvenOdd groups compile the same child leaves into
an exact postfix XOR program; XOR of the child-inside predicates is the outer
EvenOdd result and bounds each raster operation to its own leaf without changing
WPF parity semantics. A temporary typed compiler guard rejects overlapping
nonzero translated-equivalent leaf streams before scene submission; that exact
pattern removes the current Parallels D3D12 device in the shared path backend,
so it remains an observable `unsupported_command` until the backend can execute
it safely. Nonsingular affine transforms on native arc records are
baked without flattening: ProGPU transforms the arc's two ellipse basis vectors,
factors the resulting `T*T^T` metric into orthogonal output axes/radii, projects
the start parameter into that basis, and reverses the sweep exactly when the
affine determinant is negative. A translation-only fast path preserves the
source radii, axis, angles, sweep, and padding bit-for-bit while translating only
the endpoints and center. Combined-geometry children and meaningful group pens
currently fail closed until their contours or strokes can be composed without
approximation. Exact singular affine transforms now lower fill and stroke
coverage to empty, matching WPF's zero-determinant area semantics without
attempting to invert or factor an arc basis.

Canonical fixed-size `MILCMD_COMBINEDGEOMETRY` state now retains the optional
matrix transform, two geometry dependencies, and WPF Union/Intersect/Xor/
Exclude operation. Null operands become explicit empty leaves. Identity-local
`PathGeometry` operands lower to ProGPU's native postfix boolean program,
preserving each operand's own fill rule and executing the result in the native
path atlas on every backend. The same shared shallow
fill lowerer now accepts fixed rectangle and ellipse operands, including
geometry-local affine transforms and non-uniform rounded rectangles with WPF's
radius clamping, point order, and cubic constant; line operands become explicit
empty leaves. It also accepts affine-transformed line/quadratic/cubic path
operands using the same exact point/bounds baking as groups while preserving the
operand's own fill rule. A recursively flattened `GeometryGroup` is also a
boolean leaf: its root fill rule is retained while its group/child transforms
are baked into the leaf contours. Combined operands now recurse into that same
bounded geometry DAG and append their two subtrees plus operation in postfix
order. Nested combined transforms compose into descendant leaf transforms;
segment/node appends roll back together on failure, and conservative bounds
cover all nonempty descendants. Group/combined references share one cycle-
checked geometry DAG; deletion and malformed operation updates fail
transactionally. The same exact nonsingular affine arc factorization is shared
by group leaves and arbitrary-depth boolean leaves, including reflected/sheared
arcs and sweep reversal. Singular transformed operands become exact empty
leaves. Stroked operands remain fail closed. Combined children inside a
`GeometryGroup` also remain fail closed
because treating a boolean result as raw outer-fill contours would change WPF
semantics.

The shared WGSL path rasterizer keeps these arcs analytic. It rejects samples
outside each path record's exact bounds, rejects quadratic and cubic work on
rows outside the curve control hull, and tests arc sweep membership with
oriented cross products of the normalized start/end vectors rather than
per-crossing trigonometric reconstruction. The existing half-open endpoint and
derivative rules are unchanged, and the complete path-atlas parity fixture
retains its prior pixel result.

`NativeMilBatchBuilder` and `NativeMilRenderDataBuilder` provide the matching
managed producer for this supported subset. They write the canonical WPF
framing and packed field offsets directly into reusable buffer writers, expose
only typed resource/color/primitive inputs, and are shared by package smoke
tests so LibreWPF does not need private-structure probes or hand-coded arrays.

`MILCMD_PUSH_CLIP` keeps axis-aligned, non-rounded rectangles on the semantic
clip-rectangle fast path. Other retained fixed geometry, paths, recursive
groups, and recursive combined geometry compile to retained vector-mask paths
with the same analytic line/quadratic/cubic/arc segments, fill rules, and
bounded postfix boolean programs used by native fills. Each path is frozen in
logical target coordinates using the transform active at push time, and nested
paths are ordered intersections. Rectangle and vector-mask state remain
independent, so a cheap bounds clip can constrain exact vector coverage without
changing it. Scope records store only arena prefix counts and a retained mask
index; push/pop does not copy accumulated path data. Degenerate and singularly
transformed geometry lower to an exact empty clip; oversized boolean programs
fail closed, and geometry bounds are never substituted for coverage. Fixed
ellipses and rounded rectangles use analytic quarter arcs rather than cubic
circle approximations.

- Generate packed protocol declarations and size metadata from a checked-in
  neutral manifest produced from WPF MCG inputs.
- Implement scalar animation resources, remaining transform kinds, curve dashes,
  exact translated-equivalent EvenOdd overlap execution,
  remaining pen draws,
  brushes, drawings, images, glyph runs, caches, multi-guide/dynamic
  guidelines, effects, and
  complete render-data decoding.
- Lower every supported update to stable semantic resource identities and
  generation numbers; unchanged resources must not be rebuilt.
- Add fixture capture/replay comparison against WPF's `CMilDataStreamReader`
  behavior and the existing managed LibreWPF renderer.

### Stage 2 — targets, scheduling, and parity services

- Add connection/partition/channel objects, handle duplication, out-of-band
  target updates, sync-flush replies, tier/capability replies, present/vblank
  scheduling, dirty regions, and device-loss recovery.
- Implement HWND and generic targets without leaking host-specific ownership
  into the retained scene layer.
- Add native hit-test result mapping and compositor diagnostics.

### Stage 3 — effects, 3D, media, and interop

- Complete shader effects, opacity masks, bitmap caches, 3D cameras/models,
  D3DImage/external texture binding, media frames, and color/text parity.
- Keep unsupported shader bytecode and external-handle forms fail-closed until
  a typed conversion or native backend implementation exists.

### Stage 4 — DirectX and DXGI facade parity

- Move the existing managed `ProGPU.DirectX` device/resource/view/pipeline
  compatibility model onto a native handle ABI shared with the MIL endpoint.
- Implement the measured D3D11/D3D12/DXGI/D3DCompiler export surface required
  by real package consumers. Do not attempt an unbounded system-DLL clone.
- On Windows, use Dawn's D3D12 backend for the compositor and explicit shared
  texture/fence paths for D3D11/D3D12 interop. Validate adapter LUID, format,
  alpha mode, row pitch, synchronization, resize, occlusion, and device loss.
- Preserve WebGPU behavior and semantic output across wgpu-native and Dawn;
  backend-specific differences require golden-image and metrics evidence.

### Stage 5 — LibreWPF selection and release gate

- Add an explicit runtime selector: managed portable, native MIL WebGPU, or
  native MIL Dawn/DirectX. The managed portable lane stays buildable and
  testable throughout migration.
- Package native runtimes for Windows x64/arm64 and the existing supported
  non-Windows RIDs. Verify exact ABI and protocol versions at startup.
- Run package-mode Toolkit/AvalonDock, Xceed when licensed, SciChart, input,
  clipping, hit testing, multi-window, DPI, and shutdown tests.

## Windows validation matrix

The primary integration guest is the discovered Parallels Windows 11 ARM64 VM.
The lane records guest OS build, .NET SDK, CMake/MSVC/Clang versions, adapter and
driver identity, Dawn backend, feature/limit set, and whether rendering uses
hardware, WARP, or another fallback. Required comparisons are:

1. Microsoft WPF MIL output on Windows versus LibreWPF managed portable output.
2. LibreWPF managed portable versus native MIL semantic streams.
3. Native wgpu/Dawn output versus Dawn D3D12 output.
4. DirectX compatibility API behavior versus native DirectX where the API is
   intentionally supported.

Tests use deterministic scenes, pixel tolerances, semantic stream hashes,
resource-generation/damage metrics, GPU validation output, and process lifetime
checks. A screenshot alone is not a parity result.

### Windows ARM64 qualification evidence — 2026-08-24

The current branch was qualified in the Parallels Windows 11 ARM64 guest with
OS build `26200.9168`, .NET SDK `10.0.400` / runtime `10.0.11`, Visual Studio
Build Tools `17.14.39`, ARM64 MSVC `19.44`, CMake `3.31.6`, and Ninja `1.12.1`.
The live adapter was `Parallels Display Adapter (WDDM)`, driver
`20.18.2641.57516`, using the D3D12 backend. The complete bounded gate was:

```powershell
.\eng\build-progpu-native-windows.ps1 `
  -Rid win-arm64 `
  -Compiler MSVC `
  -Generator Ninja `
  -BenchmarkProfile Smoke
```

Results:

- Both provider-resolved Dawn and wgpu-native renderer modules built with the
  ARM64 MSVC toolchain; all 11 native tests passed, including the MIL channel
  and Dawn ABI contracts.
- The live C++ sample rendered nine retained commands in five draw calls,
  uploaded 11,616 vertex bytes, completed a D3D12 readback, and emitted its PPM
  plus backend evidence file.
- The managed native-host sample lowered 16 source commands to 13 native
  commands and six draws, uploaded 27,464 vertex bytes plus 55,552 coverage
  bytes, and passed pre-render, post-render general-buffer, and readback-heap
  allocation probes before completing its readback.
- Two- and sixteen-positioned-glyph retained scenes passed exact native versus
  managed pixel parity with zero differing pixels and zero steady-frame
  managed allocations. The Parallels D3D12 profile uses a typed CPU R8 coverage
  atlas fallback in both implementations; normal adapters keep the shared GPU
  compute rasterizer. The 16-glyph native submission measured `0.5108 ms`
  versus `1.2558 ms` for the managed submission in the one-frame diagnostic.
- The 384-command mixed-picture native-only stress completed eight synchronized
  frames at `0.2721 ms/frame`. A separate live differential scene stayed within
  the independent-AA budget (maximum channel delta 2/255, zero pixels over
  3/255, mean absolute delta `0.0000622`). Solid/group opacity, external and
  masked images, semantic mixed scenes, semantic mask/effect chains, retained
  vector clips, blur/drop-shadow, Overlay and ColorDodge blending, and managed
  versus C++ text shaping also passed their declared parity contracts.
- The staged package contains both native renderer variants for `win-arm64`.

The affine-transform checkpoint at ProGPU commit `360a6f7e` was requalified
with the same complete gate. ARM64 MSVC compiled the new matrix resource,
visual-transform, and nested transform-scope paths; all 11 CTests passed,
including transformed semantic-state/bounds fixtures, null no-op scopes, and
transactional failure cases. Live retained rendering/readback, package staging,
the managed-host allocation probes, effect/vector/text cases, and isolated
stress/differential processes all completed. In that follow-up run the native
384-command stress measured `0.1244 ms/frame`; the bounded differential again
had maximum channel delta 2/255 and zero pixels over 3/255. Timing is diagnostic
for this VM, while the pass/fail contracts and recorded adapter identity are
the qualification evidence.

The solid-pen/line checkpoint at ProGPU commit `dadb26a5` was then qualified
with that same complete command from a clean Windows checkout. Both native
modules linked for ARM64, all 11 CTests passed (including cap mapping,
cap-aware transformed bounds, line metrics, Dawn ABI, and transactional
animated/dashed-pen rejection), and the live C++ and managed D3D12 samples
again completed their readback and post-build allocation probes. The bounded
mixed-picture differential remained at maximum delta 2/255 with zero pixels
over 3/255; external/masked images, semantic scenes, mask/effect chains,
vector clips, text shaping, Overlay, and ColorDodge remained inside their
declared contracts. The run reached its normal completion marker and staged
both `progpu_native.dll` and `progpu_native_dawn.dll` in the `win-arm64`
package runtime. One eight-frame synchronized managed-picture native profile
measured `0.6335 ms/frame`; VM timings remain diagnostic rather than release
thresholds.

The retained-dash checkpoint at ProGPU commit `fca6c7a2` was qualified from a
clean Windows checkout with a focused integration gate over the renderer
already covered by the immediately preceding full matrix. ARM64 MSVC rebuilt
both native modules and the MIL executable; the Windows MIL and Dawn contract
tests passed 2/2. The project-reference package consumer then built with zero
warnings and ran live on D3D12, compiling its retained dashed line through both
wgpu-native and Dawn MIL channels before completing renderer readback (`draws=1`,
16,384 pixels). This gate specifically covers variable packet decoding,
thickness-relative intervals/offset, dash-cap semantic-stroke lowering,
transactional validation, both exported ABIs, and managed package production.

The rectangle-pen checkpoint at ProGPU commit `89f0a838` passed the same
focused Windows ARM64 integration lane. MSVC rebuilt both native modules and
the MIL test executable, the MIL/Dawn contracts passed 2/2, and the updated
project-reference package consumer built with zero warnings. Its live D3D12
run compiled a dashed line plus a dashed fill-and-stroke rectangle through both
native MIL exports before completing readback (`draws=1`, 16,384 pixels). The
Windows checkout was clean at the exact qualified commit.

The solid-ellipse checkpoint at ProGPU commit `f24d715f` passed that focused
lane from a clean Windows checkout. The MIL and Dawn contracts passed 2/2, and
the package consumer compiled a fill-plus-solid-stroke ellipse through both
native MIL exports before completing live D3D12 readback (`draws=1`, 16,384
pixels). The same gate isolated and corrected Windows instance creation:
managed `WgpuContext` now supplies wgpu-native's typed D3D12 instance extension
instead of a backend-unspecified descriptor. WebGPU-init-only, render-only, and
combined MIL/render processes all completed with exit code zero under the
Parallels SYSTEM integration session. The C++ retained renderer independently
selected the Parallels D3D12 adapter, rendered nine commands in five draws,
uploaded 11,616 vertex bytes, and completed readback.

The uniform rounded-rectangle pen checkpoint at exact ProGPU commit
`84cdcead` passed the same focused Windows ARM64 lane from a clean checkout.
ARM64 MSVC rebuilt both native modules and the MIL executable, and the MIL/Dawn
contracts passed 2/2. The project-reference package consumer built with zero
warnings, compiled a pen-only rounded rectangle through both native MIL
exports, and then completed managed D3D12 rendering/readback (`draws=1`,
16,384 pixels). The independent C++ retained renderer also selected the
Parallels D3D12 adapter and completed its nine-command, five-draw,
11,616-vertex-byte readback gate.

The retained line-geometry checkpoint at exact ProGPU commit `5c4757c0`
passed the focused Windows ARM64 lane from a clean checkout. ARM64 MSVC rebuilt
both native modules and the MIL executable, and the MIL/Dawn contracts passed
2/2. The zero-warning project-reference package consumer compiled a typed,
transformed `LineGeometry`/`DrawGeometry` pen through both native MIL exports
before completing live D3D12 rendering/readback (`draws=1`, 16,384 pixels).
The independent C++ retained renderer again completed its nine-command,
five-draw, 11,616-vertex-byte D3D12 readback gate.

The retained rectangle/ellipse-geometry checkpoint was qualified at exact
ProGPU commit `a1c0fd81` (feature implementation `bc6b5029`) from a clean
Windows checkout. ARM64 MSVC rebuilt `progpu_native.dll`,
`progpu_native_dawn.dll`, and the MIL executable; the MIL/Dawn contracts passed
2/2. The zero-warning project-reference package consumer compiled transformed
retained line, uniform rounded-rectangle, and ellipse geometry resources
through both native exports, then completed live Parallels D3D12
rendering/readback (`draws=1`, 16,384 pixels). The independent C++ retained
renderer again completed nine commands, five draws, 11,616 uploaded vertex
bytes, and readback. The gate required the consumer's app-local native DLL
hashes to match the freshly rebuilt modules after detecting and replacing an
older incremental-build artifact.

The retained general-path and endpoint-arc checkpoint was qualified at exact
native implementation commit `51550b6e` from a clean Windows checkout with
the complete ARM64 MSVC gate. Strict `/W4 /WX` first exposed an implicit
fill-rule enum conversion in the semantic path resource; the implementation
now keeps the C ABI field type explicit. Both native modules linked, all 11
CTest contracts passed, and the live C++ and managed samples completed D3D12
rendering/readback on the Parallels adapter. The managed sample lowered 16
source commands to 13 native commands and six draws, uploading 27,464 vertex
bytes plus 55,552 coverage bytes. The eight-frame native managed-picture
stress measured `0.1235 ms/frame`; the bounded differential had maximum delta
2/255, zero pixels above 3/255, and mean absolute delta `0.0000622`. Retained
path-atlas qualification rasterized 49 paths with 4,112 path-upload bytes and
727,552 coverage-staging bytes, remaining inside its independent-edge budget
(maximum delta 46/255, 1,048 pixels over tolerance, mean `0.0171`). Exact
Overlay and ColorDodge cases, image/mask/effect chains, vector/text contracts,
and final `win-arm64` package staging also passed.

Package-consumer commit `a11ad9fd` then exercised that exact staged native
implementation rather than a source-only fixture. Its app-local SHA-256 hashes
matched the newly staged `progpu_native.dll` and `progpu_native_dawn.dll`; the
consumer built with zero warnings, compiled a transformed retained path with a
quadratic segment and rotated WPF endpoint arc through both MIL exports,
installed the wgpu-native semantic stream, rendered it live on D3D12, and read
back 16,384 pixels. The installed scene reported 15 semantic resources and two
draw calls before the independent renderer smoke completed.

The geometry-group/combined-geometry checkpoint at exact ProGPU commit
`41af1e66` passed the complete Windows ARM64 MSVC gate from a clean checkout.
Both native modules rebuilt under strict warnings and all 11 fresh CTests
passed, including MIL geometry-DAG, boolean-program, null-operand, cycle,
deletion, and transactional rollback cases plus the Dawn ABI contract. The
independent C++ and managed samples completed live D3D12 rendering/readback;
the managed sample again produced 13 native commands, six draws, 27,464 vertex
bytes, and 55,552 coverage bytes with successful allocation probes. The
bounded differential remained at maximum delta 2/255 with zero pixels above
3/255 and mean `0.0000622`; vector, image, mask/effect, text, Overlay, and
ColorDodge contracts also passed, and the package runtime was restaged.

The updated zero-warning package consumer then verified exact SHA-256 matches
for both freshly staged DLLs, compiled two retained paths plus an EvenOdd
`GeometryGroup` and an Exclude `CombinedGeometry` through both MIL exports,
installed the wgpu-native semantic stream, and completed live D3D12 readback.
The installed graph reported 17 semantic resources and two draw calls before
the independent immediate renderer smoke completed its 16,384-pixel readback.

The retained line-path stroke checkpoint at exact ProGPU commit `70c88279`
passed the complete Windows ARM64 MSVC gate from a clean checkout. Both native
modules rebuilt under `/W4 /WX`; all 11 CTests passed, including closed/open
stroke topology, geometry-gap dash caps, affine/dash/pen propagation,
unsupported smooth/curved strokes, and closed-gap dash-seam fail-closed cases.
The independent C++ and managed hosts completed live Parallels D3D12
rendering/readback and allocation probes. The bounded differential remained at
maximum delta 2/255 with zero pixels above 3/255 and mean `0.0000622`; path
atlas, image/mask/effect, semantic-layer, text, Overlay, and ColorDodge
contracts all passed. The synchronized eight-frame native diagnostic measured
`0.0902 ms/frame` on this VM.

The updated zero-warning package consumer then copied the exact staged
`progpu_native.dll` and `progpu_native_dawn.dll`; each app-local SHA-256 matched
its staged source. It compiled the existing path/group/combined graph plus a
transformed dashed closed `PathGeometry` whose first edge is a WPF geometry gap
through both MIL exports, installed the wgpu-native stream, and completed live
D3D12 readback. The installed graph reported 18 semantic resources and three
draw calls before the independent immediate renderer completed its
16,384-pixel readback.

The fixed-child `GeometryGroup` checkpoint at exact ProGPU commit `18ccb55c`
passed the complete Windows ARM64 MSVC gate. Both wgpu-native and
provider-resolved Dawn modules rebuilt under `/W4 /WX`; all 11 CTests passed,
including exact transformed rectangle, ellipse, non-uniform rounded-rectangle,
and empty line-child group contours. The independent C++ and managed hosts
completed live D3D12 rendering/readback and allocation probes. The bounded
mixed differential remained at maximum delta 2/255, zero pixels above 3/255,
and mean `0.0000622`; external and masked images, semantic mask/effect layers,
path atlas, blur/drop-shadow, text, Overlay, and ColorDodge contracts also
passed before fresh `win-arm64` package staging.

The zero-warning project-reference package consumer copied both staged DLLs
app-locally and verified identical source/destination SHA-256 values:
`73fcc3871408d4642d6ace3817b30c36194e9938c36dd60f8e4d09325ec4495f`
for `progpu_native.dll` and
`709e59f97f484dc74dd5693f207dbbe96ba568d1f692b93c6df186e5d535c8c8`
for `progpu_native_dawn.dll`. Both MIL exports compiled the group containing
transformed rounded-rectangle and ellipse children. The installed wgpu-native
stream then completed live D3D12 readback with 18 semantic resources, three
draws, and 16,384 pixels before the independent immediate renderer smoke.

The shared fixed-operand `CombinedGeometry` checkpoint at exact ProGPU commit
`7d0fad61` passed the complete Windows ARM64 MSVC gate. The refactored shallow
fill lowerer compiled into both native modules under `/W4 /WX`; all 11 CTests
passed, including transformed fixed boolean leaves, non-uniform rounded
rectangles, preserved identity-local path operands, and the Dawn contract. Live
C++ and managed D3D12 rendering/readback, allocation probes, and the complete
bounded differential matrix passed. The mixed differential remained at maximum
delta 2/255, zero pixels above 3/255, and mean `0.0000622`; path-atlas,
image/mask/effect, text, Overlay, and ColorDodge results stayed within their
established contracts. The synchronized eight-frame native diagnostic measured
`0.1618 ms/frame` on this VM before fresh package staging.

The zero-warning project-reference consumer then verified exact app-local
matches for the staged modules: SHA-256
`288438736839fc4e673fe4dbd7a714eda8158df181c694d0efd3d92dadf1e984`
for `progpu_native.dll` and
`31b0fe54964b8163b4a1d132359e89de58367b31550020d924c681f6cc4732b6`
for `progpu_native_dawn.dll`. Both MIL exports compiled transformed fixed
rounded-rectangle and ellipse boolean operands, and the installed wgpu-native
stream completed live D3D12 readback with 18 semantic resources, three draws,
and 16,384 pixels.

The affine path-leaf checkpoint at exact ProGPU commit `9634af73` passed the
complete Windows ARM64 MSVC gate. Both modules rebuilt under `/W4 /WX`, and all
11 CTests passed with exact transformed line, quadratic, cubic, implicit fill
closure, conservative bounds, legacy identity-path operands, and transformed-
arc fail-closed coverage. Live C++/managed D3D12 rendering and readback,
allocation probes, text, path atlas, image/mask/effect, Overlay, ColorDodge, and
the bounded differential matrix all passed. The mixed differential remained at
maximum delta 2/255, zero pixels above 3/255, and mean `0.0000622`; the
synchronized eight-frame native diagnostic measured `0.0925 ms/frame` before
fresh package staging.

The zero-warning project-reference consumer verified identical staged/app-local
SHA-256 values:
`6493681ddc832c58b5d549a22cae070839268a7f66d41aae70c0c9450ba59f3f`
for `progpu_native.dll` and
`28a82155eedaa4c1b3c73b982f3c5e8f4e475687eef300946be8d6f1158d4379`
for `progpu_native_dawn.dll`. Both MIL exports compiled a transformed
line/quadratic/cubic path leaf in the retained group, and the installed
wgpu-native stream completed live D3D12 readback with 18 semantic resources,
three draws, and 16,384 pixels.

The recursive `GeometryGroup` checkpoint at exact native implementation commit
`e0281b69` passed the complete Windows ARM64 MSVC gate. Both modules rebuilt
under `/W4 /WX`, all 11 CTests passed, and the MIL contract covered nested group
transform composition, outer-fill-rule ownership, groups as combined-geometry
boolean leaves, cycles, rollback, and transformed-arc fail-closed behavior. Live
C++/managed D3D12 rendering and readback, allocation probes, text, path atlas,
image/mask/effect, Overlay, ColorDodge, and the bounded differential matrix all
passed. The mixed differential remained at maximum delta 2/255, zero pixels
above 3/255, and mean `0.0000622`; the synchronized eight-frame native
diagnostic measured `0.1562 ms/frame` before fresh package staging.

Package-consumer checkpoint `14603fa2` then verified identical staged/app-local
SHA-256 values:
`e6e71dbca0b0e846de332c7bbade0362a9d19f2e4d16eef93aa73dce8640352e`
for `progpu_native.dll` and
`5a0bae5f610cfecf5a945b850a91251c725f97d7592903c78e9e2d24a5fcd79d`
for `progpu_native_dawn.dll`. Its 40-command, 17-resource seed compiled a
recursively transformed affine path group through both MIL exports. The
installed wgpu-native stream completed live Parallels D3D12 readback with 18
semantic resources, three draws, and 16,384 pixels; the separate immediate
renderer smoke also passed.

The recursive `CombinedGeometry` checkpoint at exact ProGPU commit `8bf9a0c5`
(native implementation `6326cdf2`) passed the complete Windows ARM64 MSVC
gate. Both modules rebuilt under `/W4 /WX`, all 11 CTests passed, and the MIL
fixture verified a five-node postfix boolean tree with exact segment offsets,
leaf fill rules, nested group/combined transform composition, operation order,
conservative bounds, and rollback. Live C++/managed D3D12 rendering/readback,
allocation probes, text, path atlas, image/mask/effect, Overlay, ColorDodge,
and the bounded differential matrix passed. The mixed differential remained at
maximum delta 2/255, zero pixels above 3/255, and mean `0.0000622`; the noisy
eight-frame VM diagnostic measured `0.6330 ms/frame` before package staging.

The zero-warning project-reference consumer verified exact staged/app-local
SHA-256 matches:
`6ac27898f1f067854ac3e79bf415ecd41f9f79c3208a0d45618e0cf47047520d`
for `progpu_native.dll` and
`d98b7f7dd3a0315c5420ca5ca63f85354e9daec5bb8ede4468e097fd191dd906`
for `progpu_native_dawn.dll`. Its 42-command, 18-resource channel seed compiled
a nested group/combined boolean tree through both MIL exports. The installed
wgpu-native stream completed live Parallels D3D12 readback with 18 semantic
resources, three draws, and 16,384 pixels; the immediate renderer smoke also
passed.

The affine-arc recursive-geometry checkpoint at exact ProGPU commit `b9011c23`
passed the complete Windows ARM64 MSVC gate on 2026-08-25. Both native modules
rebuilt under `/W4 /WX`; all 11 CTests passed, including reflected/sheared arc
sample equivalence, sweep reversal, singular-transform rejection, exact
translation record preservation, recursive group/boolean arc leaves, outer
fill ownership, and transactional rollback. The independent C++ and managed
D3D12 samples, allocation probes, text, images, masks/effects, Overlay,
ColorDodge, and the bounded differential matrix all passed. The mixed
differential remained at maximum delta 2/255, zero pixels above 3/255, and mean
`0.0000622`. The 49-path atlas retained its existing maximum delta 46/255,
1,048 pixels over tolerance, and mean `0.017107928`; this is an unchanged
historical independent-edge-AA contract rather than a regression. VM timing was
noisy during this run and is intentionally not used as qualification evidence.

The zero-warning project-reference package consumer copied the exact staged
DLLs and exercised both the focused recursive-group arc scene and the broader
recursive group/boolean scene through the wgpu-native and Dawn MIL exports.
They completed live D3D12 readback with 18 semantic resources and three draws;
the focused scene staged 40,960 coverage bytes and the broader scene 41,472.
The staged module SHA-256 values were
`a94dab843f3f253e004e128e6ff9fc4160676691cc467cb0288a6071b0f37025`
for `progpu_native.dll` and
`a1f0c7067bd442b989708f4e7243927074a75e9419f8013d4cdef5d565b59807`
for `progpu_native_dawn.dll`.

At exact safety implementation commit `ef6091e9`, the close-translated-
equivalent EvenOdd diagnostic was converted from a device-removal path into a
deterministic fail-closed contract. The resource update remains transactional,
while semantic scene compilation returns `unsupported_command` before WebGPU
context/device creation. The strict Windows ARM64 build, both modules, all 11
CTests, the independent C++/managed D3D12 readbacks, allocation probes, and the
full differential matrix then passed again. Supported focused and broad package
scenes retained 18 resources, three draws, and 40,960/41,472 coverage bytes;
the guarded diagnostic exited at `NativeMilChannel.CompileScene` without GPU
submission. Fresh staged SHA-256 values were
`5b403c179cc0aa9ae9395b2e486aa36d8574fce510b560acf4c744daba6a0a9b`
for `progpu_native.dll` and
`5a39d04f8dcccae29c093e63dd5e3d5c2effa3b4b68418763afb8f90ee2af856`
for `progpu_native_dawn.dll`.

Translation preservation, bounded XOR leaf execution, row/bounds rejection,
sample-grid changes, and curve approximations had not eliminated the backend
failure; all approximation experiments remain removed and analytic 8x8
rasterization retained. The guard compares typed segment kinds, arc metadata,
translated control/end/center points, invariant radii, and positive overlapping
bounds. Non-overlapping equivalents and non-equivalent mixed leaves keep the
normal GPU path. Exact rendering for the guarded overlap remains an open parity
item; fail-closed behavior is the supported interim contract.

The isolated curved-stroke implementation at `e0a9d15f`, with the MSVC
portability correction at `42e05f29`, first passed the focused Windows ARM64
lane. The joined/closed contour implementation at `38245edd` then added exact
mixed line/quadratic/cubic/arc segment composition and native tangent joins;
package checkpoint `3816050b` added a closed joined curve to the retained seed.
At that exact checkpoint both wgpu-native and Dawn rebuilt under MSVC `/W4
/WX`, all 11 CTests passed, and the complete bounded Windows D3D12 smoke profile
passed: independent native and managed readback, zero-allocation probes,
retained masks/effects, text shaping, path atlas, images, Overlay, ColorDodge,
and the declared differential scenes. The zero-warning project-reference
package consumer compiled the 46-command, 20-channel-resource seed through both
MIL exports and completed live D3D12 readback with 20 semantic resources, three
draws, and 41,472 coverage bytes. Exact staged SHA-256 values were
`1c0e48225057db64eaf97eab5ba239b8be5c365525bc4b68bba58d5f906a7926`
for `progpu_native.dll` and
`efaad18f8ee89a1c53f0dc612e99371f9a3d24cbcfdf66b3129af5875ef1bb74`
for `progpu_native_dawn.dll`.

The curved-cap implementation at `4f5dcc20` then composed Square, Round, and
Triangle open-contour caps as reusable native path-cap primitives with exact
curve endpoint tangents and affine state. ARM64 MSVC rebuilt both native
modules under `/W4 /WX`; the MIL and Dawn contract tests passed. Package
checkpoint `48bea705` applied Round/Triangle caps to the retained analytic arc,
compiled the unchanged 46-command/20-channel-resource seed through both MIL
exports, and completed live D3D12 readback with 20 semantic resources, three
draws, and 41,472 coverage bytes. Exact focused-build SHA-256 values were
`2afaa42721aa4ca9b6faa714755117d518abe23d47878613d6ea585b2dbdb164`
for `progpu_native.dll` and
`e64c515b74c131ae8e7b17eb86e4c301cbb4f07bc58d3bdb5b7644b441106309`
for `progpu_native_dawn.dll`. The complete differential matrix remains
qualified at `3816050b`; this cap checkpoint used the focused strict-build,
contract, and live-package lane.

The smooth-join implementation at `1431509c` then replaced the former broad
rejection with per-segment typed state. The WPF source trace established that
`SegSmoothJoin` is read after widening its segment and passed as `fRound` to the
next `DoCorner`, so the native compiler emits a Round path join regardless of
the pen's default join only at that endpoint. Solid mixed and line-only
contours use this exact geometry composition; dashed smooth joins still fail
closed. Strict ARM64 MSVC rebuilt both modules and the MIL/Dawn contracts
passed. Package checkpoint `6868d909` marked the line-to-quadratic corner in the
closed mixed-curve seed as smooth; both exports compiled it and live D3D12
readback retained 20 semantic resources, three draws, and 41,472 coverage
bytes. Exact focused-build SHA-256 values were
`b932861929989d6f847df95c0562a5629849507bace4e44dcdcc410b11e76237`
for `progpu_native.dll` and
`20806036f956a84b0b217329183f85ca8f130c7c4aba6fc4394705d6df6170e8`
for `progpu_native_dawn.dll`.

The exact rectangle-clip implementation at `37f496f2` added canonical
`MILCMD_PUSH_CLIP` production and native target-space nested intersections.
Local native suites and the typed managed builder test passed. ARM64 MSVC then
rebuilt both native modules under `/W4 /WX`; the MIL and Dawn contract tests
passed. Package checkpoint `d22a94c9` pushed a retained rectangle clip around
the existing mixed scene. Its 48 commands and 21 channel resources compiled
through both exports, and live D3D12 readback completed with 21 semantic
resources, three draws, and 41,472 coverage bytes. Exact focused-build SHA-256
values were
`014999b22d86f2192ea56697dde3d5bc47a88991831a39636a4db26c29fccb69`
for `progpu_native.dll` and
`ba780db29da7fbedd7834768180b9d9976775532f3cf0c50c4d52cc94b56d0b7`
for `progpu_native_dawn.dll`.

The exact geometry-clip implementation at `66d5f74b` then connected retained
fixed, path, group, and combined geometry to the semantic vector-mask resource.
Nested path/group/combined clips preserve analytic arcs, outer group fill
ownership, recursive boolean programs, and push-time affine state; mask arenas
truncate by saved prefix count after pop instead of copying segment vectors.
The same checkpoint replaced fixed ellipse and rounded-rectangle cubic circle
approximations with analytic arc segments. All ten local native tests passed.
Windows ARM64 MSVC rebuilt both native modules under `/W4 /WX`, and all 11
native/Dawn CTests passed on the Parallels VM. Package checkpoint `a2502e36`
added the retained arc path as a second clip around the mixed MIL scene. The
unchanged 48-command/21-channel-resource seed compiled through both exports;
live D3D12 readback completed with 23 semantic resources, three draws, and
41,472 coverage bytes. The complete Windows sample, allocation/readback,
differential, text, path, image, mask, effect, and blend smoke matrix passed.
Exact staged SHA-256 values were
`43e452fb73b6e103bc81ab56836c3e68d43a30b8bec7c8931df73ec8f5d05672`
for `progpu_native.dll` and
`9ca1765e660c8cc0d69c8c3eccba3d6971b9c4a05b04a5fc33975dee26e9c938`
for `progpu_native_dawn.dll`.

The dashed closed-gap implementation at `c12e6d60` removed the obsolete seam
rejection for line-only closed figures. The decoder already rotates each open
stroked run to the first edge after its geometry gap, so a run that crosses the
figure start retains one ordered polyline, one dash phase, and DashCap at both
gap boundaries without flattening or splitting. Native tests now assert the
wrapped point order, dash offset, interval count, and cap state. All ten local
native tests passed; strict Windows ARM64 rebuilt both modules and the MIL/Dawn
contract tests passed. Package checkpoint `0048f430` moved the existing line
geometry gap to force a start-crossing dashed run. Both MIL exports compiled
the 48-command/21-channel-resource seed, and live D3D12 readback retained 23
semantic resources, three draws, and 41,472 coverage bytes. Exact focused
SHA-256 values were
`39a28937c25d977310597efb3c6e7f0ed9f077cd8617b2f95582d3cca58e0161`
for `progpu_native.dll` and
`daefb160737962ec81fb78238b44508a7c6a7235c8daf1bc129b0f5df2dda14a`
for `progpu_native_dawn.dll`.

The singular-affine implementation at `f244dc2d` then closed the remaining
zero-determinant fill, stroke, and clip ambiguity. WPF's
`CShapeBase::GetArea` multiplies rectangle area by the absolute 2D determinant
and treats a degenerate general transform as no scannable workspace, so the
native MIL compiler now lowers singularly transformed fixed, path, group, and
combined geometry to exact empty coverage instead of trying to invert or
factor an arc basis. Direct line strokes follow the same rule, and a singular
geometry clip becomes an exact empty clip rather than bounds coverage. All ten
local native tests passed. Strict Windows ARM64 MSVC rebuilt both modules under
`/W4 /WX`, and all 11 native/Dawn CTests passed on the Parallels VM. Package
checkpoint `7b91b21f` added a typed singular `MatrixTransform` scope around
direct and retained draw commands. Both MIL exports compiled its 50-command,
22-channel-resource seed; live D3D12 readback retained 24 semantic resources,
three visible draws, and 41,472 coverage bytes. Exact staged SHA-256 values were
`1dec50b6aef18b22f894739a9bff477a31bd0751cae1baabd9d3efc562212b65`
for `progpu_native.dll` and
`83ff9ae3133fbe9ecd789202f10ea5dfc483528f207c8ef3d34af05e45c038d9`
for `progpu_native_dawn.dll`.

The degenerate point-cap implementation at `957adfdd` then matched WPF's
unstarted-widener behavior without manufacturing a line direction from object
shape or flattening. Immediate zero-length lines and wholly degenerate open
path contours use a horizontal shape-space tangent and compose their typed
non-Flat cap halves; wholly degenerate closed contours force Round/Round and
therefore form one point disk. The hot path uses two fixed stack arrays, and
nonempty dashed zero-length strokes remain fail closed until their initial dash
phase is represented exactly. All ten local native tests passed. Strict
Windows ARM64 MSVC rebuilt both modules under `/W4 /WX`, and all 11 native/Dawn
CTests passed on the Parallels VM. Package checkpoint `9d3d0033` added immediate
and retained open/closed degenerate strokes. Both MIL exports compiled its
52-command, 23-channel-resource seed; live D3D12 readback retained 27 semantic
resources, three draws, and 41,472 coverage bytes. Exact staged SHA-256 values
were
`6afa15e6fff5a41e274674be9678d80f6bb88085078a0685eb3673d5e5467f4e`
for `progpu_native.dll` and
`560c4691baa714d366cc4817c853e41ae8d17a6140d74f06b3db5a505636d666`
for `progpu_native_dawn.dll`.

The degenerate dash-phase implementation at `70b738b7` then applied WPF's
`CDashSequence::Initialize` parity rule to those point caps. Finite nonzero
patterns normalize positive or negative offsets over the effective even-length
cycle, repeat odd source lists, keep exact-boundary offsets on the preceding
interval, emit the cap pair only when that interval is a dash, and emit no draw
when it is a gap. Zero-total-length cycles remain fail closed. All ten local
native tests passed. Strict Windows ARM64 MSVC rebuilt both modules under
`/W4 /WX`, and all 11 native/Dawn CTests passed on the Parallels VM. Package
checkpoint `61ed465d` moved the retained degenerate path onto a boundary-offset
dash resource and pen. Both MIL exports compiled its 56-command,
25-channel-resource seed; live D3D12 readback retained 27 semantic resources,
three draws, and 41,472 coverage bytes. Exact staged SHA-256 values were
`53d589f6580afd495e2bcb98d64c23c7acb1b450baf60027a5b7b371618774c3`
for `progpu_native.dll` and
`81a9450fc3af12677152fdb8777ab1ba346c1f5017e425858d476bd6e9076feb`
for `progpu_native_dawn.dll`.

The degenerate ellipse implementation at `bbb4b2c2` then traced
`CFigureData::InitAsEllipse`, whose four cubic segment types all carry
`SmoothJoin`. A zero X or Y radius therefore lowers exactly to one line with
Round/Round ends, while two zero radii reuse the point-disk cap pair. Degenerate
fills remain empty, immediate and retained `EllipseGeometry` paths share the
same lowering, and local affine state stays on the reusable geometry primitive.
Nonempty dashes on a one-axis ellipse remain fail closed with the broader curve
dash gate. All ten local native tests passed. Strict Windows ARM64 MSVC rebuilt
both modules under `/W4 /WX`, and all 11 native/Dawn CTests passed on the
Parallels VM. Package checkpoint `e909fd60` added immediate and retained
one-axis ellipses. Both MIL exports compiled its 58-command,
26-channel-resource seed; live D3D12 readback retained 29 semantic resources,
three draws, and 41,472 coverage bytes. Exact staged SHA-256 values were
`8e235e440a980fcdf63c4770c33a2afbcd9f92a06667671daa33c7406e50457a`
for `progpu_native.dll` and
`2ecd3a808e9ee65d50cae7637e365d00820febb02a63067849ace0b73d54df58`
for `progpu_native_dawn.dll`.

The degenerate rectangle implementation at `762887cb` then followed
`CRectangle::WidenToShape` and `CPlainPen::Get90DegreeBevelOffset` rather than
asking the generic stroke rasterizer to interpret coincident closed edges.
Because WPF omits the inner boundary unless both original dimensions exceed
the full pen width, every zero-area rectangle is exactly one outer figure.
Sharp Miter and Bevel cases lower to the same four- or eight-edge vector path;
Round joins and source-rounded rectangles lower to four analytic elliptical
quarter arcs with WPF's independent dimension clamps. Degenerate fills remain
empty, local affine state remains typed on the path, and nonempty dashed
collapses still fail closed. Immediate and retained fixtures cover line and
point collapses, all three public joins, rounded-source radii, transformed
bounds, and the dash boundary. All ten local native tests passed. Strict
Windows ARM64 MSVC rebuilt both modules under `/W4 /WX`, and all 11 native/Dawn
CTests passed on the Parallels VM. Package checkpoint `557c67fb` added immediate
Round and rounded collapses plus retained transformed rectangle geometry. Both
MIL exports compiled its 62-command, 28-channel-resource seed; live D3D12
readback retained 32 semantic resources, issued six draws, and staged 61,440
coverage bytes. Exact staged SHA-256 values were
`35610b8e6e6250d8d150e4a855e52a306f28af12dde286b41822baf5d5bab3eb`
for `progpu_native.dll` and
`7f3cf20154beb9c305de9b2477fbd6cb967292da61405afb35b2f46f936fa19a`
for `progpu_native_dawn.dll`.

The non-uniform rounded-rectangle implementation at `e17acda6` then removed
the single-radius analytic-primitive restriction for positive independent X/Y
radii. Immediate and retained rectangles construct the same eight-segment
typed contour: four exact elliptical quarter arcs, four connecting lines, and
the `SmoothJoin` bit on every incoming WPF segment. Fill uses the shared vector
path batch, while solid stroke reuses ProGPU's connected arc/line geometry and
emits eight native Round joins. Geometry-local affine state remains on both
resources and the wgpu-native/Dawn scene stream is identical. Nonempty curved
dashes and asymmetric cases with either radius zero remain fail closed. All ten
local native tests passed. Strict Windows ARM64 MSVC rebuilt both modules under
`/W4 /WX`, and all 11 native/Dawn CTests passed on the Parallels VM. Package
checkpoint `f7fef044` made the immediate package draw non-uniform and changed
the retained `RectangleGeometry` used directly and recursively to independent
radii. Both MIL exports compiled the unchanged 62-command,
28-channel-resource seed; live D3D12 readback retained 34 semantic resources,
issued ten draws, and staged 78,848 coverage bytes. Exact staged SHA-256 values
were
`01dedafe1c059b043a422385f8d04085235d0f0b526be382fc8f3f97d2eb6641`
for `progpu_native.dll` and
`bcc551bf815c18ffb601d517f2c10be702fdf1e0b86a11cdd6b39b95c02b10a9`
for `progpu_native_dawn.dll`.

The zero-axis rounded-rectangle checkpoint then implemented WPF's earlier
`CShape::AddRoundedRectangle` equivalence rule at native commit `9a615714`.
For positive width and height, either zero radius now lowers immediate and
retained rounded-rectangle records to the exact sharp rectangle analytic fill
and closed-polyline stroke while retaining the rounded-rectangle metric.
Degenerate zero-axis asymmetric rectangles continue to fail closed because
they enter WPF's general widener rather than the optimized `CRectangle` path.
All ten local native tests passed. Strict Windows ARM64 MSVC rebuilt both
native modules under `/W4 /WX`, and all 11 native/Dawn CTests passed in the
Parallels VM. Package checkpoint `6a4f9f90` compiled an immediate zero-axis
draw and retained zero-axis `RectangleGeometry` through both MIL exports in a
64-command, 29-channel-resource seed. The project-reference consumer built
with zero warnings; live D3D12 readback retained 38 semantic resources, issued
11 draws, and staged 78,848 coverage bytes. Exact qualified SHA-256 values were
`4c773f255b27ef00990ca52b89e428750a4108289de60ba5a50412b19c354d2f`
for `progpu_native.dll` and
`9b7434a0d2bea32861f2b3018078cff8dd183271da4ebffb9657aa4282b83476`
for `progpu_native_dawn.dll`.

The canonical static-transform implementation at `f6f82b91` then added all
remaining two-dimensional static resource packets: Translate, Scale, Skew,
Rotate, and ordered TransformGroup. Leaf transforms follow WPF's float matrix
evaluation, including center translation and modulo-360 angle handling; groups
remain retained dependency graphs and re-resolve current child state for visual,
drawing-scope, geometry, group, combined-geometry, and clip consumers. Native
fixtures cover meaningful ordered composition, nested groups, child updates,
animation rollback, cycle rejection, and referenced-child deletion rejection.
All eight locally configured native suites passed. Strict Windows ARM64 MSVC
rebuilt both native modules under `/W4 /WX`, and all 11 native/Dawn CTests
passed in the Parallels VM. Package checkpoint `8bc860e4` exercised all five
public managed builder APIs through both MIL exports in a 74-command,
34-channel-resource seed. Its identity-equivalent group preserved the prior
live D3D12 contract exactly: 38 semantic resources, 11 draws, and 78,848
coverage bytes. The project-reference consumer built with zero warnings. Exact
qualified SHA-256 values were
`301561a6f02de5a392b042f763134720a9a4b3d29f47b379c1018fc31c429d9c`
for `progpu_native.dll` and
`c3a800ba100508178a0d9f5837b07f9c6428a2bb616b1bb0d6a4708d0529da06`
for `progpu_native_dawn.dll`.

Transform-animation implementation `04ae7747` then added canonical
`DoubleResource` and `MatrixResource` current-value packets and retained their
typed dependencies from every static transform family. Native resolution reads
the current referenced scalar/matrix on each scene compilation, preserves base
values only when the animation handle is zero, and propagates resource updates
through nested groups without transform-packet rewrites. Fixtures cover scalar
and matrix replacement, live updates, wrong resource types, rollback, and
referenced-animation deletion rejection. All eight locally configured native
suites passed. After merging the latest ProGPU `main`, strict Windows ARM64
MSVC rebuilt both complete native modules under `/W4 /WX`; all 11 native/Dawn
CTests passed in the Parallels VM. Package checkpoint `d07ab05d` exercised the
two new resource builders and animated transform handles through both exports
in a 78-command, 36-channel-resource seed. Its identity-equivalent current
values preserved live D3D12 output at 38 semantic resources, 11 draws, and
78,848 coverage bytes; the project-reference consumer built with zero warnings.
Exact qualified SHA-256 values were
`a903edec8bb58e314e2738d64f8246ccc7a9f83e2d0c33755f3855ff043c233e`
for `progpu_native.dll` and
`e19d905e42d5030bf2aded0182fa1c8eb9bfc27f9a974cc3aa4d21b6507d33b0`
for `progpu_native_dawn.dll`.

Native gradient implementation `1a937dbd` and managed packet-builder
checkpoint `5d3b96f0` then added retained linear/radial gradient resources,
`PointResource` current values, bounds-relative mapping, brush transforms,
anisotropic focal radial state, both interpolation modes, all three spread
modes, and stable out-of-range stop normalization. Native fixtures validate
live point/double updates, transform ordering, enum remapping, stop payloads,
wrong-type rejection, and dependency-protected deletion. All eight locally
configured native suites passed. Strict Windows ARM64 MSVC rebuilt both native
modules under `/W4 /WX`, and all 11 native/Dawn CTests passed in the Parallels
VM. The managed builder tests passed 6/6 and the project-reference package
consumer built with zero warnings.

Focused package gate `5db0910e` compiled one mixed solid/linear/radial scene
through both MIL exports using 15 commands and six channel resources, then
installed and rendered it on live D3D12. The retained renderer reported five
semantic resources, one batched draw, zero coverage-staging bytes, a valid
submission, and nonblack readback; the following direct render also read back
16,384 pixels. The broader unchanged 78-command/36-resource MIL seed passed
both export contracts. Its dense path/boolean live render remained noisy on
the documented Parallels adapter and was not used as gradient evidence. Exact
qualified SHA-256 values were
`84f9ff3fcc3b1030fba0150891a92d176ea63d5cca7641af97d7f57d36f0cb54`
for `progpu_native.dll` and
`3779ab39f5d324f666eccc2452d0a21caf5ac5c2bea8d9eee2acede9fe8c6bf5`
for `progpu_native_dawn.dll`.

GeometryDrawing implementation `43ef1cf5` and focused package gate
`64206983` then added the exact 24-byte resource-update and 16-byte nested draw
records to the C++ channel and managed packet builders. The native fixture
verifies shared geometry lowering, null-geometry no-op behavior, protected
dependencies, wrong-type rejection, and rollback. All ten local native CTests
passed, the managed canonical-builder filter passed 6/6, and the package
consumer built with zero warnings. After merging current ProGPU `main`, strict
Windows ARM64 MSVC rebuilt both native modules under `/W4 /WX`; all 11
native/Dawn CTests passed in the Parallels VM. The focused 15-command,
six-resource GeometryDrawing scene compiled through both MIL exports and
rendered on live D3D12 with three semantic resources, one batched draw, zero
coverage-staging bytes, a valid submission, nonblack retained readback, and
16,384 direct-render pixels. Exact qualified SHA-256 values were
`14636dca53dbecb0defd05a356642ac39cac9982d4ef918dc3d50e538cf99c3a`
for `progpu_native.dll` and
`5abd082989ae7df2b77cd727081f761d1211d5803d71cfd9102056f1a2d6034c`
for `progpu_native_dawn.dll`.

DrawingGroup implementation `49d448af` and focused package gate `85f55ab2`
then added canonical resource type `91` and command `0x8b`, including WPF's
52-byte command-view prefix (56 bytes when framed) followed by its ordered
child-handle payload. Groups retain typed child drawings, transform, exact
geometry clip, static opacity, and live `DoubleResource` opacity dependencies;
nested compilation reuses the same `DrawDrawing`/`DrawGeometry` lowering and
preserves the parent semantic scope. Native fixtures cover live opacity
updates, transformed clips and analytic bounds, protected dependencies,
cycles, wrong child types, and transactional rollback. Opacity masks,
guideline sets, effects/cache state, and nondefault edge, bitmap-scaling, or
ClearType options remain explicit compile-time unsupported states until their
native semantic resources are implemented.

All ten local native CTests passed, the managed canonical-builder filter passed
7/7, and the project-reference package consumer built with zero warnings.
Strict Windows ARM64 MSVC rebuilt both native modules under `/W4 /WX`, and all
11 native/Dawn CTests passed in the Parallels VM. The focused 23-command,
ten-channel-resource DrawingGroup scene compiled through both MIL exports and
rendered on live D3D12 with four semantic resources, one batched draw, zero
coverage-staging bytes, a valid submission, nonblack retained readback, and
16,384 direct-render pixels. Exact qualified SHA-256 values were
`d20b7d78eff8905c7d1130c12980bbe2bc02a70337cbb4461c1279562fe624da`
for `progpu_native.dll` and
`e8c7dce855f34877abe3c211a7970235444402a37bfd940f0a4afbfea5f1a6a2`
for `progpu_native_dawn.dll`.

ImageDrawing implementation `6d99ced4`, focused package gate `03acffe0`, and
expectation correction `46175bf3` then added canonical resource type `89` and
command `0x89` with its exact 48-byte command-view payload (52 bytes framed).
The retained drawing references canonical `TYPE_BITMAPSOURCE` handle `95`.
Because WPF's original `MilCmdBitmapSource` transports an in-process
`IWICBitmapSource*`, portable hosts bind copied straight-alpha RGBA8 pixels to
that handle through the typed
`progpu_native_mil_channel_set_bitmap_source_rgba8` sideband. No process
pointer enters the retained graph or semantic scene. The same compiled scene
image resource and draw command execute through wgpu-native and Dawn.

Native fixtures verify missing-binding failure, exact destination/source/bounds
state, copied upload bytes, resource generation, dependency-protected deletion,
and invalid stride/type rejection. All ten local native CTests passed, the
managed canonical-builder filter passed 8/8, and the project-reference package
consumer built with zero warnings. Strict Windows ARM64 MSVC rebuilt both
native modules under `/W4 /WX`, and all 11 native/Dawn CTests passed in the
Parallels VM. The focused 12-command, five-channel-resource ImageDrawing scene
compiled through both MIL exports and rendered on live D3D12 with two semantic
resources, one image draw, zero coverage-staging bytes, a valid submission,
nonblack retained readback, and 16,384 direct-render pixels. Exact qualified
SHA-256 values were
`d396e5bcc5b9093271878499fafabae9e0b1fb0e7db6fd9aac8379e14ea64749`
for `progpu_native.dll` and
`4fe6051479644bfe40019e5d45570f68c57aeaae5040096b2fc257fe60c405d5`
for `progpu_native_dawn.dll`. Rect animations, D3DImage/video sources,
incremental bitmap invalidation, and same-device external texture bindings
remain explicit follow-up work.

GlyphRun implementation `c8efc666`, transport optimization `6c762f2b`, focused
package gate `b21fd324`, and fixture correction `fa8d6a33` next added canonical
`MilCmdGlyphRunCreate` command `0x3a`, retained `GlyphRunDrawing` resource type
`88`/command `0x88`, and nested `DrawGlyphRun` command `0x49`. The canonical
glyph-create command retains its 76-byte fixed command view followed by glyph
indices, advances, optional X/Y offsets, and DWORD padding. Its embedded
`IDWriteFont*` field is always zero outside the source process. Portable hosts
instead bind copied SFNT/TTC bytes, face index, and bold/italic simulations to
the glyph-run handle through the typed
`progpu_native_mil_channel_set_glyph_run_font_sfnt` sideband. Identical font
payloads share one retained native byte buffer across glyph-run resources.

Scene compilation decodes TrueType outlines through `progpu_native_text`,
caches the resulting semantic outline resource by glyph-run handle and raster
size, and emits positioned glyphs through the shared semantic text command
consumed by wgpu-native and Dawn. Direct render-data glyph commands and
retained GlyphRunDrawing resources therefore share the same font decode,
positioning, style, transform, and renderer path. Missing font bindings,
invalid fonts/faces/styles, wrong resource types, deletion of referenced
glyphs/brushes, and malformed packets fail closed or roll back transactionally.

All ten local native CTests passed, the managed canonical-builder filter passed
10/10, and the project-reference package consumer built with zero warnings.
Strict Windows ARM64 MSVC rebuilt both modules under `/W4 /WX`; all 11
native/Dawn CTests passed in the Parallels VM. The focused 14-command,
six-channel-resource glyph scene compiled direct and retained glyph draws
through both MIL exports and rendered on live D3D12 with three semantic
resources, one batched draw, 13,312 coverage-staging bytes, a valid submission,
nonblack retained readback, and 16,384 direct-render pixels. Exact qualified
SHA-256 values were
`f75a6e979f52d5a606294cb1698c48efcb6a96b78e961f23820495af1697d510`
for `progpu_native.dll` and
`e95c7107f76ef1bb221b0784919fe5bd8f72ac8c004ef016db4794b8e7a5d399`
for `progpu_native_dawn.dll`.

This checkpoint intentionally supports solid foreground brushes, horizontal
TrueType `glyf` outlines, logical-pixel raster sizing, and static glyph state.
Sideways text, gradient/tile text brushes, CFF/CFF2 and variable/color/bitmap
glyphs, target-DPI-aware raster selection, text decorations, and incremental
font-resource registration remain explicit parity work.

DrawingImage implementation `6071925d` then added canonical resource type
`59` and command `0x71` with its exact 12-byte command view (16 bytes framed).
The canonical update retains only the referenced Drawing handle. Portable
hosts bind that drawing's exact local content bounds through
`progpu_native_mil_channel_set_drawing_image_bounds`; unlike WPF's original
in-process resource graph, those bounds are not present in the packet and are
required to map vector content into an ImageDrawing destination rectangle.
Missing or nonfinite bounds fail closed, while a null Drawing remains a
canonical no-op.

Scene compilation recursively reuses retained GeometryDrawing,
GlyphRunDrawing, ImageDrawing, and DrawingGroup lowering. It scales and
translates source bounds into the destination, intersects the destination with
the active clip, and protects both DrawingImage-to-Drawing and
ImageDrawing-to-DrawingImage dependencies. Axis-preserving destination clips
become scissors; rotated or sheared clips become exact four-edge semantic
vector masks rather than broadened rectangle bounds. Cycles and invalid image
source types fail closed transactionally.

All ten local native CTests passed, the canonical managed packet test passed,
and the project-reference package consumer built with zero warnings. Strict
Windows ARM64 MSVC rebuilt both modules under `/W4 /WX`; all 11 native/Dawn
CTests passed in the Parallels VM. The focused 19-command,
eight-channel-resource DrawingImage scene compiled through both MIL exports
and rendered on live D3D12 with four semantic resources, one batched draw,
zero coverage-staging bytes, a valid retained readback, and 16,384
direct-render pixels. Exact qualified SHA-256 values were
`85ef5bb9c18505b97f11bf40302a8d93c50d3bd13b7afbd412fac55b7ba67cf1`
for `progpu_native.dll` and
`bae571f2a8d3cf707c92919613c8a5bece2f6e462b19c9bcd6167cd0ea66bc2c`
for `progpu_native_dawn.dll`. DrawingImage used as an ImageBrush source,
animated destination rectangles, incremental source-bounds updates, and
effects/cache state remain explicit follow-up work.

Bitmap-scaling checkpoint `ebe966b6` next made DrawingGroup's canonical
`bitmapScalingMode` field executable for nested retained images, including
bitmap-backed ImageDrawing reached through DrawingImage or another group.
Unspecified inherits the parent scope, LowQuality/Linear selects shared linear
sampling, HighQuality/Fant selects ProGPU's Mitchell-Netravali cubic sampler,
and NearestNeighbor selects shared nearest sampling. The state is host-neutral
and is consumed identically by wgpu-native and Dawn.

The native fixture verifies nearest and cubic payload selection, and all ten
local CTests passed. Strict Windows ARM64 MSVC rebuilt both modules under `/W4
/WX`; all 11 native/Dawn CTests passed. The existing DrawingImage package
scene then passed both exports and live D3D12 retained/direct readback. Current
qualified SHA-256 values are
`812312ae4d91c30a363f801985d2f881a6aa528709331f0985279756a5337790`
for `progpu_native.dll` and
`8cf312ffadac52d7109239de3fee4f25e34358bcc963e6f33965799fe3d9f607`
for `progpu_native_dawn.dll`.

Static-guideline implementation `d4112930`, package gate `59851d8c`, and
validator correction `dab52e58` next added the exact uniform-offset subset of
WPF `GuidelineSet`. Canonical type `92` and command `0x8c` use the WPF
20-byte fixed view followed by X and Y double arrays. Static arrays are sorted
as WPF does. A DrawingGroup's handle at offset 36 selects the set for its
children; no handle inherits the parent scope and an explicitly empty set
disables snapping for that scope.

WPF only constructs an active snapping frame under a finite scale/translate
transform. Each static coordinate is transformed to device space with WPF's
float evaluation, and its device offset is computed with the native
`CFloatFPU::OffsetToRounded` tie rule (half coordinates choose the numerically
larger integer). One coordinate per axis is therefore an exact uniform
translation for every semantic draw family. ProGPU stores those device-space
coordinates in resource kind 17, references them from the unchanged 64-byte
semantic state using flag bit 2, and resolves the DPI-dependent offset in the
shared state cursor used by wgpu-native and Dawn. A rotated/sheared transform
pushes WPF's equivalent empty frame. Dynamic pairs and multiple coordinates
require piecewise geometry deformation and deliberately fail closed in this
first slice.

The public package gate builds a focused 19-command, eight-channel-resource
scene, compiles it through both MIL exports, and requires live retained
readback. Integration testing exposed and fixed a registry-boundary defect:
the typed kind-17 validator existed, but the known-resource range still ended
at kind 16. A public scene-validation regression now covers a guideline
resource referenced by state. All ten local native CTests and the focused
managed builder/producer suites passed. Strict Windows ARM64 MSVC rebuilt both
exports under `/W4 /WX`; all 11 native/Dawn CTests passed. The fresh app-local
DLLs compiled the focused scene through both MIL channels and rendered on live
D3D12 with five semantic resources, one batched draw, zero coverage-staging
bytes, a valid nonblack retained readback, and 16,384 direct-render pixels.
Qualified SHA-256 values are
`9a76e7a16eb989cad3932e4d24e9e3ca1247069d8bd14114120cf073e038a270`
for `progpu_native.dll` and
`4a9e55ff26301d50138c7f02cd8be02645541ea29dd37081e2f787d2cc69c8b7`
for `progpu_native_dawn.dll`.

Solid-opacity-mask implementation `f9f49b86` and package gate `9ecc8a9b`
then added the exact spatially uniform subset of canonical DrawingGroup
opacity masks. A transform-free static SolidColorBrush contributes
`brush.Opacity * brush.Color.A` to the inherited group opacity, so applying
that value before recursively lowering the group's children is exactly
equivalent to WPF's alpha-mask composition for every semantic draw family.
The mask remains a retained canonical brush dependency and updates through
the normal SolidColorBrush command; no backend-specific mask material or
semantic ABI expansion is required.

The decoder distinguishes unsupported and invalid input: known linear or
radial gradient masks fail with `unsupported_command`, while a missing or
wrongly typed brush handle fails with `invalid_handle`. Tile, animated,
transformed, gradient, and other spatially varying masks remain fail closed
until group bounds and reusable mask-render-target/material resources exist.
Native tests cover the initial alpha product and a retained brush update.

The public package consumer added `--mil-drawing-group-only` to the JIT,
NativeAOT, build, release, and package-verification lanes. All ten local MIL
CTests passed; strict Windows ARM64 MSVC rebuilt both exports under `/W4 /WX`
and all 11 native/Dawn CTests passed. Fresh app-local DLLs compiled the
focused scene through both MIL exports and rendered on live D3D12 with four
semantic resources, one batched draw, zero coverage-staging bytes, a valid
nonblack retained readback, and 16,384 direct-render pixels. Qualified
SHA-256 values are
`3b5aa2a63c1335877e8ca49ecb37abcc705be1a9940a77fbf5f19150219f69c1`
for `progpu_native.dll` and
`341e01504aeb9380a33676704f1712cf25ee433f80554344bb080a3e0514be93`
for `progpu_native_dawn.dll`.

The qualification push also corrected both checked-in exported-symbol
allowlists to include the public DrawingImage bounds sideband entry point.
Linux ARM64 had built successfully and passed all 15 native suites before its
symbol-surface guard exposed that earlier packaging omission.

Aliased-edge implementation `55bf8628` and package gate `3f5f72dc` then
enabled canonical DrawingGroup `EdgeMode.Aliased` for vector content. The
group scope inherits WPF's unspecified value and makes an explicit aliased
value sticky for descendants. Shared semantic analytic primitives and
polylines receive their existing edge-aliased flags, while vector-path fills,
strokes, caps, and joins select the one-sample raster path instead of the
eight-sample antialiasing grid. Image and glyph sampling are intentionally
unchanged, and clip-mask geometry remains exact rather than being broadened
or converted into an aliased bounds clip.

The canonical parser continues to validate the existing DrawingGroup edge
field and rejects values outside Unspecified/Aliased transactionally. Native
tests assert the emitted primitive flag, while the public
`--mil-drawing-group-only` scene now carries Aliased through both native MIL
exports. All eight configured local native suites passed. Strict Windows
ARM64 MSVC rebuilt `progpu_native.dll` and `progpu_native_dawn.dll`; all 11
native/Dawn CTests passed. After the fresh DLLs and `wgpu_native.dll` were
copied beside the project-reference consumer, both exports compiled the
focused scene and live D3D12 retained/direct rendering completed with four
semantic resources, one draw, zero coverage-staging bytes, and 16,384 direct
pixels.

Vector-only ClearType-hint implementation `db057403` and package gate
`4af0b1c5` next consume the final canonical DrawingGroup render-option field
without overstating text parity. WPF's `CDrawingContext::PushRenderOptions`
uses `ClearTypeHint.Enabled` to call `SetClearTypeHint(true)`, and the software
render target consults that state only while deciding whether `DrawGlyphRun`
may use ClearType on an alpha surface. It has no vector or image effect.

ProGPU therefore carries Enabled as inherited scope state and accepts exact
non-text subtrees. Any nonempty direct or retained glyph run reached under
that state returns `unsupported_command` before a semantic scene is
published, because the current shared native glyph rasterizer is grayscale
and silently drawing it would be false parity. Source validation continues
to accept only Auto/Enabled. Native tests cover an accepted vector subtree
and a rejected real SFNT-backed GlyphRunDrawing subtree.

All eight configured local native suites, the managed canonical builder
contract, and the zero-warning project-reference package build passed.
Strict Windows ARM64 MSVC rebuilt both exports and all 11 native/Dawn CTests
passed. With fresh native, Dawn, and wgpu-native DLLs copied app-local, both
MIL exports compiled the hinted vector scene and live D3D12 retained/direct
rendering completed with four semantic resources, one draw, zero
coverage-staging bytes, and 16,384 direct pixels. True ClearType text remains
an explicit shared glyph-rasterization/backend follow-up.

Canonical visual render-options implementation `7db3ddb9` and package gate
`0e1b4029` next add `MilCmdVisualSetRenderOptions` (`0x21`) to the retained
visual protocol. The decoder consumes the canonical 36-byte payload and the
WPF flag mask for bitmap scaling, edge mode, compositing mode, ClearType hint,
text rendering mode, and text hinting mode. Bitmap scaling, aliased edges, and
the vector-only ClearType subset are retained per visual and inherited through
child visuals into nested DrawingGroup/ImageDrawing content. Default values
remain no-op/inherit, matching WPF's `PushRenderOptions` behavior.

Unknown flags and invalid enums are malformed input. Known compositing,
TextRenderingMode, and TextHintingMode flags return `unsupported_command`
transactionally, and a visual ClearType hint reached by real glyph content
also fails closed until the shared text rasterizer provides true ClearType.
Native tests cover root-to-child inheritance, nearest-neighbor image sampling,
aliased vector output, rejected glyph content, and transactional rejection.
All eight configured local native suites and the managed canonical packet test
passed. Strict Windows ARM64 MSVC rebuilt both exports; all 11 native/Dawn
CTests passed. With fresh native, Dawn, and wgpu-native DLLs copied app-local,
the visual-to-DrawingGroup inheritance scene compiled through both exports and
live D3D12 retained/direct rendering completed with four semantic resources,
one draw, zero coverage-staging bytes, and 16,384 direct pixels.

Native text render-option implementation `83f9febd` next completes the text
fields already present in canonical `MilCmdVisualSetRenderOptions` (`0x21`).
The C++ channel now validates and retains WPF `TextRenderingMode`
Auto/Aliased/Grayscale/ClearType and `TextHintingMode` Auto/Fixed/Animated,
then applies those inherited values only while compiling glyph content.
CompositingMode remains a known, transactional `unsupported_command`.

The glyph compiler maps WPF modes onto the existing shared semantic text
styles consumed by `Text.wgsl`: Aliased selects threshold coverage, Grayscale
selects gamma-corrected monochrome coverage, and ClearType selects the shared
RGB-shifted coverage path. `ClearTypeHint.Enabled` promotes an otherwise Auto
text-rendering scope to ClearType, matching the current managed ProGPU WPF
policy. Explicit text-rendering mode wins over that hint.

Auto and Fixed hinting use the same physical-placement policy as the managed
native-scene compiler. Axis-preserving glyph runs at raster sizes up to 24 px
snap Y to an integer pixel, keep X on one of four quarter-pixel phases, and
select a phase-specific retained outline. Larger glyphs snap both axes to
integers. Animated text and rotated, sheared, or reflected placement remain
unsnapped. Each unique glyph/raster-size resource retains four outline records
that share decoded SFNT path segments, so phase selection does not duplicate
outline decoding or managed objects.

The local native build and all ten configured CTests pass, including retained
SFNT glyph tests for grayscale, ClearType hint promotion, explicit Aliased plus
Fixed snapping, and Animated unsnapped placement. The canonical managed packet
test also passes. Package checkpoint `c7139459` adds
`--mil-text-render-options-only` to source, package, release, and NativeAOT
lanes. Strict Windows ARM64 MSVC rebuilt both exports and all 11 native/Dawn
CTests passed. With fresh native, Dawn, and wgpu-native DLLs copied app-local,
the focused ClearType/Fixed scene compiled through both exports and live D3D12
rendered three semantic resources, one draw, 53,248 coverage-staging bytes,
and 16,384 direct pixels. Qualified hashes are
`4703ddeaebf3ddea3ce7f503e935093e79cabb5bac5c3d26ff2890444f011fa2`
for `progpu_native.dll` and
`9de7c391543e027410523b75dc8a394255ca1045e2359f9049f27ba387939a15`
for `progpu_native_dawn.dll`.

This establishes parity with ProGPU's current managed WebGPU/DirectX text
modes, not pixel identity with WPF's DirectWrite glyph hinting or system
display parameters; those remain an explicit platform-text parity gate.
Canonical DrawingGroup has no text-rendering or text-hinting fields, so
object-level DrawingGroup text options continue to fail closed.

Retained Visual clip implementation `f134b690` next adds canonical
`MilCmdVisualSetClip` (`0x1f`) and `MilCmdVisualSetScrollableAreaClip`
(`0x28`). The channel retains the geometry dependency and optional scroll
rectangle, protects live dependencies from deletion, validates finite
nonnegative rectangles, and keeps batch updates transactional.

The scene compiler follows WPF's ordering for the exact rectangle subset.
Scrollable-area clips are transformed by the parent scope before the Visual's
offset and transform, snapped inward with ceiling left/top and floor
right/bottom, and intersected with the inherited scissor. A Visual carrying a
scroll clip also snaps its offset through parent device space before mapping it
back into local space. The regular Visual clip is applied after the Visual
offset and transform. Plain RectangleGeometry with an axis-preserving effective
transform becomes a shared semantic scissor; rounded, rotated/sheared, ellipse,
and arbitrary path Visual clips return `unsupported_command` instead of being
broadened to bounds.

Package checkpoint `909d6ae8` adds `--mil-visual-clip-only` to JIT,
NativeAOT, package verification, build, and release lanes. All ten local native
CTests, the canonical managed packet test, and the zero-warning package-consumer
build pass. Strict Windows ARM64 MSVC rebuilt both exports and all 11
native/Dawn CTests passed. Fresh app-local native, Dawn, and wgpu-native DLLs
compiled the focused clip scene through both MIL exports and rendered on live
D3D12 with three semantic resources, one draw, zero coverage-staging bytes, a
nonblack retained readback, and 16,384 direct-render pixels. Qualified binaries
from 2026-08-25 17:11 are 1,960,448 bytes with SHA-256
`0261b5eda34a53db96526e7b27709b052619da561d468d5b131945ed475d54d8`
for `progpu_native.dll`, and 1,999,360 bytes with SHA-256
`9068358ec8f291c261943eef95849c1eac78397bb0446b83d395e9ae5c330116`
for `progpu_native_dawn.dll`.

Exact rounded/path Visual clips remain a reusable ProGPU vector-mask task.
Layout clips are a source-built WPF producer concern and are not claimed by
these canonical Visual commands.

Static solid Visual opacity-mask implementation `070bed14` next adds canonical
`MilCmdVisualSetAlphaMask` (`0x23`). The retained Visual holds and protects its
Brush dependency, while a shared native uniform-mask resolver now serves both
Visual and DrawingGroup scopes. For a transform-free, nonanimated
SolidColorBrush, `Brush.Opacity * Color.A` composes into inherited Visual
opacity for every shared semantic draw family. Retained brush updates change
the next compiled scene generation without rebuilding managed objects.

Missing or wrongly typed handles are invalid; known gradient/spatial masks
return `unsupported_command`; clear-then-delete succeeds; and deletion while
referenced fails transactionally. Package checkpoint `cfe13009` adds
`--mil-visual-opacity-mask-only` to JIT, NativeAOT, package verification,
build, and release lanes. All ten local native CTests, the canonical managed
packet test, two focused typed producer tests, and the zero-warning consumer
build pass.

Strict Windows ARM64 MSVC rebuilt both exports and all 11 native/Dawn CTests
passed. Fresh app-local DLLs compiled the focused mask through both MIL exports
and live D3D12 rendered three semantic resources, one draw, zero
coverage-staging bytes, a nonblack retained readback, and 16,384 direct pixels.
Qualified binaries from 2026-08-25 17:25 are 1,961,472 bytes with SHA-256
`a76fe43b7e7a26b6ccaab71e80261e2704f0308c03c3e3a35abc4d80ff66038c`
for `progpu_native.dll`, and 1,999,872 bytes with SHA-256
`ac396e3973a2bc5a851925dff0d97f3cf43ebaaaa7b332df797cfbc3946341cd`
for `progpu_native_dawn.dll`. Gradient, tile, transformed, animated, and other
spatial Visual masks remain part of the reusable ProGPU mask-target work.

Static Visual guideline implementation `31cd23ca` next adds canonical
`MilCmdVisualSetGuidelineCollection` (`0x27`), including its packed UInt16
counts and trailing float coordinates. The native channel retains sorted
coordinates, validates padding, payload size, and finite float values, and
keeps multi-guide packets transactionally valid while returning
`unsupported_command` at scene compilation until piecewise deformation exists.

The exact zero/one guide per axis subset reuses the shared semantic GuidelineSet
resource already consumed by WebGPU/Dawn and DirectX. Scale/translate mapping
preserves WPF's float conversion and target-space coordinate behavior;
rotated/sheared Visuals push an empty snapping frame. Unlike DrawingGroup, each
WPF Visual resets the parent guideline frame before applying its own. Native
tests prove both mapped values and that a child Visual with no guidelines does
not inherit the root's resource.

Package checkpoint `50710315` adds `--mil-visual-guideline-only` to JIT,
NativeAOT, package verification, build, and release lanes. All ten local native
CTests, the canonical managed packet test, two focused typed producer tests,
and the zero-warning consumer build pass. Strict Windows ARM64 MSVC rebuilt
both exports and all 11 native/Dawn CTests passed. Fresh app-local DLLs compiled
the focused scene through both MIL exports and live D3D12 rendered four
semantic resources, one draw, zero coverage-staging bytes, a nonblack retained
readback, and 16,384 direct pixels. Qualified binaries from 2026-08-25 17:39
are 1,964,032 bytes with SHA-256
`36406b7138010c2c3b47e136a32efa62f07e148027640b68edafc0b67ea07318`
for `progpu_native.dll`, and 2,002,432 bytes with SHA-256
`deaa21c42b156f0aa5f78bcb10593bfc53c650da69dab320cb625fdcd8a585be`
for `progpu_native_dawn.dll`. Multiple guides per axis remain the same explicit
piecewise-deformation gap as canonical GuidelineSet.

Retained Visual effect implementation `93929c07` next adds canonical
`MilCmdVisualSetEffect` (`0x1d`), `MilCmdBlurEffect` (`0x6e`), and
`MilCmdDropShadowEffect` (`0x6f`) with resource types 36 and 37. The channel
retains and protects the Visual's effect dependency, applies resource updates
on the next compiled generation, rejects deletion while referenced, and keeps
failed batches transactional. Managed builder checkpoint `7f02bd4a` also
carries the WPF blur kernel and rendering bias through the neutral portable
effect DTO rather than reconstructing effect state in a host bridge.

The exact Gaussian mapping follows WPF milcore rather than treating WPF's
logical radius as WebGPU sigma. WPF truncates Radius to an integer, scales it
by the smaller orthogonal transform row length, truncates and caps the physical
kernel radius at 100, then uses `radius / 3` as standard deviation. ProGPU feeds
that sigma into the existing shared semantic blur pass. DropShadow computes
the WPF local offset `(depth * cos(direction), -depth * sin(direction))`, maps
it through the normalized orthogonal transform, and runs the existing shared
blur, shadow-composite, and source-composite passes. Both wgpu-native/Dawn and
DirectX therefore consume the same retained scene and effect descriptors.

This checkpoint is intentionally narrower than general WPF Effect parity.
Only Gaussian BlurEffect and DropShadowEffect with static values and an
orthogonal effective transform are accepted. Box blur, animated effect fields,
shear, and composition with an active Visual clip, opacity mask, or non-unit
opacity return `unsupported_command`. WPF applies Visual effect before opacity
mask/opacity and after clip; the current semantic layer does not yet represent
separate inflated-source and final-composite clip regions, so accepting those
combinations would silently change ordering. The native effect currently uses
a conservative full-target isolated layer; retained dirty-region tightening is
follow-up performance work.

Native regressions cover blur sigma, drop-shadow direction/color/opacity,
dependency lifetime, Box rejection, and modifier-combination rejection. All
ten local native CTests, the canonical managed packet test, the focused typed
LibreWPF producer tests, and the zero-warning project-reference consumer build
passed. Package checkpoint `6702b9b7` adds `--mil-visual-effect-only` to JIT,
NativeAOT, package verification, build, and release lanes.

Strict Windows ARM64 MSVC rebuilt both exports and all 11 native/Dawn CTests
passed. Export checks found `progpu_native_mil_channel_create` and
`progpu_native_mil_channel_build_scene` in both DLLs. Fresh app-local native,
Dawn, and wgpu-native DLLs compiled the retained DropShadow scene through both
MIL exports and live D3D12 rendered four semantic resources, two draws, zero
coverage-staging bytes, a nonblack retained readback, and 16,384 direct pixels.
Qualified binaries from 2026-08-25 18:05 are 1,973,248 bytes with SHA-256
`eb55945dff526f5535fd7c10795e2e0e91baea787aac6c165ab7cfea3fa4c4cf`
for `progpu_native.dll`, and 2,011,648 bytes with SHA-256
`2c9c1f5fc1ee4f41b9361280d53a32201e3b4215c3cd70a0c0cf68c130766eda`
for `progpu_native_dawn.dll`.

### BitmapCache execution design

Canonical cache support is the next retained Visual slice. WPF defines
`MilCmdVisualSetCacheMode` (`0x1e`) as a 12-byte command view containing the
Visual and CacheMode handles. `MilCmdBitmapCache` (`0x8d`) is a 28-byte command
view for resource type 94 and carries RenderAtScale, an optional animated
DoubleResource handle, SnapsToDevicePixels, and EnableClearType. The native
resource clamps the current scale to zero or greater; a scale sufficiently
close to zero draws no cached content.

WPF's `CMilVisualCache` evidence also fixes the execution model. Cache bounds
are local Visual content bounds. Realization dimensions are the ceiling of
those bounds multiplied by RenderAtScale and system DPI, capped by the backend
texture limit. Cache update renders only the Visual's content and descendants,
ignoring the root Visual's outer offset, transform, clip, opacity, mask, and
effect. Normal tree rendering then draws the cached texture through the outer
Visual transform; opacity is applied at that composite, and
SnapsToDevicePixels post-offsets the transformed cache origin by its fractional
device coordinates. WPF may reuse the cache as effect input only under its
explicit no-opacity/no-mask/no-inflation conditions.

The shared native representation therefore needs an owner-keyed retained
cache page, not the existing depth-indexed temporary layer slot. Its stable
identity is independent of command position and sibling updates; a content
revision invalidates raster content, while placement, opacity, clip, mask, or
other outer-state changes only rebuild the composite. Cache pages participate
in the existing 256 MiB bounded layer budget, carry explicit logical bounds,
raster scale, pixel-snap, and text-mode fields, and are released when their
owner disappears or the device is lost. Nested caches require independent
pages rather than sharing one slot at a materialized depth.

The first executable checkpoint now adds
`PROGPU_NATIVE_SCENE_LAYER_CACHE_CONTENT`. It uses the existing
`composite_revision` field as a nonzero stable owner identity and
`content_revision` as the nonzero pixel version, assigns up to 16 owners to a
separate persistent GPU-slot pool, and keys reuse independently of the whole
scene hash. The preflight rejects backdrop caches and duplicate owners, charges
cache and effect pages to the shared layer budget, and preserves the page
across an unrelated sibling update. Texture extent/generation or content
version changes force a redraw; disappearance, owner replacement, and device
teardown release or invalidate the page. Portable C++ validation, managed
builder validation, and the Dawn/Metal provider integration test cover the
contract. The typed Visual-bounds checkpoint now prevents the cache from
becoming a full-target allocation. Portable hosts bind source-built WPF
descendant bounds through
`progpu_native_mil_channel_set_visual_cache_bounds`; the compiler transforms
those local bounds into an explicit local raster page. Missing/nonfinite/empty
metadata fails closed.

The next executable checkpoint adds
`PROGPU_NATIVE_SCENE_LAYER_CACHE_LOCAL_SPACE` without changing the exact
64-byte layer record. For this flag, bounds are the zero-origin raster-page
extent and `reserved0` names a preceding canonical transform-only State
resource that places the cached quad in its parent. The target cursor allocates
that page independently of target placement, content states translate/scale
the Visual-local bounds into the page, and the shared WebGPU/DirectX executor
composites all four page corners through the typed affine state. The same
record therefore supports translation, scale, rotation, shear, parent-layer
localization, and non-unit RenderAtScale while retaining one owner-keyed page.

The canonical MIL checkpoint is now executable on that foundation. The C++
channel decodes the exact 12-byte `MILCMD_VISUAL_SETCACHEMODE` command and the
exact 28-byte `MILCMD_BITMAPCACHE` resource update for canonical resource type
94. A Visual retains its typed cache handle, a BitmapCache retains its optional
type-49 DoubleResource animation, and deletion of either live dependency fails
transactionally. Packet booleans must be canonical zero or one, non-finite
scale values fail closed, and animated scale is resolved from the live resource
on every scene compilation.

`NativeMilBatchBuilder` exposes the same contract through
`NativeMilResourceType.BitmapCache`, `NativeMilBitmapCache`,
`SetBitmapCache(...)`, and `SetVisualCacheMode(...)`. The WPF-neutral
`IPortableBitmapCacheSource`/`PortableBitmapCache` seam carries the current
scale, snapping, and ClearType values without referencing PresentationCore or
using reflection; source-built LibreWPF is responsible for publishing it.

For the currently executable local-space subset, the MIL compiler emits one
owner-keyed cached semantic layer around the Visual subtree. The stable owner
identity is derived from scene identity plus Visual handle; the pixel revision
walks the typed Visual, render-data, brush, pen, transform, geometry, drawing,
image, glyph, effect, guideline, nested-cache, and animation dependency graph.
Consequently an unrelated sibling update preserves the cache version, while a
brush/resource update inside the cached subtree changes it without managed
invalidation assistance. Exact resolved scale zero suppresses the cached
subtree. Exact source-built Visual descendant bounds are required through the
typed channel sideband and become a zero-origin page sized by RenderAtScale and
frame DPI; missing bounds fail closed instead of allocating the full render
target. Root offset, transform, and opacity are excluded from the pixel
revision and applied only by the composite, so changing placement reuses the
page. Positive finite static or animated RenderAtScale values resize and
rerasterize it, and exact resolved scale zero suppresses the subtree.
SnapsToDevicePixels and EnableClearType still return `unsupported_command`.
Root composite clips, spatial masks, and guideline state also fail closed until
the local-cache layer carries those post-raster operations explicitly.

The pinned provider/Dawn Metal hardware gate now exercises the local page
directly: its first 24x18 render performs one content and one composite pass, a
composite-only translation reuses the page with zero content passes, and a
0.5 RenderAtScale update reallocates/rerasterizes a 12x9 page. The complete
package-mode managed Dawn render/readback and forced device-loss recovery pass
at provider revision `02823bf8d2e56548b2780d6b92ae7065be1d8605` and Dawn
revision `710c33013c53ab2700d332c25ff51430251a8cc4`.

Windows ARM64 qualification for this exact cache-bounds checkpoint completed
on 2026-08-25 from clean detached commit `dd3857a4` in the Parallels Windows 11
VM. MSVC rebuilt both `progpu_native.dll` and `progpu_native_dawn.dll` under
`/W4 /WX`; all 11 native/Dawn CTests passed, the base and Dawn export contracts
passed, and both independent C++ and managed samples rendered through the live
`Parallels Display Adapter (WDDM)` D3D12 device. The managed retained sample
lowered 16 source commands to 13 native commands, issued six draws in one
submission, and passed pre-build and post-build allocation/readback checks. The
bounded differential matrix completed its opacity, zero-copy image/mask,
retained semantic scene, mask/effect chain, vector clip, image effect, Overlay,
ColorDodge, and text shaping contracts. The staged ARM64 package DLL SHA-256
values are `D17701FB0669A241183AF064080A1FD1ADD29AE1B000A531CCE5E7307B2650C6`
for `progpu_native.dll` and
`02414A74F7C6CB1A84F2846D5E5B701102E4812B5AEFCBA25688AE881592BD42`
for `progpu_native_dawn.dll`. This qualifies the preceding exact-bounds
target-space checkpoint on DirectX; Windows qualification of the new
local-space/RenderAtScale checkpoint is tracked separately.

The local-space/RenderAtScale checkpoint was then qualified on 2026-08-25 from
the clean detached ProGPU documentation commit `1a75a958` (native
implementation `dee81dff`) in the same Windows 11 ARM64 VM. Strict MSVC rebuilt
both native modules under `/W4 /WX`; all 11 native/Dawn CTests and both export
contracts passed. The independent C++ and managed samples selected the live
`Parallels Display Adapter (WDDM)` D3D12 adapter and completed retained render,
allocation, and pixel-readback checks. The expected Parallels-only retained GPU
hit-test deferral remained isolated to that optional probe. The complete
bounded D3D12 smoke matrix passed opacity, zero-copy image/mask, retained
semantic scene, mask/effect chain, vector clip, image effect, Overlay,
ColorDodge, and managed/C++ text-shaping contracts; the final Overlay and
ColorDodge scenes were pixel-exact. The win-arm64 package was staged with
SHA-256 `FBC4EC3D71A1BB63CA2DE3A092C7F25D63747C47C40AF7FC9D19EA4A379FE5B4`
for `progpu_native.dll` and
`ECC81DF8437FE0C4EC8BB18D9692E248048F04270471E04DC053BF7610E5B173`
for `progpu_native_dawn.dll`. This closes the strict DirectX gate for the
current local-space cache execution subset.

The implementation sequence is intentionally architectural:

1. Add a semantic cached-layer descriptor and persistent owner-keyed page pool
   shared by wgpu-native/Dawn and DirectX.
2. Prove cache hits survive unrelated sibling/composite changes, while content,
   scale, size, text mode, and device generation changes invalidate the page.
3. Decode canonical BitmapCache/Visual packets into that descriptor and retain
   dependency lifetime transactionally. (Implemented with local-space bounds,
   composite-only placement, and positive finite RenderAtScale.)
4. Publish neutral typed cache state from source-built WPF and emit it from
   LibreWPF without reflection.
5. Qualify pixel snapping, ClearType policy, composite clip/mask/guideline
   ordering, nested cache lifetime, effects ordering, and LibreWPF package
   lanes. (Live D3D12 is qualified for the current local-space subset.)

The persistent page and composite-transform path are executable, but full cache
parity is not claimed until the remaining post-raster state and text policies
above are exact. Treating BitmapCache as a no-op, an ephemeral full-target
layer, or a depth-slot effect-cache alias would preserve neither WPF pixels nor
its performance contract and remains explicitly excluded.

Two adapter-specific limitations remain explicit. Retained GPU hit-test
readback is deferred on the Parallels display adapter because its blocking
readback path stalls, although the retained D3D12 render/readback sample passes.
The legacy managed renderer also removes the Parallels D3D12 device on the
dense 384-command mixed-picture workload; the same workload passes through the
C++ renderer, so this adapter's gate keeps full native stress and a bounded
managed differential as separate processes. Neither is evidence of
full DirectX/MIL parity; Stages 1–5 remain open until their listed protocol and
integration surfaces are implemented.

## Invariants

- No reflection or private managed field scanning in the product bridge.
- No pointer-shaped WPF objects in public package contracts.
- All protocol reads are bounds checked; unknown required data fails closed.
- Channel batches are transactional at the ProGPU boundary.
- Resource identity and generation are stable across unchanged frames.
- Native renderer APIs remain reusable by WPF, WinUI, and Avalonia.
- DirectX is a backend/interop surface over the shared renderer, not a second
  scene implementation.
