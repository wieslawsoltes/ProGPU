# Owned BitmapCacheBrush shader samplers

The native MIL ShaderEffect sampler admits the existing typed BitmapCacheBrush
resource. It uses the owned cache-page and picture-scene renderer, but captures
the selected raw texture rather than ordinary BitmapCacheBrush painting. No bitmap pixels, texture handles, replacement
renderer or new shader wire format are manufactured.

The source is the original target visual and its descendants. BitmapCacheBrush
ignores the target root's offset, transform, clip, effect, opacity and opacity
mask; descendants keep their ordinary state. The sampler's ownership preflight
and effect dependency revision follow that same distinction. They retain the
selected explicit brush cache, otherwise the target cache, otherwise the existing
default cache policy. An explicit brush cache does not accidentally traverse the
unused target cache. The original shared-page identities, raster parameters,
source bounds and explicit primary-DPI/device-limit raster policy remain separate
from receiving-frame placement. See [raw cache realization](native-cache-sampler-raster.md).

BitmapCacheBrush is not a TileBrush: it has no invented viewbox, viewport, stretch
or tiling. The shader input ignores brush opacity, RelativeTransform and Transform;
normalized shader UVs address the whole selected raw cache texture. Ordinary
brush painting retains its existing consumer-transform and opacity behavior.
RenderAtScale changes raw raster resolution, not the receiver's shader UV domain.
The existing zero/negative scale no-bitmap policy and ignored SnapsToDevicePixels
remain unchanged. Selected scale animations retain their actual resources;
ignored brush paint animations do not become raw-texture dependencies. This
does not widen the named-angle admission of other sampler/ordinary consumers.

A genuine null target paints nothing. A nonnull target must be an initialized,
owned 2D Visual with actual source-bound metadata. Missing/deleted/uninitialized
targets and unknown bounds are not null. An explicitly known-empty nonnull target
uses the shader-only owned-empty path documented in
[Explicitly empty cache samplers](native-empty-cache-samplers.md); ordinary cache
semantics are not widened. Cache-root ScrollableAreaClip is excluded from the
raw capture; descendant scroll clips keep ordinary traversal. 3D targets,
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

The paired native providers author fifteen same-owner states, each replayed cold,
warm and independently after channel retirement. The complete 64×64 RGBA oracle
uses literal normalized raw-texture bands, not captured bounds or product
mapping. States cover default/target/explicit cache policy, scale two, ignored
snapping and all six root properties, effective descendant clipping/opacity,
ignored consumer transforms, zero scale, null target, negative-origin refill,
retained empty/childless/refill states and root-versus-descendant scroll clipping.
Original WPF captures use the same fifteen states with actual source objects.
The earlier unexecuted natural-placement and multiplied brush-opacity oracles
were incorrect and are superseded; no passing observation is being changed.

Raw controls additionally retain caller output on failure; distinguish absent,
known-empty, null and uninitialized targets; prove explicit cache selection,
ignored-root versus descendant cycles, retained dependency deletion rollback,
leaf revision, hidden external-source rejection even at zero scale, actual scale
animation initialization, nonfinite rejection and retained-scene ownership.

All builds, tests, original captures and provider execution
are deferred to the final integrated tip. This is not qualified source, pixel,
package or application parity; downstream pins remain unchanged.
