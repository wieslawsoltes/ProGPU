# Native MIL hit-test ownership

## Core application dependency

LibreWPF Toolkit/AvalonDock clicking and selection use retained owner queries.
The native MIL host presents a C++ compiled scene, but its host query methods
currently ask the managed compositor for an index. The native MIL compiler now
has opt-in index emission for analytic primitives, plain path fills, images and
source-bounded glyph runs, not yet
the complete application coverage needed to enable the host. Existing source-owned geometric input fallback is
not evidence that the retained native owner-query gate is complete.

This implementation establishes the result/owner ownership boundary and starts
native index production. It does **not** close complete MIL input coverage or
host point/region query routing.
Do not mark application closure or feature freeze from this checkpoint.

## Implemented contract

`NativeGpuHitTestOwnerMap<T>` owns an immutable copy of integer-to-source-object
bindings. Native streams retain integer IDs, never managed addresses. Preserve
all signed ID bits; the result's `Hit` field, not its ID sign, distinguishes a
miss. A caller may keep an earlier snapshot after a later source replacement.
Missing owners return false; they are not discovered through reflection or
guessed from a visual's bounds or type name.

`NativeCompositor.UpdateScene` captures the successfully installed scene ID and
generation inside its render lock. The scene-qualified `BeginGpuHitTest` overload
checks both before submitting under that same lock. The original overload remains
available, and its returned token now also records the installed scene identity.
Tokens use a unique managed compositor identity rather than a recyclable native
engine pointer. This is a managed handle change (32 bytes), not a C ABI change;
the native token remains the original `uint64_t`.

`BindGpuHitTestOwners` returns an allocation-free typed snapshot after checking
the installed scene. It creates no index and submits no GPU work. Its `BeginQuery`,
`TryPoll` and `TryGetOwner` bind queries/results to that compositor and generation.
Old completed results can resolve only through their retained old snapshot; new
submission through an old snapshot rejects after another generation is installed.
Native pending-request, thread-affinity, capacity and device-loss errors retain
their existing behavior. A failed native scene update leaves installed identity
unchanged. Scene IDs/generations must not be reused for a different owner mapping,
even if its drawing bytes are identical.

LibreWPF collects actual typed source visuals when assigning MIL handles. The
batch and compiled frame carry that immutable map separately from MIL bytes.
Synthetic placement/container visuals have no source owner. Popup source roots
do; brush-only source resources must not become independent input targets when
the native index is emitted. A no-byte-change channel update still replaces the
source map. The host publishes a bound owner snapshot only after presentation,
invalidates it before installing another scene, and clears it on native teardown.
Its frame generation advances independently of unchanged MIL bytes.

Construction costs O(V) time/storage for V source visuals, only at source batch
rebuilds. Stable frame binding is O(1) with no allocation, and each owner lookup
is amortized O(1). Dictionary insertion/reference metadata is dependency-bound,
not a numeric CPU fallback requiring SIMD. No shader, raster quality, font metric,
geometry algorithm, GPU fallback policy or pixel readback is added here. Existing
native GPU query readback remains caller-span based, preserving result ordering,
intersection detail, capacity rules and diagnostics without per-owner submissions.

## Design references and decisions

Primary references inspected on 2026-09-09; no third-party source was ported.

