# Explicitly empty BitmapCacheBrush sampler sources

An initialized nonnull BitmapCacheBrush target may retain the existing explicit
`SetVisualSourceEmptyBounds` witness. Shader sampler capture accepts that witness
only after resolving the selected cache/current brush policy and traversing the
complete cache-root-aware ownership closure. The actual target handle, cache and
descendant topology remain owned; no null rewrite or invented positive rectangle
is used.

The private child scene authorizes only the exact validated sampler brush to omit
cache-page allocation. It still produces the normal owned transparent picture.
The authorization is not copied into other capture contexts, and another brush
cannot use it. Ordinary BitmapCacheBrush painting, nested generic cache consumers
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

The original nine sampler states remain unchanged. Three appended states retain
an empty inner group, detach that empty group, then reattach the same group and
ordered leaves with the prior negative-origin content. Both providers and the
original WPF companion author twelve states/36 captures each. Raw controls also
cover ordinary-cache rejection, other-brush isolation, caller-tail atomicity,
unknown metadata, invalid hidden dependencies, selected-cache failure and refill.

No builds, tests, syntax/verifier checks, original observations, UI/GPU/VM runs or
CI were executed. This is an implementation and authored-control checkpoint, not
qualified source or pixel parity. Downstream pins remain unchanged.
