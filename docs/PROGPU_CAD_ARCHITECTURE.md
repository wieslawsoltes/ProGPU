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
- Single-line TrueType TEXT resolves through a typed host font service, shapes
  during immutable snapshot construction with the existing ProGPU Unicode/
  OpenType pipeline, and stores packed glyph indices, positions, font runs, and
  conservative metric/outline-aware bounds. OCS normal/rotation, effective
  entity width, oblique shear, generation mirrors, justification, and nested
  block transforms compose into one affine glyph-run transform. Recording stays
  one `DrawGlyphRun` command per
  contiguous fallback-font run and requests retained vector coverage for CAD
  zoom; it never expands ordinary text into per-glyph path commands. Font
  substitution is an explicit diagnostic. Overline, underline, and strike-
  through toggles retain bounded filled decoration rectangles in the same
  affine basis. Extrusion and MTEXT remain diagnosed fidelity gates. Documented
  decimal-character, degree, plus/minus, diameter, percent, and DXF Unicode
  escapes are decoded before shaping.
- Model-space lineweights are recorded as fixed device-space strokes; explicit
  zero-width lineweights use the ProGPU hairline sentinel. Non-continuous CAD
  linetypes currently produce a bounded warning and remain a tracked fidelity
  gap rather than being silently claimed as complete.
- `CadShxFont` provides the first bounded SHX source layer. It parses the
  standard compiled `AutoCAD-86 shapes 1.0` container into one immutable owned
  byte store, retains each shape program as a packed slice, validates the
  directory/range/record boundaries and exact EOF marker, and exposes the
  standard font header metrics. `CadShxInterpreter` executes standard commands
  0 through 14 into caller-owned retained analytic line/arc paths with bounded
  recursion, commands, output, scale, coordinates, and the specified four-entry
  position stack. `CadShxGlyphCache` and `CadShxTextLayout` retain interpreted
  glyphs per font/shape/orientation and produce bounded standard-font character
  placements. A typed host resolver now supplies those caches to standard
  horizontal SHX TEXT lowering. The immutable snapshot packs placements and
  affine text bases, and the plan compiler records each drawable placement with
  its shared analytic glyph path. Unicode and Big Font containers, vertical
  STYLE layout, decoration metric policy, and automatic desktop filesystem
  discovery remain explicit gates.

The exact approved ProGPU-owned implementation provenance for this slice is
`src/ProGPU.Scene/RenderCommand.cs` (`DrawingContext.DrawLine`, `DrawEllipse`,
`DrawPath`, `DrawSpline`, and `DrawGlyphRunRange`),
`src/ProGPU.Text/TextLayout.cs`, and `src/ProGPU.Vector/PathGeometry.cs`
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
  substitution, missing glyphs, and bounded parsing as those capabilities land.
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
SOLID, 3DFACE visible-edge semantics, a rotated non-uniform MINSERT array, and
retained shaped TrueType TEXT while framework chrome uses dynamic theme
resources.

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
- The first commands translate, rotate, or uniformly scale a deduplicated stable
  handle set, set entity visibility, assign an existing layer, and add or remove
  one model-space entity. They resolve every model-space handle before mutation,
  preserve the original visibility vector, apply the inverse transform for
  undo, and advance one document generation for execute, undo, or redo. The
  rotate command accepts a finite non-zero axis and radians, normalizes the axis
  without overflow, and deliberately exposes ACadSharp's documented origin-axis
  operation only; pivoted composition remains unsupported until a public
  composition-order contract is established. Uniform scaling uses the public
  origin overload and accepts only a positive, finite, non-unit factor with a
  finite inverse. Non-uniform scaling is not exposed because entity families
  such as circles cannot preserve their authored type under anisotropic scale.
  Transform commands roll back an already-applied entity prefix if a later
  entity fails. Add requires a detached zero-handle object; remove retains the
  same object for undo and treats a collection cancellation as a failed command
  with no published generation.
- Layer assignment resolves the complete entity set and target table entry
  before mutation, retains each prior layer, and validates every retained table
  identity before undo/redo. A missing or externally replaced layer therefore
  fails before partial property changes; setter failure rolls back the already
  applied prefix.
- Layer visibility is a separate multi-layer command over the authored `IsOn`
  state. It resolves every case-insensitively deduplicated table name before
  mutation, retains each prior state, and restores the snapshot's existing
  visibility filtering on undo. A distinct multi-layer command owns plot
  eligibility (`PlotFlag`) and retains the snapshot state consumed by later
  print planning without changing screen visibility.
- Layer color assignment accepts indexed and true explicit colors, rejecting
  `ByLayer`, `ByBlock`, and the header-only `ByEntity` sentinel. It restores each
  prior table value and updates inherited entity RGB through the same immutable
  snapshot style resolution used by both picture compilers.
- Layer lineweight assignment accepts declared explicit widths, hairline, and
  `Default`, while rejecting entity-only `ByLayer`/`ByBlock` and undefined wire
  values. Inherited entities resolve the new physical/cosmetic thickness in the
  immutable snapshot; undo restores each authored layer enum.
- Layer linetype assignment resolves an existing explicit table entry before
  mutation and rejects entity-only `ByLayer`/`ByBlock` targets. Inherited entity
  styles retain the resolved name in the snapshot, while non-continuous pattern
  rendering remains behind the documented A-alignment fidelity gate.
- Layer creation transfers one detached zero-handle `Layer` into the document
  table and reverses that ownership on undo. LIFO history guarantees dependent
  in-history edits are undone first; arbitrary populated-layer deletion remains
  unsupported until entity/block reference reassignment has a complete semantic
  transaction.
- Linetype assignment uses the same table-identity and rollback contract for
  explicit, `ByLayer`, and `ByBlock` entries. The immutable snapshot retains the
  newly resolved linetype name. Entity linetype-scale assignment accepts only
  positive finite values and restores each prior authored scale. These edits do
  not imply dash rendering: non-continuous patterns continue through the
  explicit unsupported diagnostic until the documented A-aligned endpoint
  planner is implemented.
