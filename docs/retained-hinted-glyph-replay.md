# Retained hinted glyph replay

This explicit consumer API preserves one original prepared paragraph generation.
It is implementation work, not Display selection, source-editor/input admission,
native/package qualification or an application performance claim.

## Primary-source design gate (2026-10-01)

- [Skia shaped text](https://docs.skia.org/docs/dev/design/text_shaper/),
  [SkParagraph public contract](https://github.com/google/skia/blob/main/modules/skparagraph/include/Paragraph.h)
  and [Skia font-scaler separation](https://skia.org/docs/user/tips/) separate
  positioned text from glyph rendering. Adopt original positions and source
  identity; reject re-shaping and a second font decoder at replay.
- [DirectWrite/Direct2D](https://learn.microsoft.com/en-us/windows/win32/direct2d/direct2d-and-directwrite)
  separates reusable layout/interaction from rendering and supports spatial
  glyph brushes. [Win2D CanvasTextLayout](https://microsoft.github.io/Win2D/WinUI3/html/T_Microsoft_Graphics_Canvas_Text_CanvasTextLayout.htm)
  exposes retained formatted text. Adopt retained lifetime and paint separation;
  adapt spatial paint to ProGPU's existing white glyph mask plus one ordinary
  brush draw, preserving the caller's brush domain. Reject automatic platform
  measuring/rendering-policy substitution.
- [WebRender font-instance contract](https://doc.servo.org/webrender_api/font/struct.FontInstanceOptions.html)
  keeps rendering flags in instance identity. Its [historical overview](https://github.com/servo/servo/wiki/Webrender-Overview)
  describes retained display lists, viewport visibility and atlas/quad batching;
  it is historical design context, not proof of today's implementation. Adapt
  exact immutable generation/outline identity to the existing atlas cache and
  culling; reject assuming a glyph ID alone determines physical coverage.
- [Vello architecture](https://github.com/linebender/vello/blob/main/ARCHITECTURE.md)
  and [renderer overview](https://github.com/linebender/vello) distinguish
  production-oriented CPU/GPU rendering from the experimental compute renderer.
  [Parley concepts](https://github.com/linebender/parley/blob/main/doc/concept.md)
  distinguish itemization and shaped runs. Preserve ProGPU's existing selectable
  CPU/SIMD/compute/shader algorithms rather than importing a foreign renderer,
  changing defaults or claiming compute is invariably faster.
- [HarfBuzz shaping contract](https://harfbuzz.github.io/what-does-harfbuzz-do.html)
  reinforces that shaping owns script-specific substitution/positioning. Keep
  the original logical/source maps, fallback runs and explicit variable instance
  under the originating read lease; replay performs no Unicode/font execution.

No foreign implementation is copied. These are architectural comparisons; the
production algorithms reused here are original ProGPU GlyphAtlas/Text.wgsl,
native glyph execution and MIL brushed-glyph coverage composition.

## Decisions across the complete rendering contract

| Concern | Consumer decision |
| --- | --- |
| Startup and pipeline initialization | Preparation allocates immutable records; existing raster pipelines remain lazy until real work. |
| Shaping/layout and display-list reuse | One original producer read lease survives caller disposal; every occurrence, including no-ink and repeated descriptors, is retained. |
| Visibility | Cull only actual physical padded raster bounds mapped into the logical frame. Brush bounds and source input are separate. |
| Cache keys/eviction | Geometry identity plus original outline slot; reuse existing atlas eviction/residency/generation, not a parallel unbounded coverage cache. |
| Upload and batching | Upload selected original segments on demand through existing staging; use existing CPU/SIMD/compute/shader coverage and draw batching. |
| Worker preparation | No new worker scheduling or native context concurrency; preparation borrows the existing destruction-excluding producer read lease. |
| DPI, phase and hinting | Exact prepared DPI, scale one and phase zero at raster replay; original positions remain unsnapped. Nonidentity bases and static zoom stay explicit gates. |
| Fallback/variables | Preserve producer-selected font/face/axes and every source owner. Never substitute design outlines or fallback after atlas failure. |
| Device loss/atlas generations | Existing atlas lifetime remains authoritative; failed or abandoned encoders invalidate unpublished physical coverage. |
| Paint | Solid glyph styles or canonical glyph coverage masked spatial paint; source Rect remains unchanged, with original outer opacity/clip/blend. |

## Required evidence before integration

Matched cold/first-interaction timings, sustained-scroll percentiles/worst frames,
allocation/residency measurements, raw GPU differential/image controls, browser
AOT and complete stock/Dawn JIT/NativeAOT package cases remain required. Run them
against the same final binaries after implementation stabilizes; no measurement
or success is inferred from source review, existing CI or pipeline acquisition.
Static-buffer refresh and optional incremental pages need their own transactional
producer/atlas ownership contracts before those routes are admitted. Ordinary
compiled-scene retained replay must preserve
the full brush contract without weakening assertions, deadlines or defaults.
