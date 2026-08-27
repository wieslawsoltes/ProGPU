# ProGPU.CAD Feature Matrix

Status values: `planned`, `foundation`, `in progress`, `verified`.

| Area | Required result | Status | Verification gate |
|---|---|---:|---|
| Dependency | ACadSharp fork pinned as a submodule | foundation | clean recursive checkout and build |
| IO | DXF/DWG stream open with progress, diagnostics, cancellation, and initial byte/entity limits | foundation | version/corrupt/fuzz fixtures and path host adapter |
| Save | caller-owned stream save with version/certification gates | foundation | atomic path replacement and advertised-version semantic round trips |
| Model | ACadSharp `CadDocument` authority with locked callbacks and content/saved generations | foundation | broader edit transaction and concurrency tests |
| Editing | typed atomic commands, undo/redo, dirty generations | planned | command property/fuzz tests |
| 2D | all standard entities, blocks, XRefs, hatches, dimensions, images | planned | multi-DPI/zoom image and semantic tests |
| 3D | meshes, surfaces, solids, ACIS SAT/SAB, orbit/depth | planned | matched managed/native 3D fixtures |
| Text | complete ProGPU Unicode/OpenType text stack | planned | shaping/fallback/color/variable-font regressions |
| SHX | regular, Unicode, Big Font, shapes, substitution | planned | Autodesk-spec conformance and corpus fuzzing |
| Rendering | retained analytic GPU scene with incremental chunks | planned | zero-allocation stable replay; p50/p95/p99 |
| Camera | uniform-only pan/zoom/orbit after compilation | planned | entity-count-independent CPU counters |
| Line quality | model cosmetic and paper/plot physical lineweight | planned | visual + physical print measurement fixtures |
| Native parity | equivalent managed/native behavior and quality | planned | differential scene/pixel/performance suite |
| Desktop app | standalone ProGPU.CAD desktop viewer/editor | planned | automated UI smoke and representative workflows |
| Browser app | browser/AOT viewer/editor with streamed IO | planned | browser AOT build and UI/performance smoke |
| Printing | layout/viewports/plot styles/preview/output | planned | deterministic print-plan and output comparisons |
| Export | vector and raster output with text/line fidelity | planned | round-trip and image-quality baselines |

This matrix is a coverage index, not a completion claim. A row becomes
`verified` only when its stated gate covers the full required result.
