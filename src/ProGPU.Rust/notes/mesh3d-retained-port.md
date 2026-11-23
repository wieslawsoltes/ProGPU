# Retained Mesh3D viewport integration

`Scene::mesh3d` records one immutable original Viewport3D producer and its
subsequent ordinary Texture consumer. `Mesh3DViewportDraw::from_display` uses the
actual selected display DPI, truncates each positive physical dimension after
clamping to one, and chooses four samples below scale 1.5 or one sample otherwise.
The offscreen projection/view and target size remain independent of a receiving
picture or nested capture. Empty source inventories emit neither command;
nonempty inventories with absent geometry still clear their actual attachment.

Source placement composes the complete receiving matrix. The original extension
also selects the shared final camera (`view * globalTransform`); a private ordered
camera command retains that effect without inventing a main-pass draw. Mesh record
opacity and the subsequent texture opacity each consume original active opacity.
Parent clips, masks and blend apply to the texture consumer; offscreen shaders
retain their independent material/alpha policy. The ordinary managed image path
now uses original transformed-quad bounds and exact axis-aligned QuadClipper UV
interpolation, preserving the independent scissor for non-axis-aligned quads.

Whole-scene/layer resource admission counts actual viewport attachments and
potential material Gaussian storage/passes. A frozen per-command resource plan
captures source metadata once inside the exclusive preparation transaction;
texture acquisition cannot exceed it. The map retains exact command owners,
clears on outer commit/cancellation/failure, and never substitutes a pointer to a
new owner. Opaque sources need metadata only when a possible original live blur
requires its size. No source texture is acquired during this preflight.

`PreparedScene` owns each prepared offscreen source and encodes it before its
consumer, including loaded targets and retained AA children. The output image
owns the complete GPU/source lease generation; normal submission retention also
transfers the source through actual queue completion. Preparation itself does
not submit these viewport passes. Explicit host frame numbers drive cache aging;
nested preparations do not restart the viewport ordinal. Per-viewport metrics
retain original generation/cache counters, with actual uploads and draws also
included in ordinary scene totals. Source camera, material and GPU receipts are
in [source](mesh3d-source-port.md) and [GPU](mesh3d-gpu-port.md) notes.

The shared native `--mesh3d` and browser `?scene=mesh3d` gallery authors six actual
source viewports: solid lighting, wireframe, solid-wireframe, gradient/occluded
edges, sampled texture and blurred texture. They include recursive front/back
models, four light kinds and independent final consumer clipping/masking.

Only implementation and source formatting were performed. Compilation, original
bytes/pixels, native/browser execution, source-provider/package controls and
sustained performance/lifetime qualification remain open. General late-camera
text/path, platform source adapters, static-DXF providers and encoder-wide blur
execution dedup retain independent boundaries; this integration does not qualify
them or change canonical shaders/compiler defaults.
