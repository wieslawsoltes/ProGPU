# Antialiased source Clear controls

These are authored controls, not execution or parity receipts. No test, build,
verifier, local GPU/VM run, workflow dispatch or runtime staging was performed
for this implementation checkpoint. Final integrated-tip validation remains
required on both native providers and actual original Windows implementations.

The shared fixture calls the actual Direct2D source APIs. Twenty descriptors
produce 39 source configurations: real legacy layers and OPTIONS1 layers, with
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
opacity has separate cases. Source DPI axes include (1,1), (2,2), (1,2), (2,1).
Suffix content inside and outside the scopes proves subsequent drawing/order.

The independent scalar pixel oracle intersects each rectangle with the physical
unit pixel, multiplies the two overlap lengths, and composes the explicitly owned
RGBA8 intermediates. It does not consume scene mask payloads, copy signed-distance
shader arithmetic, or substitute outward allocation for binary center coverage.
Full RGBA bytes, including transparent alpha and untouched pixels, are exact;
there is no tolerance or baseline-image waiver. Both providers compare cold and
warm whole images and require one main submission, exact source draw/wire counts,
and the replay draw count derived from ordinary fills, Clear operations and
ordinary versus initialized-AA composites. Every executed Clear must also upload
its 16 original color bytes on every replay; aggregate uniform totals include
the separately owned layer/mask uniforms and are not claimed as a Clear-only
allocation measurement.

The previously rejected bounded AA arrangements remain in the source tests as
positive explicit CLEAR_TARGET cases. The unknown ordinary owner with no target
metrics and no inner AA still rejects atomically; a bounded inner AA attachment
has a distinct positive case. Invalid-color first-error/index and untouched
output checks remain negative. All older aliased Clear and transparent-layer
fixtures are unchanged.

Fractional corner checks intentionally exposed the need for source-qualified
axis-clip area coverage, rather than the ordinary rounded-mask SDF. The paired
axis-clip product is a dependency, not justification to change these mathematical
expectations or claim Windows/native agreement before final execution.
