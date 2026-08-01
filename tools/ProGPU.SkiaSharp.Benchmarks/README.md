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
premultiplied-color conversion, 64-element color-array conversion, and retained
path construction/bounds. Each implemented API cluster adds an equivalent workload
or an explicit explanation that its behavior is already covered by a broader
component/application benchmark. GPU/rendering clusters additionally require a
deterministic final-frame workload, image-quality comparison, WebGPU timestamps,
and the platform-native profiler specified by the repository performance policy.
