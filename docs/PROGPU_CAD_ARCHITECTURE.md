# ProGPU.CAD Architecture and Delivery Specification

Status: foundation, 2026-08-27

## Scope

`ProGPU.CAD` is the CAD application and engine layer for opening, processing,
editing, rendering, printing, and saving DXF/DWG documents on desktop and in the
browser. ACadSharp owns the file-format object model. ProGPU owns presentation,
interaction, acceleration, text, printing, and platform integration.

The existing `ProGPU.Dxf` package remains available during migration. It is an
approved in-repository source for direct ports into `ProGPU.CAD`; it is not the
new document model. New features target ACadSharp `CadDocument` and must not add
new netDxf dependencies.

## Clean-room provenance

No third-party renderer implementation is copied, ported, translated, or
transcribed. External engines below were consulted only for public contracts,
documented algorithms, architecture, observable behavior, and test ideas.
ACadSharp is consumed as the reviewed MIT-licensed submodule at
`external/ACadSharp`. Its public object model and readers/writers are a normal
dependency, not copied implementation.

Approved ProGPU-owned sources that may be ported directly are:

- `src/ProGPU.Dxf`: the current DXF renderer, retained static-buffer adapter,
  hatch handling, and SAT/SAB readers.
- `src/ProGPU.Scene/Extensions`: spline, hatch, ACIS, 3D line, and retained-DXF
  compositor extensions.
- `src/ProGPU.Scene/Shaders/AcisSolid.wgsl`, `Hatch.wgsl`, `Line3D.wgsl`, and
  `RetainedGlyph.wgsl`.
- `src/ProGPU.Backend/Shaders/GlyphRasterizer.wgsl`, `PathRasterizer.wgsl`, and
  `Text.wgsl`.
- The managed/native scene compilers and shared native C ABI already in this
  repository.

Every direct port must name its exact source file in the change and add matched
old/new differential tests. Foreign file organization, helper names, control
flow, lookup data, and comments are prohibited implementation sources.

## Architectural boundaries

```text
DXF / DWG bytes
      |
      v
ACadSharp readers <--> mutable CadDocument <--> ACadSharp writers
                           |
                  edit transaction + generation
                           |
                           v
              immutable ProGPU.CAD snapshot
                /          |           \
       spatial index   resource table   semantic handles
                \          |           /
                    viewport compiler
                           |
             retained ProGPU scene/resources
                           |
             managed or native compositor
                           |
         screen / browser / bitmap / print target
```

## Implemented immutable-scene slice

The first phase-2 slice is implemented in `src/ProGPU.CAD`:

- `CadSnapshotCompiler` captures the mutable document and matching content
  generation under one lock, resolves visible layers and indexed stroke styles,
  normalizes supported planar entities from OCS to double-precision WCS, and
  emits fixed typed line, circle, arc, ellipse, spline, polyline, solid, and
  3D-face streams.
- `CadSpatialIndex` is an immutable median-split AABB hierarchy. Construction is
  `O(N log N)`; caller-buffer queries allocate no managed memory and are
  `O(log N + K)` on typical spatial data, `O(N + K)` worst case.
- `CadPlanSceneCompiler` rebases WCS coordinates and records an exact top-view
  projection through retained analytic ProGPU commands. Circle projection uses
  the existing affine analytic ellipse primitive, arcs use one `ArcSegment`,
  and NURBS use the retained spline extension. It performs no fixed-detail
  curve tessellation and carries no viewport or camera state.
- Lightweight polylines retain their whole path as one command. Straight and
  positive/negative bulge segments remain analytic in the entity OCS, with a
  checked affine OCS-to-WCS projection. Wide polylines are deliberately reported
  as unsupported until filled-outline lowering lands; they are not confused with
  cosmetic lineweight.
