# Native MIL integration with System.Drawing main

The additional release hour starts at 11:58 UTC on 2026-09-10. It does not waive
required checks or authorize a red merge. ProGPU main advanced from `73cda9a5`
to `8842f828` when #140 merged; this integration retains both histories, including
the already completed #155 rebase and native MIL fixes through `97eef74f`.

## Conflict decisions

- Keep both Win2D/WindowsAppSDK and System.Drawing test package declarations.
- Classify the complete combined package graph: 80 projects, not either parent's
  standalone count. The existing per-project and dependency audits remain.
- Keep native pad/conical spread flags and hatch sets alongside the new tile and
  path-gradient kinds and their boundary limits. Retain both native brush tests.
- Apply projective texture coordinates before address-mode mapping and the shared
  sampler, retaining cubic filtering, ignore-alpha and premultiplied image paths.
- Keep texture address modes, raster operations and explicit opacity together.
  Native destination mapping uses #140's quad transform and the retained opacity.
- Retain the compact texture flags in RenderCommand; add the new texture union
  views without reintroducing duplicate boolean fields or changing their meaning.
- Preserve checked Win32 enabled-state handling and the modal input gate. Keep
  #140's opacity and Z-order operations, not its duplicate raw EnableWindow path.

Conflict-marker removal is not runtime proof. Build and test the combined head
using the existing prepared dependency worktree; the isolated merge worktree has
no initialized submodules. Exact-head CI and complete package/application gates
remain mandatory, including the outstanding rendering failures in
`native-mil-release-validation-2026-09-10.md` and the later collapsed-group fixture
recorded in `native-mil-stroke-spine-bounds.md`.

## Combined command storage and canonical ellipses

The merged System.Drawing suite exposed a 600-byte managed `RenderCommand`, above
its existing 576-byte budget. Independent boolean flags now occupy the remaining
bits of the existing options word. Source primitive/scope geometry retains its
own discriminator and reuses otherwise-inapplicable text/texture vector slots.
Those public views remain default for source annotations, so compact retained
classification is unchanged; the existing immutable source sidecar owns replay.
Clearing absent source metadata must not erase real font/cubic texture options.
Raster position/rectangle/radii and native generated ABI records are unchanged.
This is allocation-free fixed-work managed storage, not a new native transport or
a measured frame-time claim. Native compilation consumes the same expanded values.

The ellipse export also exposed branch-cut angle subtraction rounding an exact
half turn upward, adding a fifth cubic span. Both original ProGPU arc resolvers
(`ProGPU.Vector/ArcSegmentGeometry.cs` and native `Geometry/progpu_native_arc.hpp`)
now preserve exactly signed pi when the solved center offset is zero. Other arc
angles, retained analytic arcs, radii correction and quality limits are unchanged.
The [SVG endpoint-to-center specification](https://www.w3.org/TR/SVG/implnote.html#ArcConversionEndpointToCenter)
is the mathematical reference; no third-party implementation was copied. This
uses the cross-engine ownership/reuse decisions already recorded in
`native-mil-hit-test-ownership.md#design-references-and-decisions`; shaping, font
caches, uploads, worker scheduling and device-loss behavior are unaffected.

The System.Drawing Release run passes 621/621 tests; paired managed retained/arc
tests pass 265/265, and the native geometry utility passes including both endpoint
orders, sweep directions and large-arc flags. These results do not qualify the
remaining native/managed pixel failures, exact-head packages or platform gates.
