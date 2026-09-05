# ProGPU.CAD raster IMAGE research and design record

Date: 2026-08-30

Scope: ACadSharp-backed DXF/DWG `IMAGE`/`IMAGEDEF` metadata, immutable ProGPU.CAD
snapshots, retained managed/native rendering, exact selection, editing, printing,
desktop/browser sample reuse, and typed GPU-resource lifetime. This is a clean-room
design record; no third-party implementation text or structure was copied.

## Authoritative contracts consulted

- Autodesk's [IMAGE DXF entity contract](https://help.autodesk.com/cloudhelp/2017/ENU/AutoCAD-DXF/files/GUID-3A2FF847-BE14-4AC5-9BD4-BD3DCAEF2281.htm)
  defines the WCS insertion point, one-pixel U/V vectors, pixel dimensions,
  display flags, brightness/contrast/fade, half-pixel clipping coordinates, and
  outside/inside clipping mode.
- Autodesk's [IMAGEFRAME contract](https://help.autodesk.com/cloudhelp/2026/ENU/AutoCAD-Core/files/GUID-D736B7E4-92C8-41DF-899D-622DB58D09F3.htm)
  distinguishes hidden, displayed-and-plotted, and displayed-without-plot frames.
- Autodesk's [FRAME contract](https://help.autodesk.com/cloudhelp/2020/ENU/AutoCAD-Core/files/GUID-29BD70BB-07BF-41A2-8C2F-AD41C9402486.htm)
  confirms the shared frame override behavior.
- Autodesk's [RASTERVARIABLES DXF contract](https://help.autodesk.com/cloudhelp/2020/ENU/AutoCAD-DXF/files/GUID-DDCC21A4-822A-469B-9954-1E1EC4F6DF82.htm)
  defines persisted frame, display-quality, and insertion-unit fields.
- Skia's [drawImageRect API](https://api.skia.org/classSkCanvas.html) keeps image
  identity separate from per-draw source/destination rectangles, sampling, paint,
  clip, and transform state.
