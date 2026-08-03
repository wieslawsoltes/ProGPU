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
| [Skia `SkPath`](https://api.skia.org/classSkPath.html) | Path copies are value-identical and internally share storage until mutation; translation replaces the destination path. | Share reference-counted packed command storage until mutation. Compose translations lazily while updating cached loose/tight bounds exactly; detach and materialize only when a later command or general transform requires writable coordinates. |
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
- Packed path copy and identity are `O(1)` and share the immutable command
  buffer. Translation is `O(1)`, composes a pending offset, and updates cached
  loose/tight bounds exactly. The first later mutation detaches in `O(C)` for
  `C` commands; general affine/perspective transforms remain `O(C)` and
  recompute extrema.
- Union-only dirty-region recording is `O(1)` per rectangle. Canonicalization is
  `O(R log R + B)` for `R` rectangles and `B` output bands, uses bounded reusable
  scratch storage, and runs only before a query that observes canonical topology.
- Stroke flattening performs adaptive subdivision to a maximum error of
  `0.25 / resScale` logical pixels and a fixed depth of eight. Average work follows
  visible curvature; worst-case work and temporary storage are `O(2^8)` per
  curve. Flatness additionally requires control-point projections to stay
  inside and ordered along the chord, so collinear overshoot and reversal still
  subdivide. Join/cap triangles are emitted into fixed stack spans and appended
  to packed path storage in one reserved batch, avoiding repeated capacity and
  bounds bookkeeping. Retained thread-local command capacity is capped at 4,096
  commands.
- A non-trivial boolean operation compiles `A + B` input segments, dispatches
  WebGPU compute, and reads at most `15(A + B)` output segments (minimum capacity
  64). Shader modules, compute pipelines, and layouts are retained per device.
  Readback reconstruction uses a 256-byte stack usage map or one pooled larger
  map and no intermediate segment copy; its current contour-linking worst case
  remains `O(S^2)` for `S` output segments.

## Validation and measured evidence

The Release benchmark runner uses the same semantic workload against official
SkiaSharp 4.151 and the ProGPU shim. The final query and stroke confirmations
use three alternating processes, 32 warmups, 24 samples, and exact checksum
equality. Other table rows use stable alternating-process medians with the same
operation counts and exact checksums. Longer default operation counts keep the
create, copy, and region samples above the timer-noise floor. Focused tests
cover source-mutation isolation, contour traversal, transformed tight bounds,
bounded allocation, region canonicalization, stroke caps/joins/dashes, path-op
resource cleanup, AOT-safe callbacks, and rendered boolean output.

Representative best stable medians on Apple Silicon during implementation:

| Avalonia-shaped workload | Official SkiaSharp | ProGPU final | Allocation, official / ProGPU |
| --- | ---: | ---: | ---: |
| Path-measure construction | 2,052 ns | 516 ns | 80 / 56 B/op |
| Path-measure query | 26.790 ns | 22.511 ns | 0 / 0 B/op |
| Packed copy plus translation | 403.812 ns | 136.904 ns | 88 / 80 B/op |
| Dirty-region unions plus queries | 2,424.975 ns | 1,156.994 ns | 0 / 0 B/op |
| Stroke expansion | 25,977.708 ns | 22,603.645 ns | 168 / 83 B/op |
| Synchronous non-trivial path combine | 3,692.625 ns | 1,644,322.875 ns | 144 / 4,079 B/op |

The boolean row is intentionally reported as an open latency boundary, not a
performance win. This slice reduces its previous ProGPU median from 5,368,755 ns
and 6,710 B/op by retaining per-device pipelines, removing an unnecessary sleep
after blocking device polling, and eliminating intermediate readback arrays.
The remaining cost is the synchronous CPU-to-GPU-to-CPU round trip required by
the current `SKPath.Op` implementation. The follow-up design must preserve
observable topology and mutation isolation while allowing retained rendering to
consume an unresolved GPU operation; it must not hide the gap with an incorrect
bounds-only result or CPU raster fallback.

The WebGPU path-combine resource change was profiled at exact commit `ebb105be`
against the benchmark baseline. EventPipe measured 6,442,824 to 1,907,781 ns/op
and 6,353 to 3,721 B/op with the same checksum. The baseline attributed 34.68%
exclusive time to synchronous map, 25.08% to the removed sleep, 24.88% to shader
module creation, and 6.13% to pipeline creation; the candidate retained only
synchronous map (72.94%) and device polling (12.91%) as material costs. Matched
Time Profiler measured 8,561,072 to 1,996,378 ns/op. Allocations plus VM Tracker
reduced persistent heap plus anonymous VM from 123,952,704 to 118,978,704 bytes
and total allocation from 4,351,609,392 to 1,703,119,040 bytes. Metal System
Trace retained the same 2,523,136-byte IOAccelerator and 344,064-byte resource
list state, with zero command-buffer errors, drawable waits, hangs, hang risks,
or compiler spills.

The final packed-stroke change was also captured from matched Release binaries:
`2c1062c8` before batching and `12c551a4` after batching plus reversal-safe
flatness. Time Profiler measured 25,735.015 to 23,149.362 ns/op; the Allocations
workload measured 25,716.550 to 22,634.872 ns/op and preserved 80 managed B/op.
Persistent native heap plus anonymous VM changed from 105,450,992 to 105,274,672
bytes, with anonymous VM
unchanged at 101,793,792 bytes. The CPU-only workload submitted no Metal work.

Raw `.nettrace`, `.trace`, `.ktrace`, Instruments scratch, and exported XML were
deleted after retaining compact benchmark JSON, manifests, target logs,
top-function tables, and summaries. The temporary baseline worktree and launch
JSON files were also removed.
