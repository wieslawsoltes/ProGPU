# Native MIL hit-test ownership

## Core application dependency

LibreWPF Toolkit/AvalonDock clicking and selection use retained owner queries.
The native MIL host presents a C++ compiled scene, but its host query methods
currently ask the managed compositor for an index. The native MIL compiler does
not yet emit a hit-test index. Existing source-owned geometric input fallback is
not evidence that the retained native owner-query gate is complete.

This implementation establishes the result/owner ownership boundary. It does
**not** close native MIL hit-test emission or host point/region query routing.
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

## Remaining implementation and final qualification

1. Emit retained hit primitives and a shared C++ `hit_test_index` from MIL visual
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
