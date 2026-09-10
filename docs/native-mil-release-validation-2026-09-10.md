# Native MIL release validation — 2026-09-10

The user requested a one-hour release push starting at 10:21 UTC. API expansion
is frozen. Required CI and real application gates remain mandatory; this report
does not qualify the broader DirectX/Direct2D/Win2D scope.

## First failure-fix batch

Acceptance application: LibreWPF source-built native MIL host and package MVP.
Actions: formatted/trimmed text, retained image updates, cached brush strokes,
native scene submission. Baseline: ProGPU `2a998c86`, rebased on main `73cda9a5`
(merged PR #155); LibreWPF `bc6c30a89`.

- Correct retained native image blending: picture/patch output is already
  premultiplied, so plain, masked and chained image pipelines must not multiply
  its RGB by alpha again. Color-matrix output retains straight-alpha blending.
  The existing picture-generation pixel oracle now passes on Apple M3 Pro Metal.
- Admit compatible color bitmap targets with ignore-alpha, using the existing
  clear/upload/copy/snapshot implementation. A8 remains separately constrained.
- Preserve normalization scratch capacity when merging font requirements.
  The source-built styled/tabbed collapse, RTL, selection and Toolkit header
  fixtures now pass, including their original source-range assertions.
- Apply width wrapping before a final mandatory break; permit the implemented
  tab-aware trimming path. Both native text suites pass locally.
- Canonicalize retained 3D packed input sections to mesh-referenced prefixes;
  unused trailing vertices must not shift index/light data in the wire format.
- Fix strict GCC enum typing and Windows Clang aggregate initialization.
- Preserve valid empty dash output and aliased native fill sampling. Admit zero
  dash intervals with bounded work and SIMD validation, while rejecting negative,
  nonfinite, all-zero and excessive patterns.
- Refresh source/ABI assertions for ABI 4, typed source hit metadata and shader
  helper factoring. Transparent-output fixtures explicitly clear to transparent,
  rather than asserting against the compositor's nonblack default background.
  Pixel tolerances and owner/geometry assertions are not widened.

## Evidence and outstanding gates

### Second failure-fix batch

The real source-built macOS ARM64 host now passes at LibreWPF `ecef7f4c1`,
including native owner input, geometry selection, image/text updates and device
recovery in the same window (23 commands / 20 resources / 9 draws). It explicitly
supplies the pre-theme hyperlink decoration; package theme startup is separate.

Native internal tests and geometry-utility tests pass after correcting the
hit-resource generation fixture and discarding float-equivalent empty split
edges before boolean side classification. The source rectangle/pen fixture now
checks the renderer's actual 21 line/triangle stroke pieces, their owner and tight
stroke bounds, rather than expecting a synthetic analytic rectangle stroke.
The Direct2D GPU fixture uses separate scene identities and the observed five
cold submissions; original pixel checks now pass through geometry, effects and
cache variants, stopping later at Viewport3D sibling/depth pixels.

Nested managed layer compilation must borrow, not return, outer mask draw-call
lists. This lifetime fix restores cached stroke painting. The original four
pixel comparisons still fail on coverage/alpha differences; their tolerance is
unchanged. Focused mask/layer coverage: 95 pass / 4 fail. Cached opacity-mask
fixtures explicitly clear transparent. The remaining zero-dash rejection fixture
now uses an actually invalid all-zero pattern.

Windows CI compiled both native RIDs and MSVC successfully, then rejected twenty
new exports absent from the maintained manifests. Both provider manifests now
match the actual native library symbol sets exactly. GCC exposed a second
enum/unsigned conditional, now explicitly typed. No export check was disabled.
The round-join bounds fixture now includes the incoming horizontal strip's
actual y=1 extent; the Direct2D compatibility executable advances to its existing
bevel/round Widen checkpoint 344. MIL advances to ellipse input compilation.
These later failures remain blocking, not skipped or treated as parity.

All six native RID builds completed at the baseline source. They are build-only,
unqualified artifacts, not payloads for subsequent fixes. Windows managed payload
CI passed at LibreWPF `bc6c30a89`; that exact artifact was downloaded.

The full managed run at `bd4b7313` reports 4,540 passed / 5 failed / 7 skipped
(4,552 total). Four failures are cached-stroke coverage/alpha comparisons, now
painting but still different from ordinary strokes. The fifth is a stale source
assertion for the native fixture's old four-submission count; it is updated to
the five submissions already checked by the executed native fixture. No pixel
tolerance is relaxed. The GCC diagnostic test table now declares its function
pointer element type explicitly instead of relying on library-specific deduction.
These last test fixes require their own current-head CI result.

The full macOS ARM64 native run at `bd4b7313` reports 16 passed / 3 failed
(19 total). Remaining failures are Direct2D WebGPU Viewport3D sibling/depth
pixels, compatibility Widen checkpoint 344, and MIL ellipse input compilation
(initial phase 0, status 5). Geometry utility, native internal and both text
suites pass. The separate source-built host first-frame/input/device-recovery
gate passed as recorded above; this is not package or cross-platform qualification.

Other required checks: Svg.Skia zero-length dash parity must rerun; CAD browser
capture timed out in baseline CI. Neither is waived. Complete package-mode MVP,
Windows native/ProGPU comparison, Linux/macOS application actions and exact-head
payload provenance remain required. PR #139 must not merge until these gates are
green; dependent LibreWPF PR #115 follows only after its own exact-head gate.

Deferred work remains tracked in the delivery plan: general Direct2D/Win2D API
expansion, complete cross-platform COM application compatibility and all remaining
platform modality/text/document contracts. The experimental shared Metal-surface
initializer is preserved separately at local commit `123e9ee1`, not shipped or
claimed as Cocoa modal-window completion.
