# Retained EDIT interaction

The explicit `DrawingTextLayout` EDIT caret, selection and point queries consume
one original formatted generation. Ordinary `GetCaretStop`, selection and visual
navigation retain their existing shaping-cluster contracts. These queries do not
call the word classifier or admit an ordinary Forms provider.

## Original ownership and frames

`TextLayout` captures source grapheme ownership before fallback-font partitioning
only when retained Drawing formatting requests it. `OriginalGraphemePolicy`
extracts the **same** `StringInfo.GetNextTextElementLength` policy already used by
the ProGPU-owned managed shaper at parent `84d50f6fb14e`. The shaper and writer now
call that one helper. This is not a pinned Unicode-version or Microsoft EDIT
segmentation-parity claim, and interaction never resegments source at query time.

The EDIT view joins original writer boxes only when their captured source owners
overlap, retaining each row, bidi level, top and measured advance frame. A source
endpoint strictly inside that owner returns its unchanged UTF-16 index at the
exact retained logical trailing caret. It does not interpolate ink, reshape a
prefix, change a glyph cluster or borrow mutable font/format state. Point hits
use the whole-owner start/end inventory, which remains distinct from word stops.

Missing ownership, malformed UTF-16, vertical frames, a grapheme crossing rows or
bidi frames, gaps/noncontiguous visual ownership, a shaping cluster spanning
different original graphemes, or a missing/ambiguous trailing edge
reject explicitly. The complete candidate is validated before publication;
rejection never publishes a partial geometry view. Wrapping that separates one
original owner is not repaired by this API. Interior CRLF positions without a
writer-owned caret remain explicitly unsupported, while retained empty/hard rows
and shared-wrap affinities keep their ordinary exact frames. Empty-row insertion
indices are remapped from original boxes to merged owners, retaining their order
after a preceding fallback owner coalesces. Cross-original-grapheme ligatures
remain a source admission requirement; rejection is not full EDIT support.

The native Windows geometry receipt from run `36903729345`, SHA-256
`301650a7ba564d28783fc98d954fa23b4f71e16a9a6e19f4454bca927d2630c9`, independently
observes identical full BGRX prints for emoji ranges 4–7/7–9/4–9, joiner ranges
1–4/4–6/1–6 and combining ranges 7–8/8–9/7–9. Interior carets 7/4/8 respectively
coincide with the owning end 9/6/9 while the source indices remain unchanged.
These are structural acceptance relations, not permission to copy the oracle's
absolute font metrics, add a pixel to bounds, or normalize signed selection.

## Applicability and cost

Both renderers consume the same original Drawing glyph commands and selection
rectangles. This metadata view changes no shader, native glyph generation,
font fallback, draw position, DPI, atlas, GPU lifetime or native text ABI. Native
WPF interaction/navigation and ordinary managed interaction remain unchanged.
There are no new native crossings, font loads, GPU operations or readbacks.

Capture costs O(S) source scanning/storage. View construction costs
O(G log S + K + C + G log G) metadata work and O(S + G + C) storage for source
units S, original boxes G, scanned owner references K and carets C, with immutable
generation-local ownership. Its prefix/owner walks are
dependency-bound rather than independent SIMD lanes. The explicit view is lazy
and cached by retained Drawing; queried selection allocations keep the existing
shared rectangle algorithm. No latency, allocation-free replay or UI parity
claim follows from focused source checks.

## Primary design references

- [HarfBuzz cluster ownership](https://harfbuzz.github.io/working-with-harfbuzz-clusters.html):
  keep shaped clusters and their source indices intact; do not rewrite glyph data.
- [DirectWrite range hit testing](https://learn.microsoft.com/en-us/windows/win32/api/dwrite/nf-dwrite-idwritetextlayout-hittesttextrange)
  and [Win2D caret queries](https://microsoft.github.io/Win2D/WinUI2/html/M_Microsoft_Graphics_Canvas_Text_CanvasTextLayout_GetCaretPosition.htm):
  retain source range and affinity independently of glyph ink.
- [Skia reusable shaped text](https://skia.org/docs/dev/design/text_c2d/),
  [Parley layout](https://docs.rs/parley/latest/parley/layout/index.html),
  [WebRender display lists](https://github.com/servo/servo/wiki/Webrender-Overview)
  and [Vello rendering separation](https://github.com/linebender/vello): retain
  layout metadata separately from paint, clipping, GPU caches and device lifetime.

Only original ProGPU algorithms are reused. Full-source native classifier-domain
qualification, exact renderer/package pixels and final source/editor UI admission
remain separate gates.

## Focused implementation checks

The actual managed Drawing source builds with zero warnings/errors. A linked
device-free source harness passes 42 cases with zero failures/skips: eleven new
owner/frame/rejection/generation controls, the three original Drawing geometry
families plus retained hard/empty-row and wrap-affinity controls, and
27 unchanged retained-layout cases. The three actual-source Forms comparisons
failed against `ce48bd4a` before this change: emoji/joiner subranges had separate
half-span rectangles and different interior/end X values; combining selection
already covered its owner, but caret 8 returned source 7 at the leading edge.
No expected coordinates, tolerances or source indices were relaxed.
