# CAD 3D projected pick-target Instruments capture notes

Captured on 2026-09-01 from the final Release benchmark binaries:

- `ProGPU.CAD.Benchmarks.dll`: `6e43154a4a4a014932b5e871a0075570dd656f4ec0f34dea43ecfdc019773949`
- `ProGPU.CAD.dll`: `aa564b3a90a465b0583b022ed3706735707f08edcbd436babdbc22e05c18dfd5`

The Allocations and Time Profiler targets used a 128-by-128 grid with eight
depth layers, one warmup, two measured build iterations, and 10,000 queries in
each of the exact point, semantic-depth, projected three-DIP pick-target, and
projected Crossing families. The Metal target used the same code and query
families with a 16-by-16 grid, eight layers, one warmup, two iterations, and
1,000 queries so it could exit naturally inside the ten-second capture.

All three final manifests report `RecordExitCode` zero. Allocations reports
19,140,992 persistent and 71,071,568 total heap-plus-anonymous-VM bytes for the
complete process workload. The standalone benchmark accounting reports zero
managed allocation for every warm query family. Metal reports zero target GPU
resources, current allocated bytes, application command-buffer submissions,
drawable waits, compiler spills, hangs, and errors. The system-wide Metal
export includes completion events not paired with target submissions; they are
not attributed to this CPU-only benchmark.

Earlier captures made before the final closest-point correction, a
duration-limited Allocations attempt, and an initial tiny-grid Metal run whose
benchmark validation rejected the fixture were replaced. They are not used as
evidence. Raw `.trace` bundles were removed after the compact XML, JSON,
Markdown, logs, and manifests were exported.