- Lineweight assignment accepts only declared `LineWeightType` values, retaining
  explicit widths plus `ByLayer`, `ByBlock`, `Default`, and hairline semantics
  without resolving them prematurely. Undo restores each authored enum value;
  the next immutable snapshot resolves it through the existing layer/block
  style contract, so both picture compilers receive the same physical or
  cosmetic stroke policy.
- Color assignment retains indexed, true-color, `ByLayer`, and `ByBlock` values
  as authored and rejects the header-only `ByEntity` sentinel before mutation.
  Undo restores each semantic value rather than baking resolved RGB, while the
  snapshot regression verifies that an explicit true color reaches the shared
  retained stroke style unchanged.
- Transparency assignment likewise retains explicit 0–90, `ByLayer`, and
  `ByBlock` values instead of baking alpha into the mutable document. The shared
  bounded property transaction captures a rollback vector, validates retained
  entity/table identity, and reverses an already-applied setter prefix on
  failure. Snapshot coverage verifies the existing integer-to-alpha rendering
  contract after the edit.
- A direct session edit from another owner invalidates both history branches.
  The expected generation is checked again under the document lock so a race
  cannot apply an undo to the wrong document state. Failed resolution or command
  execution does not publish a generation or enter history.
- Commands are typed and begin from handles or an explicitly transferred
  detached entity. ACadSharp assigns a new handle when a removed object is added
  back, so applied commands retain and revalidate object identity after their
  first handle resolution. This lets an earlier translate/visibility command
  undo correctly across delete/restore without guessing or mutating ACadSharp's
  handle allocator. Each command records only enough prior semantic state for
  deterministic undo/redo, never a duplicate full document.
- Nested edits publish one atomic generation. Selection, hover, camera, and
  temporary tool overlays are view state and do not dirty the document.
- Background compilation captures a generation and publishes only if it still
  matches; obsolete work is discarded. The UI may continue drawing the previous
  immutable snapshot while the next generation compiles.
- Collaboration and scripting consume the same command contracts. Neither is
  allowed to mutate ACadSharp collections behind the transaction boundary.

## Static BLOCK/INSERT lowering

Static INSERT entities expand into the immutable primitive streams once per
document generation. The original top-level INSERT handle is retained on every
expanded header so hit testing and editing continue to address the block
reference as one CAD object. Nested transforms use an original double-precision
affine value with allocation-free point/vector application and composition. For
a block-space point `p`, block base point `b`, insertion position `P`, OCS basis
`O`, rotation `R`, and nonzero scale `S`, the mapping is
`P + O * R * S * (p - b)`. ACadSharp's public object model supplies `P` as the
WCS-equivalent position; the raw DXF group is OCS data.

Child geometry remains analytic under affine transforms: transformed circles
and arcs carry affine axes into the existing ellipse/path command transforms,
ellipses retain transformed major/minor axes, splines transform control points,
and bulge polylines transform their planar basis without fixed sampling. Exact
axis-extrema bounds are recomputed after transformation. Layer `0` inherits the
effective INSERT layer, while ByBlock color, lineweight, linetype, and
transparency inherit the resolved parent INSERT style. All descendants retain
their own nonzero layers and explicit properties.

MINSERT uses the same lowering. For zero-based column `c` and row `r`, column
spacing `dc`, and row spacing `dr`, its cell origin is
`P + O * R * (c * dc, r * dr, 0)`. Thus rotation applies to both each block and
the whole array, while the independently specified spacing is not multiplied by
the per-block scale. Cells are emitted row-major and every primitive retains the
single top-level INSERT handle. The affine axes are composed once per array;
each cell changes only the translation, so lowering remains allocation-free per
instance apart from the immutable primitive output itself.

Expansion is bounded by configurable depth, array-instance, and entity-count
limits and detects cycles along the active block path. The array limit is
checked before any cell is emitted, including for an empty block whose instances
would not consume the entity budget. Exceeding the global entity budget fails
the snapshot explicitly at the bound instead of returning a partially rendered
drawing; depth and cycle failures diagnose only the affected INSERT. Expansion
is `O(I + E)` before the `O(E log E)` BVH build for `I` block instances and `E`
expanded visible entities; camera replay remains independent of both source and
expanded entity counts. Dynamic evaluation graphs, XRefs, and attribute text
are explicitly diagnosed rather than rendered approximately. Shared block-
fragment/GPU instance reuse remains a later optimization after inherited-style
variants and instance hit identity are fully specified.

## Retained TrueType TEXT lowering

The snapshot compiler accepts an `ICadTextFontResolver`; hosts may use the
process `FontManager` plus an explicit embedded fallback, so browser builds do
not depend on system font files. Font resolution is outside render and replay
hot paths. A missing face rejects only the affected entity, while a deliberate
fallback emits `CADSNAP005` rather than silently changing document typography.
SHX and Big Font styles are never sent through a TrueType substitute.

Supported single-line TEXT is shaped at a normalized one-unit em size. The
immutable snapshot interns typefaces by identity and stores one packed glyph
index/position stream plus contiguous fallback-font ranges. Entity height,
effective entity width, oblique shear, generation mirrors, OCS rotation/normal,
and ancestor block transforms stay in a double-precision affine basis; glyph data
is not regenerated for camera changes. Left/center/right and top/middle/bottom/
baseline anchors use the Autodesk second-point rule. Conservative bounds union
the shaped advance/vertical metrics with available glyph outline bounds and transform all
four affine extrema into WCS before BVH construction.

Aligned TEXT derives baseline orientation and uniform character scale from the
two OCS endpoints while preserving the effective width-factor aspect ratio. Fit
TEXT preserves entity height and derives only horizontal scale. Both finish
exactly on the transformed second point even after font substitution, nested
non-uniform block transforms, or backward generation; backward text anchors at
the second point and traverses the same segment in reverse. Non-baseline or
non-coplanar two-point combinations fail explicitly.

