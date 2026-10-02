# Storage copies with active aliased clips

Compatible-target bitmap `CopyFromMemory` can suspend active aliased,
axis-aligned clip scopes while performing its existing physical-pixel SRC
replacement. The current drawing transform does not place the storage copy.
Subsequent drawing still consumes the exact capture-time clip rectangles and
the caller's unchanged transform. This is not general layer flushing.

The additive C++ builder operation validates every open SAVE frame as clip-only,
then appends balanced restores, the existing upload/SRC/image/pop transaction,
and saves of the original owned state resources. No old command is reserialized
or mutated. A failure rolls back appended commands/resources and bounded stack
metadata. Input/owner scopes, transforms, opacity, masks, guidelines and all
materialized layers remain rejected. Existing root-only copy APIs are unchanged.

For full overwrite, the target records independent replacement history, then
recreates its captured clip states before publishing the new builder and resource
generation. A failed replacement retains original history, exports, source
leases and clip state. Partial writes continue to retain old content. Invalid
or failed target state and unsupported mixed-DPI histories keep their original
failure behavior. Ordinary owned bitmap storage is not reimplemented here.

The work starts from qualified shader foundation `627148d3de0b79aa83bbd3eed6b48f1e0cf098a2`.
The later owned bitmap work from PR259 must be preserved when integrating this
independent compatible-target change.

Raw controls author late upload failure rollback, transform/opacity/input-scope
rejection, unchanged old API admission, unclipped replacement, restored nested
clips and materialized-layer rejection. Paired provider pixels and the actual
original Windows/WIC active-copy sequence are still being authored; no local
native renderer build, GPU/VM execution, original success or parity is claimed.

Microsoft's [CopyFromMemory contract](https://learn.microsoft.com/en-us/windows/win32/api/d2d1/nf-d2d1-id2d1bitmap-copyfrommemory)
defines storage coordinates, matching formats and batch-flush failure behavior.
That documentation does not alone prove the active clipped-target sequence;
independent original Windows execution is a required hosted gate before claiming
its observable behavior is qualified.
