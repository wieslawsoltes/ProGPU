# ProGPU.CAD Feature Matrix

Status values: `planned`, `foundation`, `in progress`, `verified`.

| Area | Required result | Status | Verification gate |
|---|---|---:|---|
| Dependency | ACadSharp fork pinned as a submodule | foundation | clean recursive checkout and build |
| IO | DXF/DWG stream open with progress, diagnostics, cancellation, and initial byte/entity limits | foundation | version/corrupt/fuzz fixtures and path host adapter |
| Save | caller-owned stream save with version/certification gates and deferred destination commit | foundation | atomic path replacement and advertised-version semantic round trips |
| Model | ACadSharp `CadDocument` authority with locked callbacks, atomic generation capture, and content/saved generations | in progress | broader edit transaction and concurrency tests |
| Editing | typed atomic commands, undo/redo, dirty generations | in progress | bounded generation-synchronized history plus translate/visibility/layer/lineweight/color/transparency assignment and single model-space add/remove commands, handle-reassignment-safe identity retention, cancellation, divergence, and branch tests implemented; broader multi-entity/property/tool command families and fuzz tests remain |
| 2D | all standard entities, blocks, XRefs, hatches, dimensions, images | in progress | line/circle/arc/ellipse/NURBS/lightweight and legacy 2D polyline/SOLID/3DFACE/TrueType TEXT plus bounded nested static INSERT/MINSERT snapshot and top projection implemented; MTEXT, dynamic blocks/XRefs/attributes, remaining families, and multi-DPI/zoom image tests remain |
| 3D | meshes, surfaces, solids, ACIS SAT/SAB, orbit/depth | in progress | immutable 3DFACE and legacy 3D-polyline records with exact 3D bounds implemented; shaded/depth compiler and matched managed/native 3D fixtures remain |
| Text | complete ProGPU Unicode/OpenType text stack | in progress | single-line TrueType TEXT now uses typed font resolution, retained ProGPU shaping/fallback runs, two-point Align/Fit scaling, metric-driven overline/underline/strike-through rectangles, conservative affine bounds, and vector glyph-run recording; add MTEXT formatting/columns/background, vertical styles, CAD font search/substitution UI, and broader bidi/color/variable-font regressions |
| SHX | regular, Unicode, Big Font, shapes, substitution | in progress | bounded immutable standard AutoCAD-86 parser, commands 0–14 analytic interpreter, reusable catalog/FMP policy, ordered bounded desktop discovery, standard control/placement layout, retained horizontal TEXT snapshot/path recording, and pinned compiled-fixture regression implemented; Unicode/Big Font contracts, decorations/vertical layout, conformance corpus, and fuzzing remain |
| Rendering | retained analytic GPU scene with incremental chunks | in progress | immutable typed streams, BVH, WCS rebase, affine analytic static-block/array expansion, retained shaped glyph runs, first retained scene, and JSON CPU baseline harness implemented; shared block fragments/instances, incremental chunks, and full GPU replay metrics remain |
| Camera | uniform-only pan/zoom/orbit after compilation | in progress | first recorded plan scene is camera-independent; GPU camera integration and entity-count-independent CPU counters remain |
| Line quality | model cosmetic and paper/plot physical lineweight | in progress | fixed device-space/hairline recording implemented; linetype, paper/plot, and physical output fixtures remain |
| Native parity | equivalent managed/native behavior and quality | planned | differential scene/pixel/performance suite |
| Desktop app | standalone ProGPU.CAD desktop viewer/editor | in progress | shared retained viewer, native open/save pickers, drawing-local plus explicitly ordered support-path SHX discovery, dynamic toolbar/status shell, and a real `floorplan.dxf` open/render smoke pass; editing tools and certified output remain |
| Browser app | browser/AOT viewer/editor with streamed IO | in progress | shared retained viewer and picker/download workflow compile through native-linked Wasm, and Release AOT publish passes; interactive browser IO and performance smoke remain |
| Printing | layout/viewports/plot styles/preview/output | planned | deterministic print-plan and output comparisons |
| Export | vector and raster output with text/line fidelity | planned | round-trip and image-quality baselines |

This matrix is a coverage index, not a completion claim. A row becomes
`verified` only when its stated gate covers the full required result.