The bounded content decoder maps Autodesk's three-digit decimal `%%nnn`,
`%%d`, `%%p`, `%%c`, `%%%`, and four-hex-digit `\U+XXXX` sequences before
shaping. Numeric sequences must contain exactly three decimal digits. It also records the visual
ranges toggled by `%%o`, `%%u`, and `%%k`; toggles still active at end of TEXT
close automatically. Underline geometry uses the resolved OpenType `post`
position/thickness, strike-through uses the `OS/2` position/thickness, and
overline uses the horizontal ascender plus the font's underline thickness.
These become filled local rectangles, so width, oblique shear, mirrors, Align/
Fit, OCS, and ancestor transforms apply exactly once. Visual cluster runs merge
in one pass; a toggle boundary that splits one shaped cluster fails explicitly
instead of changing shaping or partially painting a ligature/combining cluster.
Decoration rectangles participate in conservative WCS bounds. Invalid UTF-16,
malformed numeric controls, unknown font-specific controls, and missing required metrics
remain explicit gates; control syntax is never painted literally.

The decoration-specific engine audit preserves the same layout/render split:
SkParagraph exposes simultaneous underline/overline/line-through state outside
the glyph stream; DirectWrite and Win2D attach underline/strike-through to text
ranges and surface separate renderer calls; WebRender tracks interned line-
decoration primitives separately from interned text runs; Parley resolves
optional decoration offsets and sizes from containing-run metrics; and Vello's
glyph encoder remains a glyph-run operation while ordinary filled geometry is a
separate scene primitive. HarfBuzz defines shaped clusters as indivisible and
explicitly identifies cluster mapping as the seam for ranged attributes. ProGPU
therefore adopts retained metric-driven rectangles and cluster-safe range
mapping, while rejecting per-glyph decoration expansion, re-shaping around a
toggle, or putting decoration state into cached glyph identities.

Snapshot work is `O(C + G)` for `C` input/decoded UTF-16 code units and `G`
shaped glyphs, with bounded output storage `O(C + G + R + F + D)` while
decorations are present, for fallback runs `R`, interned faces `F`, and merged
visual decoration runs `D <= G`. Configurable per-entity UTF-16 and document-
wide glyph limits reject oversized input atomically before it can enter retained
streams. Plan recording is `O(R + D)` commands. Stable replay uses the existing
ProGPU retained glyph cache,
DPI/subpixel policy, fallback, color-font, variable-
font, and vector-text coverage contracts. MTEXT still requires bounded inline-
format, paragraph, column, background, and attachment lowering. It remains
explicit instead of inheriting the older `ProGPU.Dxf` renderer's estimated
bounds, per-character width loop, or formatting-stripping approximation.

This is a managed ACadSharp snapshot/resource adapter. It changes no shader,
stable C ABI, native renderer, or compositor algorithm. Both compositors already
consume the same retained glyph-run contract, so no paired native implementation
change applies; matched native picture/pixel coverage remains the integration
gate when the CAD differential suite lands.

## Bounded standard SHX source

`CadShxFont.Parse` is the initial clean-room SHX ingestion boundary. The input
is caller-owned only for the synchronous parse. A successful parse copies it
once into immutable owned storage and retains every program as a
`ReadOnlyMemory<byte>` slice, so no per-shape program copy or runtime text
parsing is required. Default limits cap a source at 16 MiB, 65,535 directory
entries, and 2,000 program bytes per shape. The parser rejects malformed
directory ranges, duplicates, unterminated names/programs, truncated records,
invalid standard-font metrics, trailing data, and unsupported container
signatures before publishing a font.

Parsing takes `O(B + S)` time and `O(B + S)` owned storage for `B` input bytes
and `S` shape-directory entries. Shape lookup is expected `O(1)` and program
storage remains packed.

`CadShxInterpreter` implements the standard command stream directly from the
Autodesk contract: 16 encoded vector directions; draw/move modes; cumulative
divide/multiply scale; balanced push/pop with the specified four-location
stack; one-byte standard subshape calls; single and repeated signed XY
displacements; octant, fractional, bulge, and polyarc commands; and command 14
dual-orientation gating. Arcs stay analytic `ArcSegment` values. A full octant
circle becomes exactly two retained semicircles because the endpoint-based path
record cannot encode a coincident-start/end full circle as one segment; no
curve is sampled. Pen-up commands advance the retained endpoint without
emitting geometry, and discontinuous moves start a new figure when drawing
resumes. The font header's above/below units and final pen-up endpoint remain
available for the later height, baseline, character-advance, and vertical-
advance lowering.

Interpretation is `O(C + S)` time and `O(S + D)` storage for recursively
executed commands `C`, emitted retained segments `S`, and active subshape depth
`D`. Defaults cap commands and segments at 100,000 each, recursion at 32,
absolute coordinates and cumulative scale at one billion, and position depth
at the format-defined four entries. Cycles, missing/reserved subshapes,
unbalanced stack operations, zero/overflowing scale, malformed operands,
unreachable terminators, unsupported vertical commands, and limit overruns fail
the whole interpretation. Each direct call returns a fresh caller-owned path.
`CadShxGlyphCache` instead interprets once per immutable font identity, shape
number, and orientation, keeps the mutable path private, and exposes immutable
advance/bounds/segment metadata. Its locked cache is safe for concurrent
snapshot workers; lookup is expected `O(1)` after the first bounded execution.
Unicode two-byte subshape references and Big Font ranges use different
contracts and are rejected instead of being guessed as standard records.

