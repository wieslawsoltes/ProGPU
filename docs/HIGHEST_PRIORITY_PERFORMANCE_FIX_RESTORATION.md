# Highest-priority performance fix restoration

## Scope

This change restores five performance contracts that were lost while removing the
former mainline scene-fragment work:

1. record the designer dot grid as one analytic command;
2. invalidate `ScrollViewer` only when scrollbar hover state changes;
3. prune inactive animation subtrees and update sample animations from a registry;
4. reuse the Avalonia drawing-state stacks through a bounded pool; and
5. retain DataGrid/PropertyGrid row recordings while applying scroll translation at
   replay time through the current incremental compositor.

The implementation is clean-room with respect to external engines. Their public
contracts and architecture informed the design; no source text or organization is
ported.

## Primary-source research

- [Skia `SkPicture`](https://api.skia.org/classSkPicture.html) and
  [`SkPictureRecorder`](https://api.skia.org/classSkPictureRecorder.html) retain a
  reusable command stream with cull bounds. Adopted: immutable row/grid recordings.
- [Skia `SkCanvas`](https://api.skia.org/classSkCanvas.html) supports picture replay
  and quick rejection. Adapted: retain row commands but keep ProGPU's typed command
  cache and clip contracts.
- [SkParagraph `ParagraphCache`](https://skia.googlesource.com/skia/+/7a1bf999357aa755768f7b72265b91eea5c2758c/modules/skparagraph/src/ParagraphCache.h)
  separates reusable shaped/layout results from drawing. Adopted: row retention does
  not alter shaping or glyph-cache ownership.
- [DirectWrite/Direct2D repeated text layout](https://learn.microsoft.com/en-us/windows/win32/direct2d/how-to--draw-text)
  recommends reusing `IDWriteTextLayout`, while the
  [Direct2D quickstart](https://learn.microsoft.com/en-us/windows/win32/direct2d/direct2d-quickstart)
  recommends minimizing resource creation. Adopted: reuse row recordings and bounded
  drawing state.
- [Direct2D `DrawImage`](https://learn.microsoft.com/en-us/windows/win32/api/d2d1_1/nf-d2d1_1-id2d1devicecontext-drawimage%28id2d1image_constd2d1_point_2f_constd2d1_rect_f_d2d1_interpolation_mode_d2d1_composite_mode%29)
  applies target offset at draw time. Adapted: late scroll translation is stored on
  the retained row visual rather than rebuilding row content.
- [Win2D `CanvasVirtualControl`](https://microsoft.github.io/Win2D/WinUI3/html/T_Microsoft_Graphics_Canvas_UI_Xaml_CanvasVirtualControl.htm)
  and its
  [invalid-region event](https://microsoft.github.io/Win2D/WinUI3/html/E_Microsoft_Graphics_Canvas_UI_Xaml_CanvasVirtualControl_RegionsInvalidated.htm)
  constrain work to visible/invalid content. Adopted: virtualized visible-row
  recording and dependency-sensitive invalidation.
- [WebRender picture caching](https://github.com/mozilla/gecko-dev/blob/master/gfx/wr/webrender/src/picture.rs)
  tracks primitive, clip, image, opacity, and transform dependencies per cached tile.
  Adapted: explicit DataGrid row-cache dependencies. Rejected: tile-cache and
  compositor-surface complexity for a single control.
- [Vello `Scene`](https://github.com/linebender/vello/blob/main/vello/src/scene.rs)
  retains encoded commands and preserves capacity across reset. Adopted: retained
  command buffers whose capacity survives rerecording.
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
- DataGrid records `O(V * C)` work only when the visible row set or row dependencies
  change (`V` visible rows, `C` columns). Fractional scrolling within that retained
  window changes only a transform in `O(1)`. Large scrolling realizes a new
  `O(V * C)` window.

Pixel-affecting state remains part of each cache key: viewport, theme, columns,
selection/hover/edit state, row layout, font, items source/version, and visible
window. Scrollbar, header, focus, and editor overlays remain independently current.

## Validation

Run focused command-count, image, invalidation, animation-pruning, state-pool, and
row-reuse tests first, followed by the Release core and headless suites and
`ShaderResourceTests`. A macOS performance improvement is not claimed without matched
Release Time Profiler, Allocations/VM Tracker, and Metal System Trace captures.