| Engine/reference | Application to this boundary |
| --- | --- |
| [WebRender overview](https://firefox-source-docs.mozilla.org/gfx/RenderingOverview.html) and [retained hit tester](https://github.com/servo/webrender/blob/main/webrender/src/hit_test.rs) | Adopt retained scene ownership and separate spatial/clip metadata. Do not resolve an old result against a newly traversed tree. Adapt the concept to existing ProGPU native GPU queries, not WebRender's CPU implementation. |
| [Skia geometry](https://api.skia.org/classSkPath.html) and [SkParagraph API](https://github.com/google/skia/blob/main/modules/skparagraph/include/Paragraph.h) | Keep geometry containment separate from layout positions and text affinity. Reject replacing either with a source-owner bounds rectangle. No new paragraph composition or glyph discovery belongs in this map. |
| [Direct2D geometry](https://learn.microsoft.com/en-us/windows/win32/api/d2d1/nn-d2d1-id2d1geometry), [DirectWrite point queries](https://learn.microsoft.com/en-us/windows/win32/api/dwrite/nf-dwrite-idwritetextlayout-hittestpoint) and [Win2D geometry](https://microsoft.github.io/Win2D/WinUI3/html/T_Microsoft_Graphics_Canvas_Geometry_CanvasGeometry.htm) | Preserve distinct fill/stroke containment, transforms and text-relative metrics. Owner selection does not replace text caret logic or extend COM admission. |
| [Vello scene encoding](https://docs.rs/vello/latest/vello/struct.Scene.html) and [Parley layout](https://docs.rs/parley/latest/parley/struct.Layout.html) | Preserve independently reusable scene and layout results. Keep source identities outside encoded rendering data; do not append/rebuild an entire scene for each query. |
| [HarfBuzz shape plans](https://harfbuzz.github.io/harfbuzz-hb-shape-plan.html) | Retain the existing face/features/language/variation-qualified shaping plan. GPU owner IDs must not become surrogate font or cluster identities. |

Startup/lazy GPU creation, worker preparation, visibility culling, glyph/path/
texture keys and eviction, demand-driven uploads, GPU batching, DPI/subpixel/
hinting and fallback/variable-font state are unchanged by this metadata boundary.
The retained-scene references motivate reuse; the text references explicitly keep
layout/shaping reuse independent. Device recovery changes compositor identity and
must rebuild/rebind the new frame; an old token must never cross that boundary.
No timing or memory improvement is claimed without final matched measurements.

## Opt-in C++ producer connection — implementation checkpoint

`scene_build_request_flags::hit_test_index` (C `HIT_TEST_INDEX`, C# `HitTestIndex`)
requests one canonical index in the compiled scene. Normal rendering requests are
unchanged; combining this flag with `visual_brush` is invalid. MIL records each
visual handle's signed bits only around its own content, clears that owner before
descending into children, and does not create owners for brush-source traversal.
Visual bitmap caches reject the option before a cache shortcut can omit content.

The reusable `semantic_scene_builder::set_hit_test_owner` records sparse command
boundaries; null excludes subsequent commands. It is metadata, not another draw or
ABI crossing. Image coalescing cannot cross an owner boundary. Reset clears the
boundaries. `add_recorded_hit_test_index` reads builder-owned command/resources
directly and calls the existing native quadtree builder and index serializer.
It does not serialize and parse the scene a second time or install a partial
index after rejection. An empty scene retains a valid empty index root.

Connected coverage: analytic rectangle/rounded rectangle/ellipse fills and normal
centered analytic strokes, ordinary nondegenerate geometry-line strokes with
flat/square/round/triangle caps, plain filled paths with original line/quadratic/cubic/
arc segments, image destination quads, source-owned glyph ink rectangles, affine
placement and exact rectangular world clips. Bounds serve
only broad-phase pruning; analytic/path parameters and clip edges remain available
to the canonical query shader. Semantic path fill-rule values are explicitly
converted to the opposite `ProGPU.Vector.FillRule`/hit-shader numbering.

Remaining families explicitly reject with `unsupported_hit_test`, surfaced by MIL
as `unsupported_command`: other draw types, unannotated layers/effects/masks/cache isolation,
boolean topology, guideline state, image effects, missing glyph ink metadata,
and device/hairline analytic flags. This option
is therefore **not enabled in LibreWPF host requests** yet. It is a producer
connection, not a reduced replacement for required application input.

Original implementation provenance: `ProGPU.Vector/GpuHitTesting.cs` factories,
`ProGPU.Vector/Shaders/GpuHitTesting.wgsl`, existing C++ `HitTesting` index and
`Scene/Builder` resource ownership. Managed algorithms/shaders remain unchanged;
the missing producer is C++-specific. Matched primitive-value fixtures accompany
native owner/clip/path/reset/rejection and canonical MIL parent/child fixtures.
An import-based module consumer also exercises the new API. These are authored
regressions, not executed qualification.

Preparation is O(C + P + S + P*D) time and O(R + P + S + D) auxiliary storage for
commands C, resources R, emitted primitives P, copied segments S and quadtree
depth D. Owner traversal and tree construction are dependency-bound. Four-corner
placement uses NEON/SSE2 independent lanes and bounded scalar reduction; fixed
matrix inversion retains dependent double arithmetic. Unsupported architectures
reject this encoder until their intrinsic implementation is connected. There is
no new GPU submission/readback, pixel fallback, or speed claim. Existing GPU
queries and their execution policy are unchanged.

## Source opacity connection

The MVP's opacity animations and Toolkit/AvalonDock source drawing scopes use
opacity without changing the underlying input geometry. Source WPF point and
region drawing walkers deliberately retain input through PushOpacity, including
zero opacity; visual input visibility is separate from rendered alpha.

Native `scene_layer_hit_test_mode::source_opacity` annotates a retained layer
without changing its stream or raster parameters. Admission requires SrcOver,
no effect or mask resource, and only isolation/derived content bounds flags.
These layer bounds size rendering storage; they do not replace source clipping
or become a hit rectangle. MIL selects the annotation for ordinary/animated
PushOpacity and unmasked visual opacity groups only when index emission is
requested. Unannotated or differently composed layers remain unsupported.
The index walks balanced layer/save stacks, retains source owners and transforms,
and indexes actual enclosed commands. Native source-geometry opacity mode also
keeps commands whose effective scene-state opacity is zero, including logical
image rectangles. Generic native default opacity filtering remains unchanged.

Managed `DrawingContext.PushOpacity(opacity, affectsHitTesting: false)` records
`IsSourceOpacityScope` on the existing command. Compact scalar-state and general
snapshots preserve it; the hit cache saves its prior input opacity and applies
an identity factor, while rendering still sees the real alpha. WPF's product
command sink selects that source policy, including visual-state command replay.
Ordinary ProGPU calls keep their previous opacity-sensitive input policy.

Matched fixtures cover zero/fractional opacity, nested save/layer or clip stacks,
outer transforms and actual clips, subsequent owners, retained alpha, reset,
generic policy preservation, unsupported blend/layer rejection and canonical
MIL regular/animated scopes with zero-opacity visual parents, with/without typed
isolation bounds. Header and module consumers share the enum/signature contract.
This is not full input qualification. The managed retained visual connection
below handles its separate opacity-culling branch; masks/effects/cache coverage
and native host query routing remain in the application queue.

Provenance is the original ProGPU builder/state and managed opacity-stack code.
The research references above were revisited; additionally
[Direct2D layers](https://learn.microsoft.com/en-us/windows/win32/direct2d/direct2d-layers-overview)
and [Win2D layer creation](https://microsoft.github.io/Win2D/WinUI3/html/M_Microsoft_Graphics_Canvas_CanvasDrawingSession_CreateLayer.htm)
support keeping group rendering separate from source input policy. Skia/Vello
inform balanced scopes; WebRender informs separate spatial/clip ownership. None
of these rendering APIs supplies WPF's hit policy. SkParagraph/Parley/HarfBuzz
shaping/layout/cache contracts are unchanged. No third-party implementation was
copied. New native metadata is O(L) for L annotated layers with geometric growth
and capacity reuse; stack traversal is dependent O(C), not a numeric CPU fallback.
Existing SIMD primitive placement and the canonical GPU shader are unchanged.
No new readback, per-item submission, pixel work or performance claim is added.

### MVP ordinary line input

The real package application's `MvpShapeLine` in `MainWindow.xaml` uses endpoints
(16,98)/(154,76), thickness 4 and round caps. MIL's existing
`append_resolved_line_stroke` emits a geometry-line resource for its undashed pen;
the hit producer previously rejected every DRAW_GEOMETRY command. It now emits
the canonical LineStroke record for ordinary line primitives, preserving each
cap, local endpoints/thickness, combined transform, source owner and actual clip.
No WPF-local lowering or change to MIL rendering is needed. Geometry resources
with other kinds, device-width flags, and separate stroke batches still reject.
Lengths at/below 0.0001 remain explicit unsupported input: the shared shader's
generic degenerate-line disk is not source-directed cap geometry.

Direction metadata ports original `GpuHitTesting.CreateLineStrokeHitTestData`;
endpoint subtraction/squaring uses NEON/SSE2 and a fixed length reduction. Existing
intrinsic four-corner placement handles bounds. In both managed/native LineStroke
encoders, square caps expand the conservative radius padding by sqrt(2), because
a diagonal square corner lies outside the previous endpoint envelope. The exact
cap data/query is unchanged; this is broad-phase correction, not inflated hit
geometry. New native records add O(L) fixed work for L lines, with no per-line
submission/readback or stroke-outline allocation. No performance claim is made.

Matched fixtures cover all 16 cap pairs, nonidentity placement, source owner and
clipping, direction data and the diagonal-square envelope. Native canonical MIL
scene 9813 uses the actual MVP line values; native builder scene 9812 also keeps
point-cap rejection explicit. These fixtures are authored for final execution.
The existing engine references apply; additionally the primary
[Direct2D stroke-containment contract](https://learn.microsoft.com/en-us/windows/win32/api/d2d1/nf-d2d1-id2d1geometry-strokecontainspoint(d2d1_point_2f_float_id2d1strokestyle_constd2d1_matrix_3x2_f_float_bool))
reinforces preserving width/style/transform separately. Original ProGPU geometry
records, Vector factories and canonical WGSL supply the implementation; no third-
party code was ported. Shaping, caches, workers, startup, atlas/DPI/hinting policy,
GPU batching and device recovery are unchanged. Full native host coverage and
qualification remain open.

### Retained source visuals without raster work

`ISourceGeometryHitTestCommands` publishes an existing source-owned command buffer,
stable for synchronous capture. LibreWPF's retained visual implements it directly;
no second recording, reflection, OnRender call or WPF-local hit encoder is added.
When `CompileVisualTreeCore` culls an opacity-zero source node, the enabled hit
builder traverses those commands and children, preserving local/parent transforms,
template order, local/outer/composite/geometry clips and actual command owner IDs.
The matching zero-opacity branch in the existing cached-descendant traversal uses
the same capture. Invisible nodes stay excluded; generic untyped zero-opacity
visuals and disabled/suspended hit testing retain their old culling behavior.
Visual Size is never source hit geometry. Explicit command opacity still uses
its recorded source/generic policy; visual alpha and raster state are unchanged.

Nested pictures borrow retained snapshots and providers; nested visuals must
publish the same typed source contract. Embedded visuals join the compositor's
existing version tracking through one cached observer, preventing an unchanged
parent from reusing stale input after an embedded child changes. The observer is
borrowed only during capture. Logical image scopes retain their source
rectangle, including empty contents, and suppress only internal render commands.
Unknown draw commands, effects, caches and masks outside logical images reject.
Glyphs require nonempty declared ink bounds rather than falling through to the
legacy position estimate; authoritative-empty glyph metadata needs a distinct
future representation. Command scopes cannot pop enclosing visual state or
remain open. Failed capture faults publication until Clear, so callers cannot
publish the accumulated prefix after catching an unsupported/invalid input.
Visual/picture recursion shares a 256-level bound. Normal primitive/clip encoding
still uses the existing builder and its coverage limits; this does not qualify
all input families or replace the outstanding native host-query connection.

Original provenance is `Compositor.CompileVisualTreeCore` command ordering,
`ResolveHitTestTransform` placement, `AddVisualHitTestBoundsSubtree` clip ordering,
`GpuPicture` retained storage and `GpuRenderCommandHitTestCacheBuilder` encoding.
The native counterpart is the already connected MIL source-geometry/layer mode
and canonical scene 9811 fixtures; C++ has no corresponding managed-visual early
return to change. No native/shader/ABI edit is required for this connection.
Managed fixtures now cover the retained zero-opacity path, real clips/owners,
pictures/images, empty updates, disabled input, generic policy, bounded cycles,
failure/reset, and a compositor fixture asserting no source rendering at zero
alpha. They are authored/compiled, not executed parity evidence.

The primary references above were revisited for this connection. WebRender's
separation of scene/spatial state motivates retaining input independently from
raster culling; Skia's [canvas scopes](https://api.skia.org/classSkCanvas.html),
Direct2D/Win2D layers and Vello inform balanced composition metadata. ProGPU's
existing algorithms supply the implementation, not those engines' source code.
SkParagraph/Parley/HarfBuzz layout/shaping reuse, startup, worker scheduling,
font/fallback/variation state, DPI/hinting, atlas keys/eviction and device lifetime
are unchanged. Input capture does not eagerly render or upload invisible content.
Axis-preserving rectangular clips keep the allocation-free bounds path; rotated
rectangles use actual four-edge ProGPU paths, and failed geometry-clip encoding
cannot degrade into bounds. Scheduling is O(V + C) plus existing primitive/clip
encoding and the existing embedded-version tracker's O(E squared) worst-case
identity lookup for E distinct embedded visuals, with O(D) bounded traversal
storage and existing O(E) retained version storage. The ordered scope/tree metadata is dependency-bound; numeric
placement retains the existing intrinsic vector operations. GPU query execution
and configurable fallbacks are unchanged. Final matched performance, output,
resource-lifetime and cross-platform qualification remain required.

## Image and glyph-run producer connection

### Logical image scopes

Native `save(state, local_hit_rectangle)` now records an optional source-owned
rectangle for a complete balanced save/restore scope. It copies metadata only
with an active owner; restore publishes the exact closing command index. The
encoder emits one rectangle with the saved state and skips internal rendering
commands, including nested logical scopes. It does not substitute a bounds
rectangle for arbitrary geometry: only a source operation explicitly declaring
rectangle input semantics may use this API. Outer masks/guidelines still reject;
internal drawing clips/effects do not redefine an image's input contract.

Canonical MIL uses this for DrawingImage DrawImage lowering, including an empty
source drawing, so flattening an ellipse or sparse drawing no longer turns the
image destination into narrower geometry coverage. Ordinary rendering requests
do not acquire extra scopes. No C ABI or scene-stream layout changes; C++ static
consumers rebuild for the optional save parameter. Sparse annotations grow
geometrically, reuse capacity at reset, and add O(I) storage for I annotated
scopes. Save/restore matching is constant-time stack metadata; index generation
skips each scope in O(1), retaining the existing SIMD rectangle placement.

Paired applicability: the existing managed DrawTexture encoder has destination
rectangle semantics and its explicit-ID fixture shares the native scope values.
The paired managed command now carries `IsImageHitTestScope` on its existing
destination PushClip. Both compact rectangle-clip and general retained snapshots
preserve it. The hit cache emits the source rectangle under outer clip/opacity,
then tracks nested clip depth without indexing internal drawing commands or
altering outer opacity/clip state. Unclosed scopes reject index publication;
Clear drops their state. Direct compositor-owned clip calls obey the same depth.
No extra rendering primitive or command enum is introduced. Managed capture
remains O(C), with O(1) additional scope state and existing SIMD placement.

LibreWPF product sinks publish this through `IWpfImageHitTestScopeCommandSink`
from `WpfDrawingReplay.TryReplayDrawingImage`, including authoritative empty
drawings. DrawingImage's source getter returns false for absent Drawing; that
case retains rectangle input. A true result with null drawing is inconsistent
and rejects, as do unavailable bounds (the separate bounds getter's false).
Bounds/diagnostic/native-WPF sinks keep their ordinary drawing behavior and do
not produce this managed GPU index. Fixtures cover compact and general snapshot
round trips, internal clip/opacity isolation, empty scopes, cleanup and actual
product sink replay. Native input remains disabled in the host until remaining
application coverage and routing are complete. These fixtures are not runtime
or renderer-output qualification.

The research references above were revisited for this boundary: WebRender's
separate picture/spatial/clip ownership supports retaining source input metadata
outside raster details; Skia save/restore and Vello layer scopes inform balanced
ownership, not their implementation text. Win2D DrawImage's source/destination
contract does not define WPF hit behavior. SkParagraph, Parley and HarfBuzz stay
unchanged: paragraph interaction is not part of an image scope. Source WPF's
HitTestDrawingContextWalker.DrawImage is the behavioral authority for the
destination rectangle, including empty DrawingImage content. No third-party
implementation was ported. Final paired runtime and performance gates remain.

Image input covers each actual destination quad, not the command's culling
envelope. The native encoder uses the existing semantic image payload reader and
the same patch → image → state transform order as rendering. Source/storage
format, sampling mode, cubic coefficients, color matrices and external/picture
resource ownership stay on their existing paths; input does not inspect pixels.
Coalesced draws keep their common owner, and an owner change stops coalescing.
Patch gaps and rotations retain exact rectangle coverage in local coordinates.
The managed retained command cache now follows the same per-patch rectangle
contract, including empty patch batches, instead of one enclosing bounds hit.

Glyph-run input is a different source contract: WPF's point and region drawing
walkers use the ink rectangle including baseline, not outline holes. Native MIL
passes canonical `ManagedBounds` to `draw_glyph_run` separately from its transformed
render/culling bounds. The optional local ink pointer is copied synchronously
into sparse builder-owned command metadata only when a hit owner is active.
It does not enlarge every draw record or add storage/allocations to ordinary
unowned rendering. The source rectangle is transformed once with active state;
raster padding, hinting, synthetic glyph passes and font-size estimates never
replace it. Missing metadata rejects index production; an explicitly empty ink
rectangle contributes no hit. Reset clears the metadata. Direct shaped-text
callers without source hit bounds remain unsupported by this option.

LibreWPF native compilation now requires `HasInkBounds`; legacy size/layout-only
descriptors fail rather than supplying guessed input coverage. The source-built
GlyphRun already publishes this typed contract. The managed retained cache uses
the same precise rectangle primitive when a glyph command supplies bounds; its
older raw-string/position-only estimates are not copied into native production
and are not qualified text-input parity. Source text caret/cluster/selection
logic remains independent and unchanged.

Behavioral references are source WPF `HitTestDrawingContextWalker.DrawImage`,
`HitTestWithPointDrawingContextWalker.DrawGlyphRun` and
`HitTestWithGeometryDrawingContextWalker.DrawGlyphRun`, plus the DirectWrite/
Direct2D and retained-scene references above. No foreign implementation text was
ported. Original ProGPU image parsing, matrix composition and canonical rectangle
query encoding are shared. Preparation adds O(Q + T) time/storage for Q image
quads and T owned glyph commands; glyph metadata grows geometrically and retains
capacity across reset. SIMD corner transforms remain unchanged; there is no new
CPU pixel work, GPU submission or shader variant. Tests pair native image/ink
records with managed cache output, exercise real canonical MIL/SFNT input, and
reject missing source ink metadata. Execution and performance claims remain
deferred to final qualification.

## Remaining implementation and final qualification

### Closed solid stroke batches — MVP connection, 2026-09-09

Acceptance action: pointer/selection queries on LibreWPF package MVP
`MvpShapePath` (`M 0,40 L 24,0 L 48,40 Z`, width 2, miter join, offset 178/20).
MIL already emits its fill and a closed polyline stroke batch. Native hit capture
now accepts that batch and retains every edge and its closing join. This is an
implementation connection, not a claim that native host queries are enabled.

Original ProGPU provenance is `Backend/progpu_native_geometry_dash.hpp`
`append_polyline` (closed traversal/domain selection),
`Backend/progpu_native_geometry_stroke.hpp` `create_join_triangles` (the actual
renderer join algorithm), and the existing canonical `GpuHitTesting.cs`/WGSL
LineStroke and PathFill representations. Each flat body and join triangle keeps
the source owner and clip. The canonical all-hit query deduplicates owner IDs;
these are index records, not separately blended coverage draws or submissions.
Triangle payloads contain their three real boundary segments, not AABB hits or
antialias-expanded render vertices. Raster rendering is unchanged. Affine joins
use local geometry then the outer transform; conformal joins use the renderer's
world-domain construction, preserving its scale-sensitive miter threshold.

Managed rendering already uses `StrokeJoinGeometry.WriteWpfLineJoin` and retains
closed contours through `StrokeCoverageGeometry.TryPrepareLinearPath`. No new
managed stroker or shader is needed. Matched managed/native fixtures use the
MVP's contour for miter, bevel and round joins, with an independent apex-miter
oracle. Native fixtures additionally exercise source ownership, anisotropic
placement, actual state clips, ignored closed endpoint caps, transactional
rejection after a supported batch, and canonical MIL fill-plus-stroke scene 9815.
These are authored fixtures; execution stays in the final qualification phase.

This uses the existing engine research record above. The public
[Direct2D stroke containment contract](https://learn.microsoft.com/en-us/windows/win32/api/d2d1/nf-d2d1-id2d1geometry-strokecontainspoint(d2d1_point_2f_float_id2d1strokestyle_constd2d1_matrix_3x2_f_float_bool))
was revisited: stroke style and transform remain inputs, not bounds substitutes.
No third-party implementation was copied. Retained/input ownership stays
independent of raster effects; text engines and shaping caches are unaffected.

For E closed edges, preparation adds O(E) time and O(E) retained records, bounded
by one line plus eight join triangles (24 segments) per edge, before the existing
quadtree build. Scratch is eight stack triangles. Existing NEON/SSE2 line metrics
and four-corner placement are shared. The existing join helper's bounded scalar
topology/math is reused unchanged, not claimed as newly SIMD-qualified. There is
no CPU pixel readback, outline rasterization or additional managed/native call.
Round joins retain the renderer's existing eight-triangle quality bound; this
does not establish mathematical-circle or native-Windows pixel parity.

Open/dashed/spline/device-width batches and degenerate edges remain explicit
unsupported inputs; none can publish a successful partial index. Other semantic
geometry, exact geometry clips, effects/caches and host query routing still need
application closure. No ABI, module interface, shader, or `progpu_native_mil.cpp`
source-digest change is involved. Full platform/module/package/CI gates remain.

1. Complete retained hit primitives and the shared C++ `hit_test_index` from MIL visual
   traversal. IDs must be source visual handle bits, not resource IDs or draw
   ordinals. Reuse semantic fill/stroke/path/text preparation; preserve visual
   and nested render-data clipping, transforms, ordering and source input state.
2. Preserve that index independently of bitmap-cache/effect rendering shortcuts.
   Brush-source replay must not invent independent hits. Boolean clips need real
   topology, not their AABB; index-format/shader gaps must remain explicit.
3. Route native host point/all-owner/region queries and popup diagnostics through
   the presented snapshot. Preserve bounded caller buffers and neutral geometry
   candidate DTOs. Address synchronous source callbacks versus asynchronous GPU
   queries without silently invoking managed rendering or returning stale hits.
4. Run source-owner unit cases and the existing package consumer's new native GPU
   owner/generation fixture, then Toolkit/AvalonDock input and the unchanged full
   SDK application gates on final packages. Include device recovery, popup
   replacement, clipped selection and representative latency/allocation runs.

The package consumer fixture uses an explicitly built native index to exercise
the ownership boundary, not a MIL-produced index. Its success cannot close item 1.
All execution remains deferred until feature freeze; compilation is recorded in
the LibreWPF implementation report.