- Legacy 2D POLYLINE uses the owning polyline elevation and OCS normal, ignores
  the historically unused vertex Z value, and shares the same one-path analytic
  bulge representation. Legacy 3D POLYLINE retains an independent packed WCS
  point stream with exact XYZ bounds and records one top-projection path. Width,
  extrusion, and unresolved curve/spline-fit semantics are reported rather than
  flattened or silently treated as centerlines.
- Ellipses and elliptical arcs preserve their WCS major/minor basis, parameter
  sweep, and exact double-precision extrema. The plan compiler records one unit
  analytic ellipse or arc under an affine transform, never a sampled polygon.
- SOLID entities retain exact OCS-normalized corners and render as filled paths.
  3DFACE entities retain WCS corners and invisible-edge flags; the plan
  wireframe records only visible edges while preserving the same face record
  for the later shaded/depth compiler. Nonzero ellipse/SOLID thickness is
  reported until complete 3D side-surface lowering is available.
- Model-space lineweights are recorded as fixed device-space strokes; explicit
  zero-width lineweights use the ProGPU hairline sentinel. Non-continuous CAD
  linetypes currently produce a bounded warning and remain a tracked fidelity
  gap rather than being silently claimed as complete.

The exact approved ProGPU-owned implementation provenance for this slice is
`src/ProGPU.Scene/RenderCommand.cs` (`DrawingContext.DrawLine`, `DrawEllipse`,
`DrawPath`, and `DrawSpline`) and `src/ProGPU.Vector/PathGeometry.cs`
(`PathGeometry`, `PathFigure`, and `ArcSegment`). The new adapter and indexing
algorithms are original ProGPU code based on the public contracts and Autodesk
coordinate specification; no third-party renderer source was used. No shader or
managed/native compositor implementation changed, so the parity audit finds the
native side not applicable to this typed CPU snapshot/recording adapter. Both
compositors continue consuming the same pre-existing retained command contract.

### Document authority

- One `CadDocument` is the semantic authority. Entity handles remain the stable
  identity used by selection, properties, undo, collaboration, and diagnostics.
- Mutations occur through document edit transactions. A successful outermost
  transaction advances one monotonic content generation; a failed or cancelled
  transaction publishes no generation.
- File reads and writes are coarse asynchronous operations. ACadSharp progress
  and non-fatal notifications are translated into typed ProGPU.CAD diagnostics.
- Save never mutates the active document generation. Dirty state compares the
  saved generation with the current content generation.
- A document snapshot is immutable and tagged with its source generation. It
  stores compact, pointer-free typed data needed by rendering and processing;
  it never exposes mutable ACadSharp collections to worker or GPU work.

### Retained rendering

- Document generation, layer/style generation, viewport generation, and device
  generation are independent. Camera-only changes must not traverse or rebuild
  the ACadSharp entity tree.
- The first compiler records analytic paths, splines, hatches, 3D edges, images,
  and shaped glyph runs through existing public ProGPU drawing contracts. The
  second compiler retains GPU resources and submits one scene update per changed
  immutable generation and one render submission per frame.
- Pan, zoom, orbit, and view projection update bounded camera/view uniforms.
  Geometry is not CPU-transformed for each frame. Large world coordinates are
  rebased to a stable local origin in the compiled snapshot while exact double
  coordinates remain in the semantic document and spatial index.
- Visibility is hierarchical: layout/space, frozen or disabled layer, block or
  XRef bounds, entity bounds, then primitive-level work only where needed.
  Selection and snapping share semantic spatial indexes; they do not require a
  duplicate per-frame rendered hit-test scene.
- Device loss invalidates device resources but not semantic snapshots, shaped
  text, or CPU spatial indexes. Atlas movement/repack advances the relevant
  generation and recompiles before moved UVs can be submitted.

### 2D, 3D, and quality rules

- ACadSharp OCS/elevation/extrusion data is normalized with the Autodesk
  arbitrary-axis contract before WCS/view projection. Planar entities are not
  assumed to lie on world Z.
- Curves remain analytic through the existing ProGPU path/spline contracts.
  Tessellation, when a backend requires it, is view-error bounded, cached by
  immutable geometry/style generation, and never substitutes a fixed low-detail
  approximation for deep zoom.
