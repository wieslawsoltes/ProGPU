# Original BitmapCacheBrush shader sampler reference

`BitmapCacheBrushSamplers.cs` adds an actual Microsoft WPF BitmapCacheBrush to
the original identity shader. One unparented ContainerVisual owns an inner
ContainerVisual and two retained DrawingVisual leaves. Their mutable rectangle
geometries and color brushes, both cache objects, sampled root, brush, transforms,
effect and PixelShader retain identity across all fifteen states. No UIElement,
source bitmap substitute, reflection or product renderer supplies the source.

The [BitmapCacheBrush contract](https://learn.microsoft.com/en-us/dotnet/api/system.windows.media.bitmapcachebrush?view=windowsdesktop-10.0)
distinguishes explicit, target and default cache selection. It excludes six outer
root properties from capture and ignores brush-cache snapping. The fixture sets
all six excluded properties on states 3–14: offset, transform, clip, blur effect,
opacity and a fully transparent opacity mask. Descendant clip and opacity remain
real source state. The original [shader sampler implementation](https://github.com/dotnet/wpf/blob/381194e1ffe4d64fb747556fcaf76e1c34fe9df8/src/Microsoft.DotNet.Wpf/src/WpfGfx/core/resources/ShaderEffect.cpp#L419)
uses the selected raw cache texture directly. Its normalized shader coordinates
do not paint the ordinary BitmapCacheBrush over the receiver: consumer brush
opacity, Transform and RelativeTransform are ignored. The source cache's own
raster dimensions are independent of the receiving frame.

The receiver draws a local 32x24 rectangle at visual offset (8,10), clipped to
that world rectangle on a 64x64 opaque-black target. The independently authored
literal bands for the integral-cache profile are:

| State | Source change | Expected receiver-local coverage |
| --- | --- | --- |
| 0 | Default cache | Red [0,16), green [16,32), full Y [0,24) |
| 1 | Target cache scale 2 | Same normalized coverage |
| 2 | Explicit cache scale 1, snapping enabled | Same normalized coverage |
| 3 | Six excluded root properties | Same normalized coverage |
| 4 | Inner clip to green, inner and brush opacity .5 | Green component 128 over the complete receiver; brush opacity ignored |
| 5 | Relative X .25, absolute Y 2 | Exact state-0 frame; both consumer transforms ignored |
| 6 | Same explicit cache, scale zero | Black |
| 7 | Genuine null Target, same owned source retained | Black |
| 8 | Reattach same target; first becomes blue; negative origin | Blue [0,16), green [16,32), full Y [0,24) |
| 9 | Remove both leaves from the same attached inner group | Black; same non-null target |
| 10 | Detach that empty inner group from the same target | Black; same non-null target |
| 11 | Reattach the same group and ordered leaves | Exact restoration of state 8 |
| 12 | Root ScrollableAreaClip outside all source ink | Same state-8 blue/green frame; root scroll clip ignored |
| 13 | Also set the descendant group's scroll clip outside all source ink | Black; the same leaves remain attached |
| 14 | Clear both scroll clips | Exact restoration of state 8 |

Zero-scale no-ink follows the public
[RenderAtScale contract](https://learn.microsoft.com/en-us/dotnet/api/system.windows.media.bitmapcache.renderatscale?view=windowsdesktop-10.0).
State 8 restores scale one on the same explicit cache. All source leaves remain
attached to their original inner group during the null-target state. In states
9–10 the now-detached leaves, drawings and geometries are still retained by their
original owner; no source, brush or cache replacement manufactures empty paint.
State 9 preserves a real empty group in the target graph, while state 10 removes
that edge too. Actual descendant bounds must be empty in both states. State 11
reattaches the exact same group and leaves in their original order, preserving
their state-8 rectangles, colors and negative origin. The shader continues to
reference the same non-null target and explicit scale-one cache throughout.
Actual descendant/content bounds and selected properties are recorded before and
after rendering; none derive expected pixels. Every byte is compared over three
replays: retained, same-owner warm and independent literal construction, totaling
45 captures. Raw BGRA/PNG evidence precedes pixel failure publication. Cache-policy,
null/zero-scale/attached-empty and refill equivalence are separate complete-frame
controls. The original source mutations are unchanged. Schema 2 corrects the
previous unexecuted natural-placement and consumer-opacity expectations; those
expectations were not observed original behavior.

Schema 3 adds the three scroll-clip mutations without changing any prior input.
Two small ContainerVisual subclasses expose only the original protected scroll
clip property; they do not alter painting or bounds. The root cache entrypoint
captures its content and children without the root's outer properties, while
[ordinary descendant traversal retains the scroll clip](https://github.com/dotnet/wpf/blob/381194e1ffe4d64fb747556fcaf76e1c34fe9df8/src/Microsoft.DotNet.Wpf/src/WpfGfx/core/uce/drawingcontext.cpp#L4816),
including when scroll acceleration is unavailable. The outside descendant clip
deliberately expects black independently of whether original bounds tighten.
State 14 restores the same live source owners, not a replacement visual or bitmap.
These controls concern clipping, not scroll acceleration or fractional snapping.

The target RenderTargetBitmap remains 96 DPI, but that is not proof of the primary
DPI used by the original cache rasterizer. The fixture records the UI thread's
GetDpiForSystem value as a diagnostic only; it does not establish WPF's historically
cached native primary scale and does not drive expected pixels. These literal
bands assume integral source cache extents (the paired native corpus uses 96 DPI).
Fractional/near-integer extent rounding, per-axis device clamping and awareness
transitions require separate original controls. An unexpected output fails the
literal corpus; it is never used to derive another oracle or relax a tolerance.

The original ProGPU identity shader, evidence helpers and retained-source fixture
structure reuse `ImageSamplers.cs`, `DrawingImageSamplers.cs` and
`VisualBrushSamplers.cs` at `416892fbf91bb6e9d666bedb1d77ffc094afdd36`.
No foreign implementation is copied. Existing original families, the 60-second
deadline and explicit software-unavailable control are unchanged; an unavailable
shader qualifies zero cases. The pixel loop is reference-only, with no product
CPU raster or per-command crossing introduced.

The three empty/refill states extend the ProGPU-owned fixture at
`247eda817e9365744046da72996f3c840f7c3853`; they do not synthesize an original
WPF bounds sideband or infer ordinary cache-allocation policy from shader output.

These are authored expectations, not observed original results. No build, syntax
check, test, VM, GPU, UI, CI or reference probe has run. Generic cache allocation,
UIElement automatic wrappers, cyclic sources, arbitrary filtering/DPI, native
providers, packages and source hosts retain separate final qualification gates.
