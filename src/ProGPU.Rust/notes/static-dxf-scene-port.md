# Retained static DXF Scene execution

`Scene::draw_static_dxf(StaticDxfDraw)` records the actual immutable buffer owner
at its original painter position. `StaticDxfRoute::Extension` is the typed
extension0 route; `Legacy` preserves the direct DrawStaticDxf branch's refusal
to enter a ClearType-suppressing source capture. The check precedes null/empty
source shortcuts. Portable archive bytes cannot supply either original external
payload, so archive replay/capture does not invent one.

Source placement composes only the final consumer transform. The buffer retains
its source command inventory, material generation, static zoom and compilation
DPI. The original renderer consumes no outer semantic opacity. The actual target
projection, bookkeeping CanvasSize and DPI come from the renderer frame; static
viewport composition and identity View remain independent of the ordinary
target's late camera. Original inner static clip records are not replayed as
consumer scissors. The enclosing ordered pass retains the actual binary scissor.

Flat and complete layer-tree admission includes all selected generation buffers,
coverage textures and target uniforms before preparation. Immutable source
identity deduplicates generation cost within a target; separate layer accounting
is conservatively bounded. Source-device admission also visits static resources
before empty source capture publication. Prepared draws own their uploaded
generation, frozen pipelines and typed mask resources after engine replacement,
failed/canceled later preparations and source recording release. Queue completion
retains the prepared owners through the common submission path.

Vector/Text use the original outer blend policy and one resolved mask. Chains and
area masks use the existing ordered coverage materialization. Only proven local
R8 coverage origins are localized on a bounded target; direct completed
root-coordinate inverse/analytic records there remain explicitly unsupported.
Hatch retains its independent canonical mask/blend policy. Null and fully clipped
draws produce no encoded draw. `PreparedScene::static_dxf_metrics` separates
source uploads/cache hits from target work; shared resident atlas bytes and cold
path work are not counted again for repeated occurrences.

The Scene adapter adds no source compiler, source callback during encoding, readback,
shader rewriting or CPU coverage fallback. The bounded R8 mask-source renderer's
separate unsupported extension-family gate remains intact. Existing static
compiler source/provider boundaries remain documented in `static-dxf-port.md`.
Source formatting only; no compilation, tests, browser runs or performance/pixel
qualification was performed.

| Original source | SHA-256 |
|---|---|
| `Scene/Extensions/StaticDxfExtensionPipeline.cs` | `fe89047ec3bfa4755ab934fa369ce305807551bbf824484544f389220ab3fadb` |
| `Scene/Compositor.cs` | `a45a99f135806d248fc3792dcbdb5ebe17c094821c6aed080dcdedf4025237cd` |
| `Scene/DxfStaticBuffer.cs` | `1f150883f9dfa947debc9d1ac1c615eac663c8c08bbf5fc81d770148754da4bf` |
