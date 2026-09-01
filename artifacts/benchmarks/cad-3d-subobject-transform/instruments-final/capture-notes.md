# ProGPU.CAD modern-MESH subobject-transform Instruments capture

Captured on macOS 26.6 with Xcode `xctrace` 16.0 (17E202), .NET
10.0.5, and the same final Release binaries identified by
`../final-release.json`:

- benchmark: `c05c51fc1dc53f270c4bfa7c135c00e0019de56406e634c01f8c7fbc5d6de811`
- ProGPU.CAD: `8354ee5f175d5793730b7e9f00b32519c1184442347d47a8e2926cb09b15eb8a`

Allocations, Time Profiler, and Metal System Trace each launched the exact
128-by-128 modern-MESH workload with 16,641 control vertices, 16,384 authored
faces, 1,024 selected faces, two warmups, and 16 iterations apiece of
translation, rotation, and scale plus snapshot and Mesh3D scene rebuild. Raw
trace bundles were deleted only after compact XML exports completed. The
retained manifest, target logs, tables, and JSON/Markdown summary show that all
three recordings exited zero and that each launched target emitted the complete
benchmark report with the hashes above.

The paired uninstrumented 24-iteration-per-transform run is the latency and
managed-allocation source. Complete transform plus pre/post snapshot and Mesh3D
scene compilation measured the following p50/p95/p99 values:

- translation: 408.1063/448.6865/463.4769 ms and 209,356,224 managed bytes;
- rotation: 431.4559/548.9130/604.4687 ms and 209,047,849 managed bytes; and
- scale: 410.4458/670.3365/681.9217 ms and 209,357,734 managed bytes.

The exact retained undo+redo pairs measured 0.0245/0.0279/0.0288 ms with 304
managed bytes for translation, 0.0286/0.0710/0.0736 ms with 288 bytes for
rotation, and 0.0090/0.0113/0.0142 ms with 288 bytes for scale. Those bytes
come from `CadDocumentHistory` reason strings and session events; replaying the
command's retained coordinates creates no new coordinate storage.

Allocations reported 14,034,240 persistent and 1,617,079,344 total
heap-plus-anonymous-VM bytes across process startup and all three instrumented
lanes. Time Profiler retained its samples and reported no potential hang or
hang risk. Metal reported zero target resources, current allocated size,
application submissions, drawable waits, compiler spills, hangs, or errors.
Its completion rows are unrelated system activity because this CPU document
edit and immutable scene compilation never initialize WebGPU.
