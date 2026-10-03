# Managed source ShaderEffect capture frames

The managed effect compositor now has an explicit source capture descriptor
carrying original double bounds and four independent double padding values.
The source adapter shares this immutable value with its shader sampler request;
it no longer needs to turn asymmetric padding into one maximum scalar. This is
an additive path. Ordinary `WpfShaderEffect.Padding`, scalar frame overloads and
the explicit `Visual.EffectRasterPadding` policy remain available unchanged.

## Coordinates and ownership

`ShaderEffectSourceCapture` owns eight original doubles, with bit-exact equality
and hashing. `IsValid` checks finite positive bounds, representable original float
endpoints and finite nonnegative float-domain padding. It is a metadata check,
not a guarantee of representable inflated extents, target DPI or device limits.

`EffectCaptureFrame.TryCreateSource` separately narrows the original source
origin and double-summed far endpoint. It then inflates each float edge by its
corresponding narrowed padding, without maximizing or ceiling the sides. It
computes the extent before applying the source host's actual source-to-local
translation. The same capture extent feeds the implicit texture and framed
ImageBrush/VisualBrush sampler. Raw BitmapCacheBrush samplers retain their own
independent cache frame; padding does not resize them.

Source scopes already rebase recorded commands. They must publish that actual
translation through `Visual.EffectSourceTranslation`; neither the compositor nor
the sampler invents it by negating a rounded output origin. Source integration
must install the descriptor and rebase together, verify the original bounds
identity before publication, and clear rebase metadata when the source effect is
removed. `SourceCapture = null` selects the original managed scalar path again.
The new value participates in effect invalidation and cache identity, so a
same-size capture with a changed origin cannot reuse stale pixels. Rebase changes
invalidate the visual. No source hit-test geometry is expanded by padding.

An explicit raster override retains the old symmetric normalization and
origin/size arithmetic over the source bounds and actual rebase. It does not
repair invalid source metadata. Source frame failures publish no partial frame
and are detected before effect texture allocation. Existing source-opacity and
final-clip ordering, real GPU resource ownership and retirement remain unchanged.

## Actual target mapping and complete input texture

The explicit per-axis overload of `EffectCaptureFrame.TryCreateSource` takes
`(source, sourceTranslation, pixelsPerUnit, dpiScale, out frame)`. Its XY value is
the actual target projection-to-viewport scale, not semantic DPI, the maximum of
two DPI values, or a decomposition of a visual's affine transform. The separately
supplied positive finite `dpiScale` remains the caller's actual text/snapping and
nested-effect DPI. The compositor retains it during offscreen rendering.

`TryResolveSourcePixelsPerUnit` constructs the host's original float orthographic
projection, normalizes its viewport against the physical target, and multiplies
the stored projection coefficients by the viewport's half extents. It does not
replace that order with `viewport/logicalSize`. Source hosts can pass their actual
pre-replay target snapshot through this shared resolver; the compositor resolves
from its current projection and actual root/offscreen target. Neither path reads
an earlier frame's compositor state as a substitute for an eager source request.

The vector overload multiplies each independently padded float endpoint by the
corresponding XY value, floors the near edges and ceilings the far edges. It
retains signed physical origins and complete texture dimensions as exact float
integers, bounded to the inclusive +/-2^24 coordinate domain and at most 2^24
pixels per axis. Larger footprints fail explicitly; no integer truncation hides
an unrepresentable origin. Invalid extents or nonfinite projection coefficients
fail before frame publication. Device allocation limits remain separately owned.

`RasterBounds` and `ProjectionExtent` describe the complete outward texture in
rebased logical coordinates. Rendering offsets by that raster origin and uses
the floating projection extent, not its integer bookkeeping ceiling. There is
no one-logical-unit minimum on this explicit route. `SourceToRaster` describes
the source-to-physical mapping as metadata; it does not replace semantic DPI
with 1 or transform a framed ImageBrush/VisualBrush's physical-identity content.

Final shader evaluation stays in the existing direct output draw. Its original
padded coverage selects `TextureUvBounds` within the complete input texture;
there is no extra post-effect intermediate or second final-sample shader. UV
endpoints survive clipping and retained-command translation. Texture-size and
derivative metadata continue to describe the complete input texture. Raw cache
sampler textures remain independently sized and mapped.

