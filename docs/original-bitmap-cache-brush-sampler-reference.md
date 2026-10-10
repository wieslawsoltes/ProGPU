# Original BitmapCacheBrush shader sampler reference

`BitmapCacheBrushSamplers.cs` adds an actual Microsoft WPF BitmapCacheBrush to
the original identity shader. One unparented ContainerVisual owns an inner
ContainerVisual and two retained DrawingVisual leaves. Their mutable rectangle
geometries and color brushes, both cache objects, sampled root, brush, transforms,
effect and PixelShader retain identity across all twenty states. No UIElement,
source bitmap substitute, reflection or product renderer supplies the source.

The [BitmapCacheBrush contract](https://learn.microsoft.com/en-us/dotnet/api/system.windows.media.bitmapcachebrush?view=windowsdesktop-10.0)
distinguishes explicit, target and default cache selection. It excludes six outer
root properties from capture and ignores brush-cache snapping. The fixture sets
all six excluded properties on states 3–19: offset, transform, clip, blur effect,
opacity and a fully transparent opacity mask. Descendant clip and opacity remain
real source state. Actual original public API execution rejects assignment of
Transform and RelativeTransform, including identity Transform objects, and
rejects nondefault brush Opacity. Both transforms retain their original null
values and opacity remains one. Reassigning the default opacity is accepted.
The source cache's own raster dimensions are independent of the receiving frame.

The receiver draws a local 32x24 rectangle at visual offset (8,10), clipped to
that world rectangle on a 64x64 opaque-black target. The independently authored
software bands below use the independently defined first-band predicate: integer
receiver-local scan X, normalized by 32, multiplied by the selected physical cache
width, then nearest-even rounding and edge clamping. At 96 system DPI, scale one
has 16 source texels and changes band at local X15; scale two has 32 texels and
changes at X16. At 192 DPI both selected scales change at X16. These software
coordinates do not select a GPU sampling phase. The integral-cache states are:

| State | Source change | Expected receiver-local coverage |
| --- | --- | --- |
| 0 | Default cache | Red first band, green remaining band, full Y [0,24) |
| 1 | Target cache scale 2 | Same source colors; independently computed scale-two sampling phase |
| 2 | Explicit cache scale 1, snapping enabled | Exact state-0 phase and colors |
| 3 | Six excluded root properties | Same normalized coverage |
| 4 | Inner clip to green and inner opacity .5; brush opacity .5 rejected | Green component 128 over the complete receiver; actual brush opacity one |
| 5 | Relative X .25 and absolute Y 2 assignments rejected | Exact state-0 frame with unchanged null transforms |
| 6 | Same explicit cache, scale zero | Black |
| 7 | Genuine null Target, same owned source retained | Black |
| 8 | Reattach same target; first becomes blue; negative origin | Blue first band, green remaining band, full Y [0,24) |
| 9 | Remove both leaves from the same attached inner group | Black; same non-null target |
| 10 | Detach that empty inner group from the same target | Black; same non-null target |
| 11 | Reattach the same group and ordered leaves | Exact restoration of state 8 |
| 12 | Root ScrollableAreaClip outside all source ink | Same state-8 blue/green frame; root scroll clip ignored |
| 13 | Also set the descendant group's scroll clip outside all source ink | Black; the same leaves remain attached |
| 14 | Clear both scroll clips | Exact restoration of state 8 |
| 15 | Attach a nested empty DrawingImage containing a cache brush with an empty visual target | Same state-8 blue/green frame |
| 16 | Attach that cache target's retained red leaf; leave the DrawingImage geometry empty | Same state-8 blue/green frame |
| 17 | Add the retained rectangle to the DrawingImage's GeometryGroup | Black: re-entry updates the explicit cache while the shared inner group is already entered |
| 18 | Clear that same GeometryGroup, retaining its drawing, brush and source | Same state-8 blue/green frame |
| 19 | Restore the same rectangle to the same GeometryGroup | Exact restoration of state 17 |

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
60 captures. Raw BGRA/PNG evidence precedes pixel failure publication. Cache-policy,
null/zero-scale/attached-empty and refill equivalence are separate complete-frame
controls. The original source mutations are unchanged. Schema 2 corrects the
previous unexecuted natural-placement and consumer-opacity expectations; those
expectations were not observed original behavior.

Schema 3 adds the three scroll-clip mutations without changing any prior input.
The two retained containers expose the original protected scroll clip property
through one small subclass; it does not alter painting or bounds. The root cache entrypoint
captures its content and children without the root's outer properties, while
[ordinary descendant traversal retains the scroll clip](https://github.com/dotnet/wpf/blob/381194e1ffe4d64fb747556fcaf76e1c34fe9df8/src/Microsoft.DotNet.Wpf/src/WpfGfx/core/uce/drawingcontext.cpp#L4816),
including when scroll acceleration is unavailable. The outside descendant clip
deliberately expects black independently of whether original bounds tighten.
State 14 restores the same live source owners, not a replacement visual or bitmap.
These controls concern clipping, not scroll acceleration or fractional snapping.

Schema 4 adds the nested DrawingImage sequence without changing states 0–14.
A third retained visual draws an ordinary ImageBrush over `(-4,4,8,12)`. Its real
DrawingImage owns one DrawingGroup and one GeometryDrawing. That drawing retains
an ordinary BitmapCacheBrush and a GeometryGroup even when the latter has no
children and the image bounds are exactly empty. The cache brush keeps its
original target, explicit scale-one cache and initially detached red source leaf.
State 16 attaches that leaf while the image remains empty. States 17–19 mutate
only membership of the original `(0,0,8,12)` RectangleGeometry in the retained
GeometryGroup. The originally authored red/green expectation for states 17 and 19
was unexecuted. Original software execution instead exposes the cache re-entry
interaction described below. No substitute bitmap, zero-area stand-in or fake
positive drawing bound is supplied.

The fixture checks the nested image, drawing, geometry, brush, cache and visual
identities and actual empty/nonempty bounds before and after every capture.
Empty image and empty nested visual are independent source states. Their pixel
observations do not by themselves prove native graph ownership, deletion guards,
cycle handling or sideband transitions; the paired native/source controls retain
those separate obligations. This sequence is inside a BitmapCacheBrush shader
source and does not broaden the direct DrawingImage shader sampler policy.

The target RenderTargetBitmap remains 96 DPI while cache allocation uses the
original primary display scale. Schema 6 records the stable UI-thread
GetDpiForSystem input and uses it for the explicitly integral cache-profile
expectation. That query alone is not proof of WPF's historical native DPI cache;
awareness transitions, fractional/near-integer allocation and device clamping
remain separate qualification gates. A changed observed DPI aborts the capture.

The software phase follows the original WPF contract: integer shader scan
positions and nearest-even texture-index conversion. Sixteen independent controls
cover both ties, adjacent texels, clamping, selected scale and 96/192 DPI. The
existing 19 TileBrush software arithmetic controls are unchanged. Source reads
used `ShaderEffect.cpp` for scan origin, `fxjit/PixelShader/pshader.cpp` for sampler
conversion, and its documented CVTPS2DQ operation. The arithmetic is independently
expressed; no foreign renderer implementation was copied. The native GPU fixture,
shader bytes, visual inputs, all 20 states and 60 replays remain unchanged.

Schema 7 corrects the two nested expectations from original source behavior,
without changing any of the original twenty source states or shader bytes. The
cache set updates the target cache before the distinct explicit brush cache.
When painting the nested BitmapCacheBrush, another cache-update pass can start.
The active target cache rejects recursive update, but the explicit cache has not
yet been entered. Its source walk skips the inner visual group that is already
being traversed by the target cache. That explicit cache therefore remains
transparent; its shader sample leaves the opaque black background unchanged.
This inference follows the original cache-update and graph-walk contracts and
is independently discriminated by the controls below. It is not a generic rule
that a nested DrawingImage is empty or unsupported.

| Re-entry control | Independently expected software output |
| --- | --- |
| Original target plus explicit caches | Black |
| Explicit brush cache only | Red/green at scale-one sampling phase |
| Target cache only | Red/green at scale-two sampling phase |
| Default cache only | Red/green at scale-one sampling phase |
| Same BitmapCache object assigned to target and explicit brush | Black; the two cache slots still exist |
| Equal scale-one values on distinct cache modes | Black; matching dimensions do not merge the two cache slots |
| Nested receiver moved outside the inner group, as its next sibling | Blue/green; the entered nested leaf is omitted, while the sibling group is traversable |

Every control retains all original nested image/drawing/cache resources, source
rectangles, shader bytes and six excluded root properties. Typed construction
changes only the stated cache attachment, scale or parent edge; no reflection or
substitute bitmap participates. Complete identity/property checks run before and
after three replays, including an independent literal instance. Every BGRA byte
and retained/warm/literal equality remains strict. States 17 and 19 additionally
match the independently retained null-target frame. The new inventory is seven
controls and 21 replays, alongside the unchanged 20 states and 60 replays. Native
and hardware qualification counts are explicitly zero.

The source explanation uses `CMilVisualCacheSet::Update`,
`CMilVisualCache::Update`, `CMilBitmapCacheBrushDuce::GetBrushRealizationInternal`
and `CGraphIterator::Walk` from the original .NET WPF source. These reads establish
ordering and entered-node semantics; no foreign implementation is copied.
Earlier diagnostic controls also moved all leaves directly below the cache root
and placed a white sibling outside the active inner group. Their blue/green and
partial-white outputs distinguished traversal suppression from a global shader
or allocation failure at both 96 and 192 DPI. Independent ETW observations showed
nonzero cache allocations and updates; allocations alone never proved contents.

This is a bounded original SoftwareOnly observation. The native authored nested
fixture still expects ordinary red/green capture and does not inherit this
software implementation interaction. That native/provider comparison, hardware
behavior and source-host admission remain unqualified; passing these original
controls does not claim parity for them.

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

Schema 5 records the original setter outcomes and verifies atomic preservation
of opacity and both transform identities after rejection. Earlier schema5/6
receipts preserved the two nested failures while the source interaction was
unknown; schema7 supersedes their authored nested expectations with the explicit
source-walk controls above. Generic cache allocation, UIElement wrappers, real
cyclic source graphs, arbitrary filtering/DPI, native providers, packages and
source hosts retain separate qualification gates.

Local original Microsoft WPF validation on 2026-10-09 passed all 20 states/60
replays and all 7 controls/21 replays at each of 96 and 192 DPI on x64. The ARM64
software-unavailable control passed the same complete inventory and qualified
zero shader or re-entry cases. All 180 original-state captures were byte-identical
to the saved schema 6 evidence across these three executions. The reference
build had zero warnings/errors, and the workflow YAML parsed. These focused
captures do not substitute for a complete successful hosted reference workflow.