`CadShxTextLayout` scans one standard-font TEXT value in `O(C + G)` time and
retains `O(G)` placements for `C` UTF-16/control-code units and `G` characters.
It accumulates each font-authored pen-up endpoint as the next origin rather than
estimating character widths. Autodesk decimal controls address their exact
three-digit shape number; degree, plus/minus, and diameter controls and literal
Unicode equivalents map to the standard format's reserved shapes 256, 257, and
258. Percent, DXF four-hex-digit escapes, and decoration toggles are decoded
without changing glyph identity. Missing shapes, malformed controls, surrogate
pairs, unsupported nonstandard Unicode, empty control-only strings, coordinate
growth, and code-unit/glyph limits fail explicitly. Decoration flags are
retained per placement for the later metric policy; they are not silently
dropped or rendered at guessed positions.

`CadSnapshotCompiler` accepts an `ICadShxFontResolver`, keeping desktop font
search, browser-bundled assets, and application substitution policy outside the
document and render hot paths. Standard horizontal SHX TEXT scales the font's
above metric to entity height, preserves its below-baseline metric and actual
path bounds, and composes effective width, oblique shear, generation mirrors,
OCS rotation/normal, justification, and ancestor block transforms into one
double-precision basis. Align and Fit use the two authored endpoints: Align
changes both axes while preserving the width-factor aspect ratio, and Fit keeps
the authored height while changing horizontal scale. A substituted SHX font
emits `CADSNAP006`. Missing glyphs, non-horizontal authored advances, Big Font,
vertical STYLE, and decoration toggles reject the affected entity rather than
guessing layout.

`CadShxFontCatalog` is the default reusable resolver for hosts and benchmark
fixtures. Initialization parses or registers immutable standard caches under a
portable filename plus explicit aliases; lookup strips either Windows or Unix
directory separators and compares names case-insensitively without touching the
filesystem. Hosts may install explicit SHX-to-SHX filename mappings and one
alternate font. A present mapping wins even when the requested font is also
registered; if its target is absent, lookup falls back to the originally
requested filename. Style-name aliases are considered only after filename
lookup, and alternate/style/mapped substitutions remain diagnostic. Registration
is transactional on alias collision, repeated resolution is locked and expected
`O(1)`, parsed bytes are owned once, and Big Font requests never enter the
standard catalog. The catalog caches an immutable resolver generation until its
configuration changes; each document compile captures that generation once so
concurrent host registration cannot mix font policy inside one snapshot. The
shared sample exposes this catalog so desktop code can
register discovered support files and browser code can register bundled byte
assets through the same API. Ordered filesystem search remains host
initialization work rather than synchronous snapshot behavior.

`CadShxFontDiscovery.DiscoverAsync` is the opt-in desktop host adapter for that
filesystem work. It captures a document's distinct standard-SHX style filenames
under the session lock, then releases the document before doing any IO. It probes
the drawing directory first and explicit support directories in caller order,
using exact filenames without enumerating directories. An FMP replacement is
probed before its original name; if the mapped target is unavailable, discovery
probes the original filename so the catalog can apply its documented fallback.
Defaults bound the operation to 256 directories, 4,096 distinct style requests, 16 MiB per parser
input, and 256 MiB total. Candidate sizes and reads are preflighted before any
registration, first-existing corrupt files do not silently fall through to a
lower-priority directory, and missing/rejected resources produce bounded typed
diagnostics. Parsing is `O(B + S)` for total bytes `B` and SHX directory entries
`S`; exact path probing is `O(F * D)` for unresolved filenames `F` and search
directories `D`. Browser hosts continue to register bundled bytes directly, and
snapshot compilation/render replay performs no discovery or file IO.
The shared sample now invokes this adapter before snapshot compilation when its
picker returns an existing fully-qualified desktop file. Its public ordered
support-directory list is copied for the operation; the drawing directory still
wins. Virtual browser files never enter that path and continue through the same
byte-only catalog seam. The status bar reports loaded, missing, and rejected SHX
resource counts rather than hiding substitution state.

`CadFontMappingTable.Parse` provides the bounded configuration seam for the
documented FMP format. It accepts ordinary ASCII input containing exactly one
requested/replacement pair per non-empty line, separated by one semicolon;
requested names contain no path and may omit their extension, while replacement
filenames require an extension. It trims only surrounding ASCII space/tab and
rejects comments or other undocumented syntax, duplicate requested names,
paths, control/non-ASCII bytes, ambiguous separators, missing extensions, and
empty tables. Defaults cap the source at 1 MiB, 16,384 mappings, and 1,024 bytes
per line. Parsing is `O(B)` time and `O(M + T)` retained storage for source bytes
`B`, mappings `M`, and filename characters `T`. Applying an SHX-to-SHX table to
`CadShxFontCatalog` validates the complete table before changing one resolver
generation; cross-kind mappings remain retained configuration data for the later
unified TrueType/SHX resolver and cannot silently enter the standard SHX path.

The snapshot owns packed `CadShxGlyphInstance` placements but references the
resolver-owned immutable `CadShxGlyph` metadata. Plan recording is `O(G)` and
emits one existing `DrawPath` command for each drawable stroked glyph, applying
only a placement transform; spaces and other pen-up-only shapes emit no command.
All repeated character instances share the same cached `PathGeometry`, so
snapshot compilation neither reinterprets nor clones glyph outlines. TrueType
and SHX placements share one document-wide glyph budget. Freezing the recorded
scene uses the normal retained picture ownership contract.

The binary container layout was independently observed from the compiled
`external/ACadSharp/samples/test_shape.shx` artifact pinned by this repository;
no ACadSharp or other third-party parser implementation was consulted or
copied. The regression reads that compiled artifact as an observable fixture.
Program semantics and the 2,000-byte definition limit come only from the
official Autodesk shape/font documentation linked below. The pinned fixture
also executes through the new interpreter, while independent synthetic tests
cover every command family, direction geometry, analytic endpoints/radii,
horizontal/vertical behavior, state composition, cycles, malformed programs,
and configured bounds. The managed snapshot/recording adapter changes no
shader, C ABI, compositor algorithm, or GPU resource contract. It feeds the
pre-existing retained analytic `DrawPath` command consumed by both managed and
native picture compilers, so a distinct native SHX parser or per-glyph boundary
call is neither required nor permitted. Existing native analytic-path lowering
tests remain the paired integration gate; CAD-specific managed/native image
differentials remain required before full fidelity acceptance.

