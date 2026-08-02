# Native byte-table pixel swizzle evidence

Product comparison:

- baseline: exact Preview.39 merge `3efcf9e5116aaadebce44f202b618a9d4d430a87`
- candidate: product commit `bfc6c62a52583b3c1500fb2c5402d98b490c968d`
- hardware: Apple M3 Pro, macOS 26.4.1, arm64, .NET 10
- workload: `swizzle-in-place-4k`
- protocol: three interleaved process pairs, 128 warmups, 192 samples per process
- checksum: `12185046443090060243`
- baseline aggregate median: `90.3750 ns/op`
- candidate aggregate median: `79.9417 ns/op`
- latency change: `-11.55%`
- throughput change: `+13.05%`
- managed allocation: `0 B/op` for both
- candidate aggregate p95: `109.4250 ns/op`; no tail claim is made because the baseline distribution contains scheduler interference

Profiler correlation used 20,000,000 operations per sample and checksum
`895921851728446851`:

| Lane | Preview.39 | Candidate | Change | Allocation/resource result |
| --- | ---: | ---: | ---: | --- |
| Time Profiler | 93.264 ns/op | 46.231 ns/op | -50.43% | 0 managed B/op |
| Allocations + VM Tracker | 97.338 ns/op | 49.173 ns/op | -49.48% | 0 managed B/op; persistent heap+anonymous VM 107,098,816/107,111,136 B |
| EventPipe sampled thread time | 92.400 ns/op | 85.772 ns/op | -7.17% | swizzle body 95.70%/95.18% exclusive samples |
| Metal System Trace | 97.363 ns/op | 47.386 ns/op | -51.33% | zero target submissions, errors, spills, hangs, resources, or current-size rows |

`baseline-*.json` and `candidate-*.json` are the raw uninstrumented
distributions. `profile/*.json` and target logs are the compact results emitted
by the exact binaries under each profiler. The Preview.39 benchmark harness
received only the missing direct `ProGPU.Backend` project reference required
to build it in an isolated worktree; no baseline product source was changed.

All raw `.trace`, `.nettrace`, Speedscope, TOC/table exports, per-capture Xcode
scratch, and the temporary exact-baseline worktree were deleted after these
figures were extracted, reclaiming 535 MiB.
