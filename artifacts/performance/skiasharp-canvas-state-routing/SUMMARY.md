# Retained canvas state routing

This evidence compares exact unpublished Preview.41 tag commit `19867237` with
product commit `b1a30c1c` on an Apple M3 Pro, macOS 26.4.1, and .NET 10.0.5.
The benchmark is `canvas-retained-state-routing` from
`tools/ProGPU.SkiaSharp.Benchmarks`.

## Result

Three interleaved Release process pairs used 128 warmups, 192 samples, and
10,000 state cycles per sample. Across 576 samples per implementation:

| Implementation | Median | P95 | Managed B/op | Checksum |
| --- | ---: | ---: | ---: | ---: |
| Preview.41 | 177.5375 ns | 226.1542 ns | 0 | 17022205643649352006 |
| Candidate | 109.2416 ns | 121.9334 ns | 0 | 17022205643649352006 |
| Official SkiaSharp 4.151.0 | 190.4417 ns | 211.9458 ns | 0 | 17022205643649352006 |

The candidate lowers median latency by 38.47%, raises throughput by 62.52%,
and lowers P95 latency by 46.08% versus Preview.41. It is 42.64% lower latency
than the official wrapper in this process set. Cold one-cycle managed
allocation fell from 7,880 to 4,472 bytes (43.25%); official SkiaSharp reports
1,752 managed bytes but may allocate in its native implementation, so that is
not treated as a total-memory comparison.

## Profiling

Matched 10-million-operation-per-sample captures used the final Release
binaries and the same semantic checksum `4230757312777397478`:

| Capture | Preview.41 | Candidate |
| --- | ---: | ---: |
| Xcode Time Profiler | 220.872 ns/op | 105.889 ns/op |
| Xcode Allocations + VM Tracker | 217.826 ns/op | 109.157 ns/op |
| EventPipe sampled thread time + verbose GC | 231.644 ns/op | 110.841 ns/op |
| Xcode Metal System Trace | 231.903 ns/op | 107.858 ns/op |

EventPipe attributed the baseline hot route through
`PushRectClipScope`, `Buffer.BulkMoveWithWriteBarrier`, and
`PopActiveClipScope`; the removed duplicate active-clip stack frames disappear
from the candidate report. Both Metal summaries report zero target resources,
submissions, waits, errors, spills, hangs, and `currentAllocatedSize` rows,
confirming that this remains a CPU retained-state change. Persistent native
heap plus anonymous VM differed by only 60,176 bytes in the opposite direction
(`105,536,944` versus `105,597,120`), which is startup/JIT noise and is not used
as an improvement claim.

The `baseline-instruments` and `candidate-instruments` directories retain only
compact manifests, target logs, and resolved summaries. Raw Instruments
traces, Xcode scratch, EventPipe traces, temporary JSON, and the exact-baseline
worktree were deleted after extraction; roughly 1.2 GiB of task-owned tracing
and preliminary-capture data was reclaimed.

## Clean-room design record

The design uses public contracts and independently measured behavior only:

- [Skia `SkCanvas`](https://api.skia.org/classSkCanvas.html) defines the
  one-based matrix/clip save stack and restore-to-count behavior.
- [Direct2D axis-aligned clips](https://learn.microsoft.com/windows/win32/direct2d/id2d1rendertarget-pushaxisalignedclip)
  require strict LIFO nesting with layers.
- [Win2D drawing sessions](https://microsoft.github.io/Win2D/WinUI3/html/T_Microsoft_Graphics_Canvas_CanvasDrawingSession.htm)
  keep stateful drawing commands at the retained API boundary.
- [WebRender's rendering overview](https://searchfox.org/mozilla-central/source/gfx/docs/RenderingOverview.rst)
  separates compact retained display lists from frame/GPU preparation.
- [Vello](https://github.com/linebender/vello) keeps transforms and clips in a
  compact scene encoding before GPU evaluation.
- [Parley](https://docs.rs/parley/latest/parley/) and
  [HarfBuzz shaping](https://harfbuzz.github.io/shaping-and-shape-plans.html)
  retain reusable text layout/shaping results; this canvas-state change does
  not move or invalidate that boundary.

ProGPU adopts compact typed value buffers, LIFO state ownership, and deferred
materialization of full clip commands only at a layer snapshot. It rejects
foreign implementation structure, reflection, GPU initialization for state
bookkeeping, and duplicate full-command storage. Save, push, and pop are
amortized O(1); occasional growth is O(D) for stack depth D. A layer snapshot
is O(S + C) time and O(C) output for S active scopes and C clips. Warm steady
state is allocation-free.
