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
collection does not mutate a captured generation. Logs and TRX files, including
the initial strict decoration failures, remain under `artifacts/text-interaction`.
Full PR CI, renderer pixels, package consumers and final source-editor/platform
qualification remain separate requirements.
