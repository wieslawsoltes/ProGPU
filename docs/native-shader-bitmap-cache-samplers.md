# Owned BitmapCacheBrush shader samplers

The native MIL ShaderEffect sampler admits the existing typed BitmapCacheBrush
resource. It captures through the same owned cache-page and picture-scene path as
ordinary BitmapCacheBrush painting. No bitmap pixels, texture handles, replacement
renderer or new shader wire format are manufactured.

The source is the original target visual and its descendants. BitmapCacheBrush
ignores the target root's offset, transform, clip, effect, opacity and opacity
mask; descendants keep their ordinary state. The sampler's ownership preflight
and effect dependency revision follow that same distinction. They retain the
selected explicit brush cache, otherwise the target cache, otherwise the existing
default cache policy. An explicit brush cache does not accidentally traverse the
unused target cache. The original shared-page identities, raster parameters,
nonzero source bounds, consumer transforms and opacity remain separate.

BitmapCacheBrush is not a TileBrush: it has no invented viewbox, viewport, stretch
or tiling. RelativeTransform is resolved against the actual receiving extent,
then Transform and the consumer frame are applied by the existing cache renderer.
RenderAtScale changes raster resolution, not the source's natural logical extent.
The existing zero/negative scale no-bitmap policy and ignored SnapsToDevicePixels
remain unchanged. Scale and opacity animations retain their actual resources;
sampler transform animation uses the same explicit Matrix/Scale/Translate/group
contract as other sampler families, without broadening named-angle admission.

A genuine null target paints nothing. A nonnull target must be an initialized,
owned 2D Visual with actual positive source bounds. Missing/deleted/uninitialized
targets and unknown bounds are not null. An explicitly known-empty nonnull target
is still unsupported for this family; the VisualBrush-only empty-source contract
does not silently widen ordinary cache semantics. ScrollableAreaClip, 3D targets,
media/external images and DrawingBrush sources remain unsupported. Cycles and the
original recursion budget fail before scene publication, including nonpainting
branches. Root properties explicitly excluded by BitmapCacheBrush are not
traversed as painted dependencies.

This preflight proves ownership of the selected source graph, not eager semantic
validation of every nested current value. Existing render replay still owns
nested effect, numeric, frame and cache admission. ImageBrush, DrawingImage,
VisualBrush and ordinary cache consumers retain their separate policies.

## Original contract and validation status

The public [BitmapCacheBrush contract](https://learn.microsoft.com/en-us/dotnet/api/system.windows.media.bitmapcachebrush)
defines cache selection, ignored root properties and ignored snapping.
[RenderAtScale](https://learn.microsoft.com/en-us/dotnet/api/system.windows.media.bitmapcache.renderatscale)
defines zero and negative scale and distinguishes raster resolution from layout.
ShaderEffect's original sampler API permits BitmapCacheBrush, not DrawingBrush.

Native ownership/provider controls and original WPF captures are authored with
this implementation. All builds, tests, original captures and provider execution
are deferred to the final integrated tip. This is not qualified source, pixel,
package or application parity; downstream pins remain unchanged.
