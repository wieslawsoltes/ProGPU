# Explicit source soft-wrap spaces

The original Microsoft reference separates a soft-wrapped space's source run,
advance, line width and caret position. For `AB CD EF ` in a right-to-left
paragraph at 13 DIP, 96 DPI and width 25, the first original line contains `AB `
in one LTR GlyphRun with advances 9, 9 and 4. Its full public width is 18, its
trailing-whitespace length is zero, and the space's ending caret lies outside
that width. The last line instead has a paragraph-direction trailing run.
Hebrew in an LTR paragraph exhibits the opposite-direction counterpart. Multiple
spaces may be partitioned at a complete cluster within the whitespace sequence.

These observations are from the independent original Microsoft producer
[`2cabe60e6a8324d9d1d1ad74171ce4cb68d26184`](https://github.com/wieslawsoltes/ProGPU/commit/2cabe60e6a8324d9d1d1ad74171ce4cb68d26184),
[successful x64/ARM64 run 38639376145](https://github.com/wieslawsoltes/ProGPU/actions/runs/38639376145).
Each architecture retains 198 supplemental cases, 410 lines and 622 runs, with
actual draw frames, source metrics, carets and selection. All 960 pre-existing
reference cases remained field-identical. Schema 106 is a separate exact-byte
input; it does not replace an older receipt or relax an existing comparison.

## Producer contract

`PROGPU_NATIVE_SOURCE_PRESERVE_SOFT_WRAP_SPACE_GEOMETRY` is an explicit source
option. Its default is false, independent of whole-run positioning policy. A
matching producer is required; older producers reject the new flag. Existing C
record sizes, offsets, import versions and managed overloads stay unchanged.

The first complete opposed U+0020 separator cluster stays with fitting content
even when its advance extends beyond the line width. For example, the original
`TAR ` line has content width 24 and advances totaling 28 under a 25-DIP limit;
`AB CD ` has content width 41 and advances totaling 45 under a 41-DIP limit.
Further opposed spaces count toward fitting. With emergency breaking enabled
the source fitter may retain the last fitting
prepared cluster within that space sequence. It never clears unsafe flags,
cuts a shaper cluster, reshapes a suffix or changes the source break inventory.
The original full advance extent remains in the fitted partition, independently
validated during import.

For a soft line ending in complete U+0020 clusters, the shared visual writer
keeps the original resolved tail level and applies its existing cluster L2
ordering. The tail must have one complete original embedding level. An opposed
tail remains in the original positioned glyphs and interaction boxes, but its
extent is excluded from the source content width. RTL tail spaces lie before
the content origin; LTR tail spaces follow the content. The line's content
origin remains explicit, and no alpha/ink bound replaces it. Width accumulation
uses the selected original source arithmetic; per-glyph advances and offsets
are unchanged. Negative tail advances and invalid extents reject publication.

Hard/final lines retain the original L1 reset and full width. Generic shaping,
layout and bidi calls still select canonical L1/L2. NBSP, en-space and other
space families are outside this bounded U+0020 policy; they retain their existing
independent behavior and remain explicit differences in the original corpus.

Creation, reflow, cached C views, managed readers and native flat-resource import
preserve the flag. Import recomputes the actual source writer and interaction
from the owned original inputs; removing the flag from an affected resource
cannot admit stale widths, order or positions. Original contexts and paragraph
handles may retire while the resulting resource owns its data.

This CPU producer feeds both the managed renderer and both native providers.
They consume the same positions, source records and owners; no backend-local
text repair or shader change is needed. Existing source-run GlyphRun framing,
paragraph-to-device placement, caret rounding and ordinary WPF Display activation
remain separate unfinished contracts. This option alone does not qualify them.

## Research and implementation provenance

The implementation extends original ProGPU source fitting, cluster ordering,
the measured writer and strict source import. No foreign implementation text,
structure or helper was copied. The existing
[cross-engine ownership comparison](source-hinted-text-ownership.md) remains
applicable, with these primary references checked for the line-end change:

| Reference | Decision |
| --- | --- |
| [Unicode UAX #9 L1/L2](https://www.unicode.org/reports/tr9/#L1) | Preserve the generic per-line algorithm. The source-specific observable contract is an explicit separate policy. |
| [Skia shaping stages](https://docs.skia.org/docs/dev/design/text_shaper/) and [SkParagraph line geometry](https://github.com/google/skia/blob/main/modules/skparagraph/src/TextLine.cpp) | Retain reusable shaping and distinguish whitespace geometry from measured content; do not adopt SkParagraph's independent selection policy as a WPF oracle. |
| [DirectWrite line metrics](https://learn.microsoft.com/en-us/windows/win32/api/dwrite/ns-dwrite-dwrite_line_metrics) and [Win2D line metrics](https://microsoft.github.io/Win2D/WinUI3/html/T_Microsoft_Graphics_Canvas_Text_CanvasLineMetrics.htm) | Keep source range, newline and trailing-whitespace concepts distinct. Actual WPF observations decide this compatibility behavior. |
| [WebRender retained scene](https://firefox-source-docs.mozilla.org/gfx/RenderingOverview.html) | Keep corrected CPU results under existing retained resource ownership; no layout work enters GPU replay. |
| [Parley layout](https://github.com/linebender/parley/blob/main/parley/src/layout/line_break.rs) and [Vello scene](https://docs.rs/vello/latest/vello/struct.Scene.html) | Keep paragraph layout separate from reusable GPU scene encoding. Reject CSS hanging-space rules as a substitute for original Microsoft behavior. |
| [HarfBuzz clusters](https://harfbuzz.github.io/working-with-harfbuzz-clusters.html) | Preserve complete original clusters and dependency boundaries; do not trim the input to make a fit. |

Lazy initialization, fallback/variable font identity, worker scheduling, retained
scene reuse, culling, atlas keys/eviction, upload demand, GPU batching and device
loss are unaffected. Original em/DPI, hint interpreter and raw placement identity
remain unchanged. The suffix walk and extra width scan are bounded linear work
per selected line; fitting remains under its original candidate-visit budget.
There are no new allocations, native crossings or font calls in the writer.
Sequential prefix accumulation and bidi ordering have genuine data dependencies;
the existing independent SIMD metric lanes remain unchanged. No performance
improvement or complete source-rendering parity is claimed.

## Validation

Authored controls cover both paragraph/run directions, soft versus hard/final
ends, partial space sequences, unchanged canonical behavior, full-level identity,
complete clusters, raw glyph/owner preservation and continuation after producer
retirement. Flat import retains all source arrays and rejects altered flags,
positions, widths, carets and prepared boundaries. The diagnostic probe accepts
the two immutable schema-106 hashes in addition to all original receipts, and
selects the new policy explicitly with `preserve-soft-wrap-spaces`.

Local authoring validation passed seven CPU native CTest suites under
ASan/UBSan, including the existing generic text, source frame/run/policy tests
and the changed paragraph/transport/import suites. The six managed source
transport controls and 37 original parser/comparison controls passed with zero
skips. All six exact old/supplemental receipt parsers passed without loading a
native module; the probe built with zero warnings/errors. Strict enabled and
disabled hinting compilation and the complete generated-contract audit passed.
These CPU unit results do not qualify a new native runtime package.

Whole Build 38645948846 passed all 59 jobs at `89594077`. Its exact packages
completed all 7,008 original comparisons, but the unchanged audit rejected
856 previously matching line domains: counting the first hanging separator
incorrectly moved it to the following line. Both original comparison bodies
and all canonical-policy results remained unchanged. The failed qualification
is retained. The additional first-separator CPU control fails on that original
candidate and exercises the repair through creation, reflow and strict import;
the existing partial-space controls remain intact. All seven CPU suites pass
with ASan/UBSan after this repair. This correction still needs
its own whole producer Build and all original comparisons before qualification.

The whole successful exact producer Build must precede package staging and the
unchanged old and supplemental corpus runs. Full source pixels, application
input and ordinary Display admission remain required afterward.
