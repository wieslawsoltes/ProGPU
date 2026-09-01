# Modern-MESH smoothing and crease acceptance capture

Captured on 2026-09-01 with macOS 26.6, .NET 10.0.5, the final Release
benchmark binaries, a 64 by 64 quad control grid, 4,225 vertices, 4,096 faces,
and 512 selected faces.

`final-release.json` records three warmups and twelve measured iterations.
Smooth More plus snapshot/scene rebuild measured 110.8749 ms p50 and
128.4133 ms p95/p99. Face crease plus rebuild measured 179.8041 ms p50 and
244.7409 ms p95/p99. Retained Smooth More Undo/Redo measured 0.0002 ms p50,
0.0023 ms p95/p99, and 288 managed bytes per pair. Retained crease Undo/Redo
measured 0.1730 ms p50, 4.0163 ms p95/p99, and 420,272 managed bytes per pair.
The JSON retains SHA-256 identities for the benchmark, CAD, backend, scene,
headless-test, and WinUI binaries.

The matched Xcode Allocations and Time Profiler run used two warmups and eight
iterations of the same workload. Its retained exports and compact summary are
under `instruments-final/`; the raw traces and 61,086,904 bytes of Xcode scratch
data were deleted after export. Allocations reported 20,651,072 persistent
native-heap plus anonymous-VM bytes, 631,442,656 total allocated bytes, and
610,791,584 transient bytes. Time Profiler reported no potential hangs, hang
risks, or command-buffer errors. This CPU scene-compilation workload submitted
no Metal commands and allocated no observed Metal resources.

A separate 30-second Metal System Trace launch used one warmup and four
iterations. Instruments stopped the target but did not finalize within the
profiler's bounded window. The profiler terminated the process tree, deleted
the incomplete 53,248-byte trace and 293,952,056 bytes of Xcode scratch data,
and returned exit code 4. `instruments-metal-final/metal.log` and the target log
are retained as failure evidence. No Metal result is claimed from that lane.

Release validation on the same source tree passed 1,469 ProGPU.CAD tests and
3,848 core ProGPU tests with zero failures or skips. Focused smoothing, crease,
round-trip, transaction, and shared-shell coverage passed 11 tests.
