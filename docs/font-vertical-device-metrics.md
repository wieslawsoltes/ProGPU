# Exact font-provided vertical device metrics

The original Forms grid run `39025614225` uses Segoe UI 9pt at 96 DPI. Microsoft
reports font autoscale height15, while portable Forms reports16 and expands the
515-pixel client to549. Both retain Font.Height16. The APIs have different
contracts: native ContainerControl uses the selected HFONT's TEXTMETRIC; ordinary
Drawing height uses scaled line spacing.

Independent Windows ARM64 GDI diagnostic `39026793093` records45 combinations of
Segoe UI, Arial and Consolas; five point sizes; and96/144/192 reference DPI.
The exact original VDMX entries match all30 Segoe UI/Arial heights, ascents and
descents. Consolas has no table in these observations. Archive12099115921 has
SHA256 `5731e54a4930d3c84d9d88df7180a2a4b6d88b4a2afca590cb744335d3a232b2`.
This is diagnostic metric evidence, not package or desktop qualification.

`SfntVerticalDeviceMetrics.TryRead` validates the full original table inventory
before publishing an exact size and aspect-ratio record. It keeps signed original
extrema and first applicable ratio selection. Missing sizes or ratios are distinct
from malformed data; no nearest size, interpolation, scaling or post-sum rounding
is substituted. Group offsets must identify actual group starts, record heights
must be ordered, and invalid unselected groups also reject. The bounded count and
offset metadata require ordered scalar traversal; this is a table query rather
than a glyph or pixel kernel. Temporary group offsets use the existing shared
ArrayPool and do not escape the call.

`TtfFont.TryGetVerticalDeviceMetrics` borrows the selected immutable face's original
bytes. It checks raw VDMX directory records so an ignored out-of-range record or
duplicate cannot masquerade as absence. Static TrueType, square-pixel, nonsymbol
faces are the initial contract. Any raw fvar record keeps variable fonts outside
this admission. Ordinary font construction, line metrics, glyph layout and
rendering remain unchanged. The returned metric has no font/resource ownership.

The parser follows Microsoft's [OpenType VDMX specification](https://learn.microsoft.com/en-us/typography/opentype/spec/vdmx).
The later original Microsoft Sans Serif capture establishes a legacy version-zero
layout with separate terminal defaults for ANSI and non-ANSI inventories. Those
inventories are validated independently for default ordering; version1 keeps a
single final default. Full group/offset validation and first applicable selection
remain unchanged. See [source device line metrics](source-device-line-metrics.md)
for the exact native evidence and explicit paragraph policy.
Version0 selects its ANSI subset; version1 accepts its all-glyph records for the
explicit ANSI source contract. Other source charset policies and absent-table
GDI metrics remain separate implementation work. Source consumers must explicitly
select this metric policy and preserve their existing unsupported-family behavior.

Seventeen authored controls cover exact/non-nearest queries, aspect ratios,
charset/version/default selection, every truncated prefix, unselected corruption,
raw duplicate/out-of-range records and variable rejection. The actual managed
reader also matches the30 original Windows observations without altered inputs.
The local full suite passes6230 with10 platform skips, and all230 headless tests
pass. Whole producer CI, Svg.Skia parity, rebuilt consumers and the unchanged
original grid input/capture gate remain required before release.
