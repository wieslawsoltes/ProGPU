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
Qualified PR259 merge `9994c19a73fb032bdb093702dfa212c1c35e271f` is now merged,
preserving its ordinary owned-bitmap storage and source-copy behavior.

Raw controls author late upload failure rollback, transform/opacity/input-scope
rejection, unchanged old API admission, unclipped replacement, restored nested
clips and materialized-layer rejection. Four shared source sequences cover
partial/full replacement at 96/192 DPI, two captured nested clips, a changed
drawing transform during the upload, padded caller storage, transparent SRC
replacement, caller mutation after the call, and drawing after each clip pop.
Both providers require exact independent binary-color pixels and cold/warm/
independent replay with 2/1/2 submissions. The same public-vtable sequence runs
on actual system Windows Direct2D/WIC, comparing original HRESULT/state/pixels
and the independent expected pixels against native Dawn replay. These are
authored gates, not observed results; no local native renderer build, GPU/VM
execution, original success or parity is claimed.

## Bounded post-commit checks

On implementation `97ac8ac81`, Apple Clang 21 strict C++20 syntax
(`-Wall -Wextra -Wpedantic -Werror -fsyntax-only`) passed for the scene builder,
image builder, Direct2D target, builder controls, compatibility controls and an
explicit instantiation of the shared four-variant pixel fixture. Processes were
bounded to 45 seconds each. The complete native contract verifier passed:
143 MIL commands/141 packet layouts, coverage ledger, 93 owned GPU fields,
all generated contracts and Unicode tables, and three inline-array generator
controls. The first syntax pass identified incorrect fixture flag names;
the corrected product preflight checks the actual transform/opacity values,
and source-owner scopes have a separate atomic rejection control.

No native library or renderer was linked or executed. The Windows-only and
provider-specific translation units require their hosted SDK/dependency lanes;
the original observation and GPU/cold-warm gates above remain pending. The full
diff is whitespace-clean; no C wire layout or generated binding changed.

Microsoft's [CopyFromMemory contract](https://learn.microsoft.com/en-us/windows/win32/api/d2d1/nf-d2d1-id2d1bitmap-copyfrommemory)
defines storage coordinates, matching formats and batch-flush failure behavior.
That documentation does not alone prove the active clipped-target sequence;
independent original Windows execution is a required hosted gate before claiming
its observable behavior is qualified.