- Model-space lineweight display is cosmetic in device pixels and does not grow
  under zoom. Paper-space and print lineweights use physical units and plot
  scale. Wide polylines remain geometry, not cosmetic lineweight.
- Existing ProGPU winding, DPI, four-phase subpixel snapping, 8x8 high-precision
  coverage, vector glyph cache, color-font, fallback-font, and shaped glyph-index
  contracts remain unchanged.
- TTF/OTF CAD text uses the complete ProGPU Unicode, bidi, fallback, shaping,
  layout, and vector/atlas text stack. SHX is an additional typed font source,
  not a fallback that bypasses shaping or text identity. Its design must cover
  regular, Unicode, Big Font, stacked text, shape references, search paths,
  substitution, missing glyphs, and bounded parsing before implementation.
- 3D content shares the renderer's depth, camera, resource, device-loss, and
  retained submission contracts. ACIS/SAT/SAB acceleration must preserve the
  full boundary representation and may not collapse solids to a wireframe-only
  approximation.

### Managed/native parity and boundary

Every scene or shader feature is audited for both compositors. Shared algorithms
use canonical shader files and matched output tests. Managed/native crossings
are batched by immutable snapshot generation: no per-entity, per-primitive,
per-glyph, or per-table-entry P/Invoke is allowed. Any new wire record begins in
the public C header, is blittable and fixed width, is generated into C#, and is
validated for version, size, alignment, offsets, ranges, and device domain.

## IO and browser policy

- Desktop paths use ACadSharp stream readers/writers through a typed
  `ICadDocumentStore` service. Browser hosts supply streams or random-access
  adapters; engine APIs do not depend on native file dialogs.
- Format selection uses both the requested format and validated content. A file
  extension alone is not a security boundary.
- Reads have explicit limits for file size, object count, recursion, decoded
  string size, decompression, proxy graphics, XRefs, image data, and ACIS data.
  Limits and cancellation are part of the public load contract.
- Saves write a new stream/file and replace the destination only after success
  where the platform permits. Warnings are returned to the caller; unsupported
  or lossy output must never be silently reported as a clean save.
- DWG writer support is capability/version gated because upstream stability is
  not uniform across versions. Round-trip conformance is required before a
  version is advertised as production save support.

## Standalone sample hosts

The shared interactive viewer lives in `ProGPU.CAD.Sample` and is hosted
unchanged by `ProGPU.CAD.Sample.Desktop` and `ProGPU.CAD.Sample.Browser`.
It freezes one generation-tagged scene into an owned `GpuPicture`; wheel zoom
and pointer pan then update only the replay camera matrix. Camera motion never
revisits ACadSharp or recompiles geometry. The representative scene exercises
lines, OCS circles/arcs, analytic ellipses, bulge polylines, NURBS, filled
SOLID, and 3DFACE visible-edge semantics while framework chrome uses dynamic
theme resources.

The desktop host uses the existing ProGPU WinUI/GLFW presentation path. The
browser host uses `BrowserGpuRuntime`, canonical ProGPU browser assets, SIMD,
native Wasm linking, and the same shared viewer assembly. One shared shell now
uses ProGPU's typed `FileOpenPicker`, `FileSavePicker`, and `StorageFile`
contracts to open DXF/DWG bytes through `CadDocumentStore`, rebuild one retained
picture, and save to native paths or browser downloads. Save remains explicitly
labelled uncertified in the UI and opts into development output; an unknown
destination extension is rejected rather than receiving mismatched content.
Staged saves defer their saved-generation commit until the platform storage
write succeeds, so a failed native write or browser download cannot mark the
session clean.

The hosts remain an executable engine-integration baseline, not yet the complete
CAD editor shell: layers/properties, selection, editing tools, printing, and
round-trip-certified output remain tracked application phases.

