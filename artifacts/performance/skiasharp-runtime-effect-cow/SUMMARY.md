# Runtime-effect immutable snapshot optimization

## Scope and algorithm

Product commit `fa77c4ad24942431e8369b92cbeda342d77c948b` is compared with
the exact Preview.40 merge `3dbf79b802ba346ae72959f0c0a22d7ec0af3f1a`.
`SKRuntimeEffectUniforms` now publishes its current byte storage as an immutable
snapshot and clones only when a later mutation would modify shared storage.
Child-free effects reuse the empty child array, and identity instances no longer
store a per-object `SKMatrix`; a derived instance carries the matrix only for a
non-identity transform.

Creating another snapshot is `O(1)` time and storage beyond the retained
instance. The first mutation after publication is `O(U)` time and storage for
`U` uniform bytes; later mutations before another publication are `O(1)`.
Child copying remains `O(C)` for `C` declared children. The implementation does
not initialize WebGPU, upload data, or submit GPU work.

## Matched process benchmark

Apple M3 Pro, macOS 26.4.1, .NET 10.0.5, Release. Three interleaved process
pairs used 128 warmups, 192 samples per process, and 10,000 operations per
sample. The pooled result contains 576 samples per side.

| Build | Median ns/op | P95 ns/op | Managed B/op | Checksum |
| --- | ---: | ---: | ---: | ---: |
| Preview.40 | 220.8291 | 271.3584 | 544 | 1721237190835759209 |
| Candidate | 140.1958 | 257.5708 | 360 | 1721237190835759209 |

Median latency is 36.51% lower, throughput is 57.51% higher, and managed
allocation is 33.82% lower. Scheduler interference dominates the tail, so no
P95 improvement is claimed. An exploratory matched official SkiaSharp 4.151.0
process set measured 1,424.2417 versus 143.3208 ns/op and 824 versus 360
managed B/op with the same checksum. That counter excludes native allocations,
so it is not used as a total-memory comparison.

## Matched macOS profiling

All profiler pairs launched the exact commits above and produced identical
checksums within each pair.

| Profiler | Workload | Preview.40 ns/op | Candidate ns/op | Managed B/op | Observation |
| --- | --- | ---: | ---: | ---: | --- |
| Time Profiler | 3 x 20,000,000 | 206.865 | 175.498 | 544 -> 360 | 15.16% lower latency; 17.87% higher throughput |
| Allocations + VM Tracker | 3 x 500,000 | 259.502 | 177.112 | 544 -> 360 | 31.75% lower latency; no retained managed regression |
| EventPipe sampled thread time | 3 x 10,000,000 | 212.559 | 166.342 | 544 -> 360 | 21.74% lower latency; 27.79% higher throughput |
| Metal System Trace | 3 x 20,000,000 | 224.460 | 172.181 | 544 -> 360 | 23.29% lower latency; 30.36% higher throughput |

EventPipe attributed 38.54% exclusive baseline samples to `ToShader`; that
frame disappeared from the candidate top 15 after the compact path became
inlineable, while 98.79% exclusive candidate samples stayed in the intended
benchmark loop. Both Metal traces exported zero target command-buffer
submissions and zero `MTLDevice.currentAllocatedSize` rows. This confirms that
the CPU snapshot path does not create a GPU device or multiply native GPU
resources.

## Clean-room research

The design used public contracts and architecture documentation only:

- [Skia Runtime Effects and SkSL](https://docs.skia.org/docs/user/sksl/) for
  immutable effect inputs and explicit child/uniform boundaries.
- [Direct2D supported pixel formats and alpha modes](https://learn.microsoft.com/windows/win32/direct2d/supported-pixel-formats-and-alpha-modes)
  and [Win2D premultiplied alpha](https://microsoft.github.io/Win2D/WinUI2/html/PremultipliedAlpha.htm)
  for retained resource/value separation.
- [WebRender blob-image architecture](https://searchfox.org/firefox-main/source/gfx/wr/webrender/doc/blob.md)
  for retained immutable payloads and demand-driven downstream work.
- [Vello](https://github.com/linebender/vello) for typed retained scene/resource
  boundaries rather than per-call compatibility adapters.
- [DirectWrite glyph runs](https://learn.microsoft.com/windows/win32/directwrite/glyphs-and-glyph-runs)
  and [HarfBuzz shaping outputs](https://harfbuzz.github.io/shaping-and-shape-plans.html)
  to verify that this effect snapshot remains below and independent from
  reusable shaping/layout state.

ProGPU adopts immutable retained snapshots, copy-on-write mutation isolation,
and compact identity state. It rejects sharing writable snapshot storage,
reflection, a CPU-pixel fallback, recomputing text state, copied foreign control
flow, and GPU initialization for this CPU ownership API. No foreign source was
copied, translated, or adapted.

## Validation and cleanup

Focused runtime-effect tests pass 7/7, including mutation isolation,
non-identity transform fidelity, and a 400-B/op allocation ceiling. The full
core suite passes 3,242/3,242, the headless suite passes 225/225, and the XAML
compiler suite passes 307/307. The official API metadata gate remains
`reference=4222`, `matching=4222`, `missing=0`, and `extra=998`; documentation
and package-manifest gates pass. Exact-head CI initially passed every completed
Ubuntu/macOS/native-Dawn/portable/mobile/retained-text/official-Skia gate; final
exact-head results are recorded in PR #66.

Compact raw distributions and target-result JSON remain in this directory.
After extraction, approximately 8.6 GiB of task-owned EventPipe conversion
intermediates, Instruments traces, Xcode scratch, and exact-baseline build state
were deleted. The final profiler directory alone was 952 MiB and the detached
Preview.40 worktree was 102 MiB. No task-owned `.trace`, `.nettrace`,
Speedscope, scratch directory, path marker, or temporary worktree remains.
