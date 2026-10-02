# Native image-effect allocation measurement

## Boolean vector-mask measurement boundary, 2026-09-29

Build `36543489551`, macOS job `109324228538`, at
`5b2acd00d599fbe3562bdef85288f846292265c5` passed all 131 owned-popup contracts
but reported 4,168 bytes in
`SemanticSceneBuilderWritesBooleanVectorMaskWithoutAllocation(sampleGrid: 4)`.
The other two sample grids passed. This fixture still called `Assert.True`
before its final allocation reading. The reading now immediately follows the
same 10,000 builder calls, before either assertion. All payload validation,
three sample-grid cases, original warmup and exact zero-byte threshold remain.
This excludes assertion execution from measurement; it does not establish the
source of the observed delta or attribute it to the independent runtime report.
Fresh whole exact-head CI is required; the failed producer is not qualified.

## Boolean vector-mask isolated measurement, 2026-10-02

Build `37007183412`, macOS job `110838113585`, at
`b044a8cee25ea32be4842253e013381882c3d023` reported 3,536 bytes for sample grid
4; grids 1 and 8 passed. The managed suite finished with 5,538 passes, one
failure and nine existing skips. The counter already preceded both assertions.
The origin of the measured allocation is not established, and this result is
not attributed to dotnet/runtime issue 134724.

The Boolean fixture now uses the same dedicated-worker and non-inlined
measurement boundary as `SemanticImageEffectBuildsWithoutAllocation` below.
Each worker owns the original stack buffers, six segments, three Boolean nodes,
transform, mask and serialized payload. All three sample grids retain the
original single warmup, payload assertions, 10,000 builder calls and malformed
payload rejection checks. Thread/delegate creation, assertions and exception
propagation stay outside the measured helper. GC stays enabled; the threshold
is exactly zero, with no retries, runtime switches, skips or allowances. The
worker has the existing bounded 30-second join and retains no caller buffers.

Three positive controls, one per original grid, use that identical helper and
payload while publishing a new object through a volatile reference on every
iteration. Each must report at least `10_000 * IntPtr.Size` bytes. They verify
that the measurement still detects escaping allocations, not their provenance
in the failed run. This is test-boundary isolation only: no product builder or
native code changes. Fresh whole exact-head CI remains required.

Build run `34786219474`, Linux job `103802076430`, at `45147156` reports
4,576 passing tests, seven skips and one failure:
`SemanticImageEffectBuildsWithoutAllocation` measured 720 bytes against its
zero-byte requirement. This is separate from the Windows native query failure.

The fixture took its second allocation-counter reading after `Assert.True`.
It now captures the delta immediately after the builder loop and only then
asserts success and zero bytes. The same 10,000 calls, source payload, warm-up
call, validation and exact allocation threshold are preserved. No product code,
test exclusion, retry-to-pass behavior or tolerance is changed. This removes
test-framework execution from the measured region; it does not prove where the
reported 720 bytes originated. A current-head Linux CI result remains required.

The original focused test passes in a fresh local Metal-hosted test process.
With the corrected measurement boundary, all 122 native interop tests pass on
the same host. Neither result is presented as Linux qualification.

## Dedicated measurement boundary, 2026-09-27

The same test reported 720 bytes again in ProGPU Build `36331310015`, Ubuntu job
`108653654700`, at `a5443bf0dc9894e42eeb4ca8ef653f60c4c2c270`. Its counter
already preceded both assertions, so the earlier change alone did not eliminate
the failure. The cause of this new delta is not established; neither a sampled
allocation trace nor the separate macOS runtime-counter report would prove it.

The fixture now follows the original ProGPU-owned
[`FontQualityTests` measurement](font-metric-allocation-validation.md) and
[CAD isolation](PROGPU_CAD_ALLOCATION_VALIDATION.md). A dedicated worker owns the
same stack spans, payload and image/effect values. It performs the original one
warmup, then a non-inlined helper measures all 10,000 unchanged builder calls.
Assertions, thread/delegate creation and exception propagation are outside that
helper. The zero-byte requirement is unchanged; GC remains enabled and there
are no retries, allowances, skips or runtime switches. The worker has a bounded
30-second join and borrows no stack storage from the caller.

A second test uses the identical fixture/helper while publishing a new object
through a volatile reference in every measured iteration. It must detect at
least `10_000 * IntPtr.Size` bytes, so an ineffective counter cannot qualify the
fixture. Both tests retain the existing blurred, unfilterable-planar and invalid
effect checks. No image builder, renderer, shader, C ABI or native resource
lifetime code changes. This is measurement isolation, not an allocation
optimization or proof of the original failure's cause. Full current-head CI
remains required.

Local macOS ARM64 Release validation: the complete `ProGPU.Tests` project builds
with zero warnings/errors, and all 127 `NativeRendererInteropTests` pass without
skips. Direct invocation of the two actual allocation tests under concurrent
bounded allocation and nonblocking generation-2 collection passes 100 exact-zero
measurements and 100 positive controls. These are local measurement controls,
not Linux qualification or native renderer performance evidence. Logs, TRX and
the direct-invocation harness are retained in `artifacts/image-effect-allocation`.
