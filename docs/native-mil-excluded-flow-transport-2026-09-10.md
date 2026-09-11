# Excluded paragraph native transport

The Showcase Figure/Floater dependency now has native C requirements/layout
entry points over the existing inline-metric paragraph pipeline. These accept
resolved half-open exclusion rectangles and an explicit bounded attempt budget.
They do not measure or position source anchor subtrees. Ordinary calls retain
their scratch sizes and placement behavior.

The shared scratch estimator and arena reserve native rectangles, interval work,
candidate fragments and retained placements once per paragraph. Layout reuses
the original ProGPU excluded logical writer; font shaping, source clusters, style
metrics, inline objects and tab advances are not recomposed in another layer.
The output line count is the fragment count; maximum_lines counts rows. Each
fragment exports the explicit double top and row index in the existing generated
C placement record. Metric validation precedes publication of glyphs, lines and
placements. Failures publish zero counts, not partial layout success.

The options require exact structure size, zero reserved fields and a budget in
[1, 1048576]. Positive paragraph width is required; exhaustion is not unbounded.
Rectangle coordinates use intrinsic four-lane finite checks on NEON/SSE2, with
the scalar reference on other architectures. Ordering checks preserve zero-area
rectangles. All pointer buffers are borrowed synchronously, must not overlap and
have checked capacities/alignment. Additional scratch is O(glyphs + exclusions);
the shared fitting algorithm retains its existing bounded retry complexity.
No new per-fragment managed crossing, GPU readback or fallback is introduced.

Implementation provenance is ProGPU's own paragraph interop and excluded flow at
86afa6df, metrics at e06023d6 and interaction transport at e1435729. See
[fragment transport](native-mil-fragment-transport-2026-09-10.md) for related
research and ownership contracts. No external source implementation is copied.
Both native providers export the new functions; public option/rectangle C# records
are generated from the C header. No named C++ module surface changes in this batch.

Both native providers compile. The native text, shaping interop and interaction
interop suites pass. New ABI tests cover no-exclusion equivalence with measured
inline output, source glyph/font preservation, vertical clearance, bounded retry
failure, insufficient fragment/scratch capacity, reserved fields and NaN input.
Output canaries survive rejection. Logs: artifacts/excluded-transport-build.log,
artifacts/excluded-transport-tests-build.log and artifacts/excluded-transport-tests.log.

Remaining: managed span methods, retained snapshot/navigation integration,
actual anchor subtree placement and WPF source connection, unresolved convergence
cases, unchanged application and final package/platform/CI qualification. Managed
and native WPF modes must share the eventual typed consumer. This producer work
does not admit Figure/Floater or qualify final packages. The user's merge-first
priority pauses further feature expansion while current CI failures are addressed.

CI triage also found that the previous fragment-interaction export was inserted
out of lexical order. Linux ARM64 job 103050865783 passed all 23 tests and then
failed the symbol-list comparison. Both export manifests are sorted again and
verified against the local provider libraries, including the new excluded-flow
symbols. This corrects the manifest; fresh CI must still confirm the result.

## Managed span connection

`NativeTextShapingContext.GetExcludedFlowParagraphRequirements` and
`LayoutExcludedFlowParagraph` now borrow the generated exclusion and placement
records through the existing leased paragraph interop. All spans stay pinned
for the complete native call; each style must have a metric before pointer access.
Struct sizes are supplied by the binding, while the caller's bounded attempt
budget and reserved fields remain native-validated. Fragment capacity is passed
independently, and ordinary/inline/collapsed methods retain their existing paths.
No managed shaping, exclusion scan, per-fragment callback or geometry copy is added.

The project-reference native consumer passes with a full-width exclusion clearing
the first 20 DIPs: native fragment tops are 20, 40 and 82, total height is 102,
the last baseline is 94, and the real inline object retains its source identity.
Intrinsic widths agree with ordinary inline formatting. Reserved-field rejection
preserves glyph/fragment canaries and returns zero glyph count; short metric spans
throw before native access. The backend and consumer builds report zero warnings
and errors. Logs are `artifacts/excluded-span-build.log`,
`artifacts/excluded-span-consumer-build.log` and `artifacts/excluded-span-consumer.log`.
This local wgpu text binding test is not Dawn text, fresh package or platform
qualification. Retained snapshots/navigation and actual source anchor ownership
remain required before Figure/Floater admission.
