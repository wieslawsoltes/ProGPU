# Highest-priority performance fix restoration

## Scope

This change restores four performance contracts that were lost while removing the
former mainline scene-fragment work:

1. record the designer dot grid as one analytic command;
2. invalidate `ScrollViewer` only when scrollbar hover state changes;
3. prune inactive animation subtrees and update sample animations from a registry;
4. reuse the Avalonia drawing-state stacks through a bounded pool.

The implementation is clean-room with respect to external engines. Their public
contracts and architecture informed the design; no source text or organization is
ported.

The attempted retained DataGrid/PropertyGrid row visual was removed after matched
profiling showed that it regressed scrolling. The current incremental page key
includes the visual's global transform, so every scroll translation missed the page
cache and recompiled an overscanned command stream. Direct visible-row recording
remains the correct current implementation until the compositor has a proven
late-bound page-transform contract.

## Primary-source research

- [Skia `SkPicture`](https://api.skia.org/classSkPicture.html) and
  [`SkPictureRecorder`](https://api.skia.org/classSkPictureRecorder.html) retain a
  reusable command stream with cull bounds. Adopted: immutable grid recording.
  Rejected for DataGrid scrolling because ProGPU pages currently bake placement.
- [Skia `SkCanvas`](https://api.skia.org/classSkCanvas.html) supports picture replay
  and quick rejection. Adapted: keep direct visible-row culling with ProGPU's typed
  command and clip contracts.
- [SkParagraph `ParagraphCache`](https://skia.googlesource.com/skia/+/7a1bf999357aa755768f7b72265b91eea5c2758c/modules/skparagraph/src/ParagraphCache.h)
  separates reusable shaped/layout results from drawing. Adopted: row retention does
  not alter shaping or glyph-cache ownership; no additional row command cache is
  introduced.
- [DirectWrite/Direct2D repeated text layout](https://learn.microsoft.com/en-us/windows/win32/direct2d/how-to--draw-text)
  recommends reusing `IDWriteTextLayout`, while the
  [Direct2D quickstart](https://learn.microsoft.com/en-us/windows/win32/direct2d/direct2d-quickstart)
  recommends minimizing resource creation. Adopted: bounded drawing state.
- [Direct2D `DrawImage`](https://learn.microsoft.com/en-us/windows/win32/api/d2d1_1/nf-d2d1_1-id2d1devicecontext-drawimage%28id2d1image_constd2d1_point_2f_constd2d1_rect_f_d2d1_interpolation_mode_d2d1_composite_mode%29)
  applies target offset at draw time. Rejected for the current DataGrid because
  incremental scene pages do not yet expose an equivalent late-bound transform.
- [Win2D `CanvasVirtualControl`](https://microsoft.github.io/Win2D/WinUI3/html/T_Microsoft_Graphics_Canvas_UI_Xaml_CanvasVirtualControl.htm)
  and its
  [invalid-region event](https://microsoft.github.io/Win2D/WinUI3/html/E_Microsoft_Graphics_Canvas_UI_Xaml_CanvasVirtualControl_RegionsInvalidated.htm)
  constrain work to visible/invalid content. Adopted: virtualized visible-row
  recording and dependency-sensitive invalidation.
- [WebRender picture caching](https://github.com/mozilla/gecko-dev/blob/master/gfx/wr/webrender/src/picture.rs)
  tracks primitive, clip, image, opacity, and transform dependencies per cached tile.
  Rejected: tile-cache and compositor-surface complexity for a single control, and
  transform-keyed pages cannot accelerate continuously translated rows.
- [Vello `Scene`](https://github.com/linebender/vello/blob/main/vello/src/scene.rs)
  retains encoded commands and preserves capacity across reset. Adopted only where
  placement can remain stable; rejected for scrolling DataGrid rows.
- [Parley layout documentation](https://docs.rs/parley/latest/parley/index.html)
  reuses coarse contexts and separates reshaping from line breaking/alignment.
  Adopted: reuse scratch/state storage and avoid touching text shaping.
- [HarfBuzz buffer implementation](https://github.com/harfbuzz/harfbuzz/blob/main/src/hb-buffer.cc)
  clears logical state while retaining geometrically grown storage. Adopted: clear
  pooled stacks/recordings without discarding bounded backing storage.

## Complexity and correctness contract

- Designer grid recording and compilation are `O(1)` commands, four vertices, and six
  indices; fragment work is fixed `O(1)` with no grid-size-dependent loop.
- Stable scrollbar pointer movement performs `O(1)` hit testing and no invalidation.
- Core animation update visits only active branches: `O(A + H)` for active nodes `A`
  and their active ancestry `H`; a fully inactive root returns in `O(1)`.
- Avalonia drawing state is cleared and reused in `O(S)` for the number of active
  pushed states, with a small bounded pool and retention cap.
- DataGrid keeps direct viewport realization at `O(V * C)` for visible rows `V` and
  columns `C`. It does not record overscanned rows or create transform-keyed
  incremental-page variants while scrolling.

Pixel-affecting state remains part of each cache key: viewport, theme, columns,
selection/hover/edit state, row layout, font, items source/version, and visible
window. Scrollbar, header, focus, and editor overlays remain independently current.

## Validation

Run focused command-count, image, invalidation, animation-pruning, state-pool, and
direct-visible-row tests first, followed by the Release core and headless suites and
`ShaderResourceTests`.

The Data Virtualization benchmark uses 180 warm-up frames, 600 measured frames,
uncapped presentation, scrolling enabled, and a 40-pixel step. Three unprofiled
before/after Release runs and matched 1,200-frame Time Profiler, Allocations plus VM
Tracker, and Metal System Trace captures are retained under
`/tmp/progpu-datagrid-perf-20260728`. The unprofiled median wall throughput recovered
from 184.48 FPS to 379.02 FPS, while median managed allocation fell from 55,540 to
15,669 bytes per frame. The retained-row version emitted 175–183 draws and 560–564
vector vertices; direct visible-row recording returned to 134–135 draws and 368
vector vertices.

### Full view regression census

The same Release branch was also checked against exact `origin/main` binaries in
fresh processes:

- all 55 desktop Samples views completed 60 warm-up and 180 measured frames with no
  rendering or benchmark failure;
- all 70 source-built Avalonia ControlCatalog pages completed the same 60/180
  protocol with zero retained-composition fallback nodes;
- all eight Avalonia-hosted ProGPU samples completed with valid same-device texture
  presentation and populated output; and
- every matched ControlCatalog page kept identical draw, recorded-command, vector
  vertex, and text vertex counts.

Across the 70 exact-main ControlCatalog pairs, mean allocation fell from 4,866.0 to
4,769.8 bytes per frame (-2.0%). The mean per-page compile ratio improved 4.8%, the
mean per-page p99 frame-time ratio improved 5.0%, and mean FPS was unchanged.
Alternating repeats cleared the five apparent allocation outliers. Animated Custom
Drawing performed more backend renders in its first branch sample; normalized to an
actual backend render, allocation was unchanged at about 41.05 KiB while p99 improved
4.5%.

The desktop Samples outliers were also repeated in alternating order. Their command
structure remained unchanged and no repeatable compile or allocation regression
remained. Drawable-acquire stalls were kept separate from compositor cost. In the
embedded Avalonia DataGrid repeats, median FPS was unchanged and allocation fell
5.9%. Three longer 120-warm-up/600-measured-frame MotionMark pairs converged to
119.87 versus 120.07 FPS (-0.2%), 2.4% lower compile time, and 6.2% lower allocation.
No additional highest-priority restoration was accepted from a one-process outlier.

Matched macOS Instruments captures also exercised the source-built ControlCatalog
Buttons page for 120 warm-up and 1,200 measured frames using exact-main and branch
Release binaries. All six Time Profiler, Allocations plus VM Tracker, and Metal
System Trace targets exited successfully, rendered all 1,200 frames, and reported
zero retained-composition fallback nodes with identical draw, command, vector, and
text counts. Branch FPS stayed within 0.7% of exact main. Allocations plus VM Tracker
and Metal System Trace runs measured 4.8% and 5.2% fewer allocated bytes per frame,
respectively; physical footprint and Metal allocation showed no material residency
change. The Time Profiler allocation counter moved in the opposite direction and is
treated as instrumentation variance rather than a causal claim. Raw traces, exported
tables of contents, benchmark output, and exit-status records are retained under
`/tmp/progpu-avalonia-pool-perf-20260728`.

The census reports and alternating-repeat evidence are retained under
`/tmp/progpu-restored-sample-census-20260728`,
`/tmp/progpu-restored-controlcatalog-all-20260728`,
`/tmp/progpu-restored-avalonia-samples-all-20260728`, and their matched
`progpu-main-*` and `*-outlier-repeats-*` directories.
