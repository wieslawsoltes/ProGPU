# Device-independent native geometry utilities

## Core delivery dependency

LibreWPF's transformed layout clips (`FrameworkElement`) and editing-selection
unions/intersections (`CaretElement`) call `Geometry.Combine`. Source inspection
found that `PathGeometry.InternalCombineManaged` substitutes bounding rectangles.
That is not a boolean boundary: a triangle may become a rectangle and an exclusion
may lose its hole. Enabling that branch on Windows would not close the requirement.

This checkpoint exposes the existing C++ geometry algorithm through the main
backend without a renderer engine, native window, GPU device, COM activation or
Windows `d2d1.dll`. It is an implemented prerequisite, **not yet the source-WPF
replacement**. The WPF utility route remains unchanged until its typed adapter
preserves the complete required semantics below.

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

Next connect source-built WPF through a neutral typed service and ProGPU adapter,
not a host shim reference or reflected geometry shapes. Preserve operand
fill/figure semantics, groups/nested combinations, operand transforms, result
transform ordering, absolute/relative tolerance, empty results and WPF bad-number
behavior. Do not compute relative tolerance from the bounds-only combine being
replaced. Native float ranges versus WPF doubles need explicit handling, not
silent truncation. Register before the first layout query; ordinary unselected
Windows WPF retains its native MIL behavior. Both ProGPU renderer modes need the
correct result. Keep the Windows SDK guard until that route is qualified.

## Authored gates; execution deferred

`progpu_native_geometry_utility_tests` is a CTest-registered device-independent
include-based C ABI consumer built from the same wrapper source. It covers four
boolean modes, nonrectangular boundaries, holes, independent fill rules,
curved-result identity with the core, empty-result ownership and invalid inputs.
Its membership oracle is deliberately scalar test code. Managed tests cover
pre-load rejection, canonical layout and contour access. Fixtures are authored
and compiled, **not executed**.

Compile-only evidence: AppleClang C++20 Release builds the fixture and shared
Direct2D core with strict warnings; `ProGPU.Tests` Release compilation succeeds
with zero warnings/errors. Final IDs/totals are recorded in the PR/root checkpoint.
Pending: fixture execution, P/Invoke against both final backend modules, native
Windows differential results, WPF transformed-clip/editing-selection fixtures,
package interaction, images, lifetime, benchmarks and exact-head CI. No gate is
removed and unsupported fixtures cannot be counted as passes.
