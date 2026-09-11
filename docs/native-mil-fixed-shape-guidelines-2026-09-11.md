# Native MIL fixed-shape and contour guidelines

Acceptance is the external LibreWPF SDK application's first rendered frame with
NativeMilWgpu and the native input index enabled. After stroke-batch lowering,
preflight rejected a rectangle fill at command 223. Subsequent diagnostic runs
identified a rounded-rectangle stroke (483), ellipse fill (488), line (554),
and cubic contour (2719), all carrying the existing static per-point policy.

## Implementation

Guideline-bearing rectangle, rounded-rectangle and ellipse fills now reuse
ProGPU's existing source line/cubic construction and canonical path executor.
Maximal corner radii produce the same four cubic quarters for ellipse geometry;
no rectangular envelope is used as its fill. Ordinary analytic fills stay on
their fast path.

The previous shared widened-outline lowering now accepts original contour
segments and smooth-join metadata. It handles rounded/elliptical source strokes,
ordinary lines, and nondegenerate path contours without flattening their source
controls into a guessed polyline. Dash/end-cap policy, source brush identity,
local transforms and existing quarter-pixel widening tolerance are retained.
Raster snapping remains after widening; native source input sees unsnapped
geometry. There is no WPF-local compositor fork, new shader or CPU rasterizer.

This work reuses original ProGPU source geometry and native widening. It does
not extend the executor's generic per-point admission guard. Unlowered draw
families and degenerate cap-only paths remain explicit rather than losing their
guidelines or returning successful no-ops.

## Verification

Both providers compile on macOS ARM64. The native MIL regression executable
passes with rectangle/rounded/ellipse fill-plus-stroke and line cases. Tests
require path emission, source owner identity, separate fill/stroke extents, and
unsnapped fractional bounds. The aliased bitmap-cache-root fixture checks the
path pipeline's one-sample grid as well as the original analytic flag.
Ellipse widening produced an upper bound one float ULP below 21.75; its assertion
uses the existing outline-export geometry tolerance (0.00001), not a relaxed
pixel/image threshold. Other exact source extent checks remain unchanged.

The full native contract verifier passes after regenerating the MIL coverage
digest. CI run 34561955699 exposed the same enum-to-uint32 aggregate narrowing
in MSVC and ClangCL for both Windows architectures. The wire field now uses an
explicit uint32 conversion; local builds and the MIL regression pass after this
fix. Windows confirmation still requires the new CI run.

The diagnostic application advances to command 3069, a glyph run (kind 18,
resource 1624). Glyph guideline placement is the next application blocker and
must use the text contract, not arbitrary glyph-outline deformation. Temporary
renderer probes were removed. Final pixel comparisons, text handling, exact-head
packages, platform/VM qualification and green PR CI remain required.
