# Private font metric allocation measurement

On 2026-09-27, ProGPU Build `36310820092`, job `108596256282`, failed
`FontQualityTests.WarmedPrivateMetricReadsAreAllocationFree` with 1,024 bytes
instead of zero at commit `9ccd5eb0396d5a4c920bc65059ddba0a8f653b11`.
The retained environment identifies Ubuntu 24.04 x64, SDK 10.0.401 and host
runtime 10.0.12. Artifact `10929087437` has verified ZIP SHA-256
`eb8e165fcb22889f328c0f0b83b568cac3b7101f41f2c310f717ca1517662e99` and retains
the failing TRX and testhost EventPipe trace.

The cause of that delta is not established. In particular, the separate
[macOS ARM64 counter reproducer](https://github.com/dotnet/runtime/issues/134724)
does not prove the cause of this Linux failure. Sampled allocation events also
cannot prove absence of a 1,024-byte allocation. The original 13 font tests
passed locally; this was not a deterministic local reproduction.

The revised measurement follows the existing
[CAD measurement isolation](PROGPU_CAD_ALLOCATION_VALIDATION.md): font loading,
thread/delegate construction and assertions stay outside a dedicated worker's
non-inlined measurement method. Preserve one warmup of each of the same four
metric reads, followed by all 1,000 iterations. GC remains enabled, the result
must still be exactly zero, and there are no retries, allowances or runtime
switches. The worker has a bounded 30-second join and propagates exceptions.
The family remains owned by the calling test throughout successful measurement.

A positive control uses the same method and publishes a new object through a
volatile reference in every measured iteration. It must report at least
`1000 * IntPtr.Size` bytes, so a disabled or ineffective counter cannot qualify
the test. The metric total must remain positive in both cases.

Local Release validation on macOS ARM64: all 14 actual font tests pass. Direct
invocation of the two actual allocation tests under concurrent bounded
allocation and nonblocking generation-2 collection passed 100 zero-allocation
measurements and 100 positive controls. The required full Linux CI gate remains
authoritative and must pass on the updated commit before merge.

This is a test-boundary change, not a font or rendering optimization. Neither
managed nor native renderer code, font selection, shaders, ABI, GPU resource
lifetime, CI deadlines or test exclusions change.
