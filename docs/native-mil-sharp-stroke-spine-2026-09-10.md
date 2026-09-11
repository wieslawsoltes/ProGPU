# Sharp rectangle stroke preparation and native CI corrections

## Acceptance path

The acceptance application remains native MIL ShowcaseApp. Viewing a retained
transformed shape with a zero corner radius must produce a sharp rectangle with
the source pen width. The blocking path was native `make_fixed_bounds_path` into
`make_wpf_rounded_rectangle_geometry`, before shared stroke compilation.

The old preparation cleared smooth-join flags but retained four collapsed cubic
corners when either radius was zero. The original ProGPU helper now normalizes
both radii together, emits its existing four line segments and sets sharp joins.
Two positive radii retain the original cubic path. No alternative stroker, input
bounds approximation, CPU rendering fallback or shader change is introduced.

The subsequent line-path fixture exposed a second bounds-policy call site in
`append_path_strokes`: it remeasured a valid horizontal, gap-separated contour
using positive-area fill admission. It now selects the existing `stroke_spine`
policy before expansion, just like transformed preparation. Every fill caller
keeps positive-area admission. Dash phase, gap caps and finite original positions
remain unchanged; the existing closure/gap/seam tests now reach later assertions.

This uses ProGPU-owned `StrokeCoverageGeometry.Smooth.cs::TryPrepareRoundedRectangle`
as the managed behavioral counterpart: its existing zero-axis branch selects
`TryPrepareRectangle`. The source WPF `RectangleGeometry.IsRounded` contract
requires both radii to be nonzero; its source path export selects squared versus
rounded topology accordingly. That contract was inspected in the source-built
LibreWPF `PresentationCore/System/Windows/Media/RectangleGeometry.cs`; no foreign
implementation text was copied into ProGPU. Prior independent engine/reference
decisions remain in `native-mil-stroke-spine-bounds.md` and
`native-mil-hit-test-ownership.md#design-references-and-decisions`.

Preparation remains constant-size work, with four source line segments instead
of eight line/cubic segments for sharp rectangles. Existing intrinsic affine
mapping and the shared widener are unchanged. Font/shaping caches, upload and
worker scheduling are unaffected; no frame-time improvement is claimed.

## Tests and related fixture repairs

- Native retained zero-X and zero-Y cases verify exact transformed four-point
  spines, unchanged width two, separate analytic fill and complete stroke.
- Paired managed zero-axis tests verify the same corners and independent miter
  bounds `(7,7,26,18)` after scale two. The focused managed run passes 24/24,
  including existing sharp-corner rendering/clip and stroke tests.
- Transformed rounded/ellipse fixtures now assert separate analytic fills and
  prepared stroke records, mapped source endpoints/radii, and unchanged pen
  width. Collapsed shapes assert their normalized spine plus rigid translated
  frame, not a geometry scale applied again to the widened pen.
- Direct2D's supposedly positive opacity-mask test never supplied an opacity
  brush. Both alias modes now explicitly supply the existing gradient brush in
  the positive case. The complete portable Direct2D compatibility suite passes.
- Hosted MSVC exposed an invalid `std::byte*` argument in the new Windows alias
  fixture. The C byte-buffer call now uses an explicit `std::uint8_t*` cast.
  A new Windows build is still required; local CTests do not compile that file.

The native Release build succeeds. The full macOS arm64 run is 18/19 suites;
MIL advances past the stroke preparation and gap-build cases to a later group
fixture that identifies children by their old post-widen transforms
(`first_child_body_count`, line 10497 at this checkpoint). It is not green.
Logs are in prepared-worktree `artifacts/release-hour`, notably
`native-cap-spine-tests.log`, `native-open-spine-tests.log`, and
`managed-sharp-spine-tests.log`. The MIL coverage ledger is regenerated and native
contract verification remains required.

## Unchanged release blockers

Four managed cached-stroke image comparisons, remaining MIL contracts, exact-head
Windows/Linux/native payloads, SDK packages and real application/oracle gates
remain open. LibreWinForms PR #29 aligns the earlier dependency pin; subsequent
ProGPU fixes must be propagated through both dependent PRs before qualification.
Do not qualify newer binaries from earlier staging directories or merge red CI.

SVG run `34479034756` now retains passing frames at merge checkout `96096556`.
It reports 270/525 passing, 246 expected pixel differences, nine expected
exceptions, 21 resolved differences and no added differences/unexpected exceptions.
Artifact `10153029058` includes the previously unavailable resolved PNGs.
The expected-results inventory and threshold remain unchanged pending complete
visual review. Browser policy rejected a local HTML comparison view; no alternate
browser or server was used to bypass that restriction. Raw PNG inspection is
available, but transparent backgrounds must not be mistaken for missing ink.
