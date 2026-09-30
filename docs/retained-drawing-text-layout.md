# Retained Drawing text layout

`ProGPU.SystemDrawing.DrawingTextLayout` captures one complete horizontal
paragraph from the existing `Graphics` formatter. It owns exact glyph-index and
position arrays, physical `TtfFont` references, prepared decoration records and
an owned `TextInteractionSnapshot`. It does not retain the caller's `Graphics`,
`Font`, `StringFormat` or brush. Repeated paint records the same glyph arrays;
caret, point and selection queries reuse the captured shaping generation rather
than measuring prefixes or rebuilding interaction boxes.

The application connection is portable LibreWinForms editor painting, caret and
selection. This is its reusable Drawing dependency, **not completed editor UI**.
Source cache invalidation, selection/caret painting, pointer capture, scrolling,
empty hard-row caret navigation and native keyboard/platform acceptance remain
required. Existing `TextLayout` hard-row/vertical interaction limitations are not
qualified by capturing them. The separate empty-string caret uses the actual
source font's line-height metric, not an invented glyph or glyph ink bounds.

## Ownership and coordinates

Create through `DrawingTextLayout.Create(graphics, text, font, layoutSize, format)`.
The optional default format retains all source characters. Explicit formats must
use `StringTrimming.None`, `StringDigitSubstitute.None`, no hotkey rewriting, and
neither vertical direction nor `LineLimit`. Unknown flag bits reject before any
drawing command is recorded. Those policies need explicit source mapping before
they can participate in retained interaction; ordinary `Graphics.DrawString`
retains its existing behavior.

The whole paragraph is shaped even when the destination clips its height.
Drawing applies the selected clip, world transform, brush and supplied rectangle
location. Interaction coordinates are relative to that rectangle, including the
same horizontal and vertical alignment offset. Font size and target DPI are
captured together; drawing at another DPI rejects before recording, requiring
the consumer to rebuild its layout. Source font realization/scaling stays in the
existing caller policy.

Prepared decoration records retain baseline and displacement separately. Painting
uses the original `(originY + baseline) - displacement` order: storing a final
relative rectangle then adding the origin changes float rounding. The strict
command comparison caught that error in all eight initial decorated fixtures;
the corrected implementation preserves exact equality without a tolerance.

## Shared implementation and cost

Provenance is the ProGPU-owned `Graphics.CreateFormattedTextLayout`,
`DrawFormattedGlyphRuns` and `DrawFontDecorations` at parent `1ce43aa12`, plus
`TextLayout`'s existing logical interaction algorithms. The ordinary and retained
paths share those algorithms, exact run partitioning and decoration placement.
Run preparation replaces growable per-run glyph lists with exact arrays. Both
managed and C++ renderers consume the existing `DrawGlyphRun`/rectangle commands;
there is no second C++ shaper, shader, atlas key, fallback renderer or native ABI.

The [text design references](text-layout-advance-interaction.md#design-references-and-scope)
apply unchanged: HarfBuzz advance/offset separation, DirectWrite/Win2D source
affinity, reusable Skia/Parley layout, and WebRender/Vello layout-versus-paint
ownership. No external implementation was copied. New preparation is O(glyphs),
with O(glyphs + clusters) retained managed storage and no GPU readback or new
managed/native crossings. Run boundaries and caret/selection walks are ordered
metadata operations, not independent-lane pixel kernels. There is no global text
cache, residency claim or measured latency improvement.

## Implementation checks

The Drawing build succeeds with zero warnings/errors. The focused Drawing run
passes 58 cases with zero failures/skips: 18 new retained-layout cases plus the
unchanged formatted-text and font-quality cases. They cover exact commands,
left/center/right and RTL alignment, wrapping, combining text, decorations,
reused glyph arrays, caller disposal/mutation, complete text under clipping,
empty font metrics, target-DPI rejection and unsupported-format rejection.

Existing and new owned-snapshot cases compare the shared point/caret/navigation/
selection contract and prove that clearing/regenerating the original glyph
collection does not mutate a captured generation. The initial focused text run
passed all 88 selected shaping, interaction, snapshot and source-guard cases, with zero
failures/skips. PR #201 depends on #200; its base is `main` so that all required
Build, Docs and parity workflows run before the ordered merge. Logs and TRX files, including
the initial strict decoration failures, remain under `artifacts/text-interaction`.
Full PR CI, renderer pixels, package consumers and final source-editor/platform
qualification remain separate requirements.

Canonical editor integration exposed repeated arrow stops at an ordinary shared
cluster edge: leading and trailing affinity records had the same source index,
physical position and bidi level. Two independent movement cases fail before
the correction (ordinary text and a combining cluster). Movement now skips only
those coincident alternatives, retaining the actual affinity records and distinct
bidi/source/line positions. The full linked text/source-guard project passes
143 cases with zero failures/skips after this correction.

The first complete Drawing CI at `8138dc9e3` failed the unchanged
`WarmedPrivateMetricReadsAreAllocationFree` assertion (zero expected, 1,024 bytes
observed). That failure is retained in the job log and is not a successful gate;
neither its assertion nor the runtime/warmup policy has been relaxed.

## Original cluster range at a point

`DrawingTextLayout.HitTestCluster(PointF)` and
`TextInteractionSnapshot.HitTestCluster(Vector2)` return a
`TextClusterHitTestResult`: the existing `Hit`, plus `ClusterStart` and
`ClusterLength` in original UTF-16 units. The range belongs to the exact retained
generation that selected the hit. Empty rows return their writer-owned insertion
position with zero length; empty text returns `(0, 0)` and Drawing retains the
actual font line height. An outside hit retains the nearest selected cluster and
`IsInside == false`.

The original point-hit constructor/deconstruction and result semantics are
unchanged. Both methods use the original single selector from
`TextInteractionSnapshot` at `4b0064a9c`; the Drawing wrapper applies the same
alignment translation once. Trailing hits already contain the cluster **end**,
not its start plus one. The new range comes directly from the selected retained
box, never from subtracting a UTF-16 unit, probing another point, reshaping text,
or borrowing a mutable glyph collection. Bidi half/midpoint rules, row selection,
shared-edge ties and nearest-box order are unchanged. Query cost remains the
existing O(clusters + empty rows) scan with O(1) extra value storage and no new
allocation, GPU work or managed/native call.

This is a managed retained-layout metadata exposure, not a new shaping or hit
algorithm. Both renderer modes consume the same Drawing layout; native paragraph
interaction, C ABI, shaders and renderer commands are unchanged. The design
references above still apply. No word boundaries or Windows EDIT selection
semantics are inferred: Forms owns the immediately dependent source selection
policy and interaction integration.

Fifteen focused cases are authored in the existing text and Drawing test projects
for actual combining/supplementary/ligature source ranges, bidi halves, midpoint
and shared-edge ties, outside hits, blank/CRLF rows, original API shape and aligned
Drawing results. The existing snapshot-generation case also retains the new result
through source glyph clearing/regeneration. These additions have **not been built
or run locally**; full CI and the source editor's native acceptance remain required.
