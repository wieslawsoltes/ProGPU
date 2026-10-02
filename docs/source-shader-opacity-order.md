# Source shader input opacity ordering

`WpfShaderEffect.CaptureSourceVisualOpacity` is an immutable, explicit source
contract. Its default is false: existing generic shader and other-effect
ordering is unchanged. An original WPF source adapter sets the flag because
original visual ordering places opacity/mask inside the effect and geometry
clips outside. This is not a new shader registry or a rendering fallback.

The actual retained WPF adapter currently assigns source opacity, opacity mask
and effect to one Scene visual (`TryCreateSingleNativeRetainedVisualScopeState`
and `ProGpuRetainedCompositionCommandSink.ApplyVisualState`). It does not already
move that alpha state into a child. Merely changing native MIL therefore leaves
the paired managed source path incorrect.

The shared compositor now captures only the opted-in root's opacity/mask in the
existing offscreen input, using that capture's root translation and original
unpadded mask bounds. Descendants retain normal scope ordering. The final effect
scope retains all root rectangle/geometry/outer clips but does not multiply root
opacity again. A zero-opacity root can still execute a shader that produces
constant output; zero-opacity ancestors retain their normal exclusion. Cached
root content retains the existing layer texture/raster policy and receives the
same input-space placement and alpha scope. No evaluated effect is resampled to
implement this ordering.

The immutable flag participates in effect cache identity. Original visual
invalidation continues to own alpha, mask and mask-bounds changes. The same
effect object/parameters are retained by existing draw snapshots; no new clone
or independently mutable ordering descriptor is introduced. Offscreen snapshot
restoration, retained resource leases and failure cleanup remain shared.

Native pairing is PR290's typed gradient mask inside the retained source input.
Both native providers share that implementation. The managed source uses its
existing typed mask realization, including existing picture-mask ownership;
this does not claim new numeric equivalence for previously unqualified frames
or broaden native sampled-mask admission. The WPF adapter opt-in is a separate
source child, with the qualified producer pin unchanged until final integration.

Authored controls run the same managed compositor family on native and Dawn
contexts: explicit/default ordering, zero root opacity, zero mask alpha,
constant output with padding and output clipping, ordinary/cached input,
gradient-input comparison against ordinary drawing, same-owner mask replacement
and restoration, warm and independent-compositor replay, and zero-opacity
ancestor exclusion. Cache metadata controls require default false, retained
parameter identity, equal same-policy keys and distinct source/default keys.
These are unexecuted controls, not results.

Implementation and controls carry `[skip ci]`. No tests, builds, verifiers or
GPU/VM execution are run at this intermediate checkpoint; complete source,
provider, package and application qualification belongs to the final tip.