The complete resolved frame participates in texture reuse, including semantic
DPI, XY, physical origin, logical origin/extent and UVs. A changed frame is not
qualified for reuse until capture succeeds. Scalar frame overloads and explicit
raster-padding overrides retain their previous extent, minimum and mapping
semantics; clearing the descriptor also restores full-texture UVs.

The managed final affine quad still follows the existing compositor transform
behavior. This change does not infer a source affine capture basis or claim the
native/original fractional-affine equivalence contract is qualified. Actual
original comparisons and complete device/application gates remain separate.

## Basis, research and costs

Product changes derive from ProGPU-owned `EffectCaptureFrame`, `Visual` and
`Compositor.PrepareAndDrawEffect` at
`45bbcf69982052b4b478ee574a90d48d4412c813`. Original endpoint/padding order is
already documented in [native source padding](native-shader-capture-padding.md)
and [native local capture](native-shader-local-capture-frame.md). No foreign
implementation, coefficients or renderer code is copied or adapted.

The outward lattice and independent coverage/texture distinction additionally
reuse ProGPU's owned `progpu_native_shader_capture_frame.hpp` and
`progpu_native_shader_sample_frame.hpp`. They are not a port of a foreign engine.

Primary architecture references were examined for this change:

- [Skia canvas layers](https://api.skia.org/classSkCanvas.html) and
  [Direct2D layers](https://learn.microsoft.com/en-us/windows/win32/direct2d/direct2d-layers-overview)
  keep layer bounds, transformations and final clipping explicit. Retain those
  separations; do not derive source capture from an output clip or widen input
  geometry into the padded raster.
- [Win2D command lists](https://microsoft.github.io/Win2D/WinUI3/html/T_Microsoft_Graphics_Canvas_CanvasCommandList.htm)
  and [WebRender's picture/spatial/clip trees](https://firefox-source-docs.mozilla.org/gfx/RenderingOverview.html)
  inform retained command reuse with explicit coordinate and owner identity.
  Reuse the existing compositor cache and source snapshot, not a new intermediate
  renderer, CPU pixel copy or a globally rooted source cache.
- [Vello scenes](https://docs.rs/vello/latest/vello/struct.Scene.html) retain layer
  transform/clip/blend state in GPU-oriented command recording. Preserve current
  batching, pipelines and GPU-only capture; the new arithmetic stays fixed-size
  CPU metadata work, not a per-pixel or per-glyph loop.
- [SkParagraph](https://skia.googlesource.com/skia/+/refs/heads/main/modules/skparagraph/include/Paragraph.h),
  [DirectWrite layout](https://learn.microsoft.com/en-us/windows/win32/directwrite/text-formatting-and-layout),
  [Parley layout](https://docs.rs/parley/latest/parley/layout/struct.Layout.html)
  and [HarfBuzz plans](https://harfbuzz.github.io/shaping-plans-and-caching.html)
  separate reusable text preparation from rendering. No shaping, line layout,
  font discovery/fallback, variable-face selection or glyph resource identity
  changes are needed for capture padding.

Metadata work and storage are O(1) per source capture. No eager pipeline or GPU
allocation is introduced at startup or metadata construction. Existing visibility
culling, worker preparation, demand uploads, draw/compute batching, cache eviction,
device-loss handling and atlas-generation ownership stay in their current owners.
Capture uses the actual compositor DPI; subpixel, hinting and ClearType policy
remain unchanged. Texture dimensions can change with corrected padding, but no
latency, allocation, residency or memory improvement is claimed without measuring
the final binaries.

## Authored controls

Pure controls cover independent fractional edges, original double endpoint
narrowing, exact source rebasing, same-size origin changes, source bit identity,
cache invalidation, nullable reset, explicit legacy overrides and atomic invalid
metadata/extent/DPI results. Existing scalar frame controls remain unchanged.

The actual shared native and Dawn compositor harnesses each receive twelve
authored configurations: constant versus implicit-input shaders over retained
asymmetric, swapped-origin, reset, rebased, opacity/clipped and scalar-restored
states. Each compares complete independently specified 64-by-64 RGBA images over
cold, warm and independent compositors. No observed output supplies expectations.

No build, syntax check, test, verifier, probe, GPU/UI run, VM or CI request was
executed. Original Microsoft comparisons, both providers, complete packages,
source applications and final platform/image/performance gates remain required.
