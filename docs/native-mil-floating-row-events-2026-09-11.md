# Source-ordered native floating paragraph events

## Acceptance dependency and implementation

LibreWPF's unchanged document application fails at its first anchored-block source
admission. The native Windows reference recorded by eng/NativeAnchorReference in
LibreWPF shows that bottomless floats follow their source row and pack beside
earlier siblings. This batch connects that placement to the existing shared
measured paragraph loop, rather than repeatedly repairing a source paragraph's Y.

try_layout_floating_logical_shaped_text_at accepts ordered logical-glyph-boundary
events with measured outer sizes and horizontal alignment. The common native loop
finishes a physical row using only existing exclusions, publishes its original
glyphs/fragments, then places events owned by that row. New collision boxes become
exclusions for subsequent rows. Parent glyphs are never reshaped or cloned during
placement. Existing ordinary/excluded entry points delegate to the same loop with
no floating state and preserve their contracts.

Events in [row start, next glyph) belong to that row; terminal events belong to
the final row. Equal indices preserve sibling order. Indices inside a shaped
cluster and out-of-order events fail explicitly. This is the native glyph-boundary
contract: translating original source offsets and hard-segment boundary affinity
through shaping is still required before source admission. The Windows reference
does not independently qualify every exact-wrap boundary or RTL case.

The result retains parent text counts and extents separately from the combined
float-inclusive width/height. Each float records its source row and actual native
box. Row limits leave later events unconsumed but retain floats owned by completed
rows, including those extending below the text. The caller must keep those boxes
with the same retained source generation.

An anchor-only paragraph has an actual parent row in Windows. The new entry point
therefore takes explicit source empty-row ascent/descent and minimum line height;
it emits one non-ink row when floats exist. Real exclusions can move that row down.
This does not manufacture a font glyph or change the legacy empty-input/no-row
API. Empty source symbol/caret export still needs the C and source connection.

## Safety, work and shared semantics

All input/output/scratch spans are validated as disjoint before collision copies.
E existing boxes plus A events use caller-owned E+A collision/interval scratch and
E+A+1 interval/fragment storage. No allocations, device initialization, pointers
retained beyond the call or per-row managed/native callbacks are introduced.

One attempt budget covers measured row fitting, height refits, blocked-band
clearance and all float placement retries together. Failure clears result counts
and invalidates the whole output prefix. Source order, interval selection, row
prefixes and retry decisions are dependent scalar operations; existing SIMD
metric/coordinate and glyph-writing paths remain shared. Interval processing is
O((E+A) log(E+A)) per query; work remains bounded by the explicit attempt budget,
with the existing shaping-prefix rescans. No benchmark qualification is claimed.

Provenance is the same-repository measured/excluded loop and floater placement at
9beaaf2b, informed by the independent Windows reference. No foreign implementation
was copied. Existing interval/band design research remains applicable. Both native
providers and the header/named-module interfaces use this implementation.

## Evidence and remaining delivery

Both complete native provider libraries build. The native text suite passes,
including unchanged excluded behavior plus source-row activation, sibling packing,
full-width clearance without self-displacement, original glyph indices/clusters,
terminal/boundary events, row limits, shared retry exhaustion, invalid/overlapping
buffers, shaped-cluster rejection, height refits, mandatory breaks and empty parent
rows with/without existing exclusions. The named-module consumer compiles/runs and
executes the empty-parent floating path using source metrics.

Next connect the batched C shaping transport, original source event mapping,
retained native paragraph snapshot, optional neutral provider and original WPF
child generation. Do not route WPF through a per-line native callback or a second
paragraph composer. Do not remove ordinary anchor rejection until automatic
layout, rendering and interaction consume the same output. Final application,
package, Windows/macOS/Linux and new-head PR CI qualification remain open.

The preceding 9beaaf2b CI head has a separate System.Drawing allocation-test failure:
WarmedEnumerationDoesNotAllocatePerRecordPayloads measured 7,216 bytes against a
4,096-byte limit; 620 other tests passed. Job 103107087012 in run 34548789786 could
not yet be rerun through GitHub. No threshold or assertion was weakened. Follow
the new-head result and investigate a repeated failure; this is not marked fixed.
