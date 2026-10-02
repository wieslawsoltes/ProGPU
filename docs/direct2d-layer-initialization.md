# Direct2D layer initialization

The portable optional `scene_layer_options_native::PushLayer1` preserves the
Windows `D2D1_LAYER_OPTIONS1` identity without changing ID2D1RenderTarget's vtable.
Legacy `layer_options::initialize_for_cleartype` is not an alias for background
initialization and still rejects while real RGB coverage is missing.

`INITIALIZE_FROM_BACKGROUND` uses an owned transient GPU texture copied from the
actual current parent before any child draw. Final composition keeps geometric
mask/opacity coverage separate from child alpha: it interpolates the untouched
parent and completed child RGBA. Ordinary SRC_OVER of a copied translucent parent
would composite that parent twice. The existing semantic BACKDROP/effect contract
is unchanged. New initialization excludes effects, caches, mapped-picture frames
and composite-state relocation until those separate ownership contracts connect.

The common Texture/AdvancedBlend shaders implement the coverage resolve and
destination-aware replacement for both native providers. Independent coverage
uses an owned R32Float render attachment and textureLoad: the previous UNORM color
scratch would quantize half opacity to 128/255 before interpolation and change an
opposite opaque channel from 128 to 127. The original color/ROP scratch remains
unchanged. The new attachment participates in the original byte budget and live
memory inventory and retires with the semantic pool. Its mask binding uses the
actual source-local resolve frame, independently of the final parent frame.
No CPU pixel evaluation,
readback or extra submission supplies layer contents. Pipeline caches belong to
the engine and retire with it; immutable replay spans retain initialization.

IGNORE_ALPHA owns an alpha-one intermediate, separately from background copy.
Ordinary source-over draws retain that alpha; nested fixed/advanced replacements
write RGB only into an opaque parent. Background capture uses an alpha-only GPU
write after copying, retaining exact copied RGB. Transparent initialization uses
an opaque-black attachment clear. Reused transient slots take the current replay
scope's policy, never the last compiled scope's policy.

Legacy and OPTIONS1 scopes retain their captured clear extent and alpha policy. Clear
appends a real SRC replacement inside that owned scope, ignoring later transforms,
preserving outside history and applying the layer's mask/opacity only at pop.
An ordinary opacity-one layer can otherwise be elided into its parent. The first
nonempty Clear promotes only that innermost layer to existing FORCE_ISOLATION
storage, before recording replacement. Existing materialized layers and ordinary
draw-only layers retain their policies. Promotion preflights the maximum depth
of already closed child layers, not merely the current open stack; a rejected
promotion leaves retained command bytes unchanged. Save/clip frames cannot
redirect replacement into an outer layer. The first promotion costs O(C) over
the layer's recorded commands with O(S) bounded stack scratch; repeated clears
reuse the materialized identity without another command scan.

Antialiased axis-clip stacks now use the separately documented
[target-storage Clear operation](native-target-storage-clear.md). Preparation
promotes all AA groups inward of the nearest ordinary source layer to background
initialization, while that ordinary owner receives only demand isolation.
A target-independent command recorder also rejects Clear in
an unbounded layer unless real target metrics are supplied; it never invents an
allocation extent. Source setup must keep this boundary explicit.

Authored controls cover 48 opaque/translucent parent, full/half opacity and
original aliased geometric-mask combinations, every pixel cold/warm on both
providers, actual Microsoft device-context pixels, original command-list
translation, and atomic invalid-option/flag rejection. Legacy ClearType rejection
is retained. These controls are **not executed yet**: validation is deferred to
the final integrated stack tip.

Additional authored controls exercise both real legacy ID2D1Layer and OPTIONS1_NONE
over 32 independent mask/opacity/null/partial-clip combinations, paired native
provider cold/warm full bytes, exact draw/command/submission counts and original
Windows pixels/command callbacks. Later singular transforms and source tags
remain unchanged. Builder controls retain rejection atomicity and historical
depth accounting. These controls are also unexecuted until the final stack tip.

Remaining implementation: target-independent unbounded Clear requires its explicit
target-metrics contract; real ClearType RGB glyph
coverage and corresponding original Windows controls. None is silently admitted
by the background flag or inferred from scalar glyph outlines.

## Transparent-layer recording provenance

