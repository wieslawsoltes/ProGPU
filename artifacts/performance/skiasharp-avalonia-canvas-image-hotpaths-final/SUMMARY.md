# Avalonia.Skia canvas/image hot-path final qualification

Commit: `6466fbdbb2175bd359f98a726c90b4d2456f414a`

The representative workload is the source-built Avalonia 12.0.5
ControlCatalog `Composition` page on macOS arm64. The uninstrumented lane used
180 warm-up plus 600 measured frames and then held the same Release process for
matched EventPipe, native-heap, and `vmmap` sampling. Xcode Instruments launched
fresh Release processes for Allocations plus VM Tracker, Time Profiler, and
Metal System Trace; each capture retained the final 3 seconds of an 8-second
run.

## Uninstrumented and EventPipe result

| Signal | ProGPU | official Skia | Difference |
| --- | ---: | ---: | ---: |
| Frames/s | 80.619 | 78.305 | +2.96% |
| Average frame | 12.386 ms | 12.766 ms | -2.98% |
| P95 frame | 22.616 ms | 17.021 ms | +32.87% (regression) |
| Managed allocation/frame | 6,466.76 B | 7,485.97 B | -13.61% |
| Managed heap after measurement | 18,253,096 B | 15,356,624 B | +18.86% |
| Retained-composition fallback nodes | 0 | n/a | zero CPU fallback |

Both ten-second captures completed four EventPipe/native-memory samples with no
runtime-counter error. ProGPU working set stayed 189.58 to 189.02 MiB. Skia's
first active sample was 173.88 MiB; later `vmmap` inspection coincided with
driver/VM reclamation and ended at 128.80 MiB, so the reduction is not counted
as an application allocation improvement. The first active physical footprint
was 322.20 MiB for ProGPU versus 267.40 MiB for Skia and remains an explicit
optimization target.

## Xcode Instruments result

| Signal | ProGPU | official Skia |
| --- | ---: | ---: |
| Persistent heap + anonymous VM | 198,790,144 B | 205,265,120 B |
| Persistent heap allocation | 42,011,648 B | 37,591,264 B |
| Persistent anonymous VM | 156,778,496 B | 167,673,856 B |
| Persistent IOSurface VM | 26,214,400 B | 39,321,600 B |
| Persistent IOAccelerator VM | 9,486,336 B | 6,520,832 B |
| Drawable waits | 31 / 399.205 ms | 38 / 575.242 ms |
| Command-buffer errors | 0 | 0 |
| Compiler spills | 0 | 0 |
| Potential hangs / hang risks | 0 / 0 | 0 / 0 |
| Metal submissions / completions | 102 / 371 | 40 / 370 |

The Allocations aggregate is 3.15% lower for ProGPU, driven by lower anonymous
VM and one fewer IOSurface, while ProGPU heap allocation and IOAccelerator VM
remain higher. The ProGPU Metal export did not expose
`MTLDevice.currentAllocatedSize`; no comparison is inferred from Skia's
62,734,336-byte row. Neither Metal export exposed resource-allocation rows.

All six raw `.trace` bundles, allocation-list/table XML exports, Xcode scratch
`.ktrace` files, temporary `.gcdump` files, and heap dumps were deleted after
the compact JSON/Markdown summaries and target logs were written. The manifests
record the exact reclaimed byte counts.

## Interpretation

This slice passes correctness, average-throughput, managed-allocation, and
zero-fallback gates. It does not establish that every Avalonia.Skia workload is
faster or smaller: P95 frame latency, managed retained heap, active physical
footprint, native heap allocation, and IOAccelerator VM are measured remaining
targets. Surface readback, surface composition, immutable-image recording,
mixed-picture recording, path combination, and SaveLayer recording also remain
slower than the official package in the matched microbenchmark report.