The Release browser AOT publish succeeds. Its linker audit currently reports
annotation warnings in ACadSharp's initialization/reflection utilities and in
existing UI binding paths. Those warnings remain visible as an AOT-hardening
gate; the typed CAD snapshot, scene compilation, and retained replay paths do
not use reflection.

Exact in-repository host provenance is `src/ProGPU.Samples.Desktop/Program.cs`,
`src/ProGPU.Samples.Browser/Program.cs`, and
`src/ProGPU.Browser/BrowserAssets/`. File workflow provenance is the existing
ProGPU-owned `src/ProGPU.WinUI/Core/Storage.cs`,
`src/ProGPU.WinRT/Windows/Storage/StorageFile.cs`, and
`src/ProGPU.Browser/BrowserStorageServices.cs`. The new programs reuse those
public host/storage contracts and link the canonical browser JavaScript assets;
no third-party host implementation was copied.

The 2026-08-27 desktop Release smoke opened a real `floorplan.dxf` through the
native picker and rendered its retained plan (`967` visible entities, `194`
unsupported entities, and `709` diagnostics) with the shared toolbar/status
shell. The browser Debug build completed its native Wasm link with zero warnings;
an interactive browser picker/download smoke remains open.

## Editing model

- `CadDocumentHistory` is a bounded, generation-synchronized undo/redo owner.
  It executes only repository-defined `CadEditCommand` implementations; the
  abstract mutation methods are internal so consumers cannot inject an
  unvalidated arbitrary command behind the history contract.
- The first commands translate a deduplicated stable handle set and set entity
  visibility. They resolve every model-space handle before mutation, preserve
  the original visibility vector, apply exact inverse translation for undo,
  and advance one document generation for execute, undo, or redo.
- A direct session edit from another owner invalidates both history branches.
  The expected generation is checked again under the document lock so a race
  cannot apply an undo to the wrong document state. Failed resolution or command
  execution does not publish a generation or enter history.
- Commands are typed and operate on handles. Each command records enough prior
  semantic state for deterministic undo/redo without retaining a duplicate full
  document.
- Nested edits publish one atomic generation. Selection, hover, camera, and
  temporary tool overlays are view state and do not dirty the document.
- Background compilation captures a generation and publishes only if it still
  matches; obsolete work is discarded. The UI may continue drawing the previous
  immutable snapshot while the next generation compiles.
- Collaboration and scripting consume the same command contracts. Neither is
  allowed to mutate ACadSharp collections behind the transaction boundary.

## Printing and export

Printing is a separate physical-output compiler over a layout snapshot. It
resolves page setup, viewports, CTB/STB plot styles, physical lineweights,
transparency, raster quality, font substitution, and paper-space ordering. It
must reuse analytic vector and shaped-text resources and produce deterministic
preview/output from the same compiled print plan.

## Performance and conformance gates

Representative Release workloads report cold open, first visible frame, pan,
zoom, orbit, edit-to-frame, save, and print-plan p50/p95/p99. Counters include
entities visited, visible entities, scene updates, draw calls, boundary
crossings, bytes copied/uploaded, retained uploads, CPU/GPU allocations, cache
residency/evictions, and device-resource generations.

Stable replay targets one render submission, zero scene update, zero retained
upload, and zero managed allocation. Camera replay is `O(V + D)` GPU work for
visible primitives `V` and draw batches `D`, with CPU work bounded independently
of total document entity count after snapshot/index construction. A content
edit recompiles only affected immutable chunks and dependent block instances.

Required fixtures include all supported entity families, large coordinates,
non-world OCS, nested/cyclic blocks and XRefs, layouts/viewports, lineweights,
linetypes, hatches, splines/NURBS, images, dimensions, rich text, TTF/OTF/SHX,
ACIS, corrupt/truncated inputs, and every advertised DXF/DWG version. Tests cover
read/write/read semantic round trips, image comparisons at multiple DPI/zoom,
managed/native differential rendering, selection/snapping, invalidation,
device loss, browser AOT, and bounded-resource fuzzing.

