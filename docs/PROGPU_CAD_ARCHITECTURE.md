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

## Editing model

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
