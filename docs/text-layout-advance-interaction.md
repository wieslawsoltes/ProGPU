# Managed text interaction uses retained layout pens

`TextLayout` previously grouped clusters by positioned glyph Y and used drawing
X plus advance as interaction bounds. OpenType placement can therefore split
one combining cluster into multiple interaction boxes, move caret/selection
coordinates, and assign different line tops to fallback glyphs. Rounded glyph
height also differed from the actual line height retained by the writer.

Horizontal layout now retains its existing line partition with the writer's
physical alignment origin, top and height. Interaction walks the original
visual-order advances inside those exact lines. Glyph X/Y, font/glyph identity,
source UTF-16 clusters, bidi levels, shaping, wrapping, tabs and rendering are
unchanged. A zero-advance offset mark remains part of its original cluster.
ASCII and ordinary shaped paths publish the same frames; regeneration replaces
them. Changed public glyph membership must be regenerated before interaction.

This directly follows the original ProGPU C++ measured-advance algorithm at
`src/ProGPU.Native/src/Text/progpu_native_text_interaction_impl.hpp`, as present at
`7fdf4057ee9bbf42286e1b6c47d9af1eca49d9a7`. The native paragraph adapter already
uses this contract; no native behavior change is needed. Its existing C ABI
differential now also covers the offset zero-advance mark. Both managed and
native renderers continue to receive the unchanged drawing positions. Neither
backend gains an offset-repair transform, different shader or renderer fallback.

The extra retained state is `O(L)` for `L` lines, reusing the writer's line list
rather than adding per-glyph metadata. Pen accumulation is dependency-bound
`O(G + L)` for `G` glyphs and needs no SIMD-independent glyph work. Existing
logical-cluster sorting and returned interaction allocations are unchanged.
There are no new native crossings, font discovery, GPU initialization, uploads,
atlas/cache keys, worker scheduling or device-loss behavior. This is a correctness
change, not a measured rendering-performance improvement.

## Design references and scope

- [HarfBuzz positions](https://harfbuzz.github.io/harfbuzz-hb-buffer.html#hb-glyph-position-t)
  distinguish pen advances from draw-only offsets. Adopt that separation without
  changing shaping, fallback fonts or variable-font state.
- [DirectWrite text-position hit testing](https://learn.microsoft.com/en-us/windows/win32/api/dwrite/nf-dwrite-idwritetextlayout-hittesttextposition)
  and [Win2D caret queries](https://microsoft.github.io/Win2D/WinUI2/html/M_Microsoft_Graphics_Canvas_Text_CanvasTextLayout_GetCaretPosition.htm)
  keep source position and affinity separate from drawing. Retain our original
  UTF-16/bidi identities; do not infer a caret from ink bounds or raster snapping.
- [Skia shaped text](https://skia.org/docs/dev/design/text_c2d/) and
  [Parley layout](https://docs.rs/parley/latest/parley/layout/index.html)
  separate reusable layout/line/cluster data from positioned glyph rendering.
  Retain the actual writer's frames, with no second layout or paragraph shaping.
- [WebRender's display-list architecture](https://github.com/servo/servo/wiki/Webrender-Overview)
  and [Vello's renderer separation](https://github.com/linebender/vello)
  inform keeping interaction metadata independent of visibility, retained draw
  commands, GPU batching and raster-cache lifetime. Existing lazy initialization,
  DPI/font size, hinting and subpixel policy remain unchanged; no new cache or
  culling scheme is warranted for this fix.

Only ProGPU-owned algorithms were reused. No external implementation was copied.
The existing vertical interaction compatibility path is unchanged. Empty-row
caret admission and full source-editor caret/selection drawing, scrolling and
native desktop input remain separate required connections; this fix alone does
not complete LibreWinForms editor UI or popup qualification.

## Implementation evidence

All five initial cases failed on the original source after correcting the test's
overly specific assumption that the Inter mark must have a Y offset. Its real
shaped X placement is retained. The failures include real line-height mismatch
and caret/selection changes after controlled draw-only X/Y offsets.

After the fix, eleven new cases and all 71 existing OpenType shaping cases pass in
the linked source harness: **82 passed, zero failed/skipped**. This is not the
full renderer suite. Coverage includes left/center/right alignment, ASCII and
wrapped paths, combining clusters, zero-advance marks, LTR/RTL order and affinity,
hard breaks/empty-row gaps, and regeneration. The native C++/C ABI differential
passes with Apple Clang C++20, `-O2 -Wall -Wextra -Wpedantic -Werror`, including
all its original rejection and fragment/measured cases.

Logs and TRX files are retained under `artifacts/text-interaction/`, including
the initial fixture and linked-test assembly-identity compilation failures.
Full exact-head CI, Svg.Skia parity, package/NativeAOT and eventual application
validation remain required; no VM or desktop rendering was used here.

The first hosted producer `36302203783` at `90ae0016c` exposed one retained
source-shape guard: it requires the original `readonly struct LineRange` with
explicit start/count properties. Linux completed 4,773 cases (4,763 passed,
one guard failure, nine existing skips); the Windows source-guard group had
93 passes and that same failure. The implementation now preserves that original
struct and extends its explicit fields, retaining every guard and additionally
checking line origins, tops, heights and shared writer ownership. All 82 focused
cases plus the actual guard pass locally (83/83). The failed hosted producer is
not eligible for artifact staging; a new complete producer is required.
