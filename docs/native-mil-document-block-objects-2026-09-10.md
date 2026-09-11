# Native document measured-block placement

## Application dependency

LibreWPF's unchanged real Application.Run document contains a BlockUIContainer
before its Table. Its portable document tree currently rejects that block. The
source rich editor now uses the shared native FlowDocument formatter, so the next
required contract is placement of an actual measured UI child alongside formatted
paragraphs. Microsoft documents that this element owns one UIElement, which may
itself contain a control tree:
[BlockUIContainer.Child](https://learn.microsoft.com/en-us/dotnet/api/system.windows.documents.blockuicontainer.child?view=windowsdesktop-10.0).
That public ownership contract was consulted, not foreign implementation code.

## Implementation

`progpu_native_document_arrange_with_objects` is additive to the existing block
flow ABI. It accepts an ordered span of measured non-text leaf metrics. Objects
never enter the paragraph line array; native boxes retain the same block indices,
and callers retain child controls and source text positions. The source must
measure at the widths returned by the existing ResolveWidths service first.

The original ProGPU-owned width/measure/place implementation is shared, with no
second layout algorithm. Both native providers compile the same translation unit.
Object height participates in native sibling and ancestor placement, including
border/padding and collapsed margins. Desired-width overflow extends the document.
Zero-size objects remain actual replaced content, not through-collapsing empty
containers. Negative/nonfinite sizes, nonzero reserved fields, unordered/duplicate
indices, non-leaf targets and targets with paragraph lines are invalid. All outputs
remain unchanged on failure. Old object-free entry points retain their ABI.

The C header generates `NativeDocumentObject`. Managed/native calls use synchronous
borrowed spans and one crossing per arrangement. `IPortableDocumentFlow` exposes
the optional typed method; older providers delegate empty object spans to ordinary
arrangement and explicitly reject nonempty spans. No object may be silently omitted.

Complexity remains O(blocks + lines + objects). Ordered tree/prefix dependencies
are scalar; independent object metric validation reuses the existing NEON/SSE2
double-pair predicate. Ordered cursors avoid an extra per-block object map or
larger placement-state array. No device, shader, renderer, source reflection or
per-object native crossing is introduced. No performance claim is made.

## Validation and integration boundary

Both macOS ARM64 native providers build. Full native CTest passes 20/20. New tests
cover mixed text/object blocks, nested insets, growth, overflow, zero-sized objects,
source-line counts, failure atomicity and an independent scalar placement oracle.
Managed contract tests pass 8/8; the Release managed test project builds with no
warnings/errors. Generated native contracts verify successfully. The final native
run after the cursor-only scratch refinement also passes 20/20 (4.75 seconds).

This native placement contract alone does not complete BlockUIContainer. LibreWPF
must connect actual UIElement measurement, borrowing/detachment, retained drawing,
source point/caret/selection/navigation and desired-size invalidation. Paginated
objects, inline UI, anchored blocks and tables remain separate required contracts.
No source guard or final package/platform gate is removed by this producer change.

Evidence: `artifacts/release-hour/document-objects-{native-build,native-tests,
managed-build,managed-tests,contract-generation,contract-verify}.log` in the task
validation checkout. These results are not Windows/Linux package qualification.
