# Measured native exclusion-band placement

## Acceptance dependency

The unchanged LibreWPF Showcase Figure/Floater content needs both surrounding
text placement and independently owned anchored subtrees. This batch completes
positioned glyph emission for one candidate exclusion band. It does not yet
admit source anchors or constitute the full paragraph/document algorithm.

try_layout_text_exclusion_band consumes the existing interval fitter and real
per-item ascent/descent. It distinguishes placed, refit_height, blocked and
complete results. A height mismatch in either direction returns the required
height without consuming text or changing positioned output. A blocked band
preserves the original input start and carries the downward retry hint.
The eventual paragraph consumer must implement bounded convergence and reject
cycles; conservative oversized line heights are not accepted as a solution.

## Shared implementation

The ordinary measured writer is factored into layout_measured_core. Its existing
entry point delegates with unchanged semantics. Band placement reuses that
writer for every fragment, passing actual paragraph continuation so justification
does not mistakenly treat every fragment as the last line. No artificial glyph
or appended source text is used to trigger wrapping or justification.

Fragments retain original source ranges and glyph indices, paragraph-relative
tabs, bidi order, alignment and offsets. Their coordinates use one common
measured baseline, with NEON/SSE2 translation and finite-value checks. Fragment
line records share a row height: they are not separate vertically stacked lines.
Existing prefix-based paragraph interaction therefore cannot consume them
unchanged. That consumer remains explicitly unconnected.

Original ProGPU provenance is the measured writer and exclusion fitter at
1ecc390b. The [band fitting record](native-mil-anchor-band-fitting-2026-09-10.md)
and [interval research](native-mil-anchor-intervals-2026-09-10.md) remain applicable.
No third-party implementation code is introduced. Both wgpu and Dawn use the
same native text library. The managed renderer has no separate matching anchored
layout implementation; both WPF renderer modes ultimately share the native text
provider. Ordinary native text behavior remains covered by its existing suite.

There are no allocations, retained pointers, GPU initialization or new per-line
P/Invoke calls. Source-index restoration, fitting and row progression have ordered
dependencies; independent coordinate and metric lanes use the existing desktop
SIMD baseline. The scalar oracle checks offsets in both visual directions.
Repeated paragraph validation is still a cost to remove in the retained batched
consumer; no performance qualification is claimed.

## Evidence

The Release native text library and complete include-based test suite pass.
New cases cover required-height refits without output publication, two spatial
fragments with unequal item metrics, common baselines, LTR/RTL source identity,
continued-paragraph justification, source cluster ends, original glyph offsets,
fully blocked bands and rejected infinite metrics. The named-module consumer
builds and runs against the same archives, including an actual empty-band call.

Logs: artifacts/anchor-band-placement-build.log and
artifacts/anchor-band-placement-tests.log. Module artifacts remain under
artifacts/anchor-text-module.FxeqPf and are not checked in or shared across
toolchains.

Remaining: bounded whole-paragraph height/position retries, retained row/fragment
metadata and interaction, batched C ABI/neutral transport, native anchor sizing
and positioning, original WPF subtree/selection ownership, unchanged application
acceptance, both-provider packaging and final Windows/Linux/macOS and PR CI gates.
No source admission, runtime default or release gate changes in this batch.
