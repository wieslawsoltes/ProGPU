# ProGPU.CAD projected 3D selection Instruments capture

Captured on macOS 26.6 with Xcode `xctrace` 16.0 (17E202), .NET
10.0.5, and the same final Release binaries identified by
`cad-3d-selection-grid-256.json`:

- benchmark: `c5bb00239cd4b4d33e9e1df0752649a4a045e2153ceea7720fb35cc422e9eb70`
- ProGPU.CAD: `fc94cf72edd2d4c5b1af0e3a6bc8798e2a20a74ab5933b4c013d82cc55eb2b69`

Allocations, Time Profiler, and Metal System Trace each captured a final
three-second window from a six-second launch of the same 512-by-512 grid
workload (524,288 triangles), with three warmups, twenty index builds, and a
two-million-query bound. Instruments ended the deliberately long target at its
time limit; the empty target logs are therefore expected.

Raw trace bundles were deleted only after their compact XML exports completed.
The retained manifest, tables, profiler logs, and JSON/Markdown summary record
the capture identity. The paired uninstrumented 256-by-256 lane is the source
of latency and managed-allocation claims. Its 131,072-triangle index retained
2,359,256 bytes, built at 23.2322/41.6313/41.6313 ms p50/p95/p99, and queried
at 1.5/9.1/19.4 microseconds p50/p95/p99 while testing eight triangles and
allocating zero managed bytes per query.

Allocations reported 19,788,704 persistent heap-plus-anonymous-VM bytes and
60,382,960 total bytes during the larger instrumented workload. Metal reported
no target resource allocation, current allocated size, command submission,
drawable wait, compiler spill, hang, or command-buffer error. This is expected:
selection is a device-independent CPU query and neither initializes WebGPU nor
crosses the managed/native rendering boundary.
