# Antialiased source Clear controls

The current scalar oracle matches all 102 original Microsoft Direct2D captures
on each of Windows ARM64 and x64. The local stock Metal output also matches each
complete original frame byte for byte, including cold/warm captures. The complete
standard native run passes this bounded family, then reaches a separate cached
gradient-mask failure. Full final producer/provider/package qualification remains
required; these receipts do not establish application parity.

The shared fixture calls the actual Direct2D source APIs. Twenty-six descriptors
produce 51 source configurations: real legacy layers and OPTIONS1 layers, with
the one IGNORE_ALPHA case restricted to its actual OPTIONS1 API. Every original
Windows configuration is drawn twice into an actual premultiplied BGRA8 target,
read back completely, and separately recorded into a real command list and
translated through the native sink. Actual callback inventory owns translated
draw counts; source-call counts do not replace commands discarded by Direct2D.

The cases preserve capture-time translation (4,6), quarter/half-pixel clip edges,
late collapsed transform and source tags, opaque/null/half-alpha Clear, repeated
Clear, a disjoint empty binary clip, AA inside and outside an aliased clip,
nested AA, and AA inside/outside ordinary transparent and opaque owners. Prefix
content is drawn before the inner scope in both ordinary/AA orderings, exposing
an incorrectly promoted outer AA scope or lost owner history. Non-unit ordinary
opacity has separate cases. Source DPI axes include (1,1), (2,2), (1,2), (2,1),
(1.25,1.25), (1.5,1.5), (1.25,1.5), and (1.5,1.25). The six added fractional-DPI
descriptors preserve the original twenty, and actual Windows context and bitmap
DPI both use these source values (120/144 DPI); no image rescaling substitutes
for a native source DPI change.
Suffix content inside and outside the scopes proves subsequent drawing/order.

The independent scalar pixel oracle intersects each rectangle with the physical
unit pixel, multiplies the two overlap lengths, and composes the explicitly owned
RGBA8 intermediates. It does not consume scene mask payloads, copy signed-distance
shader arithmetic, or substitute outward allocation for binary center coverage.
Binary ancestors contribute independent pixel-center admission. Consecutive AA
clips apply the intersection area conditional on the outer clip's coverage,
preserving rounding at both stored layer boundaries. Ordinary group opacity is
quantized into premultiplied RGBA8 before source-over; an ordinary group separates
AA ancestry. These rules were checked against every original captured byte.
Full RGBA bytes, including transparent alpha and untouched pixels, are exact;
there is no tolerance or baseline-image waiver. Both providers compare cold and
warm whole images and require one main submission, exact source draw/wire counts,
and the replay draw count derived from ordinary fills, Clear operations and
ordinary versus initialized-AA composites. Every executed Clear must also upload
its 16 original color bytes on every replay; aggregate uniform totals include
the separately owned layer/mask uniforms and are not claimed as a Clear-only
allocation measurement.

Sixteen additional arithmetic-only controls assert literal rational pixel areas,
including the actual translated source's 125%/150% corner areas, mixed axes,
negative origin, edge contact, empty width, and integer target-origin removal.
They call only the independent scalar oracle and do not qualify a library or
GPU operation. Both original Windows and portable/provider entrypoints select
them; the local native controls pass without replacing GPU or original-source
qualification.

The previously rejected bounded AA arrangements remain in the source tests as
positive explicit CLEAR_TARGET cases. The unknown ordinary owner with no target
metrics and no inner AA still rejects atomically; a bounded inner AA attachment
has a distinct positive case. Invalid-color first-error/index and untouched
output checks remain negative. All older aliased Clear and transparent-layer
fixture inventories are retained. The separate layer-Clear and background-layer
oracles now use the byte quantization and source-over behavior verified by their
448 original Windows captures.

Fractional corner checks intentionally exposed the need for source-qualified
axis-clip area coverage, rather than the ordinary rounded-mask SDF. The paired
axis-clip product is a dependency. Original captures, complete byte comparisons
and scoped renderer execution remain separate evidence from full build success.
