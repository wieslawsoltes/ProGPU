# Lazy empty and packed `SKPath` geometry

## Scope and algorithm

Product commit `c8213c55ae6b09f84ce6afb3f3af60dff486bbda` is compared with
exact merged main `3c3c46b816a05b10085b229a083808454d6ec925`.
`SKPath` no longer constructs an empty `PathGeometry` before its packed-path
constructor immediately discards it. Empty paths create geometry only when a
caller requests `Geometry` or performs a mutation that needs the legacy object
graph. Packed paths retain analytic commands and materialize exactly as before.

Empty construction, packed detach, and packed bounds remain `O(1)` beyond the
already retained command stream. First materialization remains `O(N)` time and
storage for `N` analytic commands. No tessellation, WebGPU initialization,
upload, or command submission is added.

## Matched benchmark evidence

Apple M3 Pro, macOS 26.4.1, .NET 10.0.5, Release. Three interleaved process
pairs used 128 warmups, 192 samples per process, and 10,000 path builds per
sample. Across 576 samples per side:

| Build | Median ns/op | P95 ns/op | Managed B/op | Checksum |
| --- | ---: | ---: | ---: | ---: |
| merged main | 385.3167 | 473.5750 | 224 | 8402956917441101891 |
| candidate | 386.8417 | 524.6834 | 136 | 8402956917441101891 |

Managed allocation falls 39.29%, exceeding the 25% slice target. Median
latency differs by 0.40%, inside process/frequency noise; the tail contained a
single candidate-side scheduler outlier, so no process-pair latency or P95
claim is made. The shorter default workload exposed the same allocation result
but remained below the preferred multi-millisecond timer floor.

Three interleaved official SkiaSharp 4.151.0 comparisons measured approximately
725.5 versus 529.3 ns/op and 168 versus 136 managed B/op with checksum
`4054777027411939427`. The candidate is about 27% faster and uses 19.05% fewer
managed bytes. Official managed counters exclude native Skia allocations, so
this is not a total-memory claim.

## Matched macOS profiling

| Profiler | Workload | main ns/op | candidate ns/op | Managed B/op | Result |
| --- | --- | ---: | ---: | ---: | --- |
| Time Profiler | 3 x 5,000,000 | 523.428 | 406.363 | 224 -> 136 | 22.36% lower latency; 28.80% higher throughput |
| Allocations + VM Tracker | 3 x 500,000 | 418.704 | 406.572 | 224 -> 136 | 2.90% lower latency |
| EventPipe sampled thread time | 3 x 5,000,000 | 427.227 | 440.719 | 224 -> 136 | whole-process timing was 3.16% slower; no throughput claim |
| Metal System Trace | 3 x 1,000,000 | 425.999 | 414.783 | 224 -> 136 | 2.63% lower latency |

Every pair preserved its exact checksum. EventPipe attributed 0.27% exclusive
baseline samples to `SKPathBuilder.Detach` versus 0.10% for the candidate; the
intended benchmark loop retained 98.72%/98.96% exclusive samples. Both Metal
traces exported zero target command-buffer submissions and zero
`MTLDevice.currentAllocatedSize` rows, confirming this remains CPU-only.

## Clean-room research

- [Skia `SkPathBuilder`](https://api.skia.org/classSkPathBuilder.html) defines
  snapshot, detach, analytic verbs, and reserve behavior.
- [Direct2D `ID2D1GeometrySink`](https://learn.microsoft.com/windows/win32/api/d2d1/nn-d2d1-id2d1geometrysink)
  preserves figures and analytic line, quadratic, cubic, and arc segments.
- [Win2D path behavior](https://learn.microsoft.com/dotnet/communitytoolkit/archive/windows/win2d-path-mini-language)
  keeps figure construction distinct from final geometry use.
- [WebRender scene building](https://searchfox.org/firefox-main/source/gfx/wr/webrender/src)
  separates retained display data from frame-specific rendering work.
- [Vello](https://github.com/linebender/vello) and its
  [encoding contract](https://docs.rs/vello_encoding/latest/vello_encoding/struct.Encoding.html)
  retain compact scene/path encoding before GPU execution.
- [Skia shaped text](https://docs.skia.org/docs/dev/design/text_shaper/),
  [DirectWrite glyph runs](https://learn.microsoft.com/windows/win32/directwrite/glyphs-and-glyph-runs),
  [Parley](https://docs.rs/parley/latest/parley/), and
  [HarfBuzz](https://harfbuzz.github.io/shaping-and-shape-plans.html) confirm
  that reusable shaping/layout results remain outside this geometry ownership
  change.

ProGPU adopts lazy retained storage and preserves analytic commands until an
explicit materialization boundary. It rejects eager tessellation, reflection,
GPU initialization, copied foreign control flow, and any reshaping or glyph
reconstruction. No foreign source was copied, translated, or adapted.

## Validation and cleanup

The focused path suite passes 93/93, including a tightened 192-byte ceiling
for both small and 256-segment packed detach. Core passes 3,242/3,242, headless
passes 225/225, and the XAML compiler passes 307/307. Official API metadata
remains 4,222/4,222 required with zero missing; docs and package manifests pass.
Compact process distributions and profiler target JSON remain in this
directory. After extraction, 272 MiB of raw
Instruments/EventPipe data and 102 MiB of exact-baseline build state were
deleted. No task-owned trace, scratch directory, or temporary worktree remains.
