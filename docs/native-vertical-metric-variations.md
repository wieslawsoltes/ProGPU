# Retained native vertical metric variations

The neutral Text API reads VVAR deltas; it does not choose a vertical origin,
apply metrics to an outline, position sideways glyphs, or synthesize missing
vhea/vmtx/VORG information. Original DirectWrite source placement and missing-table
behavior remain separate work. No runtime or source admission changes here.

`sfnt_font_view::try_get_vertical_metrics_variation_region_count` measures the
scalar prefix after full preflight. `try_prepare_vertical_metrics_variation`
publishes an `sfnt_vertical_metrics_variation_instance` with cached scalars.
`try_get_vertical_metrics_variation` has single-glyph and batch overloads.
The batch retains caller glyph order and publishes all results or none.

The instance is a private validated **borrow**, not a font owner. Its caller must
retain immutable original font bytes and the prepared scalar prefix through all
copies of the instance. Reads require that same byte address, length, face index,
and face offset; byte-identical unrelated storage is not interchangeable. The
source owner must retain these spans in its existing selected-font lifetime.
Prepared reads allocate nothing and perform no font shaping or outline work.

## Strict new boundary

The VVAR table must use its complete version-1.0 header and a nonzero in-bounds
store. All coordinates must cover the original fvar axes exactly, in normalized
F2Dot14 range. The store's regions and every data subtable are checked, including
region indices even in unused rows. Every map entry is preflighted, including
entries beyond the font's glyph count. Reserved map bits, format 1, empty maps,
outer-index overflow and non-null out-of-range references are rejected.

Advance-height mapping can be implicit. Missing optional bearing/origin maps
remain absent, distinct from present maps yielding zero. A null data subtable
and the explicit `0xffff/0xffff` sentinel both yield zero variation. A short
mapping repeats its last entry. The neutral result exposes all map-presence
flags; deciding which are applicable to TrueType or CFF2 belongs to the consumer.

These rules follow the public [VVAR specification](https://learn.microsoft.com/en-us/typography/opentype/spec/vvar)
and [common variation formats](https://learn.microsoft.com/en-us/typography/opentype/spec/otvarcommonformats).
Scalar/delta arithmetic reuses ProGPU-owned ItemVariationStore code. Neither the
legacy HVAR policy nor the shared parser is weakened or changed.

All non-error outputs and unused tails survive a failure. Mutable aliases of
font bytes, coordinates, retained scalars, instances or sibling outputs are
rejected before writes. An error pointer aliasing protected storage is itself
left untouched. Preparation uses a dry scalar pass before publishing; batch
reads use a dry delta pass. Work is bounded by actual table/axis/map/region and
glyph counts, not arbitrary allocations or retries.

## Validation status

Implementation and raw controls are authored for the final integrated validation
phase. No compilation, tests, probes, native/GPU execution or CI dispatch have
been performed for this branch. This is not DirectWrite vertical parity.

`progpu_native_vvar_tests` owns a minimal original SFNT metadata fixture, separate
from independently drawable vertical source fonts. It covers unrelated signed
metric rows, positive/negative/default instances, reordered/repeated glyphs,
explicit and implicit advance maps, shortened-map repetition, optional absence,
null subtables and explicit no-variation indices. Adversarial cases retain
sentinels across every table truncation, malformed maps/regions/references,
insufficient scratch, late invalid glyphs, nonfinite cached scalars, aliases,
foreign byte owners and distinct TTC face indices sharing one directory.
