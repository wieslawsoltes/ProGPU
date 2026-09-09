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
centered analytic strokes, plain filled paths with original line/quadratic/cubic/
arc segments, image destination quads, source-owned glyph ink rectangles, affine
placement and exact rectangular world clips. Bounds serve
only broad-phase pruning; analytic/path parameters and clip edges remain available
to the canonical query shader. Semantic path fill-rule values are explicitly
converted to the opposite `ProGPU.Vector.FillRule`/hit-shader numbering.

Remaining families explicitly reject with `unsupported_hit_test`, surfaced by MIL
as `unsupported_command`: other draw types, layers/effects/masks/cache isolation,
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

## Image and glyph-run producer connection

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
