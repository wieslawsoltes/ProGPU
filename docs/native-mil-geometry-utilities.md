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
