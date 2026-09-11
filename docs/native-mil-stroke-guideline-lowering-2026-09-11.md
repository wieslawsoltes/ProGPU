# Native MIL stroke guideline lowering

## Acceptance path and semantics

The external LibreWPF SDK application reaches native rendering with its native
input index enabled. First-frame preflight rejected command 222, a stroke batch
carrying static per-point guidelines. The executor already supports those
guidelines on filled path segments.

Source call-order analysis of LibreWPF's WpfGfx `hwsurfrt.cpp` establishes that
stroke widening precedes filled-path guideline application. Only that behavioral
ordering was used; no WpfGfx implementation was copied. Snapping the centerline
and subsequently applying its original pen would change this contract.

## Implementation

ProGPU's shared Direct2D core now exports widened outline segments through its
existing Widen implementation, retained recording sink, and path visitor. It
preserves cubic controls and closed filled contours rather than flattening them
again or returning a bounds rectangle. Outputs change only on success.

MIL guideline-bearing polylines consume that shared export and emit canonical
filled path commands with the original brush mapping and real outline bounds.
The existing path executor applies device snapping after widening. Source input
captures the unsnapped filled outline. Normal strokes retain their existing fast
stroke-batch path. Dashes, endpoint caps, joins and local transforms use the
existing native stroke style/widening services. Quarter-pixel tolerance selection
is shared with the existing combined-stroke path; double-to-float dash conversion
uses NEON/SSE2 lanes and a bounded tail.

This is retained geometry preparation, not CPU pixel rendering. Both native
providers share the producer and core. It is not general admission of arbitrary
semantic stroke batches carrying guidelines; the unsupported executor guard
remains intact for inputs that have not been lowered.

## Evidence and remaining work

Both provider libraries compile on macOS ARM64. Native MIL regressions cover
retained cubic caps, exported versus widened bounds, transactional failure, and
the source rectangle stroke lowering to a path with unsnapped input extents.
The native MIL test executable passes. Coverage generation changes only the
decoder digest. The full native contract verifier passes, including generated
interfaces and Unicode data; these checks do not establish image parity.

The diagnostic application gets past command 222. It now rejects command 223,
kind 16 (`DRAW_ANALYTIC`), resource 133, with the same per-point-guideline guard.
That next draw family requires proper shape lowering, not removal of the guard.
The temporary renderer probe was removed. Pixel comparisons for the new stroke
path, wider cap/join/dash/transform coverage, exact-package application closure,
cross-platform/VM qualification and final PR CI remain required.
