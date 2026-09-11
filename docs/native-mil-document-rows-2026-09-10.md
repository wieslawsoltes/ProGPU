# Native document rows and column tracks

## Application dependency and source boundary

LibreWPF's unchanged Application.Run acceptance document contains a table with
80-DIP and 120-DIP columns, two paragraph cells and 2-DIP cell spacing. Its shared
FlowDocument formatter currently rejects table layout. Replaying those cells as
ordinary vertical blocks would produce incorrect widths, row height and input.

The public WPF [table contract](https://learn.microsoft.com/en-us/dotnet/desktop/wpf/advanced/table-overview)
retains Table, TableRowGroup, TableRow, TableCell and block content, with column
and row spans. This implementation is original ProGPU code extending its existing
block-flow passes, not a port of another table engine. Source WPF must retain its
actual elements, properties, TextLines, text positions, editing and invalidation.

## Native contract

The additive resolve_widths_with_rows and arrange_with_rows entry points consume
the existing block forest, plus sorted row/cell descriptors and shared fixed
column widths. Managed NativeDocumentFlow and the neutral IPortableDocumentFlow
interface expose borrowed-span equivalents. Generated native records remain the
C-header source of truth. Both wgpu-native and Dawn export the same functions.
Existing object-free and object-aware APIs retain their behavior.

Each row references a slice of outer cell-track widths, excluding spacing. Rows
may reuse an identical slice; distinct slices cannot overlap. Each cell is a
direct row child and references an ordered, positive column span. Unoccupied
tracks are allowed, overlapping cells are not. Every direct row child must have
cell metadata; undeclared children cannot fall through to vertical placement.
Nested rows use independent slices without duplicating their track arrays.

Native width resolution sums tracks and internal spacing before formatting.
Half-spacing surrounds row edges and full spacing separates cells. Insets and
margins are deducted from each cell's allocated track width; zero content width
remains exhausted space. Fixed tracks may overflow the available document width.
This is the native geometry contract to compare against actual Windows layout,
not a claim of already-qualified WPF pixel equivalence.

After formatting, the row height is its tallest cell's outer height plus spacing,
not the sum of cell heights. Cell boxes stretch to that height; original text
lines stay top-aligned. Cell boundaries stop descendant margin collapse from
escaping the cell. Ordinary paragraphs, nested blocks and measured objects retain
their existing implementation and source order. Real object metrics do not enter
the text-line array. Results publish atomically after complete validation.

## Complexity and execution

The implementation extends ProGPU's existing widths, reverse measurement and
forward placement passes. It is O(B + L + C + R + E) time and bounded temporary
storage for blocks, lines, column tracks, rows and cells. A shared track slice is
prefixed once, not once per row. Prefixes reset at each distinct slice to avoid
precision loss between unrelated tables. Caller spans are borrowed synchronously;
there is one crossing per width or placement pass, never one per cell.

Independent column validation uses the existing NEON/SSE2 double-pair intrinsic
predicate. Column prefixes and tree/row placement have ordered dependencies and
run with the native document formatter on CPU; this is not a replacement scalar
rasterizer, shader fallback or GPU readback. No new frame-time work is added to
unchanged layout replay. This change makes no measured throughput claim.

All arrays retain the one-million-entry limit, tree depth 128, aligned/disjoint
buffers and unchanged-on-failure guarantees. Invalid topology, missing cell
metadata, overlapping tracks/spans, invalid metrics, reserved bits or buffer
aliasing fail explicitly. No pointers, source controls or font objects are kept.

## Validation

Both complete native providers build and all **20/20 CTest suites pass**. The row
fixtures cover differing cell heights, descendant margins and insets, overflow,
column spans, nested/shared tracks, nonmonotonic line Y, invalid topology, aliases,
failure atomicity and 32 independently calculated scalar grid cases. A standalone
AddressSanitizer/UndefinedBehaviorSanitizer document-flow executable also passes.
Managed contract/provider tests pass **9/9**, zero skips; the full test project
build has zero warnings/errors. Generated contracts and export manifests verify.

Evidence:

- artifacts/release-hour/document-rows-native-final-build.log
- artifacts/release-hour/document-rows-native-final-tests.log
- artifacts/release-hour/document-rows-managed-build.log
- artifacts/release-hour/document-rows-managed-tests.log
- artifacts/release-hour/document-rows-contract-verify.log
- artifacts/release-hour/document-rows-sanitized

The default native package consumer now checks a text cell and a measured-object
cell through both providers before creating a GPU device. Local execution and
exact-head package CI are separate qualification steps. The complete default
consumer passes locally on macOS ARM64 through the existing project-reference
development lane, including row placement on both providers, retained rendering
and owner-query isolation. Logs: artifacts/release-hour/document-rows-consumer-build.log
and document-rows-consumer-tests.log. This is not all-RID package qualification.

## Required source integration and remaining contracts

The LibreWPF consumer is not connected by this producer change. Its next batch
must export the actual table/row/cell tree, format paragraphs at native cell widths,
draw the original block backgrounds/text/children, and retain generation ownership.
Source line order is not screen Y order inside a table: do not enable tables while
the text view still binary-searches all document lines by Y. Point hit selection,
vertical navigation and selection must use cell-aware retained source ranges.

Fixed column tracks and column spans are implemented here. Automatic/intrinsic
column sizing, row spans, RTL tables, row/cell fragmentation and full source
table interaction are not. Do not flatten or silently omit them. Figure/Floater,
inline controls and remaining application contracts still block the core path.
Broader DirectX/Direct2D/Win2D requests remain in the overall goal, not completed
by this document feature. Final package/platform/Windows VM comparison gates
and required green PR CI remain mandatory before merging.
