# Native MIL cached source-effect input

## Acceptance path

The LibreWPF showcase must retain source selection when ordinary content combines
BitmapCache with Blur or DropShadow. The native release fixture reached this
connection after the solid EllipseGeometry route was repaired.

The outer effect still excluded all local caches from its source-input declaration,
although the inner cache already publishes its original-content coordinate frame.
Consequently complete input-index compilation rejected the outer effect scope.

## Repair and paired applicability

The built-in identity effect now preserves input around an admitted local cache.
The existing nested builder scopes retain cache frame conversion, original pen
and fill geometry, final source clipping, owner identity and balanced restoration.
Positive cache scale stays independent of source input; zero scale still uses the
balanced input-only scope and emits no raster commands. Effect padding and cache
allocation rectangles never become selection geometry.

Spatial visual opacity masks remain explicitly rejected, including when the mask
is consumed by a local cache rather than the uncached effect-isolation path. The
cache's own mask and transform validation remains unchanged. This is not blanket
cache/mask/effect parity or permission to disable complete-index admission.

The managed compositor already captures the original source input before cached
effect composition. Its nine `CompositedSourceEffectsRetainOwnAndChildInputWithoutPadding`
cases pass unchanged. Native scene 9817 covers blur, zero-radius blur and shadow
without a cache, with positive scale and with zero scale; three additional cases
require spatial-mask rejection without a partial output stream.

This reuses ProGPU-owned source effect/cache scopes and changes no C ABI, shader,
sampling quality, resource lifetime or submission policy. Selection is fixed work
per visual and allocation-free; no performance improvement is claimed. The
[existing input ownership research](native-mil-hit-test-ownership.md#design-references-and-decisions)
and [cache frame contract](native-mil-cache-input.md) remain authoritative.

## Separate compiler-sensitive fixture correction

The rectangular brush/pen fixture no longer hard-codes one platform's round-join
triangle total. Native libm rounding at a quadrant can change a `ceil` subdivision
decision. It now checks every line endpoint and every join's exact three segment
coordinates against the shared renderer's output, plus complete count, bounds and
source ownership. No raster algorithm, pixel tolerance or geometry gate is relaxed.

The entire native and package/application suites remain required. Reaching a later
failure in the native executable proves these cases ran, not that the whole native
renderer or cross-platform application is qualified.
