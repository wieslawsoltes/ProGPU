# Original source Display text contract

`IPortableDisplayTextFormatting` is an additive, optional source capability. It
does not follow from `IPortableHintedTextFormatting`, original hdmx metrics, or a
successful manually chosen ppem preparation. No existing provider is advertised
by this contract-only change. LibreWPF issue #184 remains open until the native
producer, source bridge and original application/reference gates qualify.

The request retains the complete original hard paragraph, physical font bytes,
UTF-16/style/feature/digit/bidi identity and requested wrapping/intrinsic-width
policy. `PortableDisplayTextOptions` and `PortableDisplayTextStyle` carry original
double em, DPI and source dimensions. Common request floats cannot replace them.
An implementation may reject an unsupported numeric frame explicitly; it must
not round the source identity or route it to Ideal formatting.

Even exact-float DPI 1.5 can produce a non-float-exact source advance: seven device
pixels divided by 1.5. Therefore Display has separate native-owned double glyph,
line, intrinsic-width, hit/caret and glyph-binding contracts. The source does not
perform this division, round advances, recompute nominal offsets, reshape prefixes
or prefix-sum line positions. Existing float hinted/Ideal transport stays unchanged.

Retain, original occurrence selection and width-changing continuation preserve
the original full source and double identity. `ReflowDisplay` reuses the original
shaped generation at a real boundary; it is not the legacy float Reflow method.
The existing owned hinted reference/render lease machinery remains authoritative
for disposal and retirement. Exact source-frame validation precedes publication,
and both metrics-copy output spans are atomic with untouched tails.

This contract does not enable a provider, automatic Display, empty rows, tabs,
objects, exclusions, collapse, arbitrary numeric frames or variable instances.
Each unsupported policy must fail explicitly. The first native family must own
rounding, fitting/recomposition, double metrics, drawing and interaction together
before it can implement this capability. Interface metadata tests alone do not
establish native numerical parity or AvalonDock rendering.