macOS performance claims additionally require matched Release Instruments
Allocations/VM Tracker, Time Profiler, and Metal System Trace captures.

### Reproducible phase-2 CPU baseline

`ProGPU.CAD.Benchmarks` provides JSON p50/p95/p99 and allocation output for
snapshot construction, retained plan-scene recording, and spatial queries. Run:

```bash
dotnet run --project src/ProGPU.CAD.Benchmarks -c Release -- --entities 10000 --warmup 3 --iterations 24 --queries 10000
```

The initial 2026-08-27 Apple Silicon/.NET 10 baseline for 10,000 mixed analytic
entities records snapshot p50/p95/p99 of 17.699/27.464/40.229 ms, plan-scene
recording of 10.555/38.890/43.964 ms, and spatial-query p50/p95/p99 of
2.8/14.9/18.3 microseconds with zero managed allocation per warm query. Snapshot
and scene construction allocate 9,223,164 and 9,280,779 bytes per generation,
respectively. These are transparent starting measurements, not an improvement or
release-acceptance claim. Full representative viewer workloads, GPU counters,
matched managed/native results, and required macOS Instruments traces remain
open gates before performance acceptance.

Run the standalone samples with:

```bash
dotnet run --project src/ProGPU.CAD.Sample.Desktop -c Release -f net10.0
dotnet run --project src/ProGPU.CAD.Sample.Browser -c Debug
```

## Delivery phases

1. Foundation: pinned ACadSharp submodule, project/package boundary, typed IO,
   document sessions, transactions/generations, diagnostics, architecture, and
   unit tests.
2. Immutable scene: ACadSharp entity adapters, coordinate normalization,
   spatial index, layer/style resolution, retained ProGPU scene compilation,
   and managed/native differential baselines.
3. Viewer: model/layout views, selection/snapping, overlays, fast pan/zoom/orbit,
   desktop and browser sample hosts, device recovery, and performance harnesses.
4. Fidelity: every 2D/3D entity, blocks/XRefs, hatches, images/underlays,
   dimensions, linetypes, plot styles, ACIS, and full TTF/OTF/SHX text.
5. Editing: typed tools/commands, properties, undo/redo, clipboard, constraints,
   background incremental compilation, scripting, and collaboration seams.
6. Output: version-gated DXF/DWG save, round-trip certification, print preview,
   physical printing, vector/raster export, and browser download flows.

## Primary research record

Sources consulted on 2026-08-27:

