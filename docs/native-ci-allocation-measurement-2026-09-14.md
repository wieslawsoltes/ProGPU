# Native image-effect allocation measurement

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
