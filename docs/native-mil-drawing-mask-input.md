# Source drawing opacity-mask input

## Application connection — 2026-09-09

LibreWPF's existing `ProGPU.Wpf.RealPresentationFrameworkHarness/Program.cs`
records a real DrawingVisual `PushOpacityMask` / DrawRectangle / Pop sequence.
Complete native input admission previously rejected the resulting unannotated
layer, including the harness's solid brush. Managed direct command capture
already ignored masks, but typed source-only traversal rejected these commands
when opacity culling, effects or optional caches requested that traversal.

The source WPF `HitTestWithPointDrawingContextWalker.PushOpacityMask` preserves
the current point and `HitTestWithGeometryDrawingContextWalker.PushOpacityMask`
pushes a no-op modifier. Both are explicit input-neutral contracts. `Visual`
point/geometry traversal separately retains actual geometry and scroll clips.
This is source evidence, not a reproduced runtime result.

## Shared implementation

The C++ builder adds `scene_layer_hit_test_mode::source_opacity_mask`, an explicit
caller declaration for a source alpha-mask layer. Only effect-free SrcOver layers
with ordinary isolation/storage-bounds flags are admitted. The normal resource
validation remains required; a resource explicitly tagged as a source geometry
clip cannot be reclassified as an opacity mask. Generic unannotated layers remain
unsupported by complete input capture. No C wire record or shader changes.

Canonical MIL `push_opacity_mask` selects this annotation only when compiling
source input. Its brush, gradient/image mask resource, opacity, bounds and raster
commands are unchanged. Source input consumes enclosed geometry and real clips,
not mask pixels or the mask's cached bounding rectangle. Nested masks preserve
the existing owner/state stack; the index is not published after an unsupported
command. This is not permission to label arbitrary geometric masks input-neutral.

Managed typed source traversal now consumes balanced Push/PopOpacityMask scopes
without querying their brush or bounds. It retains its strict actual-clip path,
local picture/visual scope ownership and faulted-index publication on imbalance.
The existing direct managed command path already preserves enclosed geometry and
does not need a raster change. Visual-level `OpacityMask`/`OpacityMaskPicture`
admission, including combined cache/effect boundaries, remains separate and open.

The added work is O(C) structural command dispatch with an O(1) mask-depth counter
per managed traversal and existing sparse native layer metadata. It does not
introduce a compute kernel, numeric CPU fallback, CPU pixel readback, raster
repacking, per-owner GPU submission, or new GPU resource. Existing intrinsic
geometry placement and canonical GPU queries remain shared. No performance
improvement is claimed without final matched measurements.

## Research and applicability

Revisited the primary references in
[the input ownership design](native-mil-hit-test-ownership.md#design-references-and-decisions)
on 2026-09-09: WebRender's retained scene/spatial separation, Skia/SkParagraph's
geometry versus layout APIs, Vello/Parley's scene/layout separation, HarfBuzz's
shape-plan reuse, and DirectWrite/Win2D's geometric/text query contracts. Retain
those boundaries; do not put raster alpha in source caret or geometry selection.
[Direct2D layers](https://learn.microsoft.com/en-us/windows/win32/direct2d/direct2d-layers-overview)
distinguish geometric masks from opacity brushes and layer storage bounds.
Adapt that distinction as typed metadata, not a Direct2D implementation port.
WPF's source walkers, not another renderer's pixel semantics, own this policy.

Startup/lazy initialization, shaping/layout reuse, visibility, glyph/path/texture
cache keys and eviction, demand-driven uploads, worker preparation, GPU batching,
DPI/hinting/subpixel state, fallback/variable fonts and device/atlas generations
are unchanged. This connection neither duplicates the text composer nor changes
execution-policy defaults. Both managed and native paths are included.

## Authored qualification and remaining work

`DrawingMasksPreserveSourceInputAndActualClips` pairs with native canonical MIL
scene 9836: nested solid/transparent masks, smaller mask bounds than source
geometry, an actual rectangle clip, an unmasked following draw, replacement and
clearing. The native fixture additionally covers a gradient brush and asserts
that raster mask layers/resources remain present. Managed unbalanced-scope
fixtures reject missing/extra Pop without publishing partial geometry. The C++20
module consumer references the new enum through the public builder API.

These regressions are authored, not executed. Complete platform compilation,
runtime host/package input, pixel comparison, lifetime/performance and exact-head
CI remain required at feature freeze. This does not close visual mask/cache/effect
input, remaining exact clip combinations, required cached pictures, or the full
application acceptance queue. Packages are not refreshed by incremental builds.
