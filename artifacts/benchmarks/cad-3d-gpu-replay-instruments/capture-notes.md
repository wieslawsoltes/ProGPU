# ProGPU.CAD Mesh3D replay Instruments capture

Captured on macOS 26.6 with Xcode `xctrace` 16.0 (17E202), .NET 10.0.5,
and the final Release component set recorded by
`cad-3d-gpu-replay-512-batches.json`:

- benchmark: `9bbc4e5f9df9520fb0d2b0d8dcbb12f9395f6183cb60776c9c0a207c23eb79aa`
- backend: `179d837746e2b143572048c68cc923249e94d1a3356f3c8c6dab1109f985d6be`
- CAD: `f79ae4300b9698c4342acf5882771d49fcae4cb5f86b97d57f1add8f3c3c268d`
- scene: `ed323d10ec7a48551410f8ee33c4061a3d761f98ee219295475e6de8bfde3c9c`
- headless host: `693c15b9009d2172ef2ca0871364e496dc59b2167dc8c9d3cf8ffb0c0011b789`
- WinUI: `0da4c3aced74a07cf88acb47629b996ebbbabba895440808425e4da0fcc58150`

Allocations and Time Profiler used a six-second capture with a final
three-second window. Metal System Trace used a four-second capture with a final
two-second window because the longer Metal bundle exceeded the profiler's
bounded finalization time. Every lane launched the same 512-batch workload with
24 warmups and a deliberately long 1,000,000-iteration limit so Instruments,
rather than early process exit, controlled termination.

Raw trace bundles were deleted only after the exported tables completed. The
compact JSON/Markdown summary, TOCs, supported XML exports, profiler logs, and
this identity record are retained. Target logs are empty because `xctrace`
ended each deliberately long workload at the template time limit. Timing and
managed-allocation claims come from the paired uninstrumented 480-frame JSON,
not the instrumented process.
