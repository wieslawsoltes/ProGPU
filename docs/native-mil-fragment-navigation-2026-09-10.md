# Native caret navigation across text fragments

## Acceptance dependency and behavior

The unchanged LibreWPF Showcase Figure/Floater path needs keyboard caret movement
across spatial fragments and real rows. try_move_fragment_text_caret now consumes
the same retained caret and placement arrays produced by native interaction.
It returns an existing caret index in that generation, never a synthesized stop.

Horizontal movement uses physical X, then fragment X and retained affinity order
for ties. It crosses a same-row anchor gap without jumping to another row, even
when RTL source order stores the right fragment first. At an outer row edge,
paragraph direction chooses the previous/next populated row. Up/down choose the
nearest populated row and minimize distance from the caller's preferred X;
equal-distance affinity ties prefer the current affinity. At document boundaries
the current index is retained.

Input validation checks generation-local index bounds, finite coordinates,
ordered source fragment indices, row identity/top and nonoverlapping ordered
intervals. The API does not infer a current caret from an ambiguous source offset
shared by two fragments. The future retained transport must keep the index tied
to its owning paragraph generation.

## Reuse and cost

This is original ProGPU code alongside the existing interaction queries, using
the retained output at 66f6475a. The
[fragment interaction design](native-mil-fragment-interaction-2026-09-10.md) and
its cross-engine research remain applicable. It does not copy foreign source,
reshape text, allocate memory, introduce P/Invoke per caret candidate, initialize
fonts/GPU resources or change renderer caches/uploads.

Selection is an ordered comparison over source identity, row direction, physical
position and affinity. Cost is O(carets + fragments), with O(1) workspace. No new
parallel compute/fallback kernel is introduced and no performance qualification
is claimed. The retained consumer must avoid rebuilding these arrays per input.
Both native providers share the implementation; WPF's managed/native modes still
await the shared transport/source navigation connection.

## Evidence and remaining work

The Release native text suite and named-module consumer pass. Both native wgpu
and Dawn libraries compile. Cases include LTR/RTL gap crossing, paragraph-aware
row wrapping, preferred-X up/down movement, affinity ties, document boundaries,
invalid current indices and nonfinite preferred X.

The first fixture expected an invented X=25 caret between actual X=20/X=30 stops.
Inspection of the retained advances showed the mistake. The corrected assertion
requires X=20 with matching affinity; no product tolerance or geometry changed
to accommodate the fixture.

Logs: artifacts/fragment-navigation-build.log,
artifacts/fragment-navigation-tests.log,
artifacts/fragment-navigation-providers-build.log.
Module artifacts remain in artifacts/anchor-text-module.FxeqPf.

Remaining: retained C ABI/neutral transport, source editor navigation ownership,
native anchor sizing/positioning, source subtree and selection integration,
nonconvergent/zero-height layout cases, unchanged application acceptance and final
package/platform/PR CI qualification. No WPF anchor admission or release gate is
changed by this native primitive.
