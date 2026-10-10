# Retained original vertical font metrics

`retained_original_vertical_metrics` retains the exact captured font owner and
reads its immutable selected SFNT face. It is a separate, lazy source helper:
creating an ordinary horizontal prepared font does not inspect or reject its
vertical tables. No DrawGlyphRun admission changes in this checkpoint.

The helper distinguishes two absent vertical tables from a malformed partial
pair. Present `vhea`/`vmtx` tables require the original supported header version,
reserved fields, complete long metrics and every compact-tail top bearing. It
never synthesizes vertical metrics from `hhea`. A failed creation or glyph query
leaves the caller's output unchanged. `source()` retains and returns the same
captured owner, not an equivalent font or mutable source callback.

For a nonempty TrueType glyph, the original origin is `glyf.yMax + vmtx.tsb`;
the paired bottom is that origin minus the original advance height. An empty
TrueType glyph still has its original advance and bearing but does not acquire
a fabricated origin. For CFF/CFF2 the helper accepts only an explicit validated
`VORG` origin, including its default and sorted glyph overrides. A missing VORG
does not turn a control-point envelope into a true cubic extremum. As required
by OpenType, VORG is ignored for TrueType, even if its bytes are malformed.

The additive Text `try_get_glyph_vertical_phantom_deltas` reads the actual final
two phantom Y deltas through the existing tuple/point walker. It checks the
original TrueType point/component count and publishes the top/bottom pair only
after all tuples decode. It does not reuse the horizontal X pair or apply VVAR,
rounding, hinting or source placement. Existing horizontal/advance APIs retain
their behavior. Caller-owned scratch and the original selected font lease remain
required.

The authored raw controls retain complete independent static TrueType/CFF and
variable TrueType fonts. They cover full and compact metrics, default/override
VORG, empty glyphs, source mutation after capture, absent versus malformed tables,
duplicate and out-of-range directory entries, reserved header bits, untouched
outputs and unchanged horizontal preparation. The paired phantom controls use
five positive/default/negative source instances with deliberately different X
and Y phantom deltas, exact caller scratch and rejection of malformed present
gvar, wrong item counts and invalid normalized-coordinate vectors. They are
registered in the existing portable Direct2D compatibility executable, without
changing its timeout or any prior control.

Primary source contracts:

- [OpenType vhea](https://learn.microsoft.com/en-us/typography/opentype/spec/vhea)
- [OpenType vmtx](https://learn.microsoft.com/en-us/typography/opentype/spec/vmtx)
- [OpenType VORG](https://learn.microsoft.com/en-us/typography/opentype/spec/vorg)
- [OpenType gvar](https://learn.microsoft.com/en-us/typography/opentype/spec/gvar)

Implementation and independent controls are authored only. No build, test,
verifier, font probe or GPU/Windows execution has been performed. Variable metric
precedence, missing-table source synthesis, original fractional rounding and
sideways rendering remain separate contracts; this metadata helper does not
qualify any of them.
