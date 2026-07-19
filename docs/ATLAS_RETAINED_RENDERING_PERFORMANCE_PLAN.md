# Atlas retained-rendering performance plan

## Decision

The Vello-inspired Wavefront/compute-vector direction that began at
`e5b4c61901db4c984202badd31b663b229f546c3` is rejected. Commit `f6db73f` restores
the tracked tree exactly to its parent, `f5ca7c51dbcd9ab6cdd7614c199d4e9a3673562e`.
The replacement performance work keeps ProGPU's existing high-quality analytic
path atlas, glyph atlas, retained UI tree, and WinUI-compatible layout/input model.

The measured problem is CPU scene rebuilding and data movement, not insufficient
GPU raster throughput. At the restored baseline, the browser spends only
0.05-0.11 ms in the render pass but 0.95-2.97 ms compiling a scene whose root
version changes on every scroll frame. A new vector rasterizer cannot remove that
cost. The next architecture must retain local drawing output, keep it in stable
GPU storage, and update only placement and newly recycled virtualized slots.

## Non-negotiable compatibility and quality boundaries

- Measure, arrange, dependency properties, routed input, focus, theming,
  virtualization behavior, scroll offsets, hit testing, and public
  `DrawingContext` APIs remain WinUI-compatible.
- Shaping remains a complete CPU Unicode/OpenType operation. Shaped glyph IDs,
  clusters, advances, offsets, fallback choices, variation coordinates, and
  feature state are retained and reused; scrolling never substitutes a GPU text
  approximation.
- Existing physical-framebuffer sizing, DPI-aware raster size, quarter-physical-
  pixel text phase, analytic 8x8 winding coverage, path/glyph atlas keys, and
  premultiplied blending remain unchanged.
- No page may improve by suppressing required invalidation, lowering AA quality,
  dropping glyphs, flattening WinUI layout, or moving ordinary UI layout into a
  custom renderer.
- Atlas generation changes, resize/DPI changes, device loss, mutable effects,
  clips, external layers, and unsupported commands invalidate or fall back in the
  same frame. No buffer overflow silently truncates output.
- Every optimization is a controlled A/B against the same AOT workload. A patch
  that does not improve the three-run median without worsening range, allocation,
  queue depth, quality, or correctness is reverted.

## Reproducible restored baseline

Two clean Release browser-AOT artifacts were used during the rollback. The
restored artifact was built from `f6db73f`; only its `/tmp` copy received the
query-to-environment benchmark shim that is now implemented in source by
`7d5cd72`. Headed Chromium ran at 1280 x 720 CSS pixels / 2560 x 1440 physical
pixels, DPI 2, VSync disabled, 120 warm-up frames, 600 measured frames, and a
40-logical-pixel alternating scroll step. Each page ran three times.

| Page | Wall FPS runs | Median | Compile median | Allocation median | Scene reuse | Final work |
|---|---|---:|---:|---:|---:|---|
| Data Virtualization | 225.30, 226.09, 225.31 | 225.31 | 2.9283 ms | 32,912 B/frame | 0/600 | 59 draws, 356 vector, 1,304 text vertices |
| Font Glyph Browser | 212.41, 214.74, 217.98 | 214.74 | 2.4073 ms | 81,857 B/frame | 0/600 | 95 draws, 752 vector, 965 text vertices |
| Inter Typeface | 281.16, 279.12, 274.73 | 279.12 | 1.0990 ms | 30,780 B/frame | 0/600 | 18 draws, 356 vector, 868 text vertices |

All final cache misses report `Root version changed`. Median GPU render-pass work
is below 0.11 ms, while compilation is 10-40 times larger. Maximum compile frames
reach 50 ms on Data and 38-40 ms on Glyph Browser. This is the primary target.

An isolated single-line text-layout fast path was also measured and rejected. It
changed the medians to 223.29, 218.09, and 267.46 FPS respectively: Data regressed
0.9%, Inter regressed 4.2%, and Glyph improved only 1.6%. The patch was reverted
by `e9a93f7`. Shaping micro-optimization cannot compensate for whole-scene rebuild.