- Skia's [text API overview](https://docs.skia.org/docs/dev/design/text_overview/)
  and upstream
  [SkParagraph builder contract](https://github.com/google/skia/blob/main/modules/skparagraph/include/ParagraphBuilder.h)
  separate shaping/layout results from their eventual canvas rendering. They were
  checked for the required text-architecture comparison and are not an IMAGE path.
- Direct2D's [DrawBitmap contract](https://learn.microsoft.com/en-us/windows/win32/api/d2d1/nf-d2d1-id2d1rendertarget-drawbitmap%28id2d1bitmap_constd2d1_rect_f_float_d2d1_bitmap_interpolation_mode_constd2d1_rect_f%29)
  and Win2D's [DrawImage contract](https://microsoft.github.io/Win2D/WinUI2/html/M_Microsoft_Graphics_Canvas_CanvasDrawingSession_DrawImage.htm)
  use retained bitmap resources with per-draw interpolation, opacity/effect, source,
  destination, and transform state.
- Microsoft's [DirectWrite overview](https://learn.microsoft.com/en-us/windows/win32/directwrite/introducing-directwrite)
  confirms that DirectWrite owns Unicode/OpenType shaping, text layout, glyph runs,
  and glyph rendering rather than ordinary raster-image resource composition.
- WebRender's [rendering overview](https://firefox-source-docs.mozilla.org/gfx/RenderingOverview.html)
  informed the separation of immutable display-list image/clip identity from the
  device texture cache and demand-driven upload.
- Vello's upstream [image-resource design](https://github.com/linebender/vello/issues/176)
  and [scene source](https://github.com/linebender/vello/blob/main/vello/src/scene.rs)
  informed the CPU resource-handle/per-draw-instance split. No Vello source was
  transcribed or structurally ported.
- Parley's [scope](https://github.com/linebender/parley), HarfBuzz's
  [shaping concepts](https://harfbuzz.github.io/shaping-concepts.html), and
  [glyph rendering boundary](https://harfbuzz.github.io/glyphs-and-rendering.html)
  were checked as required. They are text layout/shaping systems and do not define
  ordinary CAD raster-image ownership or compositing.

## Adopted ProGPU architecture

`CadRasterImageResource` is immutable CPU metadata only: IMAGEDEF handle, path,
expected pixel dimensions, and loaded state. `CadRasterImagePrimitive` contains
the per-instance origin, one-pixel axes, dimensions, clip range, display/frame/
transparency/quality flags, and brightness/contrast/fade. Shared IMAGEDEF instances
intern one snapshot resource by reference identity. Snapshot work is bounded by
explicit resource, path-length, per-entity clip, and document clip limits.

`ICadRasterImageSourceResolver` maps the immutable request to the existing
`IProGpuTextureLeaseSource` contract. `CadRasterImageCatalog` performs only bounded
dictionary lookup during scene compilation; file/network access belongs to a host
registration stage. `CadEncodedRasterImageSource` copies a bounded encoded payload,
checks metadata and decoded-pixel limits before decoding, decodes once, and lazily
creates at most the configured number of per-device textures. No decoding, path I/O,
upload, or managed/native crossing occurs during stable picture replay.

The retained draw reuses ProGPU's production `DrawTexture`/`ImageEffect`, typed
texture leases, even-odd geometry clips, opacity stack, and canonical shared image-
effect shader. Source row zero is mapped to the CAD visual top while the persisted
insertion remains the visual lower-left. Normal clips retain the polygon; inverted
clips retain an even-odd outer rectangle plus polygon hole. High quality maps to
linear sampling and draft quality to nearest sampling. Fade mixes toward the
snapshot drawing background; disabled source transparency forces alpha to one.

The IMAGE frame independently honors persisted RASTERVARIABLES screen/plot policy
and the entity's color, lineweight, and simple/complex linetype. Exact point and
Window/Crossing selection reuse the original repository-owned WIPEOUT pixel-plane
algorithm in `CadSnapshotCompiler.Wipeout.cs`, `CadWipeoutSelection.cs`, and
`CadLineTypeLowerer.cs`; the IMAGE adaptations record that exact in-repository
provenance. Generic ACadSharp transforms,
duplicate, Undo/Redo, and DXF/DWG save/load remain authoritative for editing and
persistence. ACadSharp's feature branch additionally preserves RASTERVARIABLES
frame value `3`, units, default displayed/plotted state, and IMAGE group `290`
inside/outside DXF clipping.

## Managed/native applicability audit

This change applies to both renderers. The managed picture records the existing
texture/image-effect and clip commands. The native picture compiler consumes the
same commands, packed effect records, canonical `ImageEffect.wgsl`, texture identity,
clip scopes, sampling, and alpha rules. A missing built-in `ImageEffect` dispatch
case was enabled so its already-present typed native lowering is reachable. No C ABI,
wire record, C++ module, or shader source changed.

Parley, HarfBuzz, SkParagraph, and DirectWrite shaping are non-applicable to ordinary
IMAGE content; ProGPU's existing text stack is intentionally unchanged. Embedded
bitmap/color font glyphs continue through their separate text contracts.

## Verification and measured evidence

- `CadRasterImageTests`: 9/9 in 0.864 seconds on the development host. Coverage
  includes resource interning, half-pixel polygon clips, effects/quality/frame state,
  unloaded/unresolved diagnostics, exact normal/inverted selection, managed/native
  frame replay, printing, move/rotate/scale/duplicate Undo/Redo, raw/handle/document-
  relative catalog resolution, bounded input rejection, and DXF/DWG round trips.
- ACadSharp `RasterVariablesTests`: 5/5 in 0.373 seconds on the active .NET target,
  including IMAGE group-290 DXF conformance and RASTERVARIABLES DXF/DWG fidelity.
- Release `CadRasterImageRenderTests`: 2/2 in 1 second including headless device startup.
  The matched 96/192-DPI scenes retain the same texture, source/destination geometry,
  and image transform; two zoom replays retain one resource identity; native picture
  compilation succeeds; disposing the catalog and scene recordings does not release
  the texture until the final retained picture/replay leases are released; cancellation
  after acquisition transactionally releases the incomplete recording's lease.
- The complete Release `ProGPU.CAD.Tests` assembly passes 695/695 in 6 seconds after the
  public-API compatibility and transactional lease cleanup audit.

These focused durations are test-run observations, not p50/p95/p99 rendering
benchmarks. No FPS, latency, upload-throughput, or memory improvement is claimed.
The structural steady-state contract is one decoded CPU payload, at most one upload
per active device domain, one retained resource identity per picture regardless of
IMAGE instance/zoom replay count, zero per-frame decode, and zero retained re-upload.
Broader cold-start, sustained-scroll percentiles, device-loss stress, browser AOT
render smoke, image-quality goldens, color-profile behavior, and matched Instruments/
Metal evidence remain required before making production performance claims.

## Rejected or deferred alternatives

- Snapshot-owned decoded pixels or GPU textures were rejected because snapshots are
  device-neutral, browser-safe immutable CPU state.
- File reads in snapshot/scene/replay were rejected; hosts register bytes or typed
  sources explicitly and may perform asynchronous discovery beforehand.
- Per-instance textures were rejected in favor of interned IMAGEDEF identity and
  typed leases.
- CPU readback, per-frame upload, a CAD-only shader, and a CAD-specific native ABI
  were rejected because ProGPU already owns equivalent production paths.
- View-dependent raster resampling caches, mip generation policy, ICC/color-space
  conversion, animated/multi-frame formats, external URL fetching, and automatic
  filesystem watching remain deferred until their typed ownership, security, and
  invalidation contracts are designed and measured.