The Release benchmark's optional `--shx-interpretations` lane measures fresh
uncached path construction outside the document pipeline. Two 24-iteration,
1,000-shape runs on the same Apple Silicon/.NET 10 host reported batch
p50/p95/p99 of 3.043/6.942/7.783 ms and 1.490/4.280/5.696 ms, with 1,256,065
managed bytes per batch in both runs. The synthetic shape exercises direction
vectors plus octant, bulge, and polyarc output. These numbers establish the
interpreter construction baseline only; process-to-process tails vary, no
improvement claim is made, and they are not a steady TEXT replay target. The
allocation result motivated the implemented per-font glyph cache: normal
retained replay must not construct a `PathGeometry` for every placed character.
Snapshot/scene integration now proves cache reuse under a representative
document workload.

The same two runs measured 1,000 warm-cache layouts of eight standard glyphs at
p50/p95/p99 2.475/3.695/4.089 ms and 1.565/2.212/2.507 ms, with 584,024 managed
bytes per batch in both runs. This is a device-independent placement baseline,
not retained replay and not an improvement claim. Snapshot integration packs
those placements into generation-owned arrays and reuses the cached glyph paths;
it does not reinterpret or clone eight path graphs per TEXT entity.

The 2026-08-27 Release applicability/performance audit compared commit
`778dc69f` with this lowering on the same Apple Silicon/.NET 10 machine and the
same 10,000-entity workload. A 100-iteration alternating-order control measured
snapshot p50/p95/p99 at 6.433/12.413/21.210 ms before and
7.008/12.521/14.140 ms after, means of 7.712/7.781 ms, and
9,078,865/9,053,174 managed bytes per generation. Repeated 24- and 100-iteration
runs showed GC/system noise in individual tails, so these results establish no
repeatable regression and make no improvement claim. Matched Xcode Instruments
Time Profiler, Allocations/VM Tracker, and eight-second Metal System Trace
captures plus exported tables are retained locally under
`artifacts/progpu-cad/instruments/block-insert-20260827/`. The host reported that
its selected Metal counter profile was unavailable, but command-buffer and
`currentAllocatedSize` events were captured; their differing active-frame counts
preclude a GPU delta claim. The native renderer, stable C ABI, canonical shaders,
and GPU algorithms are not changed: both managed snapshots emit the existing
retained commands, so no paired native implementation change applies to this
ACadSharp graph adapter.

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
dotnet run --project src/ProGPU.CAD.Benchmarks -c Release -- --entities 0 --block-array-columns 10000 --warmup 5 --iterations 100 --queries 10000
dotnet run --project src/ProGPU.CAD.Benchmarks -c Release -- --entities 0 --text-entities 1000 --warmup 5 --iterations 50 --queries 10000
dotnet run --project src/ProGPU.CAD.Benchmarks -c Release -- --entities 0 --text-entities 1000 --text-decorations --warmup 3 --iterations 50 --queries 10000
dotnet run --project src/ProGPU.CAD.Benchmarks -c Release -- --entities 0 --shx-text-entities 1000 --warmup 3 --iterations 24 --queries 1000
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

The MINSERT mode creates one block reference whose single line expands across
the requested number of columns. Two consecutive 100-iteration Release runs at
10,000 cells measured snapshot p50/p95/p99 of
4.856/13.501/15.956 ms and 5.258/9.713/11.030 ms, with
8,410,515 and 8,410,267 managed bytes per generation. Retained plan recording
measured 11.462/38.207/41.159 ms and 10.727/37.593/45.143 ms, with
10,240,521 and 10,240,498 bytes per generation. The snapshot reported one
source INSERT, 10,001 expanded entities including the root, and 10,000 recorded
commands. Alternating 10,000 ordinary-entity controls were intentionally kept
alongside these runs; graph shape and spatial distribution differ, so the data
is a feature baseline and makes no relative speed or regression claim.

The TEXT mode creates 1,000 twenty-one-character TrueType entities backed by one
embedded Inter face. Two consecutive 50-iteration Release runs measured
snapshot p50/p95/p99 of 15.412/34.109/48.727 ms and
12.850/36.243/40.096 ms, with 4,288,949 and 4,297,244 managed bytes per
generation. Retained plan recording emitted 1,000 glyph-run commands and
measured 0.199/1.997/12.281 ms and 0.338/1.955/11.911 ms, with 576,810 and
576,966 managed bytes per generation. Warm spatial queries remained zero-
allocation. These measurements establish the first feature baseline only; they
make no speedup claim and do not replace the matched viewer, GPU, native, or
Instruments acceptance gates.

The optional decoration mode keeps the same decoded twenty-one characters but
wraps its three logical fields in underline, overline, and strike-through
toggles. Two consecutive 50-iteration Release runs produced 3,000 merged
decoration rectangles and 4,000 retained commands. Snapshot p50/p95/p99 was
11.345/82.567/99.794 ms and 11.598/82.011/98.052 ms, with 5,428,388 and
5,431,835 managed bytes per generation. Plan recording measured
0.770/4.363/10.838 ms and 0.854/4.446/11.553 ms, with 2,305,072 and 2,304,948
managed bytes per generation. Matched undecorated runs after the new fixed
primitive fields retained 1,000 commands and measured snapshot p50 of
12.831/11.355 ms with 4,304,227/4,311,072 bytes, and plan p50 of
0.406/0.335 ms with 576,892/576,968 bytes. Tail timings were visibly noisy;
these are feature/cost baselines with no latency improvement or regression
claim. Warm queries remained zero-allocation.

