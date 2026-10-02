# Original source Display text

The application target is unchanged AvalonDock theme startup: menus, tab headers
and title bars set `TextFormattingMode.Display`, reaching LibreWPF's explicit
`PortableTextLine.CreateCore` rejection tracked in LibreWPF #184. This work is
part of that end-to-end source path, not a new general typography family.
No source provider advertises Display at this checkpoint.

## Original capture and fitting identity

The private native paragraph producer accepts optional original `double` em/DPI
values and explicit capture/advance policies. Existing public C/managed raw hinted
APIs and all wire layouts remain unchanged. Original values are retained in the
owned paragraph, copied during continuation, and protected by its complete
output-alias guard; device em is not substituted into source font size.

Capture choices are exact 26.6, nearest physical em with half-up ties, and nearest
physical em with ties-to-even. These are explicit arithmetic choices, **not an
automatically selected WPF compatibility policy**. Invalid choices, nonfinite or
unrepresentable frames, inconsistent source scale and mismatching effective
device frames fail before publication. Arbitrary finite source doubles are kept
as doubles; existing raster representability limits remain independent.

Physical ties-to-even advance rounding occurs after the complete original
GSUB/capture/GPOS shaping operation and before the common measured writer. It
produces fitting metrics without changing raw retained run metrics, descriptors,
glyph/source indices, full bidi levels, dependency flags or offsets. The glyph
frame verifier checks fitting values against the original run and exact selected
policy; it does not discard raw identity checks. Signed integer arithmetic retains
negative half ties and rejects overflowing rounded values atomically. Original
raw calls select no additional policy and retain their old results.

The existing writer still uses float metric projection at this checkpoint. This
is not sufficient for source Display: at DPI 1.5, for example, a seven-pixel
advance has original logical value `7 / 1.5`, not a float promoted to double.
Required next work connects precise generation-owned source metrics, original
offset conversion, shaping-boundary fitting/recomposition, double line/interaction
frames and the paired optional source binding. No source-local division, snapping,
prefix shaping, unsafe-flag stripping or Ideal fallback is permitted.

## Revalidated diagnostic evidence

The earlier immutable receipt `device-em-source-rounding-hypotheses.json` was
read again without changing or rerunning it. Its SHA-256 is
`81d9b35953ecfdcfb434c382f38abb3a9d2046fa55843f938064fb75fb237f24`;
the producing `Program.cs` is
`c49b8d278d5ed950037aec71ce47bf3b57e63e5ef4427723a9f596cb56b5f704`.
The file records native source `60347a5f1026b8f95582535e6b7cfc8431a04499`
and successful producer Build `36915664259`. Actual on-disk input hashes still
match that program's checks:

- Native macOS ARM64 module: `53be0560d745941f5b3d8c454c0da5e547cc6907c5669560aa1e9ce8339d44a0`.
- Original Inter font: `40d692fce188e4471e2b3cba937be967878f631ad3ebbbdcd587687c7ebe0c82`.
- Original x64 source-observation receipt: `accdc0be607a723c915314ee48cf88fe9fe6a897c7c4c2af7fba245091ecd394`.

For interpreter 35, the complete original text was shaped once per case. All 800
original occurrences aligned by original source range, glyph and uniform full
native bidi level before parity projection. Nearest physical em plus physical
ties-to-even advances matched 800/800; original fractional em plus the same
advance rounding matched 696/800. Raw unrounded nearest-em results matched only
672/800. These are narrowly scoped hypotheses over one font and 96 cases, not
Display, offsets, wrapping, caret, pixels, source application or platform
qualification. Existing midpoint samples do not distinguish half-up from
ties-to-even device-em selection.

Original WPF `TextAnalyzer.cpp` requests GDI-compatible glyph placement with
`useGdiNatural = FALSE`, converts returned floats through the original ideal-unit
advance/offset arithmetic, and `TextFormatterImp.RoundDipForDisplayMode` defaults
to ties-to-even. Offset and midpoint policy must be established from that source
contract and an independent source oracle before provider advertisement; the
800 matching advances alone cannot establish them.

## Qualification

This implementation-first checkpoint has no new native, font, renderer, GPU,
source-app or package execution evidence. Display activation stays guarded until
the complete source producer/consumer path and original independent comparisons
are connected. Existing qualified runtime artifacts and dependency pins are
unchanged.
