# Packaged known-empty DrawingImage ownership controls

`DrawingImageEmptySourceValidation` calls the public managed channel API through
both actual packaged native libraries (`WgpuNative` and `Dawn`). It is included
in the existing ABI preamble alongside the visual-bounds and empty-cache-source
controls. The core and focused MIL consumers therefore retain this coverage in
their existing JIT/NativeAOT processes and RIDs, including the DrawingImage
selector. No selector, deadline, provider policy or staging requirement changes.

The authored cases distinguish declared from initialized typed owners, missing
and wrong-type handles, canonical null drawing from a retained real empty
DrawingGroup, and ordinary positive drawing publication. A witness is an owned
source assertion, not a generated positive rectangle. Zero is not a witness
clear operation. Only the canonical DrawingImage update clears the edge; positive
bounds cannot overwrite an active empty witness.

A real native cache-sampler scene contains the DrawingImage in the cached
visual's render-data. Its independently authored white rectangle supplies the
nonempty cache bounds. The known-empty drawing stays childless and has no
fabricated content extent. Cases retain its deletion edge, update its generation
without paint, update the selected cache, reject a late failing transaction and
an uninitialized candidate, preserve complete compiled bytes on rejection, reject
cycles and unsupported nonpainting descendants, and transition empty → positive
→ empty → canonical null. Previously returned managed scene byte arrays remain
owned after later failures and channel disposal.

The cache raster policy is an explicit CPU fixture input, not an assertion about
a real device's limits. These cases create no GPU, assert no pixels and do not
claim original Microsoft behavior for this private ownership API. Native raw,
provider pixel, actual-source reference and downstream application controls
remain separate required gates.

Status: source authored only. No build, syntax check, test, verifier, probe,
CI dispatch, native/GPU execution or VM action was performed for this change.
An exact complete successful producer Build and final source/package execution
remain required before runtime qualification or dependency staging.
