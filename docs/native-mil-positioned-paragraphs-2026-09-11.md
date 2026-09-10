# Positioned paragraphs in native document flow

The source acceptance path is LibreWPF's unchanged real application containing
Figure/Floater content. Its excluded text can have several fragments on one row
and cleared vertical gaps. Ordinary document line-height prefixes cannot express
that geometry without a second source-side placement algorithm.

`progpu_native_document_arrange_with_positioned_paragraphs` extends the existing
shared block/row arranger with explicit native paragraph extents and paragraph-local
line positions. It does not introduce another block layout. Declared line-bearing
leaves use the supplied paragraph height in bottom-up measurement and add their
local positions to the existing content origin during placement. Margins, insets,
row maximum heights and following-block advancement remain shared. Source line
order is retained, including decreasing X for RTL fragments on the same row.

Declarations are sorted unique block indices with zero reserved fields. Each line
rectangle must fit its finite nonnegative paragraph extent. Undeclared lines have
zero local offsets and keep ordinary prefix placement. Invalid line partitions
are rejected before scanning local positions, preserving linear validation rather
than repeatedly traversing overlapping malformed ranges. All output boxes, line
positions and result metadata remain untouched on failure.

The additional declaration map is O(B) storage; validation and placement add
O(B+L+P) work for B blocks, L lines and P declarations. Independent metric and
position pairs use the existing native double-precision NEON/SSE2 checks. Tree
traversal and ordered line ownership remain dependent scalar work. No device,
shaping, callbacks, source-local geometry correction or per-fragment allocation
is introduced. Empty positioned paragraphs remain outside this line-bearing
contract, not silently collapsed into a successful empty source.

Tests cover same-row reverse-X fragments, clearance gaps, source block insets,
following paragraphs, explicit paragraphs inside table cells, native row stretching,
invalid extents/reserved fields, undeclared nonzero offsets, malformed line ranges
and unchanged failure output. Both native libraries compile. Managed native
bindings, neutral adapter and source TextLine consumers are still required; this
entry point alone does not admit source anchors or qualify application startup.

Local macOS ARM64 document-flow CTest passed (1/1, 0.37 seconds), including the
table-cell case and existing ordinary flow tests. Both provider export allowlists
and generated C# record verification passed. These are local native checks, not
published package, Windows/Linux, module or full application qualification.

## Managed transport and neutral capability

`NativeDocumentFlow.ArrangeWithPositionedParagraphs` now pins the original spans
for one call to the selected wgpu-native or Dawn export. It validates capacities,
item budgets and exact local-position coverage before pointer access, fills the
existing result ABI size, and lets the shared native arranger validate topology
and geometry. No managed position correction, line copying or reshaping is added.
Ordinary entry points retain their original native calls.

`IPortablePositionedDocumentFlow` is an explicit optional capability on the
existing document service, with a sequential native-matched paragraph descriptor.
It is separate from anchor placement: a host must implement the capability before
source consumers rely on fragment-aware document arrangement. Missing support
must not select the old height-prefix path.

The native-backed consumer passes the same reverse-X/same-row and cleared-gap
paragraph through both providers, verifies original source order and following
block placement, and checks atomic rejection and short-span admission. Its
Release project-reference build passed with zero warnings/errors and the MIL-only
consumer passed. The neutral contract compiled with zero warnings/errors. WPF adapter,
source formatting request, coordinate normalization and viewer interaction remain
the next connections, not qualified by this transport check.
