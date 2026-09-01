# CAD 3D projected lasso and fence Instruments capture notes

Captured on 2026-09-01 from the final Release benchmark binaries:

- `ProGPU.CAD.Benchmarks.dll`: `b144a5eb32c2995b30f6ec48be7296876c82f5b0e2039d2671a7b5c2e770bc69`
- `ProGPU.CAD.dll`: `5be65dd7df43b5f30c15d4b7604f79fac64cd635c284f29bb6b0584bf5217e16`

The Allocations and Time Profiler targets used a 128-by-128 grid with eight
depth layers, one warmup, two measured build iterations, and 10,000 queries in
each of the exact point, semantic-depth, projected three-DIP pick-target,
projected Crossing, projected three-point lasso, and projected two-point Fence
families. The Metal target used the same code and query families with a
16-by-16 grid, eight layers, one warmup, two iterations, and 1,000 queries so it
could exit naturally inside the ten-second capture.

All three final manifests report `RecordExitCode` zero. Allocations reports
20,850,448 persistent and 71,332,080 total heap-plus-anonymous-VM bytes for the
complete process workload. The standalone benchmark accounting reports zero
managed allocation for every warm query family, including lasso and Fence.
Metal reports zero target GPU resources, current allocated bytes, application
command-buffer submissions, drawable waits, compiler spills, hangs, and
errors. Any system-wide completion events are not attributed to this CPU-only
benchmark when no target submission exists.

Raw `.trace` bundles were removed after the compact XML, JSON, Markdown, logs,
and manifests were exported.
