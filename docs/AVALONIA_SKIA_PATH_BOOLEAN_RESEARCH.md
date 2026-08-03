# Avalonia.Skia retained path-boolean research

## Scope and clean-room boundary

This slice implements observable `SKPath.Op` contracts from public API
documentation, independent tests, and measured Avalonia.Skia call shapes. No
foreign implementation source, control flow, private layout, or lookup data was
copied or translated. The implementation remains a typed ProGPU retained-path
node and uses the existing WebGPU path rasterization/operation infrastructure.

The measured Avalonia pattern repeatedly combines a rounded rectangle and an
ellipse with union or intersection, then queries emptiness and bounds. Before
this slice every call submitted a WebGPU geometry operation, synchronously
mapped its output, rebuilt managed contour objects, and discarded those objects
when the result path was disposed.

## Primary sources examined

- Skia defines difference, intersection, union, exclusive-or, and reverse
  difference, permits an input to alias the result, and requires an exact
  non-overlapping contour result when topology is requested:
  [SkPathOps API](https://api.skia.org/SkPathOps_8h.html).
- Direct2D writes a geometry combination to an explicit simplified-geometry
  sink and separates the geometry resource from its later realization:
  [ID2D1Geometry::CombineWithGeometry](https://learn.microsoft.com/windows/win32/direct2d/id2d1geometry-combinewithgeometry)
  and [geometry realizations](https://learn.microsoft.com/windows/win32/direct2d/geometry-realizations-overview).
- Win2D defines the same set-area semantics and recommends cached geometry for
  expensive repeated draws:
  [CanvasGeometryCombine](https://microsoft.github.io/Win2D/WinUI3/html/T_Microsoft_Graphics_Canvas_Geometry_CanvasGeometryCombine.htm)
  and [CanvasGeometry](https://microsoft.github.io/Win2D/WinUI2/html/T_Microsoft_Graphics_Canvas_Geometry_CanvasGeometry.htm).
- The retained display-list and GPU-execution separation was cross-checked
  against [WebRender's rendering overview](https://firefox-source-docs.mozilla.org/gfx/RenderingOverview.html),
  [Vello](https://github.com/linebender/vello), and the
  [WebGPU specification](https://gpuweb.github.io/gpuweb/).
- Text architecture was rechecked even though this slice changes no shaping:
  [DirectWrite and Direct2D](https://learn.microsoft.com/windows/win32/direct2d/direct2d-and-directwrite),
  [HarfBuzz shaping plans](https://harfbuzz.github.io/shaping-plans-and-caching.html),
  [SkParagraph cache](https://chromium.googlesource.com/skia/+/master/modules/skparagraph/include/ParagraphCache.h),
  and [Parley](https://docs.rs/parley/latest/parley/).

## Architecture decision

Adopted:

- `SKPath.Op` creates an immutable deferred boolean node in `O(1)` result work.
  Operand geometries become shared snapshots; a later source mutation detaches
  and clones before changing pixels, so aliasing and source lifetime remain
  independent.
- Exact empty/bounds classifications are stored as a compact packed state.
  Empty operands, disjoint bounds, union bounds, and proven canonical primitive
  containment answer common queries without submitting or mapping GPU work.
- Canonical rounded rectangles accept every official rotated start index. A
  convex bounding-rectangle proof recognizes contained ellipses without
  flattening either analytic contour.
- Topology inspection through the public compatibility surface materializes an
  exact result. Contained union/intersection rendering selects the exact source
  operand and feeds its analytic segments directly to the existing WebGPU
  rasterizer, avoiding a path-operation readback.
- General two-operand rendering packs both analytic segment streams into one
  WebGPU dispatch. The shader keeps one membership bit per horizontal
  supersample, applies the requested set operation to the two masks, and only
  then averages coverage. This is sample-exact for the configured 8x8 lattice;
  it does not approximate set membership by combining scalar coverage values.
- Nested render-only trees compile to an original bounded postfix program.
  Up to 63 leaf/operation instructions and a stack depth of 16 execute in the
  same analytic WebGPU raster dispatch, with explicit shader bounds checks and
  a deterministic exact-topology fallback beyond those limits.
- One unescaped deferred node may be recycled per thread. Recording, clipping,
  chaining, or otherwise sharing the node permanently marks it non-recyclable;
  retained commands can never observe reused state.

Adapted:

- Skia's eager result contract is represented lazily until a caller asks for
  contour topology. This preserves official observable behavior while matching
  ProGPU's retained scene and demand-driven GPU compilation.
- Direct2D/Win2D cached geometry becomes a device-independent analytic boolean
  tree. Device/DPI-specific raster coverage remains in the bounded PathAtlas.

Rejected:

- synchronous GPU submission and readback during every ordinary result query;
- borrowing mutable source lists, reflection, boxed adapters, unbounded global
  pools, CPU bitmap masks, polygon flattening, or CPU raster fallback;
- recycling any geometry after it escapes into a retained command or another
  boolean node;
- moving Unicode/OpenType shaping or line layout into this geometry slice.

Callers that explicitly request exact contour topology, plus expressions beyond
the bounded render program, use the existing analytical WebGPU geometry solver
and result mapping. No CPU bitmap mask, CPU rasterization, polygon flattening,
or scalar coverage composition is used.

## Cost model and measured evidence

Deferred creation and classified queries are `O(1)` after operand geometry is
retained. First source materialization remains `O(S)` time/storage for `S`
analytic segments. Exact general topology retains the existing GPU solver cost.
Contained rendering compiles only the selected operand in `O(S)` CPU packing
and `O(P * A * S)` WebGPU work for `P` atlas texels and `A` supersamples. A
direct binary render packs `S1 + S2` analytic segments and performs
`O(P * A * (S1 + S2))` WebGPU work with `O(1)` shader-local membership storage.
A nested expression with `N` postfix instructions and total leaf segment visits
`S` performs `O(P * A * (S + N))` WebGPU work and uses `O(D)` private mask
storage for stack depth `D <= 16`.
Storage is one bounded result object plus immutable operand references; the
thread cache retains at most one deferred node.

Apple M3 Pro, macOS 26.4.1, .NET 10.0.5, Release. Three alternating official
SkiaSharp 4.151.0 and ProGPU processes used 64 warmups, 96 samples, and 1,000
alternating union/intersection operations per sample. Every run produced exact
checksum `1672974465360712979`.

| Build | Median process median | Managed B/op |
| --- | ---: | ---: |
| Official SkiaSharp | 8,404.979 ns/op | 88 |
| ProGPU | 2,484.396 ns/op | 82 |

ProGPU latency is 70.44% lower (3.38x throughput) and managed allocation is
6.82% lower in the steady operation path. The official managed counter excludes
native path/result storage, so it is not used as a total-memory measurement.
The short eight-operation process case remains dominated by first source/surface
setup and is not used for the steady API claim.

## Validation and cleanup

Focused boolean/topology/render/arc/shader compatibility passes 73/73. Additional
regressions prove copy-on-write source mutation and an 88-byte steady allocation
ceiling. Shader-resource and GPU-render regressions pass 22/22, including all
five binary operations at A-only, overlapping, and B-only sample locations; a
right-nested stack-depth-three expression; and dispatch diagnostics proving the
binary and postfix rasterizers were used.
Final acceptance still requires the full core/headless/Avalonia suite,
official API metadata, package gates, and matched macOS EventPipe plus
Instruments Time Profiler, Allocations/VM Tracker, and Metal System Trace runs.
Only compact process JSON is retained under
`artifacts/performance/skiasharp-avalonia-path-ops`; raw trace bundles and
temporary benchmark output are removed after extraction.
