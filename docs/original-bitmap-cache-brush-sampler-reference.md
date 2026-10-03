# Original BitmapCacheBrush shader sampler reference

`BitmapCacheBrushSamplers.cs` adds an actual Microsoft WPF BitmapCacheBrush to
the original identity shader. One unparented ContainerVisual owns an inner
ContainerVisual and two retained DrawingVisual leaves. Their mutable rectangle
geometries and color brushes, both cache objects, sampled root, brush, transforms,
effect and PixelShader retain identity across all nine states. No UIElement,
source bitmap substitute, reflection or product renderer supplies the source.

The [BitmapCacheBrush contract](https://learn.microsoft.com/en-us/dotnet/api/system.windows.media.bitmapcachebrush?view=windowsdesktop-10.0)
distinguishes explicit, target and default cache selection. It excludes six outer
root properties from capture and ignores brush-cache snapping. The fixture sets
all six excluded properties on states 3–8: offset, transform, clip, blur effect,
opacity and a fully transparent opacity mask. Descendant clip and opacity remain
real source state. Natural source placement is kept separate from cache density;
no TileBrush stretching or viewbox is invented.

The receiver draws a local 32x24 rectangle at visual offset (8,10), clipped to
that world rectangle on a 64x64 opaque-black target. The independently authored
literal bands are:

| State | Source change | Expected receiver-local coverage |
| --- | --- | --- |
| 0 | Default cache | Red [4,12), green [12,20), Y [6,18) |
| 1 | Target cache scale 2 | Same logical coverage |
| 2 | Explicit cache scale 1, snapping enabled | Same logical coverage |
| 3 | Six excluded root properties | Same logical coverage |
| 4 | Inner clip to green, inner and brush opacity .5 | Green component 64 in [12,20) × [6,18) |
| 5 | Relative X .25, absolute Y 2 | Red [12,20), green [20,28), Y [8,20) |
| 6 | Same explicit cache, scale zero | Black |
| 7 | Genuine null Target, same owned source retained | Black |
| 8 | Reattach same target; first becomes blue; negative origin | Blue [0,4), green [4,12), Y [4,16) |

Zero-scale no-ink follows the public
[RenderAtScale contract](https://learn.microsoft.com/en-us/dotnet/api/system.windows.media.bitmapcache.renderatscale?view=windowsdesktop-10.0).
State 8 restores scale one on the same explicit cache. All source leaves remain
attached to their original inner group, including during the null-target state.
Actual descendant/content bounds and selected properties are recorded before and
after rendering; none derive expected pixels. Every byte is compared over three
replays: retained, same-owner warm and independent literal construction, totaling
27 captures. Raw BGRA/PNG evidence precedes pixel failure publication. Cache-policy
equivalence and null/zero-scale equivalence are separate complete-frame controls.

The original ProGPU identity shader, evidence helpers and retained-source fixture
structure reuse `ImageSamplers.cs`, `DrawingImageSamplers.cs` and
`VisualBrushSamplers.cs` at `416892fbf91bb6e9d666bedb1d77ffc094afdd36`.
No foreign implementation is copied. Existing original families, the 60-second
deadline and explicit software-unavailable control are unchanged; an unavailable
shader qualifies zero cases. The pixel loop is reference-only, with no product
CPU raster or per-command crossing introduced.

These are authored expectations, not observed original results. No build, syntax
check, test, VM, GPU, UI, CI or reference probe has run. Non-null empty targets,
UIElement automatic wrappers, cyclic sources, arbitrary filtering/DPI, native
providers, packages and source hosts retain separate final qualification gates.