Demand isolation is original ProGPU builder logic over the existing
`FORCE_ISOLATION` contract at `5a434bd3d`; no foreign implementation was imported.
[Direct2D layers](https://learn.microsoft.com/en-us/windows/win32/direct2d/direct2d-layers-overview),
[Win2D active layers](https://microsoft.github.io/Win2D/WinUI3/html/M_Microsoft_Graphics_Canvas_CanvasDrawingSession_CreateLayer.htm)
and [Skia saveLayer](https://api.skia.org/classSkCanvas.html) keep grouped content
separate until composition. We retain that semantic boundary without eagerly
allocating a target for an ordinary elidable layer. The existing
[cross-engine recording decisions](direct2d-command-stream-antialiasing.md#primary-research-and-design-decisions)
remain applicable: WebRender/Vello-style retained descriptions leave visibility,
worker preparation, batching, demand upload and GPU cache/device-loss ownership
inside the renderer. Parley/HarfBuzz layout, fallback, variation, glyph caches,
DPI and hinting are unaffected; recording a Clear must not initialize text or
pipelines. No new cache family or per-pixel CPU path is introduced. Empty Clear
adds neither isolation nor draw. This is a correctness change, not a measured
startup, residency or throughput claim; final exact-head execution remains due.

## Original glyph source ownership

Portable glyph draws now snapshot the complete original index/advance/offset
arrays and scalar run identity before external font or rendering-parameter
callbacks. Optional arrays stay absent; the recorder never shapes the run again
or invents advances. Original face, brush and rendering-parameter COM identities
remain retained through capture. The shared value snapshot keeps caller gamma,
contrast, ClearType level, pixel geometry and rendering mode separate from an
absent parameter object. It does not manufacture OS or monitor defaults.

The portable target itself now holds an exact in-flight capture lease across
those external callbacks. Drawing, Clear, scope changes, Begin/End, transform,
DPI, tag/state restoration and compatible storage replacement cannot invalidate
that generation while its source run is being captured. Illegal target mutation
fails before changing state; callback errors retain original first-error order.
Replacing text parameters/AA remains allowed because this draw already owns
their captured values. The final geometry writer consumes the exact capture
identity under the same target lock, without a callback-time lock or a gap before
publication. A stale cleanup cannot clear a newer capture lease. Retaining the
actual target also makes caller-reference release during a callback safe.
Twenty-four authored parameter/font callback mutation controls preserve prior
draw/clear/generation/transform/DPI/tag state, followed by a fresh successful
generation and an unchanged original font error. Two further controls release
the caller's last target reference from each callback boundary. These controls,
like the earlier source and GPU controls, are not yet executed.

The Windows command-list reader now translates genuine `DrawGlyphRun` callbacks
when the caller explicitly selected `DWRITE_RENDERING_MODE_OUTLINE`. That original
mode bypasses the font rasterizer; one whole-run outline enters the existing
shared GPU path writer with its original baseline, bidi direction, offsets and
independent text antialiasing state. Non-outline/default command-list text still
fails explicitly until its own producer is connected. The portable legacy outline
route is unchanged in scope; its existing acceptance is not new ClearType proof.
Both native providers consume the same resulting semantic path resource. No
managed render/text ABI or ordinary managed glyph policy changes in this source
adapter checkpoint.

Authored source controls mutate the caller run and replace source parameter/AA
state from a real parameter callback, then compare the complete retained scene
to the original capture. They also retain invalid-value atomicity, absent arrays,
face-call counts and exact parameter destruction. Four actual Windows command-list
controls cover both directions and aliased/grayscale outline state, leaving the
original unsupported hinted-parameter control intact. They are unexecuted under
the final-tip validation policy, and do not establish hinted or RGB pixel parity.

This follows the separation between original positioned text and rendering in
[DirectWrite](https://learn.microsoft.com/en-us/windows/win32/api/dwrite/ne-dwrite-dwrite_rendering_mode)
and [HarfBuzz](https://harfbuzz.github.io/shaping-concepts.html): already-shaped
source glyphs must not enter a second shaper. Canvas-style state remains distinct
from immutable draw inputs ([Skia](https://skia.org/docs/user/api/skcanvas_overview/));
GPU rasterization remains shared rather than moving to a platform CPU bitmap path
([Vello architecture](https://github.com/linebender/vello/blob/main/README.md)).
No external implementation source, coefficient tables or shader helper code is
incorporated. Exact modern DirectWrite hint/filter and alpha-correction behavior
remain separate from the published historical subpixel-filter model and must not
be inferred from a shifted, already-quantized scalar atlas.

The private [owned RGB coverage checkpoint](native-rgb-glyph-coverage.md) now
retains independent channel coverage through shared GPU raster and opaque-target
composition. Its explicitly selected linear box model does not change the
ordinary source admission above or supply the missing general parameter policy.

Original contracts: [OPTIONS1](https://learn.microsoft.com/en-us/windows/win32/api/d2d1_1/ne-d2d1_1-d2d1_layer_options1),
[legacy OPTIONS](https://learn.microsoft.com/en-us/windows/win32/api/d2d1/ne-d2d1-d2d1_layer_options),
[layers overview](https://learn.microsoft.com/en-us/windows/win32/direct2d/direct2d-layers-overview).
