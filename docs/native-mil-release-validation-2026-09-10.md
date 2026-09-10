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

Managed baseline: 4,533 pass / 18 fail / 7 skipped. After initial fixes, focused
coverage: 676 pass / 6 fail; dash contract updates subsequently pass, leaving
four cached-stroke pixel failures (the cached stroke currently paints nothing).
The full suite must rerun after the final fixes.

Native failures remain in geometry combination, short round-join bounds,
Direct2D submission expectations, MIL/input-index coverage and later internal
fixtures. Initial 3D material and text failures are corrected, but later failures
in the same executables still block CI. Source-built host qualification advances
through image and text collapse checks but currently stops at hyperlink underline
coverage. The host's first-frame/input/device-recovery gate has not passed.

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
