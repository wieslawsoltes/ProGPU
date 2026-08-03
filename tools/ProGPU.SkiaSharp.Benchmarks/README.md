# ProGPU SkiaSharp benchmarks

This project compiles the same benchmark source twice: once against official
SkiaSharp 4.151.0 and once against the ProGPU shim. The runner alternates three
native/ProGPU process pairs, warms every workload, retains every timing and
managed-allocation sample, verifies exact semantic checksums, and reports
median and p95 distributions.

Run:

```bash
./eng/progpu-run-skiasharp-benchmarks.sh
```

Artifacts are written to `artifacts/performance/skiasharp/`, including runtime,
OS, hardware, commit, dirty-state, raw-run, and combined comparison evidence.
Ratios below `1.0` favor ProGPU. Results from ordinary shared machines are not
used as narrow timing gates; dedicated platform runs establish reviewed budgets.

The CPU suite covers value arithmetic, matrix mapping, exhaustive scalar
premultiplied-color conversion, 64-element color-array conversion, OpenType tag
value/formatting operations, and retained path construction/bounds. It also
tracks the exact reusable-paint, positioned-text-blob, legacy stream-path, and
`WriteableBitmapImpl.GetSnapshot()` CPU-framebuffer-to-immutable-image patterns
used by Avalonia.Skia. A paired retained-picture workload records repeated draws
of that immutable image and measures whether deferred rendering reuses its GPU
resource. A mixed retained-picture workload combines saved transforms, clips,
rectangles, paths, positioned text, and immutable images so command-storage
changes are measured across the broader Avalonia.Skia recording shape rather
than against a single command kind. Shim optimization therefore follows real framework call sites rather
than metadata frequency alone. Canvas coverage decomposes retained
save/restore, matrix, and clip routing so regressions can be attributed without
changing the full framework-shaped workload. Each implemented API cluster adds
an equivalent workload
or an explicit explanation that its behavior is already covered by a broader
component/application benchmark. GPU/rendering clusters additionally require a
deterministic final-frame workload, image-quality comparison, WebGPU timestamps,
and the platform-native profiler specified by the repository performance policy.

The Avalonia surface family mirrors its reusable render-target lifecycle: a
surface is cleared and flushed for successive frames, one surface is composed
into another through `SKSurface.Draw`, and the framebuffer conversion fallback
snapshots a CPU-backed surface before reading it into the destination format.
The family also measures repeated direct `SKSurface.ReadPixels` calls so staging
buffer reuse is covered independently from snapshot ownership, plus repeated
`SKImage.ReadPixels` calls against one immutable GPU snapshot so image-owned
staging lifetime is measured directly.
Each workload validates the final or per-frame pixels so asynchronous submission
or retained-content optimizations cannot silently omit rendering work.
