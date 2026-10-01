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
The explicit EDIT source-point query changes only a strict original-grapheme
interior's X to that exact retained trailing X; Y stays the original source row's
top, not a native caret raster offset. Qualified boundaries and no-draw hard-delimiter
positions delegate unchanged ordinary source mapping. Forms keeps its original
single-line `EM_POSFROMCHAR` Y=0 policy and client/viewport/scroll conversion.

Missing ownership, malformed UTF-16, vertical frames, a grapheme crossing rows or
bidi frames, gaps/noncontiguous visual ownership, or a missing/ambiguous trailing
edge reject the complete generation explicitly. A multi-grapheme shaping cluster
sharing an original grapheme with another box also rejects; it cannot absorb
that fallback owner transitively. The complete candidate is validated before publication;
rejection never publishes a partial geometry view. Wrapping that separates one
original owner is not repaired by this API. Interior CRLF positions without a
writer-owned caret remain explicitly unsupported, while retained empty/hard rows
and shared-wrap affinities keep their ordinary exact frames. Empty-row insertion
indices are remapped from original boxes to merged owners, retaining their order
after a preceding fallback owner coalesces.

An otherwise independent cluster spanning multiple original graphemes remains
the exact original shaped box, with its missing interior edges recorded. It does
not disable queries over unrelated validated owners. Whole-edge selection keeps
the known original box; partial selection, interior caret/source-point requests
and pointer queries selecting that box reject with its exact source interval.
Hit admission uses the selected box index from the unchanged canonical traversal,
not rectangle or endpoint equality; empty rows have no selected box. No original
glyph, cluster, font, feature, position or ordinary navigation changes. Complete
ligature-interior behavior remains a source admission requirement; this explicit
query-domain limitation is not full EDIT support.

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

The exact-head Ubuntu quality job `110536636309` in Build `36911990959` at
`87e932609cb0` passed 840 of 841 tests. Its sole failure was the unchanged complete
combining source `go A😀 é fin `: view construction rejected a multi-grapheme
shaping cluster elsewhere in that generation. The original log did not record
its exact span or font identity, so identifying the trailing `fi` specifically
remains an inference. The query-domain fix changes no fixture string, font,
feature or assertion. A focused managed-source harness passes 47 cases, including
the original 42 and new disjoint/affected-domain, outer-edge and exact-hit-owner
controls. Synthetic intervals prove policy mechanics, not Windows ligature
behavior; the unchanged Ubuntu fixture still requires fresh hosted validation.

The bounded check is `dotnet test artifacts/edit-query-controls/ProGPU.Tests.csproj
-c Release --nologo -v minimal -m:1`, linking only these interaction controls and
the unchanged retained Drawing cases against actual managed source. Its final
`--no-build --no-restore` receipt is
`artifacts/edit-query-controls/results/focused.trx` (47 passed, zero failed/skipped).
