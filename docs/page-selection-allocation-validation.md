# Page device selection allocation measurement

ProGPU Build `36500155149`, job `109188833190`, failed
`PrinterSettingsCollectionQualityTests.WarmedPageDeviceSelectionReadsAllocateNothing`
at commit `3b35c2f89c07414f3a32260c1c045e25443da8fd`: the counter reported
32,664 bytes instead of zero. The quality job passed 798 other tests. Artifact
`11005415733` retains the original TRX and EventPipe trace. The cause of this
specific delta is not established; the independent runtime counter reproducer
does not attribute this product-test failure.

The retained environment is Ubuntu 24.04 x64, SDK 10.0.401 and runtime 10.0.12.
The complete 49,747,026-byte trace has SHA-256
`4a446b8ccc95de3f08468eca8a8c26399bf81edcc8619081f5777e791da0d390`.
Its parser reported no lost events; sampled allocations still do not prove the
absence or origin of a particular counter delta.

The revised test uses the existing private-font and CAD measurement isolation
pattern. Settings, worker/delegate construction and assertions remain outside a
dedicated worker's non-inlined measurement. It retains exactly the original two
warmup reads and all 100,000 iterations. The two actual property values now feed
an asserted checksum, rather than being discarded. GC stays enabled, the result
must be exactly zero, and no retries, discounts, runtime switches or test
exclusions are introduced. The worker has a bounded 30-second join and propagates
exceptions. The original CI job deadline is unchanged.

A positive control uses the same measurement and publishes an object through a
volatile reference on every iteration. It must observe at least
`100_000 * IntPtr.Size` bytes and the same property-value checksum. This prevents
an ineffective counter from qualifying the zero-allocation result.

This changes the test boundary, not printer functionality or rendering. A local
pass does not qualify the original failed Build or permit package staging: the
complete producer Build must pass on the actual updated commit.

All 13 page-settings collection tests pass locally on macOS ARM64, including
the unchanged exact-zero requirement and the new positive control, with no skips.
