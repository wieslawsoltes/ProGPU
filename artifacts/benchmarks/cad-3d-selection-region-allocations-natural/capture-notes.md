# ProGPU.CAD projected 3D region-selection Instruments capture

Captured on macOS 26.6 with Xcode `xctrace` 16.0 (17E202), .NET
10.0.5, and the final Release binaries recorded by
`cad-3d-selection-depth-8.json`:

- benchmark: `18b28bb88fc1128a2b1ba448fccea19c58974732df3b06c7a2de9e2744256fce`
- ProGPU.CAD: `1d16a1dda0151684b5fadfba6f9001719b7917dbb51fb393da31a9bd40d5315b`

The uninstrumented authority uses eight semantic layers over a 128-by-128 grid
(262,144 triangles), three warmups, twelve index builds, and 65,536 point,
semantic-depth, and projected-Crossing queries. Allocations and Time Profiler
launched the same eight-layer grid with one warmup, two index builds, and
10,000 queries of every family; each target exited naturally inside the
ten-second bound. The separate Metal System Trace uses the identical final
binaries and algorithms with eight 16-by-16 layers, one warmup, two builds, and
1,000 queries so Xcode can finalize an otherwise empty GPU trace reliably.

Raw trace bundles were removed only after compact exports completed. The
Allocations, Time Profiler, and Metal manifests, tables, logs, target output,
and summaries are retained in the sibling
`cad-3d-selection-region-allocations-natural/`,
`cad-3d-selection-region-time-natural/`, and
`cad-3d-selection-region-metal-natural/` directories.

The allocation lane reports 20,751,776 persistent heap-plus-anonymous-VM bytes
and 70,316,448 total bytes for process startup, fixture construction, repeated
index construction, and all three query families. Managed accounting in the
paired Release JSON reports zero bytes across all 65,536 exact point queries,
zero bytes across all 65,536 eight-hit semantic-depth queries, and zero bytes
across all 65,536 exact projected-Crossing queries. Metal reports no target
resource, current allocated size, application submission, drawable wait,
compiler spill, hang, or command-buffer error, as expected for CPU-only query
code.