## Primary-source architecture comparison

### Skia and SkParagraph

Skia separates recorded drawing (`SkPicture`), positioned glyph runs
(`SkTextBlob`), current matrix/clip state, and GPU-managed texture/font caches.
`drawAtlas` accepts a stable atlas plus per-sprite transforms rather than baking a
new texture for each placement. ProGPU adopts this separation as local retained
fragments plus a placement table, while retaining ProGPU's stronger typed command
and invalidation contracts.

- [Skia API overview: SkPicture and SkTextBlob](https://skia.org/docs/user/api/)
- [SkCanvas drawAtlas API](https://api.skia.org/classSkCanvas.html)
- [Skia GPU surfaces and managed texture/font caches](https://skia.org/docs/user/api/skcanvas_creation/)
- [Skia shaped-text two-step model](https://skia.org/docs/dev/design/text_shaper/)

### DirectWrite, Direct2D, and Win2D

DirectWrite keeps text layout separate from rendering; a reused layout retains
glyph positions and emits complete glyph runs. Direct2D applies the current world
transform when `DrawGlyphRun` executes. Win2D owns device-dependent resources and
recreates them on device loss. ProGPU therefore keeps shaped runs immutable,
places them through a separate transform record, and rebuilds GPU arenas after
device loss without reinterpreting text.

- [DirectWrite/Direct2D text architecture and cached layout positions](https://learn.microsoft.com/en-us/windows/win32/direct2d/direct2d-and-directwrite)
- [DirectWrite glyph-run analysis with transform, DPI, grid fit, and AA state](https://learn.microsoft.com/en-us/windows/win32/api/dwrite_2/nf-dwrite_2-idwritefactory2-createglyphrunanalysis)
- [Win2D device-resource lifetime](https://learn.microsoft.com/en-us/windows/apps/develop/win2d/loading-resources-outside-of-createresources)

### WebRender

Firefox incrementally rebuilds display lists, retains a scene larger than the
current viewport, culls it into a frame, and prepares glyphs in an atlas. Related
content is grouped into slices expected to update together. ProGPU adapts those
ideas as retained visual fragments, overscanned virtualized slot sets, and bounded
dirty-slice uploads. It does not adopt WebRender's IPC serialization because the
browser AOT host already uses a typed in-process scene and a coarse binary WebGPU
command transport.

- [Firefox rendering and WebRender overview](https://firefox-source-docs.mozilla.org/gfx/RenderingOverview.html)

### HarfBuzz and Parley

HarfBuzz explicitly supports cached shape plans and reusable buffers; its output
is glyph IDs and positions that a graphics library consumes. Parley shares
`FontContext` and `LayoutContext` scratch state and can rebreak/re-align a retained
layout without reshaping unchanged text. ProGPU already follows this CPU-result
boundary and must keep doing so: cache complete shaping plans/results by font,
script, language, direction, feature ranges, variations, and text identity; do
not repeat character-map or shaping work during compositor replay.

- [HarfBuzz shaping and output buffers](https://harfbuzz.github.io/shaping-and-shape-plans.html)
- [HarfBuzz shape-plan caching](https://harfbuzz.github.io/shaping-plans-and-caching.html)
- [Parley shared font/layout contexts and reusable layouts](https://docs.rs/parley/latest/src/parley/lib.rs.html)

### Vello decision

Vello's compute-centric vector renderer remains a useful comparison required by
the repository research gate, but it is not the implementation plan. Its own
project currently calls out incomplete GPU memory allocation and glyph caching,
and warns that the web is not its primary target. ProGPU's browser measurements
showed valid Wavefront trials at only 160.78-172.89 FPS. The adopted lesson is
only to minimize scene encoding and data movement; ProGPU will not port Vello's
path-count/tile/coarse/fine compute pipeline.

- [Vello official repository and current limitations](https://github.com/linebender/vello)

## Target architecture

```text
WinUI visual tree and layout
        |
        | content/version changes only
        v
retained local fragments ----> immutable shaped glyph runs / analytic primitives
        |                                      |
        | stable slot assignment               | atlas keys unchanged
        v                                      v
persistent vector/text/index arenas      glyph/path atlases
        |
        +---- painter-ordered batch descriptors
        |
scroll/animation placement versions
        v
compact GPU placement table + dirty intervals
        |
        v
existing Vector/Text shaders and render pass
```

### Version model

`Visual.ChangeVersion` remains the correctness umbrella used by existing code.
The retained compiler adds independently observable versions without weakening
that umbrella:

- `ContentVersion`: commands, geometry, text, brush/material, opacity, clip,
  effect, child identity/order, visibility, or atlas-relevant state changed.
- `PlacementVersion`: offset/translation or supported affine placement changed
  while local commands and child ordering did not.
- `LayoutVersion`: measure/arrange inputs changed. This still follows WinUI and
  may advance both content and placement when resulting pixels require it.
- `AtlasGeneration`: UV contents moved or were cleared.

A root `ChangeVersion` mismatch no longer implies a full rebuild. The compiler
walks a compact dirty chain and proves whether each changed node is placement-
only, content-local, structural, or unsupported. Any uncertainty uses the exact
full compilation path.

### Retained fragment

Each eligible visual owns a typed fragment descriptor:

- source visual identity and captured content version;
- fixed vector vertex/index and glyph-instance slices;
- immutable painter-ordered draw descriptors;
- brush/gradient references, atlas generations, and path/glyph residency tokens;
- local conservative bounds, clip compatibility, opacity/blend state;
- one placement-table index inherited or owned by the visual;
- explicit eligibility/fallback reason.

The fragment stores local-space data. It never owns layout, input state, or a
rasterized subtree texture. Ordinary solid vector/text content therefore keeps
analytic scaling and subpixel quality at every zoom/DPI.

### GPU placement table

The initial portable record is 64 bytes and naturally aligned:

- two `vec4<f32>` rows encoding a 2D affine transform;
- one `vec4<f32>` effective clip rectangle in physical target coordinates;
- opacity, flags, generation, and padding.

Index zero is immutable identity/no-clip/full-opacity. Existing vertices default
to zero so the first shader/ABI stage is behavior-neutral. `VectorVertex` gains a
32-bit placement index; `GlyphInstance` reuses its existing final 4-byte padding.
The vertex shaders fetch one record and apply it before the unchanged projection,
coverage, texture, gamma, and blend logic. Texture/effect commands remain on the
existing path until they receive equivalent typed support.

### Persistent arenas and fixed virtualized slots

Buffers grow geometrically and never shrink during steady interaction. A slice is
identified by `(arena generation, offset, capacity, used length)`. Recycled UI
containers retain their slice; rebinding a row or glyph card overwrites only that
slot. Adjacent dirty slices are merged only when the bytes between them are also
dirty. There is no min/max upload that retransmits unchanged gaps.

Virtualizing panels continue to calculate the same realized indices and arrange
the same controls. They expose stable slot identity to the renderer, not alternate
layout semantics. A scroll step updates one ancestor placement record. Entering
items rebind existing slots; leaving items become inactive in the next batch
descriptor. No remove/re-add is used merely for z-order.

### Painter-safe batching

The default batch stream preserves exact command order. Adjacent descriptors may
merge when pipeline, material, clip, blend, mask, target, and arena continuity all
match. Non-overlapping-sibling reordering is a later opt-in with an explicit proof
and pixel tests; it is not required for the first performance gate. Small visible
sets use direct draws. GPU cull/indirect setup is considered only when calibrated
draw count makes its dispatch cost beneficial.

## Implementation phases

### Phase A: measurement and correctness foundation

1. Keep deterministic browser query selection in production source.
2. Add GPU-completion and maximum queue-depth telemetry without timestamp-query
   assumptions or per-frame readback.
3. Report upload bytes/write count, dirty fragments/slices, placement writes,
   cache eligibility/miss reasons, draw count, vertices, allocations, median/p95/
   worst frame, and cold-start/first-scroll latency.
4. Add pixel, hit-test, atlas-generation, resize/DPI, device-loss, and mutable-
   visual tests before routing production content.

Exit: metrics are zero-cost when disabled and three-run results are reproducible.

### Phase B: behavior-neutral placement ABI

1. Add placement storage buffer and index-zero identity record.
2. Extend vector/text vertex ABIs and every primary/offscreen pipeline descriptor.
3. Apply identity placement in shaders; keep output byte-identical.
4. Cover all shape types, text modes, clips/masks, static buffers, DXF buffers,
   retained glyph buffers, and device recreation.

Exit: all tests and image comparisons pass; FPS/allocations do not regress by
more than noise before any retained feature is enabled.

### Phase C: split content and placement invalidation

1. Add typed placement/content versions to `Visual` and `LayoutNode`.
2. Classify setters explicitly; preserve `ChangeVersion` propagation.
3. Track the shortest dirty ancestor chain rather than scanning every visual.
4. Patch supported placement records on a compiled-scene hit.

Exit: a transform-only static subtree produces 599/600 cache hits, one bounded
placement write per frame, unchanged hit testing, and identical pixels.

### Phase D: retained local fragments

1. Capture compiler output for ordinary solid vectors and glyph runs in local
   space, keyed by content version and atlas generations.
2. Allocate fixed persistent slices; patch only dirty intervals.
3. Preserve exact fallback for gradients, images, effects, layers, complex clips,
   color glyphs, and mutable `DrawingVisual` until individually supported.
4. Recover synchronously from atlas repack/capacity changes before submission.

Exit: unchanged fragments cause no geometry compilation or vertex upload.

### Phase E: virtualization integration

1. Give active/recycled containers stable renderer slot IDs without changing the
   visible-range or arrange algorithm.
2. Rebind entering Data rows and glyph cards in place.
3. Update one ancestor scroll placement plus only entering slot slices.
4. Keep status/diagnostic text rate-limited and outside the retained content set.

Exit: Data and Glyph scrolling allocate below 4 KiB/frame, have no full-buffer
uploads, and have no missing/invisible cells throughout the sweep.

### Phase F: batching and upload consolidation

1. Store immutable painter-ordered batch descriptors alongside fragments.
2. Merge only adjacent compatible slices.
3. Emit one queue write per adjacent dirty interval and one placement-table write
   interval; record both counts and bytes.
4. Calibrate direct versus indirect replay rather than assuming compute wins.

Exit: draw calls and queue writes fall without wider uploads or reordered pixels.

### Phase G: feature expansion and rollout

Add rect/geometry clips, opacity groups, gradients, images, strokes, color glyphs,
effects, cached layers, static DXF buffers, and external layers one feature at a
time. Each addition needs eligibility rules, fallback, device-loss behavior,
focused pixels, browser shader creation, and A/B evidence.

## Acceptance gates

- Browser AOT, VSync off: every sample page sustains at least 200 FPS; target
  pages exceed their restored medians and progress toward 400 FPS.
- Desktop, VSync off: every visible sample page sustains at least 400 FPS.
- Data Virtualization and Font Glyph Browser: no repeat below the restored median,
  median compile below 0.5 ms, p95 below 2 ms, allocations below 4 KiB/frame,
  and no full vector/text buffer upload after warm-up.
- Inter: equivalent draw/vertex workload, no throughput or allocation regression.
- Queue depth remains at most two; results include GPU-completed rate.
- Cold startup and first interaction do not regress by more than 5%.
- Pixel comparisons preserve physical DPI, AA, subpixel phase, text gamma,
  fallback, variable-font output, clips, opacity, and painter order.
- Unsupported or overflow cases render through the prior exact path in the same
  frame and are visible in metrics.

The 1000-FPS aspiration is not an acceptance claim. At 2560 x 1440, one full
RGBA8 write per frame already requires about 13.73 GiB/s at 1000 FPS before
overdraw, blending, atlas samples, or presentation. Reaching that range requires
preserved/dirty-region presentation support in addition to the retained scene;
quality reductions are not an allowed substitute.
