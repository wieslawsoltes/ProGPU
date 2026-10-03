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

For CFF2, the admitted origin is explicit VORG plus its optional VVAR origin-map
delta. VVAR supplies the advance delta; an absent VVAR means fixed vmtx/VORG, not
an error or a gvar fallback. CFF contours are never treated as TrueType phantoms.
Missing CFF origins and missing vertical metrics remain separate unfinished work.

The per-glyph vertical cache is populated lazily and independently of the existing
outline cache. Both caches and the prepared output publish only after the complete
run validates. Repeated glyphs reuse their own immutable values; explicit/null
and signed advances retain original source identity and logical occurrence order.
Preparation uses bounded existing outline/tuple scratch, O(P) point-bound work
during the existing decode, and O(G) retained vertical records for G unique glyphs.
VVAR scalars are prepared once for the owned normalized instance.

The algorithm follows the published [OpenType VVAR contract](https://learn.microsoft.com/en-us/typography/opentype/spec/vvar)
and [vertical metric/phantom relationships](https://learn.microsoft.com/en-us/typography/opentype/spec/vmtx).
Natural design values remain unrounded floats through this path. No DirectWrite
fractional INT32 design-metric rounding rule has been inferred or inserted.
Independent integral-instance original SDK/pixel controls and separate fractional
observations remain required to establish the actual source numeric contract.

Authoring only: no build, test, syntax check, verifier, SDK probe, renderer, GPU,
VM or CI execution. This implementation is not a claim of original-Windows pixel
parity, complete vertical text support or application qualification.
