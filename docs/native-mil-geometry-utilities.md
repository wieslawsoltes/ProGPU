# Device-independent native geometry utilities

## Core delivery dependency

LibreWPF's transformed layout clips (`FrameworkElement`) and editing-selection
unions/intersections (`CaretElement`) call `Geometry.Combine`. Source inspection
found that `PathGeometry.InternalCombineManaged` substitutes bounding rectangles.
That is not a boolean boundary: a triangle may become a rectangle and an exclusion
may lose its hole. Enabling that branch on Windows would not close the requirement.

This checkpoint exposes the existing C++ geometry algorithm through the main
backend without a renderer engine, native window, GPU device, COM activation or
Windows `d2d1.dll`. The subsequent LibreWPF connection now routes portable
`PathGeometry.InternalCombine` through the typed adapter described below and
removes its bounds-only substitute. This is implementation evidence, **not
runtime-qualified WPF geometry parity or Windows SDK admission**.

## API and ownership

`progpu_native_geometry_combine` accepts two synchronous canonical
`progpu_native_path_segment` spans, independent native fill rules, a canonical MIL
combine mode (union/intersect/xor/exclude), and finite positive absolute flattening
tolerance. Segments are already in the same coordinate space. Discontinuous
segments begin separate contours; filled contours close implicitly. Empty
operands are supported. The shared converter retains curves and resolved arcs
until the existing boundary algorithm performs flattening.

One successful call returns an opaque owned result plus immutable contiguous
views: points and contour offsets. Each contour contains at least three points
and closes implicitly. Output uses even-odd fill, including holes. Offsets has
`contour_count + 1` entries; empty output has one zero offset and no points.
Views remain valid until `progpu_native_geometry_outline_destroy`. Inputs are
never retained. There is no output-size probe, repeated boolean computation,
per-point callback, marshalled object or STL object crossing the ABI.

All output addresses must be valid. After validating these addresses, failure
zeros all outputs. Invalid data, unsupported core cases, allocation failures and
unexpected failures retain distinct native statuses. No C++ exception crosses
the C API. Independent calls/results have independent ownership; destruction must
not race a reader. Mode values have compile-time checks against the owned
Direct2D enum. Existing native point/path layouts and status/fill/MIL enums are
reused; no new by-value wire struct or duplicate generated layout is introduced.

`NativeGeometryUtilities.Combine` wraps WGPU-native and Dawn modules. It validates
policy/count/tolerance arguments before loading, pins spans for one synchronous
call, bulk-copies each view once into owned arrays, checks contour ranges, and
releases native storage in `finally` even if managed allocation or validation
fails. `NativeGeometryOutline` exposes read-only point/offset memory and contour
spans. Missing exports/modules fail explicitly; no alternative renderer or
approximate result is selected. `NativeMilGeometryCombineMode` already describes
the four operations and is reused; the utility does not require a MIL channel.

## Original implementation provenance and performance boundaries

Only original ProGPU implementation is reused:

- `src/ProGPU.Native/src/Direct2D/progpu_native_direct2d_path.cpp`:
  `create_native_fill_geometry`, `polygon_contours_sink`,
  `portable_path_geometry::CombineWithGeometry`, outline/arrangement helpers,
  intrinsic candidate bounds and fill classification.
- `src/ProGPU.Native/src/Mil/progpu_native_mil.cpp`:
  `resolve_combined_stroke_outline` already consumes that same core, establishing
  why this utility must share actual boundaries rather than concatenate operand
  edges or substitute rectangular bounds.
- `src/ProGPU.Backend.Native/NativeRendererTypes.cs`: native point/path layout and
  fill rules; `NativeMilTypes.cs`: canonical combine values and backend identity.

The new helper calls this code directly. No algorithm is translated from another
project and no reduced boolean implementation is added. Main WGPU, Dawn and
browser renderer source lists share the C ABI implementation. Browser JavaScript
export/binding admission and full shared-library qualification remain separate.

Cost is existing curve flattening/topological arrangement plus O(B + C) snapshot
work/storage for B boundary points and C contours. Inputs retain O(S) segment
records. Arrangement can have quadratic candidate/intersection work; no linear
claim is made. Existing intrinsic independent-lane bounds/classification kernels
remain authoritative. Topology stitching, ordering and contour ownership have
data dependencies. This wrapper adds no whole-buffer scalar point arithmetic:
copies use compiler/runtime intrinsic bulk copies and native point allocation is
uninitialized-for-overwrite. Offset processing is per-contour metadata.

