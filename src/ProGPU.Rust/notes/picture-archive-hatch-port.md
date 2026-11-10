# Original analytic hatch GPU execution

Source SHA-256:

- `src/ProGPU.Scene/Extensions/HatchExtensionPipeline.cs`: `c8ac3e1dff8d8329d25514fee31d2c27d76f80ad833872778199811eb6bea68f`
- `src/ProGPU.Scene/Shaders/Hatch.wgsl`: `9895f6f7f4b5bb1f213f9a95625d8de8603ff0f7e32e2881a4b930d456714ab5`
- `src/ProGPU.Scene/Compositor.cs`: `a45a99f135806d248fc3792dcbdb5ebe17c094821c6aed080dcdedf4025237cd`
- `src/ProGPU.Backend/RenderPipelineCache.cs`: `c03d6d1648fbeb643150b660b685052e7bc6b2d944032b0a386a939482cc0ea4`

`ArchiveHatchDraw` retains the actual original command and privately compiles its
ordered boundary inventory. Legacy DrawHatch and built-in extension 3 share boundary
compilation but retain different original execution routes. The 32-byte record and 48-byte segment layouts preserve line,
quadratic, cubic, positive rational quadratic and positive rational cubic paths,
including original weight-bit words and double coordinate-scale limits. Control
points determine the original bounds; closure appends its final line only when
the original endpoint differs. Empty figures skip. Figure fill/stroke flags,
Boolean metadata and fill rules remain source-owned but are not consumed by this
extension. Its absent Arc branch neither emits geometry nor advances current
position. No generic arc or PathAtlas replacement was introduced.

`Scene::draw_archive_hatch` retains one immutable source. Compilation registers
the original brush even when no segments follow, then appends the original four
vertices/six indices with exact local bounds, UVs, shape kind 9 and float record
ordinal. Full record/segment/source generations travel through `CompiledScene`
and `PreparedScene`; source placement, opacity and supported archive capture
retain that same command owner. Rational paths have the original v1-v10 archive
writer rejection; direct typed hatch rendering does not invent a different wire.

The renderer includes canonical `Hatch.wgsl` unchanged, with its original five
bindings, exact 192-byte frame prefix, and the actual shared brush/auxiliary
buffers. Prepared generations upload boundaries once and own their buffers and
bindings through replay/submission; later scenes cannot overwrite earlier data.
`PreparedScene::hatch_metrics` exposes record/segment counts and actual boundary
and quad bytes; boundary uploads also participate in scene geometry counters.
Empty batches create no hatch pipeline or binding. Capacity, finite geometry,
float ordinal and actual device buffer limits reject before scene publication.

Ordinary CompilePicture DrawHatch calls only `SwitchBatch(Vector)` followed by
extension compilation (Compositor11319); it does not emit an extension drawcall.
It therefore retains the original generic Vector route. Explicit DrawExtension
id 3 selects the dedicated hatch pipeline. Both contribute to the same ordered
record inventory. The original static-DXF compilers at19256/19728 explicitly emit
Hatch extension drawcalls for DrawHatch; that separate producer is not inferred
from ordinary archive kind. Dedicated GPU resources are prepared only when an
actual extension range exists.

Original explicit-extension policy is deliberately distinct from generic Vector: its
pipeline always uses straight SrcOver; alpha-mask and per-draw blend fields are
not inputs to its shader. Actual ordered render-pass scissor still applies. The
current Renderer target contract is single-sample, matching original offscreen
execution; primary multisample integration and late GPU/static-camera execution
remain independent boundaries. They are not inferred from retained CPU frames.

No compilation, tests, fixtures, browser runs, benchmarks or rendering validation
were performed. Only touched source formatting was run; no result artifacts were
created. Original provider/application/pixel qualification remains separate.
