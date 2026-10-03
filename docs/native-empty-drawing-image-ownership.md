# Known-empty DrawingImage source ownership

`SetDrawingImageEmptySource(imageHandle, drawingHandle)` is an additive typed
source assertion: the actual retained Drawing has source-known empty bounds,
while its initialized DrawingImage still paints canonical drawing handle zero.
It does not invent positive bounds, draw the retained graph, allocate an empty
cache, or replace the real source with a null object.

Both owners must already be initialized in the same channel. Zero, absent,
deleted, wrong-type and declared-only handles return `InvalidHandle`; an image
whose canonical drawing is nonzero returns `InvalidArgument`. An initialized
DrawingGroup does not require positive bounds to be retained. This setter is
the explicit empty-bounds witness, not a request to infer emptiness by measuring
or replaying its children. Rejection leaves generations and state unchanged.

The dependency walker follows the real source drawing, including its nested
images, brushes and cache-source witnesses. Existing selected-capture policies,
mixed-resource cycle/depth checks and resource generations remain authoritative,
including nonpainting zero-scale or empty captures. Referenced deletion remains
invalid. This retains ownership, not eager semantic validation of every nested
current value or admission of a new sampler family. Generic ordinary empty-cache
painting and the canonical image paint path are unchanged.

Publishing the witness invalidates any previous positive image bounds. Every
canonical DrawingImage update clears the witness. Ordinary canonical updates
continue preserving independently supplied positive bounds exactly as before;
an empty witness has no positive bounds to carry forward. Binding positive
bounds while the witness remains returns `InvalidArgument`: a refilled source
must first republish its actual canonical drawing. A genuinely null Drawing is
witnessless. Candidate channel copies own these edges, so failed batches cannot
silently lose a previous edge or partially publish a replacement.

The implementation reuses original ProGPU's `drawing_image_state`, immutable
channel candidate and `append_cache_resource_revision` machinery, paired with
the prior `SetBitmapCacheBrushEmptySource` ownership contract. The C and C++ APIs,
both managed native-provider imports and both export inventories are additive;
canonical Microsoft MIL packets and generated wire layouts are unchanged. The
source compiler must retain the complete actual drawing graph, include this
sideband in captured session topology, and publish it after graph construction
and any nested source witnesses. Managed scene replay retains its own actual
source graph and does not consume this native channel sideband.

Controls are authored for direct owners, nested dependencies, revisions,
clear/refill, candidate rollback and failure atomicity. No build, test, syntax
check, verifier, GPU/UI/VM execution or CI qualification is claimed.