- [ACadSharp repository and format support](https://github.com/DomCR/ACadSharp)
  and [reader API](https://github.com/DomCR/ACadSharp/blob/master/docs/articles/samples/reading.md):
  adopted `CadDocument` plus format-specific reader/writer ownership; adapted
  behind typed store/diagnostic/capability services; rejected extension-only
  validation and unconditional DWG-save claims.
- [Autodesk DXF object coordinate systems](https://help.autodesk.com/cloudhelp/2024/ENU/AutoCAD-DXF/files/GUID-D99F1509-E4E4-47A3-8691-92EA07DC88F5.htm):
  adopted OCS/elevation/extrusion normalization and arbitrary-axis conformance.
- [Autodesk ELLIPSE entity contract](https://help.autodesk.com/cloudhelp/2018/ENU/AutoCAD-DXF/files/GUID-107CB04F-AD4D-4D2F-8EC9-AC90888063AB.htm),
  [SOLID contract](https://help.autodesk.com/cloudhelp/2023/ENU/AutoCAD-DXF/files/GUID-E0C5F04E-D0C5-48F5-AC09-32733E8848F2.htm),
  and [3DFACE contract](https://help.autodesk.com/cloudhelp/2024/ENU/AutoCAD-DXF/files/GUID-747865D5-51F0-45F2-BEFE-9572DBC5B151.htm):
  adopted WCS ellipse axes/parameters, OCS SOLID corners, WCS 3DFACE corners,
  and invisible-edge flags; adapted them into fixed immutable records and
  existing ProGPU analytic/fill paths; rejected polygonal ellipse sampling and
  incomplete extrusion rendering.
- [Autodesk POLYLINE contract](https://help.autodesk.com/cloudhelp/2016/ENU/AutoCAD-DXF/files/GUID-ABF6B778-BE20-4B49-9B58-A94E64CEFFF3.htm),
  [VERTEX contract](https://help.autodesk.com/cloudhelp/2024/ENU/AutoCAD-DXF/files/GUID-0741E831-599E-4CBF-91E1-8ADBCFD6556D.htm),
  and [AcDb2dPolyline vertex-position contract](https://help.autodesk.com/cloudhelp/2018/ENU/OARX-RefGuide/files/OREF-AcDb2dPolyline__vertexPosition_AcDb2dVertex__const.html):
  adopted owning elevation plus vertex XY for 2D OCS conversion, full vertex XYZ
  for 3D WCS, bulge direction, closure, and fit/width flags; adapted both forms
  into packed immutable streams and rejected fixed sampling of fitted curves.
- [Autodesk lineweights](https://help.autodesk.com/cloudhelp/2020/ENU/AutoCAD-Core/files/GUID-4B33ACD3-F6DD-4CB5-8C55-D6D0D7130905.htm):
  adopted distinct cosmetic model-space and physical paper/plot policies.
- [Autodesk shape/font descriptions](https://help.autodesk.com/cloudhelp/2024/ENU/AutoCAD-Customization/files/GUID-DE941DB5-7044-433C-AA68-2A9AE98A5713.htm):
  adopted SHX regular/Unicode/Big Font scope; parsing remains a separate bounded
  clean-room design.
- [Skia shaped-text design](https://docs.skia.org/docs/dev/design/text_shaper/)
  and [Skia text guidance](https://skia.org/docs/user/tips/): adopted separation
  and reuse of shaping, formatting, and positioned-glyph drawing; retained the
  existing ProGPU/HarfBuzz implementation instead of adding another text stack.
- [DirectWrite resource/layout model](https://learn.microsoft.com/en-us/windows/win32/directwrite/getting-started-with-directwrite)
  and [Direct2D geometry realizations](https://learn.microsoft.com/en-us/windows/win32/direct2d/geometry-realizations-overview):
  adopted device-independent semantic/layout results, device-dependent retained
  resources, and explicit flattening-quality tests; rejected fixed realizations
  as the only representation for unbounded CAD zoom.
- [Win2D cached geometry](https://microsoft.github.io/Win2D/WinUI3/html/T_Microsoft_Graphics_Canvas_Geometry_CanvasCachedGeometry.htm):
  adopted pay-once/draw-many retention and device identity; rejected per-frame
  creation and world-coordinate clipping limits.
- [WebRender overview](https://github.com/servo/servo/wiki/Webrender-Overview)
  and [current profiler counters](https://github.com/servo/webrender/blob/main/webrender/src/profiler.rs):
  adopted serializable retained display data, off-thread scrolling/scene work,
  visibility stages, and explicit upload/cache/memory counters.
- [Vello retained scene vision](https://github.com/linebender/vello/blob/main/doc/vision.md)
  and [encoding roadmap](https://github.com/linebender/vello/blob/main/doc/roadmap_2023.md):
  adopted transform-independent analytic encodings, retained fragments, GPU
  transforms, typed resources, and glyph runs; adapted to ProGPU generations.
- [Parley text stack](https://github.com/linebender/parley) and
  [layout model](https://github.com/linebender/parley/blob/main/doc/concept.md):
  adopted reuse of font context, Unicode analysis, shaping, line layout, and
  positioned results; kept CAD text styling outside Unicode shaping identity.
- [HarfBuzz shaping plans/caching](https://github.com/harfbuzz/harfbuzz/blob/main/docs/usermanual-opentype-features.xml):
  adopted reusable shaping inputs/results keyed by font, direction, script,
  language, features, variations, and content; no CAD-specific glyph remapping.
