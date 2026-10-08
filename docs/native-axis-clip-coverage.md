# Native axis-clip pixel coverage

Direct2D `PushAxisAlignedClip` retains an explicit `AXIS_CLIP_AREA` flag on its
existing analytic mask record. The flag admits only zero corner radii, an identity
source transform and opacity one. The source has already captured the clip's
target-axis bounding rectangle under the push-time transform. Unknown raw flags,
curved/nonidentity/translucent flagged records reject; ordinary flags-zero rounded
masks retain their existing distance-based coverage. Wire size and binding layouts
are unchanged, and an older reader rejects this previously unsupported flag.

The actual source producers select this policy for antialiased axis clips, with
or without Clear. The native and managed wire writers preserve it through analytic
chains. Existing group opacity remains outside the mask. Clear still replaces
actual storage before applying the enclosing clip at pop, as described in
[target-storage Clear](native-target-storage-clear.md).

## Physical frame and coverage

The shared native reader projects the four original edges with the same independent
float multiply/add lanes used by binary clips. It uses the actual per-axis DPI and
viewport, then subtracts the exact current target origin. The GPU receives those
physical edges and a unit-pixel frame. It never divides source coordinates by DPI
and multiplies them back, estimates a derivative width or substitutes a rounded
distance corner. Output arguments remain unchanged on invalid/overflowed mapping.

For a pixel interval `[p - .5, p + .5]`, each axis contributes the length of its
intersection with the clip interval, clamped to `[0,1]`. Multiplying the two
lengths gives rectangular area coverage, including corners and clips narrower
than one pixel. A corner covering half of both axes contributes one quarter,
not the one-half supplied by a maximum-edge distance evaluated on the boundary.
The same explicit branch is retained in shared Vector, Texture and text-mask WGSL
consumers used by both native providers. Its fixed arithmetic is guarded by an
authored byte-equality control; existing ordinary distance/border paths are intact.
Area evaluation and uniform storage remain O(1) per mask/pixel. Explicit byte
attachment layers additionally resolve through bounded source-local coverage
and the existing destination-aware composition machinery, preserving original
stored-byte rounding. Consecutive AA groups use conditional intersection area;
ordinary layers stop that ancestry. There is no readback or managed fallback.

## Evidence and remaining gates

This is original ProGPU implementation over its existing mask binding/frame
contracts. The [Microsoft axis-clip contract](https://learn.microsoft.com/en-us/windows/win32/api/d2d1/nf-d2d1-id2d1rendertarget-pushaxisalignedclip%28constd2d1_rect_f_d2d1_antialias_mode%29)
defines transformed axis-aligned clipping and group-edge antialiasing. The exact
area branch is our explicit mathematical policy; equivalence to the original
Windows rasterizer requires the independent original-window pixel controls, not
an inference from that API documentation.

Authored native/managed controls cover exact flag transport, analytic chains,
legacy controls, unknown wire bits, invalid frames/radii/opacity, unchanged bytes
on rejected publication, mixed-axis fractional DPI/viewport/target localization,
overflow atomicity and identical shader arithmetic. The separate actual-source
AA Clear fixture retains independent pixel-area calculations, cold/warm counters,
all original Windows bytes, transparent/opaque targets and nested clip/layer
ordering. Those assertions have not been weakened to reproduce shader output.

The complete local stock Metal AA fixture and independently captured original
Windows ARM64/x64 frames now agree on all 204 full-frame comparisons. Original
source bytes established conditional nested coverage and independent binary
pixel-center admission; no tolerance was introduced. The standard native run
still fails in a later cached gradient-mask case. Full final integrated provider,
package and source-application qualification remains required.
