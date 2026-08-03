# Avalonia.Skia retained-picture hot paths

## Scope and algorithm

Product commit `d22fcef3` is compared with exact Preview.44 merge
`84a86f68`. The tranche covers the recording APIs exercised by the unchanged
Avalonia.Skia 12.0.5 source build:

- optional image-effect payloads use a typed side buffer, reducing the inline
  `RenderCommand` value from 816 to 576 bytes;
- mutable command storage grows geometrically, returns large cleared arrays to
  `ArrayPool<T>`, and retains an exact four-command first buffer rather than a
  minimum 16-element pool bucket;
- immutable pictures classify commands once into compact core, text, texture,
  uncommon-command, and deduplicated-transform arrays; public array inspection
  remains one lazy immutable materialization;
- positioned text blobs lazily publish one owned `Vector2[]` conversion and
  reuse it on later retained draws;
- ordinary solid paints reuse package-private retained brushes and pens until
  relevant paint state changes, while public conversions remain independent;
- ordinary rectangles skip special-shader path construction, and antialiased
  image clips reuse one immutable unit rectangle placed by the command
  transform.

Append is amortized `O(1)`. Snapshot construction is `O(C)` average with `O(C)`
bounded scratch for `C` commands; the transform table is below 0.5 load and is
`O(C^2)` only under adversarial hash collisions. Replay expansion is
allocation-free `O(1)` per command. Solid-paint lookup and unit-clip placement
are allocation-free `O(1)`. The change does not alter DPI/subpixel policy,
coverage sampling, invalidation, resource leases, or WebGPU command encoding.

## Matched Release process benchmark

Apple M3 Pro, macOS 26.4.1, .NET 10.0.5. Three alternating process pairs used
64 warmups and 96 samples per process for a mixed save/translate/clip/rectangle/
path/text/image/restore picture. The pooled distributions contain 288 samples
per build.

| Build | Median ns/op | P95 ns/op | Managed B/op | Checksum |
| --- | ---: | ---: | ---: | ---: |
| Preview.44 | 7,543.213 | 78,139.320 | 35,344 | 2454466986173768955 |
| Candidate | 4,457.357 | 15,668.133 | 1,627 | 2454466986173768955 |

Median latency is 40.91% lower, throughput is 69.23% higher, and managed
allocation is 95.40% lower. Shared-machine scheduling and GC pauses dominate
the tail, so P95 is recorded but not used as the primary claim.

Three official SkiaSharp 4.151.0 processes produced the same checksum at a
pooled 396.078 ns/op median and 10 managed B/op. ProGPU has not reached native
Skia's wrapper latency for this workload. The native managed counter excludes
Skia's native picture storage and is not a total-memory comparison.

## Matched macOS profiling

Every profiler launch used the same final Release binaries and exactly four
warmups plus eight 16,384-operation samples. Both builds completed naturally
and produced checksum `17370592656828381435`.

| Lane | Preview.44 ns/op | Candidate ns/op | Managed B/op | Observation |
| --- | ---: | ---: | ---: | --- |
| Allocations + VM Tracker | 19,748.180 | 3,428.551 | 35,281 -> 1,537 | 82.64% lower instrumented latency |
| Time Profiler | 19,587.496 | 3,421.159 | 35,281 -> 1,537 | 82.53% lower instrumented latency |
| Metal System Trace | 19,956.059 | 3,226.458 | 35,281 -> 1,537 | 83.83% lower instrumented latency |

Allocations reported heap-plus-anonymous-VM total allocation of 2,441,702,816
versus 327,050,480 bytes (86.61% lower) and transient allocation of
2,332,891,472 versus 210,868,688 bytes (90.96% lower). Persistent storage was
108,811,344 versus 116,181,792 bytes: a bounded 7.37 MB increase from reusable
64 KiB pool buckets, so no persistent-footprint improvement is claimed.

The Metal pair is resource-identical: 42 observed resources totaling 3,227,648
bytes, maximum `MTLDevice.currentAllocatedSize` 1,589,248 bytes, zero target
render submissions, and zero drawable waits, compiler spills, potential hangs,
hang risks, or command-buffer errors. This CPU recording optimization neither
adds GPU work nor multiplies GPU resource identity.

A separate matched EventPipe lane used 32 samples and measured 23,176.839
versus 2,460.077 ns/op with 35,281 versus 1,537 managed B/op. Preview.44 spent
exclusive samples in `List<RenderCommand>.AddWithResize`, full command-list
copying, rectangle geometry construction, and paint conversion. Those frames
leave the candidate hot list; remaining samples are dominated by GC polling,
reference clearing, compact snapshot construction, and retained-array
allocation.

## Clean-room research

The design used public contracts and architecture records only:

- [Skia `SkPicture`](https://api.skia.org/classSkPicture.html) for immutable
  replay, operation counting, and approximate owned storage;
- [Direct2D `ID2D1CommandList`](https://learn.microsoft.com/windows/win32/api/d2d1_1/nn-d2d1_1-id2d1commandlist)
  and [Win2D `CanvasCommandList`](https://learn.microsoft.com/windows/apps/develop/win2d/quick-start)
  for retained command/resource separation;
- [DirectWrite and Direct2D text](https://learn.microsoft.com/windows/win32/direct2d/direct2d-and-directwrite),
  [Skia shaped text](https://docs.skia.org/docs/dev/design/text_shaper/),
  [HarfBuzz shaping](https://harfbuzz.github.io/shaping-and-shape-plans.html),
  [HarfBuzz plan caching](https://harfbuzz.github.io/shaping-plans-and-caching.html),
  and [Parley shared layout state](https://docs.rs/parley/latest/parley/) for
  reusable shaping/layout outputs;
- [WebRender retained display lists](https://searchfox.org/mozilla-central/source/gfx/docs/RenderingOverview.rst),
  [Vello's typed GPU scene](https://github.com/linebender/vello), and the
  [WebGPU render-bundle contract](https://gpuweb.github.io/gpuweb/#render-bundle-creation)
  for the immutable CPU-scene/GPU-encoding boundary.

ProGPU adopts immutable typed retained data, bounded scratch reuse, demand-time
materialization, and reusable text-position results. It rejects foreign source
or private layout copying, reflection, boxed per-frame adapters, hiding managed
cost in native allocation, GPU text shaping, and render bundles before current
DPI/atlas/effect/device validation.

## Validation and cleanup

Core tests pass 3,267/3,267, headless tests 225/225, and Avalonia contract tests
86/86. XAML compiler tests pass, the unchanged Avalonia.Skia 12.0.5 project
builds with zero warnings/errors, and the official API gate remains
`reference=4222`, `matching=4222`, `missing=0`, `extra=998`. Documentation and
package-manifest gates pass.

Compact distributions, target logs, and profiler manifests/summaries are kept
beside this file. Raw Instruments bundles, XML exports, Xcode scratch,
EventPipe traces, exploratory runs, and incomplete captures were deleted after
extraction; the cleanup reclaimed more than 3.4 GiB of task-owned temporary
data. No raw `.trace`, `.nettrace`, ETLX, or Speedscope artifact is retained.
