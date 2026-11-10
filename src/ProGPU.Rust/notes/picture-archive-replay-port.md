# Original picture replay and capture

Source SHA-256:

- `src/SkiaSharp/SKPicture.Serialization.cs`: `c27d0e563e7bef1c874896d13bf2790d3972bee30fb6d1456992cd1e3e116e46`
- `src/ProGPU.Scene/Compositor.cs`: `a45a99f135806d248fc3792dcbdb5ebe17c094821c6aed080dcdedf4025237cd`
- `src/ProGPU.Scene/RenderCommand.cs`: `dff6b8f8c375dcb8b2a2bae357145416bd48d05a5c12ebdbbbe0710dd98858f8`

`ArchivePicture::to_scene_with(ArchiveReplayOptions)` compiles an atomic candidate
from original commands and side-buffer owners. Ordered opacity/blend, nested
pictures, original epsilon-axis clips, geometry masks and clipped picture opacity
masks connect to Scene. Source transforms compose before geometry/text planning;
zero command matrices retain the original absent-transform sentinel. Default
`to_scene()` uses unit DPI. Actual receiving text DPI and ClearType policy remain
explicit. DrawText requires an `ArchiveTextEnvironment` with the already-ported
witnessed Unicode preparation profiles and actual ordered fallback font owners;
DrawGlyphRun keeps complete original font/index/position arrays without shaping.

Rect/rounded-rect/ellipse/circle fill and independent analytic pen quads retain
original corner arithmetic, local AA padding and hairline/fixed/legacy width
recovery. Unequal rounded radii and dashed/WPF primitive strokes use original
retained paths. Line/quadratic/cubic/polyline source paths use the original stroke
writer. General path fill uses the original 64-phase PathAtlas metadata, actual
sample-grid/gamma and semantic DPI. Triangle/quad/points/device grids keep their
separate canonical shader families. Indexed vertices retain original color blend.
The original `ClampToClip` helper returns its input unchanged; no vertex clamp
was inferred from callers.

`Canvas::draw_archive_picture[_with]` retains the complete raw generation as one
ordered command. `PictureArchive::to_canvas_picture` performs no execution and
keeps arbitrary original cull metadata separate from target allocation admission.
Canvas CPU/GPU preparation lowers before encoding. `CanvasPicture::to_archive`,
`Scene::to_archive` and `RecordedPicture::{to_archive,serialize,deserialize}` only
capture still-owned original source representations. The raw Archive command
preserves complete side buffers and inactive metadata. Compiled paths, textures,
resolved text, opaque source/filter owners and lossy source-history cases reject.
Decoded empty cull bounds remain distinct from the recorder's canonical empty.

Remaining execution boundaries (transport still retains all 39 kinds): late GPU
camera transforms; source 3D/ACIS/hatch/spline/chart/extension/native hinted
producers; unresolved theme/backdrop materials; text-derived path gamma adaptation;
original final-target direct rounded-path triangulation/specialization; crossed
clip/mask stack pops that cannot be expressed by the current Scene LIFO scopes.
Brush/stroke opacity-mask sources remain rejected until their declared outward
storage bounds are retained at actual target preparation; full-root capture is
not equivalent. Null decoded texture/visual/static records keep source no-op
behavior. No GPU resources are invented for absent serialized producers.

No compiler, tests, browser, benchmarks or execution qualification were run.
Only touched source formatting was performed; original qualification gates stay.
