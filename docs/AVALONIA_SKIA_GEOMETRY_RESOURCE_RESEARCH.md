# Avalonia.Skia geometry and region hot-path research

## Scope and observed framework contract

This clean-room slice optimizes the official SkiaSharp geometry contracts used
by Avalonia.Skia. At Avalonia commit
[`fee9c561`](https://github.com/AvaloniaUI/Avalonia/tree/fee9c561ce036e8a3e8cee2397c75ca599b4790d),
`GeometryImpl` retains `SKPathMeasure`, queries path bounds and containment, and
expands strokes with `SKPaint.GetFillPath`; `TransformedGeometryImpl` copies and
transforms paths; `CombinedGeometryImpl` uses `SKPath.Op`; and `SkiaRegionImpl`
accumulates integer dirty rectangles before querying or iterating the canonical
region. These observed public calls define the benchmark workloads. No Avalonia,
Skia, Direct2D, Win2D, WebRender, Vello, or HarfBuzz implementation text was
copied, translated, or adapted.

## Primary-source comparison

| Engine or specification | Relevant public or architectural contract | ProGPU clean-room decision |
| --- | --- | --- |
| [Skia `SkPathMeasure`](https://api.skia.org/classSkPathMeasure.html) | Construction copies the parts required for measurement, so the source can be changed or deleted; `resScale` trades work for precision. | Read ProGPU's packed command stream directly and retain an independent measured-contour snapshot. Reuse bounded per-thread contour storage; keep queries allocation-free. |
| [Skia `SkPath`](https://api.skia.org/classSkPath.html) | Path copies are value-identical and internally share storage until mutation; translation replaces the destination path. | Keep pooled packed command storage, copy only the active command range, and specialize identity/translation transforms so cached loose and tight bounds translate exactly without recomputing curve extrema. A future copy-on-write backing can remove the remaining command copy without changing the public contract. |
| [Skia `SkRegion`](https://api.skia.org/classSkRegion.html) | Regions compactly represent one integer rectangle or run-length encoded rectangles and expose canonical containment, intersection, complexity, and iteration results. | Accumulate Avalonia's union-only dirty rectangles and bounds in `O(1)` per append, then normalize once at the first semantic query. Reuse bounded thread-local sort, interval, merge, and band-map storage; canonical output remains deterministic. |
| [Direct2D geometry realizations](https://learn.microsoft.com/en-us/windows/win32/direct2d/geometry-realizations-overview) and [`CombineWithGeometry`](https://learn.microsoft.com/en-us/windows/win32/direct2d/id2d1geometry-combinewithgeometry) | Curves are flattened to a caller-controlled maximum error; lower tolerances improve fidelity at greater cost, and device-dependent realizations should be reused. Geometry combinations emit a simplified result through a sink. | Replace fixed segment counts with bounded adaptive de Casteljau subdivision at a maximum quarter-device-pixel control-to-chord error. Retain WebGPU path-operation shader modules, compute pipelines, and bind-group layouts per device; reconstruct readback directly from mapped storage. |
| [Win2D overview](https://learn.microsoft.com/en-us/windows/apps/develop/win2d/in-a-core-app) and [DPI/DIP guidance](https://learn.microsoft.com/en-us/windows/apps/develop/win2d/dpi-and-dips) | GPU-accelerated vector drawing includes stroke expansion, path measurement, tessellation, and boolean operations in XAML's DIP coordinate system. | Preserve SkiaSharp-compatible synchronous CPU observations in logical coordinates. `resScale` tightens stroke subdivision; final fill rasterization and non-trivial boolean solving remain WebGPU operations. |
| [WebRender rendering overview](https://searchfox.org/mozilla-central/source/gfx/docs/RenderingOverview.rst) | A compact display list becomes a retained scene, is culled to a frame, and reuses renderer-owned GPU resources; dirty rectangles avoid rebuilding unaffected content. | Keep packed CPU geometry as retained input, delay dirty-region canonicalization until it is observable, and keep shader/pipeline lifetime in the WebGPU device domain rather than rebuilding it per operation. |
| [Vello](https://github.com/linebender/vello) | Compact scene encoding is separate from GPU compute rasterization; prefix-sum and other parallel work use WebGPU-compatible compute with bounded temporary storage. | Preserve typed compact command streams and move only parallel raster/boolean work to WebGPU. CPU path measurement, bounds, transforms, and region queries remain deterministic synchronous value operations. |
| [HarfBuzz shaping output](https://harfbuzz.github.io/shaping-and-shape-plans.html) and [glyph rendering boundary](https://harfbuzz.github.io/glyphs-and-rendering.html) | Shaping produces reusable positioned glyphs; outline extraction/rasterization is a later stage. | Leave text shaping, fallback, glyph IDs, DPI/subpixel policy, and glyph atlases unchanged. Geometry optimizations consume retained outlines without adding shaping or character-map work. |

Rejected alternatives are fixed uniform curve subdivision, normalizing a dirty
region after every union, materializing the public path object graph for every
measure/copy, rebuilding WebGPU shader pipelines per boolean operation, copying
mapped GPU output into intermediate managed arrays, CPU bitmap rasterization,
reflection-based Avalonia adapters, and source-derived foreign control flow.

## Resulting algorithms and bounds

- Packed measure construction is `O(C + S)` for `C` commands and generated
  measurement spans `S`; its retained storage is bounded and reused on the
  constructing thread. Position/tangent queries are `O(log S)` and allocate
  zero managed bytes after warmup.
- Packed path copy is `O(C)` and rents one bounded command array. Identity is
  `O(1)`. Translation is `O(C)` with exact `O(1)` updates to loose/tight bounds;
  general affine/perspective transforms remain `O(C)` and recompute extrema.
- Union-only dirty-region recording is `O(1)` per rectangle. Canonicalization is
  `O(R log R + B)` for `R` rectangles and `B` output bands, uses bounded reusable
  scratch storage, and runs only before a query that observes canonical topology.
- Stroke flattening performs adaptive subdivision to a maximum error of
  `0.25 / resScale` logical pixels and a fixed depth of eight. Average work follows
  visible curvature; worst-case work and temporary storage are `O(2^8)` per
  curve. Join/cap triangles are emitted into fixed stack spans.
- A non-trivial boolean operation compiles `A + B` input segments, dispatches
  WebGPU compute, and reads at most `15(A + B)` output segments (minimum capacity
  64). Shader modules, compute pipelines, and layouts are retained per device.
  Readback reconstruction uses a 256-byte stack usage map or one pooled larger
  map and no intermediate segment copy; its current contour-linking worst case
  remains `O(S^2)` for `S` output segments.

## Validation and measured evidence

The Release benchmark runner uses the same semantic workload against official
SkiaSharp 4.151 and the ProGPU shim. Each final comparison uses alternating
processes, 32 warmups, 24 samples, and exact checksum equality. Focused tests
cover source-mutation isolation, contour traversal, transformed tight bounds,
bounded allocation, region canonicalization, stroke caps/joins/dashes, path-op
resource cleanup, AOT-safe callbacks, and rendered boolean output.

Representative best stable medians on Apple Silicon during implementation:

| Avalonia-shaped workload | Official SkiaSharp | ProGPU final | Allocation, official / ProGPU |
| --- | ---: | ---: | ---: |
| Path-measure construction | 2,319 ns | 570 ns | 80 / 56 B/op |
| Path-measure query | 29.7 ns | 23.5 ns | 0 / 0 B/op |
| Packed copy plus translation | 875 ns | 859 ns | 88 / 80 B/op |
| Dirty-region unions plus queries | 2,940 ns | 1,240 ns | 0 / 0 B/op |
| Stroke expansion | 27,404 ns | 24,990 ns | 168 / 83 B/op |
| Synchronous non-trivial path combine | 4,094 ns | 1,729,497 ns | 144 / 4,078 B/op |

The boolean row is intentionally reported as an open latency boundary, not a
performance win. This slice reduces its previous ProGPU median from 5,368,755 ns
and 6,710 B/op by retaining per-device pipelines, removing an unnecessary sleep
after blocking device polling, and eliminating intermediate readback arrays.
The remaining cost is the synchronous CPU-to-GPU-to-CPU round trip required by
the current `SKPath.Op` implementation. The follow-up design must preserve
observable topology and mutation isolation while allowing retained rendering to
consume an unresolved GPU operation; it must not hide the gap with an incorrect
bounds-only result or CPU raster fallback.

Before integration, matched EventPipe and Xcode Allocations, Time Profiler, and
Metal System Trace runs use the same final Release workload. Raw `.nettrace`,
`.trace`, Instruments scratch, and exported XML are temporary and are deleted
after retaining compact benchmark JSON/Markdown, manifests, logs, top-function
tables, and the final summary.
