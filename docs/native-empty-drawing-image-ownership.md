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

The native raw controls exercise every initialized drawing-family setter root,
declared/missing/wrong-type roots, positive-bounds conflict, referenced deletion,
exact resource generations and complete scene bytes after a failed later batch
command. The retained raw-cache graph includes an ordinary ImageBrush whose
empty DrawingImage owns a DrawingGroup and a GeometryDrawing with its own
BitmapCacheBrush source. Mixed image/drawing/cache/visual cycles and hidden
unsupported descendants still reject when the outer cache scale is zero.
These are ownership checks, not a new direct DrawingImage sampler admission.

The shared native provider corpus preserves states 0–14 and adds states 15–19
with the same actual handles throughout: nested empty image/cache, nested cache
refill while the image stays empty, image refill, image clear, and image refill
again. Its 20 configurations each retain cold, warm and independent-engine
replays (60 complete-frame assertions per provider). Blue/green pixels remain
unchanged while the image is empty; the original red nested cache covers the
left half only in the two positive-image states. The original Windows companion
uses actual retained WPF objects and independent full-frame literals for those
same states. Original pixels do not prove native ownership/deletion behavior.

All controls are authored only. No build, test, syntax check, verifier, GPU/UI/VM
execution or CI qualification is claimed. Existing selectors, deadlines and
the original 15-case assertions are unchanged.
