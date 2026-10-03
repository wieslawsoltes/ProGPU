# Empty cache-source ownership behind an ordinary zero paint target

An ordinary BitmapCacheBrush inside a shader cache source can have a real,
initialized, explicitly empty Visual. Its source adapter historically lowers
ordinary paint to target zero. Creating otherwise orphaned native Visual nodes
or validating only in managed source code does not preserve native dependency
ownership.

`SetBitmapCacheBrushEmptySource(brushHandle, visualHandle)` is an additive typed
ownership witness. It requires an initialized BitmapCacheBrush whose canonical
paint target is zero, and an initialized 2D Visual carrying the explicit empty
bounds witness. Missing/deleted/declared/wrong-type/positive or unknown bounds
reject before changing either owner. This is not an opaque handle, allocation
rectangle, hidden positive box or shader alias.

The source emits its actual complete Visual graph, publishes empty bounds, then
sets this edge on the corresponding ordinary brush. The cache-specific shader
ownership walk follows the actual source, its selected explicit/target/default
cache and descendants with the existing root exclusions, mixed-resource cycle
guard and depth budget. The edge participates in revision and deletion checks
even when the outer selected cache has zero scale. Ordinary nested paint still
sees canonical target zero and allocates no empty cache; direct ordinary painting
of a nonzero empty target retains its existing rejection.

Every canonical brush update clears the witness. In particular, empty-to-positive
source transitions must republish the real paint target; changing source bounds
alone cannot authorize a stale witness. Failed candidate batches preserve the
previous edge atomically. The witness remains a real owning relationship until
the brush is updated or removed, so referenced source deletion fails normally.
Current-value semantic admission beyond the existing selected policy remains
owned by ordinary replay; ownership is not eager validation of all nested values.

The C and C++ channel APIs, both managed native backends and both export lists
are paired. The source session must include these relationships in its captured
batch identity/replacement decisions and publish them after bounds. Package and
raw controls cover declaration, stale witness, cycles, hidden dependencies,
selected-cache initialization, revisions, deletion and rollback. They are
authored only. No builds, tests, verifiers, GPU/UI/VM runs or CI were executed.
