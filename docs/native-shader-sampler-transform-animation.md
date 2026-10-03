# Retained ImageBrush sampler transform animation

The native MIL compiler's former `validate_static_sampler_transform` blocked all
animation handles even though its owned transform resolver and retained revision
walker already understood those resources. This change connects that existing
contract to ShaderEffect ImageBrush input capture, without changing ordinary
transform arithmetic, the source capture lattice, shader bytecode, or filtering.

## Admission and ownership

Both brush transform roots are inspected together. MatrixTransform uses a typed
MatrixResource; ScaleTransform and TranslateTransform use typed DoubleResources;
TransformGroup preserves its exact ordered children, including repeated child
identities. Declaration validates resource kinds without demanding initialized
current values. Capture resolves every animated root through the existing owned
resolver and requires finite representable complete matrices before publishing
even an empty sampler picture. It never falls back to source base fields.

The original sampler 64-level limit and per-group child-count validation remain.
An active-path identity check rejects cycles while allowing repeated siblings.
There is no new aggregate node cap or claim of unique-node traversal complexity:
the existing resolver and this preflight visit repeated subgraphs in their actual
ordered occurrences. No generic transform resolver or allocation policy changes.

The earlier retained revision walker already includes each transform resource,
ordered child, animation handle and animation generation. Current-value-only
updates therefore change the sampler revision without mutating the bitmap,
brush, effect or parent transform. Captures own their scene bytes after animation
detachment/deletion and channel retirement; existing engine/span/submission leases
remain authoritative. An update containing a later invalid packet is still one
atomic channel transaction.

An animated combined mapping containing any named RotateTransform or
SkewTransform is rejected, even if the named child is static or sits in the other
brush transform slot. Explicit matrices do not infer named primitive history.
Wholly-static named graphs retain the preexisting host-trig path unchanged;
that path is legacy **unqualified** arithmetic and remains a separate missing
original numeric contract. This animation connection neither fixes nor labels
that gap as complete. The source-local effect-frame and external-image gates
also remain unchanged.

## Authored paired controls

The new shared fixture supplies twelve independent nearest two-texel physical
column oracles for each absolute/relative mapping: animated matrix identity,
translation and reflection; positive/negative translation; centered half-scale
and reflection; noncommuting ordered groups; a current-value-only child update;
reordered and repeated child identities; and detachment restoring static base.
Expected columns are literals, not extracted scene matrices or another product
renderer. Both native providers reuse their real callback/readback harness and
lazy per-family engines. Every complete frame is compared over cold, warm and
independent-engine replays; cold remains two submissions and warm one, with the
original one-effect cache/pass counters. No existing sampler input, pixel,
counter, timeout or provider policy changes.

Raw MIL controls retain all captures through source deletion, inspect revision
changes without owner-generation bumps, compare atomic rollback after invalid
dependencies/cycles/current values, reject missing initialization even with empty
viewports, and distinguish new named-angle rejection from old static acceptance.
The previous initialized-matrix rejection in the property-animation fixture is
intentionally replaced by positive capture/scene validation for this newly
implemented family; all unrelated negative controls remain.

Managed WPF brush replay already exports current transform values through its
existing typed transform adapter; this native-only preflight barrier did not
apply there. No managed fallback, capability advertisement or qualified producer
pin changes. Original Microsoft WPF source animation controls are an additive
companion in this same change; their SoftwareOnly/ARM-negative status is separate
from actual native provider qualification.

## Implementation provenance and status

All product behavior is reused from ProGPU-owned
`src/ProGPU.Native/src/Mil/progpu_native_mil.cpp` at
`634a037af807ce9813b2d982e00256c3722adbfc`: `resolve_leaf_transform`,
`resolve_transform_core`, `append_single_tile_brush` and
`append_cache_resource_revision`. No foreign code, trig coefficients or new
external dependency is introduced.

Authored only. No build, syntax check, test, verifier, native renderer, GPU, UI,
VM, probe or CI dispatch was performed. The MIL coverage ledger is refreshed
mechanically because the source digest changes; that is not a validation result.
Original source and complete provider/package gates await the final integrated
tip and remain required before qualification.
