# Explicitly empty BitmapCacheBrush sampler sources

An initialized nonnull BitmapCacheBrush target may retain the existing explicit
`SetVisualSourceEmptyBounds` witness. Shader sampler capture accepts that witness
only after resolving explicit raster policy and selected cache/current scale, and traversing the
complete cache-root-aware ownership closure. The actual target handle, cache and
descendant topology remain owned; no null rewrite or invented positive rectangle
is used.

Current-value checks here are the actual selected cache scale and explicit
source raster policy. Shader cache input ignores brush paint opacity/transforms;
ordinary and nested consumers keep their existing rules. Static composition and nested
semantic replay remain lazy; ownership preflight is not a claim that every static
or descendant numeric value was eagerly evaluated for a no-ink capture.

The dedicated raw sampler path authorizes only the exact selected brush to omit
cache-page allocation and produces an owned transparent 1x1 picture. It does not
mark generic capture contexts, so another brush cannot borrow that admission.
Ordinary BitmapCacheBrush painting, nested generic cache consumers
and cache allocation continue to require their existing positive bounds. The
six excluded root properties retain their original policy; hidden invalid
descendants, cycles and failed selected cache/animation values are not hidden by
the empty witness.

Absent bounds, an uninitialized/deleted target, a genuine null target, explicit
source emptiness and a zero raster scale remain distinct. The empty-bounds setter
clears prior positive coordinates and increments source generation; a subsequent
positive-bounds update clears the empty marker. Existing candidate-channel
transactions retain both fields atomically. Shader dependency revision follows
these generations, so an attached empty source can reappear without replacing
its brush, cache, source or children.

The original nine source mutations remain; their former unexecuted receiving-
frame oracle is corrected to raw cache sampling. Three further states retain
an empty inner group, detach that empty group, then reattach the same group and
ordered leaves with the prior negative-origin content. Both providers and the
original WPF companion additionally retain three scroll-clip states, giving
fifteen states/45 captures each. Raw controls also
cover ordinary-cache rejection, other-brush isolation, caller-tail atomicity,
unknown metadata, invalid hidden dependencies, selected-cache failure and refill.

No builds, tests, syntax/verifier checks, original observations, UI/GPU/VM runs or
CI were executed. This is an implementation and authored-control checkpoint, not
qualified source or pixel parity. Downstream pins remain unchanged.
