# WebScene timeout evidence

The original WebScene exact-provider test is the acceptance application. Its
unchanged CTest command retains all source fixtures, pixel/counter assertions,
the pinned provider and GPU_COMPLETE presentation. Its original 60-second limit
has the narrowly scoped release exception documented below.
Issue367 requires actual hosted stack evidence before changing renderer or
presentation behavior; a faster local run does not explain hosted timing.

The verification script now preserves its original CTest output and exit status
through `eng/progpu-webscene-timeout-diagnostics.py`. Successful runs are neither
sampled nor replayed. Only an original CTest timeout starts one separate diagnostic
replay of the same executable/provider. It has its own 60-second process bound,
fresh output directory, input hashes and `qualified:false` receipt. macOS `sample`
collects up to 55 seconds at a 10-millisecond interval. Target and collector are
owned and retired independently. Collector failure is retained; it cannot repair
the original gate. There is no retry-until-pass, and the diagnostic replay keeps
its original 60-second limit.

Build and Release upload the original evidence and separate diagnostic directory
even on failure. A successful diagnostic replay still returns the original failed
CTest status, preventing package production and the managed consumer from using
an incomplete producer. Stack residency and measured phase costs remain evidence
for investigation, not proof of a particular performance cause.

## Owner-approved release exception — 13 October 2026

The owner explicitly approved 120 seconds for the complete macOS
`progpu_native_webscene_provider_tests` CTest case after reviewing both retained
60-second failures from Build `38420884031` and the separate original-workload
diagnostics `38437426850` and `38439073114`. This overrides only that test's
timeout. It is a release exception, not a performance fix.

Every original command, fixture, provider, shader, complete-image comparison,
pixel/counter assertion, GPU_COMPLETE presentation and final submission count
remains required. No warmup, early success, skipped work, provider substitution
or retry loop is added. The separate timeout replay, other CTest cases,
LibreWPF Gallery's 60-second application limit and zero-allocation gates retain
their existing bounds and assertions.

A new whole successful producer Build and companion workflows, final-package
profiling, consumer qualification and public-package checks remain required.
[Issue #367](https://github.com/wieslawsoltes/ProGPU/issues/367) stays open for the
unresolved cold-start performance and original 60-second objective; release
notes must retain that limitation. A pass within 120 seconds does not close it.
