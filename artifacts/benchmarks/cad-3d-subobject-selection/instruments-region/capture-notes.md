# ProGPU.CAD exact Mesh3D subobject-region Instruments capture

Captured on macOS 26.6 with Xcode `xctrace` 16.0 (17E202), .NET
10.0.5, and the same final Release binaries identified by
`../final-release.json`:

- benchmark: `f28aaaf55e771bb948e4adc3d5d6b10ec0b9e031d581325db392674d53bf6d35`
- ProGPU.CAD: `80a515ac8a54b24c47c9e0bf4f057c14c61c65d641e1d54d50e6cbfd64a38dd5`

Allocations, Time Profiler, and Metal System Trace each launched the exact
128-by-128, four-layer modern-MESH selection workload with two warmups, six
index-build iterations, and 10,000 queries per lane. Raw trace bundles were
deleted only after compact XML exports completed; the retained manifest,
target logs, tables, and JSON/Markdown summary preserve capture identity and
prove all three targets exited zero.

The paired uninstrumented 65,536-query lanes are the source of latency and
managed-allocation claims. Rectangle/lasso/Fence exact face-subobject queries
measured 237.0/264.0/310.5, 249.2/279.7/323.9, and
193.1/215.0/233.3 microseconds p50/p95/p99 respectively, with zero managed
allocation in every lane.

Allocations reported 22,079,024 persistent and 73,332,128 total
heap-plus-anonymous-VM bytes for process startup, topology/index construction,
and every query family. Time Profiler reported no potential hang or hang risk.
Metal reported no target resources, current allocated size, command
submission, drawable wait, compiler spill, hang, or command-buffer error. This
is expected because selection remains a device-independent CPU query and does
not initialize WebGPU or cross the managed/native rendering boundary.
