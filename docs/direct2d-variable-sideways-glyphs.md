# Retained variable sideways original glyphs

This implementation connects the same original captured axis instance to natural
OUTLINE sideways placement. It does not change the static sideways controls,
horizontal shaping, odd-bidi sideways gate, GDI measuring policy or raster mode.

The first sideways request owns one strict retained vertical source helper and
one VVAR instance with cached region scalars. It uses the existing full captured
axis normalization, never defaults for missing axes or per-glyph source callbacks.
Each cached TrueType variable outline retains its actual point-domain yMax from
the original decode. A later sideways request does not decode that outline again.

For TrueType, VVAR advance-height deltas take precedence over the difference of
the two gvar vertical phantom deltas. If VVAR supplies a top-side-bearing map,
the origin is the actual varied yMax plus original TSB plus that delta; otherwise
the original top origin receives the actual top phantom delta. The vertical-origin
map is not used for TrueType. Empty glyphs consume their real varied advance
without needing an invented ink origin.

For CFF2, the source adapter retains explicit VORG even when VVAR supplies a
nonzero origin map, matching original Windows metrics and outlines across the
five retained instances. The map is still validated by the unchanged generic
reader. VVAR advance deltas still apply; an absent VVAR keeps fixed metrics.
CFF contours are never treated as TrueType phantoms. Without VORG, the separate
[control-maximum source policy](direct2d-cff-contour-origins.md) applies; missing
vertical metrics and origin maps lacking their VORG base remain gated.

The per-glyph vertical cache is populated lazily and independently of the existing
outline cache. Both caches and the prepared output publish only after the complete
run validates. Repeated glyphs reuse their own immutable values; explicit/null
and signed advances retain original source identity and logical occurrence order.
Preparation uses bounded existing outline/tuple scratch, O(P) point-bound work
during the existing decode, and O(G) retained vertical records for G unique glyphs.
VVAR scalars are prepared once for the owned normalized instance.

The underlying variation reader follows the published [OpenType VVAR contract](https://learn.microsoft.com/en-us/typography/opentype/spec/vvar)
and [vertical metric/phantom relationships](https://learn.microsoft.com/en-us/typography/opentype/spec/vmtx).
Natural design values remain unrounded floats through this path. No DirectWrite
fractional INT32 design-metric rounding rule has been inferred or inserted.
Independent integral-instance original SDK/pixel controls and separate fractional
observations remain required to establish the actual source numeric contract.

Authored controls add eight font/table configurations, five independently
specified instances and both explicit and null advances: 80 full-byte frames per
provider, each cold, warm and independently drawn. Literal dyadic rectangle
oracles distinguish changed vertical origins, empty-glyph movement and complete
horizontal instance ownership. Each provider keeps its existing exact one-draw,
one-submission caller assertions. The original 24 static frames are unchanged.

Raw controls additionally cover horizontal-before/after cache reuse, raw sideways
BOOL, repeated logical occurrences, lazy malformed VVAR, late-failure rollback,
equal-byte wrong-owner rejection, and a deliberately differing VVAR/gvar fixture.
That last fixture is a precedence discriminator, not an original-positive font
parity assertion; its independent Windows readings are observation-only until
the original implementation is actually measured. No existing source assertion
or deadline is relaxed.

Original Windows execution completes the full inventory. Matching metric and
outline origins alone does not qualify grayscale raster pixels, complete vertical
text, packages or applications; remaining full-byte failures stay strict.