The standard SHX TEXT mode creates 1,000 eight-character entities backed by one
cached analytic glyph. Its preflight rejects the run if any requested fixture
entity is unsupported or invalid. Two consecutive 24-iteration Release runs
retained all 1,000 entities and emitted 8,000 shared-path commands. Snapshot
p50/p95/p99 was 7.805/13.825/14.920 ms and 7.170/9.546/12.164 ms, with
1,839,947 and 1,840,014 managed bytes per generation. Plan recording measured
4.807/16.765/17.074 ms and 4.746/18.753/27.931 ms, with 10,816,601 and
10,816,564 managed bytes per generation. Warm spatial queries allocated zero
managed bytes. These are feature/cost baselines with visibly variable tail
latency, no improvement or regression claim, and no substitute for matched
viewer, GPU, native-image, or Instruments acceptance evidence.

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
  [reader API](https://github.com/DomCR/ACadSharp/blob/master/docs/articles/samples/reading.md),
  and the pinned fork's public
  [`CadObjectCollection<T>` contract](https://github.com/wieslawsoltes/ACadSharp/blob/b469bd1ec7db6d7d364425f6165609e5ccf09b04/src/ACadSharp/CadObjectCollection.cs),
  [`Entity` transform contract](https://github.com/wieslawsoltes/ACadSharp/blob/b469bd1ec7db6d7d364425f6165609e5ccf09b04/src/ACadSharp/Entities/Entity.cs),
  and [`Transform` construction surface](https://github.com/wieslawsoltes/ACadSharp/blob/b469bd1ec7db6d7d364425f6165609e5ccf09b04/src/CSUtilities/CSMath/Transform.cs):
  adopted `CadDocument` plus format-specific reader/writer ownership; adapted
  behind typed store/diagnostic/capability services. Add/remove command design
  uses only the public collection ownership, cancellation, and observable handle
  reassignment contracts; it retains ProGPU command state rather than copying
  collection implementation text or structure. Rotation likewise uses only the
  public axis-angle/radians entity operation, normalizes the caller's axis in
  ProGPU, and applies the public inverse operation for undo. Uniform scale uses
  the documented origin overload and a reciprocal factor. Rejected
  extension-only validation, unconditional DWG-save claims, private handle
  manipulation, pivot rotation based on undocumented matrix order, and exposing
  anisotropic scaling without type-preserving entity conformance.
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
- [Autodesk INSERT contract](https://help.autodesk.com/cloudhelp/2018/ENU/AutoCAD-DXF/files/GUID-28FA4CFB-9D5E-4880-9F11-36C97578252F.htm),
  [BLOCK contract](https://help.autodesk.com/cloudhelp/2021/ENU/AutoCAD-DXF/files/GUID-66D32572-005A-4E23-8B8B-8726E8C14302.htm), and
  [AcDbBlockReference model](https://help.autodesk.com/cloudhelp/2027/ENU/OARX-RefGuide/files/OARX-RefGuide-AcDbBlockReference.html):
  adopted the block MCS-to-WCS mapping, base point, insertion position,
  OCS-relative rotation/normal, scale factors, and nested memory-reuse model;
  adapted them into bounded immutable analytic expansion while retaining root
  handle identity; rejected approximate transformed extents and unbounded
  recursion.
- [Autodesk MINSERT command contract](https://help.autodesk.com/cloudhelp/2024/ENU/AutoCAD-Core/files/GUID-A780A2FA-4A2E-4574-950F-E788AB71F527.htm),
  [AddMInsertBlock API](https://help.autodesk.com/cloudhelp/2021/ENU/AutoCAD-ActiveX-Reference/files/GUID-AAEFDED2-34A3-4466-A7AA-71CAD8DCB35C.htm),
  and [AcDbMInsertBlock methods](https://help.autodesk.com/cloudhelp/2018/ENU/OARXMAC-RefGuide/files/OREFMAC-__MEMBERTYPE_Methods_AcDbMInsertBlock.html):
  adopted independent row/column counts and spacings plus rotation of both each
  block and the complete array; adapted cells into row-major bounded affine
  lowering with one semantic root handle. The scale-independent spacing order
  is an inference from Autodesk exposing scale factors and spacing distances as
  independent inputs and describing rotation, but not scale, as applying to the
  entire array. Rejected unbounded expansion and undocumented scale-dependent
  spacing.
- [Autodesk TEXT contract](https://help.autodesk.com/cloudhelp/2024/ENU/AutoCAD-DXF/files/GUID-62E5383D-8A14-47B4-BFC4-35824CAE8363.htm),
  [text symbol/control-code contract](https://help.autodesk.com/cloudhelp/2022/ENU/AutoCAD-Core/files/GUID-968CBC1D-BA99-4519-ABDD-88419EB2BF92.htm),
  [DXF string storage contract](https://help.autodesk.com/cloudhelp/2026/ENU/AutoCAD-DXF/files/GUID-2553CF98-44F6-4828-82DD-FE3BC7448113.htm),
  [AcDbText width-factor contract](https://help.autodesk.com/cloudhelp/2018/ENU/OARXMAC-RefGuide/files/OREFMAC-AcDbText__widthFactor.html),
  [text-style inheritance behavior](https://help.autodesk.com/cloudhelp/2018/CHT/AutoCAD-ActiveX/files/GUID-B6880624-B89C-4C7E-8276-6E21070CFBF6.htm),
  [TEXT command Align/Fit behavior](https://help.autodesk.com/cloudhelp/2020/ENU/AutoCAD-Core/files/GUID-D1C664DD-63D9-467E-8EC1-2F5A1777A924.htm),
  [AcDbText alignment-point contract](https://help.autodesk.com/cloudhelp/2027/ENU/OARX-RefGuide/files/OARX-RefGuide-AcDbText__alignmentPoint.html),
  [OpenType `post` underline metrics](https://learn.microsoft.com/en-us/typography/opentype/spec/post),
  [OpenType `OS/2` strikeout metrics](https://learn.microsoft.com/en-us/typography/opentype/spec/os2),
  [OpenType `hhea` ascender](https://learn.microsoft.com/en-us/typography/opentype/spec/hhea),
  [MTEXT contract](https://help.autodesk.com/cloudhelp/2021/ENU/AutoCAD-DXF/files/GUID-5E5DB93B-F8D3-4433-ADF7-E92E250D2BAB.htm),
  and [STYLE contract](https://help.autodesk.com/cloudhelp/2021/ENU/AutoCAD-DXF/files/GUID-EF68AF7C-13EF-45A1-8175-ED6CE66C8FC9.htm):
  adopted OCS/WCS point distinctions, the second-point justification rule,
  effective entity transform values and style creation defaults, two-point
  Align/Fit scaling, generation flags, decimal/symbol/decoration controls,
  OpenType decoration metrics, font metadata, and MTEXT attachment/flow/column scope; adapted
  supported TrueType TEXT into normalized retained font and decoration runs with
  conservative affine bounds; rejected guessed text
  rectangles, stripped MTEXT formatting, silent SHX substitution, and claiming
  MTEXT support before its complete contract lands.
- [Autodesk common entity property codes](https://help.autodesk.com/cloudhelp/2021/ENU/AutoCAD-DXF/files/GUID-3610039E-27D1-4E23-B6D3-7E60B22BB5BD.htm)
  and [ByBlock color behavior](https://help.autodesk.com/cloudhelp/2022/ENU/AutoCAD-Core/files/GUID-14BC039D-238D-4D9E-921B-F4015F96CB54.htm):
  adopted layer `0`, ByLayer, and ByBlock inheritance without mutating block
  definitions or cloning third-party entities.
- [Autodesk lineweights](https://help.autodesk.com/cloudhelp/2020/ENU/AutoCAD-Core/files/GUID-4B33ACD3-F6DD-4CB5-8C55-D6D0D7130905.htm):
  adopted distinct cosmetic model-space and physical paper/plot policies.
- [Autodesk LTYPE records](https://help.autodesk.com/cloudhelp/2025/ENU/AutoCAD-DXF/files/GUID-F57A316C-94A2-416C-8280-191E34B182AC.htm),
  [simple-linetype semantics](https://help.autodesk.com/cloudhelp/2023/ENU/AutoCAD-Customization/files/GUID-EF1DF0A9-2088-487C-8085-16FEE6425405.htm),
  and [linetype scaling](https://help.autodesk.com/view/ACD/2026/ENU/?guid=GUID-20B4D4B3-1220-426A-847B-5BBE36EC6FDF):
  adopted positive dash, negative gap, zero dot, entity/global scaling, and
  A-aligned endpoint requirements. A fixed repeating phase was rejected because
  AutoCAD adjusts endpoint dashes per line/arc and draws a too-short primitive
  continuously. The current snapshot therefore retains name/scale and the plan
  emits an unsupported diagnostic rather than approximating the pattern.
- [Skia dash effects](https://api.skia.org/classSkDashPathEffect.html),
  [Direct2D retained stroke styles](https://learn.microsoft.com/en-us/windows/win32/api/d2d1/nn-d2d1-id2d1strokestyle),
  [Win2D custom dash styles](https://learn.microsoft.com/en-us/dotnet/communitytoolkit/archive/windows/win2d-path-mini-language),
  [Vello stroke encoding](https://github.com/linebender/vello/blob/main/vello_encoding/src/path.rs),
  and [WebRender's retained display-list architecture](https://github.com/servo/servo/wiki/Webrender-Overview):
  adopted reusable interval/phase/cap concepts and retained device-independent
  style ownership, but none is treated as an oracle for CAD A-alignment. The
  existing ProGPU dash path remains the eventual backend after a CAD-specific
  endpoint planner produces conformance-tested intervals.
- [HarfBuzz shaping](https://harfbuzz.github.io/what-is-harfbuzz.html),
  [Parley rich-text architecture](https://github.com/linebender/parley/blob/main/doc/concept.md),
  [SkParagraph](https://docs.skia.org/docs/dev/design/text_shaper/), and
  [DirectWrite](https://learn.microsoft.com/en-us/windows/win32/directwrite/getting-started-with-directwrite):
  confirmed that shaping/layout stays separate from stroke patterns; these
  stacks become applicable to embedded-text complex linetypes, not simple dash
  alignment, so no text shortcut or foreign layout structure was adopted.
- [Autodesk shape/font descriptions](https://help.autodesk.com/cloudhelp/2024/ENU/AutoCAD-Customization/files/GUID-DE941DB5-7044-433C-AA68-2A9AE98A5713.htm),
  [special codes](https://help.autodesk.com/cloudhelp/2024/ENU/AutoCAD-Customization/files/GUID-06832147-16BE-4A66-A6D0-3ADF98DC8228.htm),
  [vector directions](https://help.autodesk.com/cloudhelp/2022/ENU/AutoCAD-Customization/files/GUID-0A8E12A1-F4AB-44AD-8A9B-2140E0D5FD23.htm),
  [text-font descriptions](https://help.autodesk.com/cloudhelp/2021/ENU/AutoCAD-Customization/files/GUID-9BBE5B28-DF02-4EC5-863A-BA04AB6F5EF1.htm),
  [Unicode font descriptions](https://help.autodesk.com/cloudhelp/2023/ENU/AutoCAD-MAC-Customization/files/GUID-D38A5A7B-1877-46B3-8120-32DA5F7430D1.htm),
  [Big Font descriptions](https://help.autodesk.com/cloudhelp/2022/ENU/AutoCAD-Customization/files/GUID-CDAF6EAF-85D1-48FC-9A78-43514E0132D5.htm),
  and [shape/font compilation](https://help.autodesk.com/cloudhelp/2024/ENU/AutoCAD-Customization/files/GUID-BC8EFEAC-D640-410A-8EC8-2EBB38DE6563.htm):
  adopted standard header metrics, bounded program size, command semantics,
  direction encoding, and the distinct regular/Unicode/Big Font contracts;
  adapted only the standard compiled container into an immutable source layer;
  rejected signature guessing, eager opcode expansion, and treating Unicode or
  Big Font records as standard shapes.
- [Autodesk support-file search-path contract](https://help.autodesk.com/cloudhelp/2027/ENG/AutoCAD-Core/files/GUID-F95EE827-7567-44EA-9D69-E9D0D37EE13F.htm),
  [FONTMAP contract](https://help.autodesk.com/cloudhelp/2027/ENU/AutoCAD-Core/files/GUID-FC45A5DC-31F5-4725-A482-C95769273C1C.htm),
  [font-substitution and FMP editing contract](https://help.autodesk.com/cloudhelp/2021/ENU/AutoCAD-LT/files/GUID-928DF015-1E04-4CC2-AF1B-0037548DFBAE.htm),
  and [missing-SHX resolution guidance](https://help.autodesk.com/view/ACADWEB/ENU/?caas=caas%2Fsfdcarticles%2Fsfdcarticles%2FAutoCAD-cannot-find-SHX-font.html):
  adopted ordered exact-filename lookup, mapping-before-original resolution,
  fallback to the original when a mapped target is missing, same-drawing/support
  path host discovery, ordinary ASCII one-pair-per-line mapping files,
  extensionless/pathless requested names, extension-bearing substitutes, and
  explicit replacement diagnostics. Adapted these into a browser-neutral
  pre-registered immutable catalog, strict bounded parser, and an asynchronous
  style-driven desktop discovery adapter whose ordering is caller-owned; rejected
  synchronous file IO during snapshot compilation, undocumented comment syntax,
  platform-global registry state, unreported alternate-font use, and treating a
  missing mapped font as missing original content. The current catalog-application
  seam is deliberately SHX-to-SHX; cross-kind SHX-to-TrueType FMP policy requires
  a later unified resolver contract.
- [Autodesk vertical text-style behavior](https://help.autodesk.com/cloudhelp/2021/ENU/AutoCAD-Core/files/GUID-32786109-F454-47DD-AA4C-FB8C37F4430D.htm)
  and [Text Style dialog contract](https://help.autodesk.com/cloudhelp/2024/ENU/AutoCAD-Core/files/GUID-1ED81E98-6463-4574-875F-183C8280C4AC.htm):
  adopted vertical SHX/Big Font mode as a distinct font capability and retained
  the explicit ordinary-TrueType vertical-style gate; rejected synthesizing
  vertical Latin TrueType layout for a contract Autodesk reserves for vertical
  SHX/Big Fonts and supported Asian vertical faces.
- [Skia shaped-text design](https://docs.skia.org/docs/dev/design/text_shaper/),
  [SkParagraph decoration declarations](https://skia.googlesource.com/skia/+/7a1bf999357aa755768f7b72265b91eea5c2758c/modules/skparagraph/include/TextStyle.h),
  and [Skia text guidance](https://skia.org/docs/user/tips/): adopted separation
  and reuse of shaping, formatting, and positioned-glyph drawing; retained the
  existing ProGPU/HarfBuzz implementation instead of adding another text stack.
- [DirectWrite resource/layout model](https://learn.microsoft.com/en-us/windows/win32/directwrite/getting-started-with-directwrite),
  [DirectWrite strikethrough renderer contract](https://learn.microsoft.com/en-us/windows/win32/api/dwrite/nf-dwrite-idwritetextrenderer-drawstrikethrough),
  and [Direct2D geometry realizations](https://learn.microsoft.com/en-us/windows/win32/direct2d/geometry-realizations-overview):
  adopted device-independent semantic/layout results, device-dependent retained
  resources, and explicit flattening-quality tests; rejected fixed realizations
  as the only representation for unbounded CAD zoom.
- [Win2D cached geometry](https://microsoft.github.io/Win2D/WinUI3/html/T_Microsoft_Graphics_Canvas_Geometry_CanvasCachedGeometry.htm)
  and [Win2D text-layout range methods](https://microsoft.github.io/Win2D/WinUI2/html/Methods_T_Microsoft_Graphics_Canvas_Text_CanvasTextLayout.htm): adopted pay-
  once/draw-many retention, device identity, and range formatting; rejected per-
  frame creation and world-coordinate clipping limits.
- [WebRender overview](https://github.com/servo/servo/wiki/Webrender-Overview)
  and [current profiler counters](https://github.com/servo/webrender/blob/main/webrender/src/profiler.rs):
  adopted serializable retained display data, off-thread scrolling/scene work,
  visibility stages, and explicit upload/cache/memory counters.
- [Vello retained scene vision](https://github.com/linebender/vello/blob/main/doc/vision.md),
  [encoding roadmap](https://github.com/linebender/vello/blob/main/doc/roadmap_2023.md),
  and [glyph-run encoder contract](https://docs.rs/vello/latest/vello/struct.DrawGlyphs.html):
  adopted transform-independent analytic encodings, retained fragments, GPU
  transforms, typed resources, and glyph runs; adapted to ProGPU generations.
- [Parley text stack](https://github.com/linebender/parley) and
  [layout model](https://github.com/linebender/parley/blob/main/doc/concept.md),
  and [decoration metrics contract](https://docs.rs/parley/latest/parley/layout/struct.Decoration.html):
  adopted reuse of font context, Unicode analysis, shaping, line layout, and
  positioned results; kept CAD text styling outside Unicode shaping identity.
- [HarfBuzz shaping plans/caching](https://github.com/harfbuzz/harfbuzz/blob/main/docs/usermanual-opentype-features.xml)
  and [cluster contract](https://harfbuzz.github.io/clusters.html):
  adopted reusable shaping inputs/results keyed by font, direction, script,
  language, features, variations, and content; no CAD-specific glyph remapping.