This is a synchronous CPU-owned *topology query*, not a rejected-compute rendering
fallback. It must work before any GPU exists. It does not change configured
GPU-first/SIMD rendering policies, introduce pixel readback/upload or claim a
fastest-path improvement. A future GPU topology provider must preserve topology,
tolerance, ownership and policy diagnostics, not substitute a render-only mask.

## Behavioral references and required WPF connection

The public [WPF Geometry.Combine contract](https://learn.microsoft.com/en-us/dotnet/api/system.windows.media.geometry.combine)
requires the selected operation and applies the supplied transform to its result;
the overload distinguishes absolute and relative tolerance. The
[Direct2D CombineWithGeometry contract](https://learn.microsoft.com/en-us/windows/win32/api/d2d1/nf-d2d1-id2d1geometry-combinewithgeometry(id2d1geometry_d2d1_combine_mode_constd2d1_matrix_3x2_f_float_id2d1simplifiedgeometrysink))
describes polygonal approximation with a supplied absolute error tolerance.
These are behavioral references, not implementation sources.

### Source-built WPF connection

`PortableGeometryOperand` is a separate bounds-free tree of path, group and
combined nodes. It does not extend retained-rendering DTO kinds. The
`IPortableGeometryOperations` provider is registered only through
`PortableWpfServiceRegistry`; an explicit provider is not overwritten by default
installation and disposing a superseded registration cannot clear its successor.
Registration itself does not load native code or create a device.

LibreWPF's `PortableGeometryOperationsBridge.Export` walks source-owned geometry
without asking for `Bounds`. Groups retain their own fill rule/transform and
children, combined nodes retain both operands/mode/transform, and leaves use the
source's managed path export with empty metadata bounds. This avoids reentering
Combine through CombinedGeometry.Bounds and avoids Windows geometry bounds calls
during export. The source bridge materializes returned polygon contours as real
WPF PathGeometry, including closed/filled/stroke state. Missing providers fail
explicitly; the rectangle substitute has been deleted.

The host adapter `WpfPortableGeometryOperations` uses the existing original
LibreWPF portable-to-ProGPU path converter and ProGPU `PathGeometry.CreateTransformed`
and `PathAtlas.CompileFillPath`. Groups collect figures (not a union of children),
and nested combinations materialize through this native utility with the standard
WPF tolerance. Before CompileFillPath, operands must be non-deferred so it cannot
enter the GPU boolean/readback path. Canonical GpuPathSegment/NativePathSegment
spans are reinterpreted without a repacking copy; authored fixtures cover layout.
Result transforms are applied to both operands before boundary construction,
matching source WPF's output-space tolerance evaluation.

Tolerance uses the maximum axis of the union of tight transformed operand bounds,
with the source WPF double-relative numeric floor. The behavioral sources are
`WpfGfx/core/uce/geometry_api.cpp` (result transform order) and
`WpfGfx/core/geometry/shapebase.cpp` (`CShapeBase::Combine`) in the LibreWPF
source tree; no WPF implementation is copied into ProGPU. Nonfinite supplied
geometry returns empty; unsupported finite values outside native float range
throw explicitly. Quantization and native-core degeneracies remain qualification
items; this does not claim double-exact WPF/native output identity. Validation
uses paired intrinsic double lanes and boundary point export uses intrinsic
float-to-double widening. Traversal is depth/budget bounded; native topology and
owned result construction retain the cost described above.

For transformed native path bounds, the adapter streams into the existing
`WpfPortablePathBoundsReader` curve-extrema helpers rather than using the vector
path's Bezier control-hull bounds. This adds no second extrema algorithm and
does not allocate another portable path snapshot merely to measure it. A cubic
fixture distinguishes actual extrema from its control hull after scaling.

Both direct host construction and activation bootstrap install the same lazy
default provider. The current LibreWPF WGPU-native host profile requires the
matching `progpu_native` binary for geometry operations in **both** managed
portable and native-MIL renderer modes. This shared CPU geometry dependency is
not a switch to native rendering. Package staging already includes Backend.Native;
actual package binary/export availability still needs final qualification.
Ordinary unselected Windows WPF retains native MIL Combine. The portable transport
choice, not OS detection, selects the new route. The fill-query connection below
closes another source route; contributing-pen bounds/stroke hit testing and other
media utility consumers still prevent Windows SDK admission. Keep the SDK guard.

### Fill bounds and point queries — core layout/input connection

Acceptance path: source-built MVP and Toolkit/AvalonDock layout clips, content
bounds and pointer input. Source inspection found OS-selected MIL calls in
`PathGeometry.GetPathBoundsAsRB`, `Geometry.GetBoundsHelper` and fill containment.
Those fill-only operations now select the frozen portable media backend and use
the registered provider. Contributing pens keep their separate existing route;
this change must not enable the older stroke-by-bounds approximations on Windows.

`IPortableGeometryOperations.GetBounds` returns a neutral rectangle with explicit
empty state, world transform and hollow-figure policy. The host reuses its exact
materialized curve-extrema reader, including analytic arc bounds, rather than
Bezier control hulls or a new flattened bounds algorithm. Generic geometry/group
queries export the bounds-free operand directly. Serialized path queries retain
their source fill rule and transform. Primitive line/cubic point/type arrays are
decoded by source WPF into typed segments, with count/kind checks and closed,
smooth and gap flags retained. This decoder is transport, not a WPF-local geometry
algorithm. Existing source primitive identity fast paths remain in place.

`progpu_native_geometry_fill_contains` and `NativeGeometryUtilities.FillContains`
reuse `create_native_fill_geometry` and `portable_path_geometry::FillContainsPoint`
in the original ProGPU-owned `progpu_native_direct2d_path.cpp`. The exported ABI
is additive and uses existing point/path records plus a caller-owned uint result;
no new wire record or module interface is introduced. Both WGPU-native and Dawn
wrappers use the same source. There is one pinned span crossing and no output
array, sizing retry, GPU/device creation, queue submission or pixel readback.
Hollows are excluded by CompileFillPath and its implicit-closure semantics are
retained. Nested combinations still use the shared native boolean utility.

The existing core edge metric arithmetic now uses independent double x/y lanes
on AArch64 NEON and SSE2, retaining ordered boundary/winding reduction and
direction-aware half-open crossings. Platforms without double SIMD retain the
documented scalar platform implementation. This also updates direct C++ Direct2D
queries; both managed and native LibreWPF renderer modes call that same core.
Geometry-query CPU execution is an explicit synchronous, device-independent
layout contract, not a rejected compute-shader fallback or a change to the
renderer execution policy. No GPU-capability probe is made and no speed claim is
made. Representative final latency and allocation measurements remain required.

Cost: bounded operand/segment export and metric traversal are O(S), native point
queries cost O(S + E) time and O(S + E) temporary storage for S input segments and
E adaptively flattened edges. Nested boolean operations retain their separately
documented arrangement cost. There is no per-edge crossing or heap allocation
inside the intrinsic metric kernel. Each source query currently exports a fresh
snapshot; this is not a zero-allocation retained-query claim.

Behavioral references are the official [WPF FillContains contract](https://learn.microsoft.com/en-us/dotnet/api/system.windows.media.geometry.fillcontains)
and [Direct2D FillContainsPoint contract](https://learn.microsoft.com/en-us/windows/win32/api/d2d1/nf-d2d1-id2d1geometry-fillcontainspoint%28d2d1_point_2f_constd2d1_matrix_3x2_f_float_bool%29).
Adopt the supplied-coordinate fill query and explicit tolerance; reuse original
ProGPU algorithms, not Microsoft implementation text. This extends the existing
geometry utility architecture, not text shaping, atlases or rendering pipelines.
Native boundary tolerance/float quantization and degenerate contours still need
Windows differential qualification; compilation does not establish MIL parity.

Authored additions cover scalar-oracle point grids with both fill rules, concave
contours, nested contours, edge/vertex probes and C ABI rejection/empty output;
managed pre-load rejection; source policy/point transport, path fill/transform,
primitive cubic flags/truncation; and exact bounds, hollows, world transforms,
nonfinite input and empty-versus-point results. No fixtures were executed.

Compile checkpoint: the final fill-query C ABI fixture and shared Direct2D core
compile/link with AppleClang C++20 strict warnings; ProGPU.Tests Release compiles
with 0 warnings/0 errors. Source PresentationCore fixtures compile with 8 warnings
and 0 errors. This native tree builds the device-independent fixture, not final
WGPU/Dawn shared libraries. SSE2, final-module P/Invoke, Windows/MIL differentials,
full application gates, Instruments/performance and CI remain unqualified.

## Authored gates; execution deferred

### Stroke-query prerequisite — not yet source-WPF pen admission

The next source-backed application dependency is contributing pens in
`BoundsDrawingContextWalker.DrawGeometry` and
`HitTestWithPointDrawingContextWalker.DrawGeometry`: ordinary stroked content
needs actual stroke bounds and membership, not an inflated fill rectangle.
The initial checkpoint implemented reusable native query transport without
switching those consumers. The follow-up source connection is recorded below;
neither checkpoint establishes runtime application parity.

`progpu_native_geometry_stroke_query` accepts complete figure records, canonical
segments, per-segment stroke/incoming-smooth flags, a pen, dash intervals and an
optional post-widen world transform. Figure ranges must partition the segment
span in order; nonempty closed figures explicitly return to their start so the
closing edge keeps its own flags. Hollow/filled figure identity is preserved,
and gaps remain within the source figure rather than becoming unrelated contours.
Geometry-local transforms belong on input spines; world transforms follow stroke
expansion. A null point requests emitted stroke bounds; a point requests only
membership, avoiding an unnecessary second query. Empty bounds, errors and hits
have distinct output/status contracts, with outputs cleared before validation.

The authoritative fixed-layout C records generate `NativeGeometryQueryFigure`
and `NativeGeometryQueryPen` through the existing contract generator; handwritten
partials add typed-cap/join constructors but do not duplicate field declarations.
Both managed backend wrappers pin caller spans for one synchronous call, use
blittable Matrix3x2/point/output records, retain no inputs and allocate no output
objects. Native construction owns one factory/path/style for the query and
releases them on every return. This is independent of GPU/device creation and
does not activate operating-system COM. The ABI is additive; existing record
layouts and ABI version remain unchanged. No named-module interface changes.

Original ProGPU provenance: the canonical line/quadratic/cubic/arc emitter was
factored from `create_native_geometry` in `progpu_native_direct2d_path.cpp` and
is shared by existing MIL/fill construction and the new multi-figure adapter.
Arc splitting retains unstroked flags on every internally emitted piece and
forces only internal smooth joins. Bounds use `get_widened_outline_bounds`,
the same emitted-coverage helper used by MIL; hits use the existing Direct2D
`StrokeContainsPoint`. There is no second stroker or WPF-local coverage algorithm.
Both managed and native hosts will consume this one backend utility once wired.
The original core semantics, including approximation limits, remain subject to
final Windows/MIL differential testing.

The initial query checkpoint rejected constant stroked segments because
`build_flat_polylines` discarded them. That implementation blocker is now closed
in the shared path core: constants retain an anchor, source endpoint eligibility
and incoming join flags; an unstroked constant remains a gap. Closed-seam flags
survive compaction. Degenerate arcs emitting no cubics retain their edge state.
An all-constant stroked run emits the original ProGPU X-axis cap pair, with a
round pair for closed runs; an empty figure does not become a point stroke.
The subsequent source connection uses this preparation. Do not enable the
Windows SDK selector before the remaining application integration and qualification.

Point coverage provenance is original `resolve_degenerate_dash_visibility` and
`try_transformed_line_stroke_bounds` in `Mil/progpu_native_mil.cpp`: odd patterns
repeat twice, visible intervals own their inclusive endpoint, negative offsets
wrap, and source/gap caps stay independent. The query core reuses its existing
`build_terminal_dash_outline`, positive-winding normalization and intrinsic
outline transforms. It constructs caps at the origin, then translates to the
anchor and applies the world transform; no fake short spine or separately blended
draw is introduced. Point hits reuse `stroke_cap_contains`, restricting each
cap to its own half-plane because there is no stroke body to cover the inner half.
Nonpoint stroke/dash algorithms and quality constants are unchanged.

Applicability: C ABI bounds, C ABI hits, Direct2D Widen, GetWidenedBounds and
StrokeContainsPoint consume this shared preparation. Both managed/native WPF
renderer modes use the same C++ utility once connected; no new managed stroker is
needed. Existing MIL callers also benefit from shared endpoint preparation.
Its separate point fallback is preserved, not copied into WPF. No public record
layout or module surface changes in this follow-up; only header comments change.
Point construction has at most two cap pieces, using the existing bounded cubic
round-cap representation. Topology compaction is sequential O(E) work, O(1)
state beyond the existing polyline output; point dash phase is an ordered O(D)
prefix walk with existing interval storage. Placement uses the shared intrinsic
transform kernel. These synchronous CPU queries perform no GPU work or readback.

Style validation uses four intrinsic float lanes in managed code and in shared
native `core::valid_stroke_style` (AArch64 NEON, SSE2, Wasm SIMD), with a bounded
three-value scalar tail. Other platforms retain the documented scalar platform
path. No repacking or allocation occurs in those validation kernels. State and
topology traversal remain sequential and bounded. Setup costs O(F + S + D) time
and O(F + S + D) retained temporary storage for F figures, S segments and D dash
entries. Existing adaptive flattening, dash expansion and widening determine
query cost; the wrapper adds no per-segment crossings, GPU work or readback.
No throughput, latency or SIMD speedup claim is made without final measurements.

Behavioral references: [WPF GetRenderBounds](https://learn.microsoft.com/en-us/dotnet/api/system.windows.media.geometry.getrenderbounds),
[Direct2D GetWidenedBounds](https://learn.microsoft.com/en-us/windows/win32/direct2d/id2d1geometry-getwidenedbounds)
and [Direct2D Widen](https://learn.microsoft.com/en-us/windows/win32/api/d2d1/nf-d2d1-id2d1geometry-widen%28float_id2d1strokestyle_constd2d1_matrix_3x2_f_float_id2d1simplifiedgeometrysink%29).
Adopt the stroke/style and post-widen-transform contracts, reuse original ProGPU
code, and reject bounds inflation and silent unsupported coverage. No third-party
implementation text was introduced. This extends the documented synchronous
utility boundary, not renderer, text, atlas or GPU pipeline architecture.

Authored fixtures cover source gaps, square caps, post-widen nonuniform scale,
dash-on/off probes, incomplete closed contours, invalid flags/ranges, explicit
constant flat-cap empty coverage, generated ABI offsets, pre-load validation and
unaligned dash arrays of lengths 1–17 against a scalar native oracle. Existing
boolean/curve/fill fixtures still compile against the factored emitter. All
fixture execution, final-module P/Invoke, SSE2/Wasm builds, Windows images,
application interactions, benchmark/Instruments evidence and CI are deferred.

Stroke checkpoint compilation: the strict AppleClang C++20 fixture target is
up to date after successful compilation/linking; an initial fixture-only
missing-field-initializer error was corrected with fully initialized segment
construction. The final ProGPU.Tests Release build succeeds with 0 warnings and
0 errors. These are compile-only results, not executed tests or parity evidence.

Point/endpoint follow-up fixtures cover all 16 start/end cap pairs on constant
line/quadratic/cubic input against independent scalar shape oracles, translated
and rotated/nonuniform-scaled queries, public COM closed-point bounds/hits, odd/negative dash
phases, and padded-versus-unpadded paths with forced joins and zero-distance gaps.
The strict AppleClang C++20 geometry-utility, Direct2D core and Direct2D
compatibility fixture targets compile and link successfully.
Fixtures have not been executed; round-boundary approximation, direct COM and
final-module managed/native comparisons still need the final differential gate.

### Source-WPF pen query connection — implementation only

MVP/Toolkit `BoundsDrawingContextWalker.DrawGeometry` and
`HitTestWithPointDrawingContextWalker.DrawGeometry` now reach typed pen operations
under the frozen portable media selection. The geometry provider exposes
`GetRenderBounds` and `StrokeContains` with `PortablePenState`, not a shim-owned
Pen or reflected brush. Missing operations on older explicit providers throw;
the default provider remains registered for both managed portable and native MIL
hosts, without loading a device at registration.

Original ProGPU `PathAtlas.CompilePathCore` owns canonical line/quadratic/cubic/arc
encoding. Its new `CompileStrokeQuery` entry point retains complete figure ranges,
hollow/empty identity, incoming smooth joins, explicit closing edges and
zero-distance gaps. Normal render/fill compilation keeps its existing output and
does not allocate query metadata. Deferred boolean input fails before the GPU
solver; the host provider first resolves the bounds-free operand tree through the
existing CPU utility. Unknown segment classes and per-figure cap overrides fail
explicitly. Source WPF figures have one supplied query pen and no such overrides.
`PathQueryFigure` is backend-neutral topology, not a handwritten native layout;
the host maps it to the existing generated native figure record and reinterprets
the canonical segment span without copying its payload.

The host provider measures actual fill bounds and unions them with emitted stroke
bounds, preserving hollow filtering. Geometry-local/ancestor transforms change
the spine before widening; drawing/world transforms are passed separately. Output
tolerance uses tight transformed curve bounds, and its pre-world approximation
is conservatively scaled by the transform's linear norm. Thickness uses its
absolute value and miter limit is clamped to one as in existing ProGPU stroke
preparation. Caps/joins/dashes retain their supplied values and native validation.
Dash narrowing uses four runtime-intrinsic lanes with a bounded scalar tail and
finite-range checks. Invalid nonfinite input follows the existing WPF empty/miss
policy; missing native capability and unsupported finite input remain failures.

Source `Geometry` bounds, serialized `PathGeometry` bounds, primitive MIL polygon
transport, generic point queries and Rectangle/Ellipse/Line instance overrides
now select this provider before legacy OS utilities. `LineGeometry` still has no
filled-area membership. Existing mathematically exact static primitive bounds
shortcuts are retained. No native stroke algorithm is copied into WPF, and the
native SDK Windows guard remains unchanged. This is a source-level connection,
not evidence that startup, package loading or the full application gate passes.

Applicability: both renderer modes share the same query provider and C++ core.
Canonical compiler provenance is the original ProGPU Vector encoder, not a
third-party port. Existing public WPF/Direct2D behavior references above remain
the specification inputs; renderer/text/cache architecture is unchanged.
Setup remains O(F + S + D) time/storage plus the existing operand/curve algorithms,
with one synchronous native query crossing after operand resolution. Exported
snapshots and query arrays are owned per call; this is not a zero-allocation
retained-query claim. No GPU submission, readback or fallback-policy change occurs.

Authored connection fixtures cover canonical encoder equivalence, arc parameters,
empty/hollow figures, gap/smooth flags, closing edges, source pen/brush/dash/transform
transport, nonfinite rejection and SIMD narrowing against scalar casts for
lengths 0–17. Test execution, final-module calls, Windows differentials, full
application interactions, allocation/latency benchmarks and CI stay deferred.

Connection compile checkpoint: final ProGPU.Tests Release builds with 0 warnings
and 0 errors; LibreWPF bridge fixtures build with 116 warnings/0 errors; source
PresentationCore fixtures with 8/0; the source-built PresentationFramework host
harness with 0/0. No tests, application/native-module execution, VM comparisons,
benchmarks or CI qualification ran. The warnings are not a cleanup claim, and
these builds do not prove final native library staging or application fidelity.

`progpu_native_geometry_utility_tests` is a CTest-registered device-independent
include-based C ABI consumer built from the same wrapper source. It covers four
boolean modes, nonrectangular boundaries, holes, independent fill rules,
curved-result identity with the core, empty-result ownership and invalid inputs.
Its membership oracle is deliberately scalar test code. Managed tests cover
pre-load rejection, canonical layout and contour access. Additional connection
fixtures cover registration ownership, bounds-free nested export, source contour
reconstruction, policy forwarding, missing services, tolerance calculation,
nonfinite input and explicit finite-range rejection. These are **not executed**
during the implementation-first phase.

Compile-only evidence: AppleClang C++20 Release builds the fixture and shared
Direct2D core with strict warnings; `ProGPU.Tests` Release compilation succeeds
with zero warnings/errors. Final IDs/totals are recorded in the PR/root checkpoint.
Pending: fixture execution, P/Invoke against both final backend modules, native
Windows differential results, WPF transformed-clip/editing-selection fixtures,
package interaction, images, lifetime, benchmarks and exact-head CI. No gate is
removed and unsupported fixtures cannot be counted as passes.
