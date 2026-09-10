# Post-merge native contract audit

This is a continuation of the native MIL ShowcaseApp release gate after ProGPU
#140. It is not another API expansion or a waiver of failing tests.

## Corrections

- Portable Direct2D target capacity exhaustion now returns `out_of_memory` for
  either allocator failure or the shared scene builder's bounded storage limit.
  It retains the first failure, an empty unpublished scene and next-BeginDraw
  cleanup. The existing automatic nested-layer test now reaches and passes its
  unchanged expected HRESULT. Native Windows command-list ingestion has its own
  documented capacity result; this does not alter that separate API.
- Widen fixtures accept the shared stroker's winding union of balanced contours,
  rather than assuming one outline or two contours. Dense `StrokeContainsPoint`
  comparisons remain; consumed rectangle interiors additionally use independent
  expanded-rectangle coverage checks. Pixel thresholds are unchanged.
- Explicit and automatic layer streams compare every semantic byte after checking
  advancing transaction generations. Only those generation fields are normalized,
  including independently checked picture-mask child streams. Other retained
  resource generations remain significant; a mismatch still fails the test.
- Negative Clear uses non-finite RGB, not valid extended-range negative RGB.
- The Windows aliased-path fixture now requires successful stream construction
  and the existing shared one-sample coverage contract. This Windows-only test
  still requires MSVC/Windows execution; it was not run locally.
- MIL fixtures consume inherited Save/Restore state, preserve empty dash-gap
  groups as no paint, and test orthogonal effect admission alongside explicit
  nonorthogonal rejection with transactional rollback. Zero source extents clamp
  rounded radii before widening: exact four-point dashed spines, dash intervals,
  round point caps, excluded gaps and point bounds replace stale cubic counts.
  Geometry-local line transforms are asserted on actual source endpoints before
  widening, separately from the visual's inherited transform.

These changes reuse ProGPU's existing builders, stroke compiler and scene cursor;
no independent renderer, fallback or copied third-party algorithm is introduced.
The prior source/research decisions in `native-mil-stroke-spine-bounds.md` and
`native-mil-hit-test-ownership.md` remain applicable. Production capacity mapping
is constant-work and allocation-free. Test-only recursive scene comparisons own
temporary byte vectors bounded by the input fixture and maximum scene depth.

## Outstanding failures and delivery gates

The local Release native build succeeds. The full macOS arm64 native run remains
17/19 passing. Subsequent targeted runs pass the previously failing layer capacity,
generation and failed-copy checkpoints, but expose later Direct2D compatibility
checkpoint 309 (composite mask representation). MIL stops at the transformed
analytic-count fixture (line 8379 at this checkpoint). Do not delete those tests
or infer whole-suite success from reaching later checks. Logs: prepared worktree
`artifacts/release-hour/native-contract-final-tests.log` and
`native-nested-generation-tests.log`.

The full managed cached-stroke image differences remain open as recorded in
`native-mil-render-target-corrections-2026-09-10.md`. Final exact-head CI, all-RID
payloads, complete SDK packages and Windows/Linux/macOS application/oracle gates
remain mandatory. The merged LibreWPF canonical WinForms source graph also needs
an aligned LibreWinForms nested ProGPU pin; ancestor compatibility is not enough.

SVG W3C CI reports 21 resolved known differences, not newly introduced differences.
The harness previously deleted passing PNGs, making those improvements unavailable
for required image review. It now retains passing PNGs as well as failing ones.
Comparison math, thresholds, failure exit codes and expected-results lists are
unchanged. This increases diagnostic artifact disk use only; production rendering
is unaffected. The next CI corpus run must supply reviewable frames before anyone
updates the expected-results inventory. This harness change has not been exercised
against a local SVG corpus in this worktree.

Both integration branches are conflict-free with their fetched bases. They are
not ready to merge while these required failures and qualification gaps remain.
