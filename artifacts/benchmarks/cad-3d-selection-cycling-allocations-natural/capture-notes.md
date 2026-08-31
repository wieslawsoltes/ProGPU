# ProGPU.CAD 3D selection-cycling Instruments capture

Captured on macOS 26.6 with Xcode `xctrace` 16.0 (17E202), .NET
10.0.5, and the final Release binaries recorded by
`cad-3d-selection-depth-8.json`:

- benchmark: `6742277da72e4aba30b00df669ff1f88cf777a8fc2a921669d01ce1fadbb5a95`
- ProGPU.CAD: `89433ba0158ebe432689c1d39632954f0b728f309c7aa35761f6127faa21370c`

The uninstrumented authority uses eight semantic layers over a 128-by-128 grid
(262,144 triangles), three warmups, twelve index builds, and 65,536 queries.
Allocations and Time Profiler launched the same eight-layer grid with one
warmup, two index builds, and 10,000 nearest plus semantic-depth queries; the
target exited naturally inside the ten-second bound. The separate Metal System
Trace uses the identical final binaries and algorithm with eight 16-by-16
layers, one warmup, two builds, and 1,000 queries so Xcode could finalize its
otherwise empty GPU trace reliably.

Raw trace bundles were removed only after compact exports completed. The
Allocations, Time Profiler, and Metal manifests/tables/summaries are retained in
the sibling `cad-3d-selection-cycling-allocations-natural/`,
`cad-3d-selection-cycling-time-natural/`, and
`cad-3d-selection-cycling-metal-natural/` directories. Two longer attempted
captures hit an Xcode finalization timeout; their incomplete traces were
removed automatically and are not part of the checked-in evidence.

The allocation lane reports 20,759,088 persistent heap-plus-anonymous-VM bytes
and 70,010,656 total bytes for process startup, fixture construction, repeated
index construction, and both query families. Managed accounting in the paired
Release JSON reports zero bytes across all 65,536 nearest queries and zero bytes
across all 65,536 eight-hit semantic-depth queries. Metal reports no target
resource, current allocated size, application submission, drawable wait,
compiler spill, hang, or command-buffer error, as expected for a CPU-only query.
