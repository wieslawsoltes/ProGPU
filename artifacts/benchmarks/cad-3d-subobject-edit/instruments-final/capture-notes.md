# ProGPU.CAD modern-MESH subobject-edit Instruments capture

Captured on macOS 26.6 with Xcode `xctrace` 16.0 (17E202), .NET
10.0.5, and the same final Release binaries identified by
`../final-release.json`:

- benchmark: `4cd8f1031e0139b5746d8f705505c4bff2a9f4bc0f795741d17531ed43bb1e11`
- ProGPU.CAD: `3c1085691076c6eb7c4368c2c7f8280ec1a39c7b1fcd95ce20f3a876be33f421`

Allocations, Time Profiler, and Metal System Trace each launched the exact
128-by-128 modern-MESH workload with 16,641 control vertices, 16,384 authored
faces, 1,024 selected faces, two warmups, and 16 edit/snapshot/scene rebuilds.
Raw trace bundles were deleted only after compact XML exports completed. The
retained manifest, target logs, tables, and JSON/Markdown summary show that all
three recordings exited zero and that each launched target emitted the complete
benchmark report with the hashes above.

The paired uninstrumented 24-iteration run is the latency and managed-allocation
source. Full generation-safe edit plus pre/post snapshot and Mesh3D scene
compilation measured 416.4275/470.7665/487.7460 ms p50/p95/p99 and
209,354,321 managed bytes per operation. The exact retained undo+redo pair
measured 0.0240/0.0297/0.0303 ms with 304 managed bytes per pair; those bytes
come from `CadDocumentHistory` reason strings and session events, while the
command's retained coordinate assignment allocates nothing.

Allocations reported 20,251,744 persistent and 628,644,544 total
heap-plus-anonymous-VM bytes across process startup and the instrumented
workload. Time Profiler retained its samples and reported no potential hang or
hang risk. Metal reported zero target resources, current allocated size,
application submissions, drawable waits, compiler spills, hangs, or errors.
Its completion rows are unrelated system activity because this CPU document
edit and immutable scene compilation never initialize WebGPU.
