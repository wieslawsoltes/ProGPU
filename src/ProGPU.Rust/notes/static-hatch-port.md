# Static DXF Hatch producer

Implemented the original Hatch-only static producer in
`picture_archive/static_hatch.rs` and its GPU children. This is independent of
ordinary retained `DrawHatch`: ordinary picture compilation still emits Vector,
explicit extension3 emits Hatch, and static compilation emits Hatch for both.

`StaticHatchBuffer::compile` retains the complete `ArchivePicture` and side
arrays; `compile_commands` retains the original no-provider command list. Both
build one private candidate, sharing exact32-byte boundary records,48-byte
segments,256-byte materials and32-byte auxiliary records across ordered draws.
The existing original boundary producer is shared, including rational-weight
limits, ignored Arc segments, explicit closing line and control-point bounds.
Null/empty commands retain their source position; a brush is registered before
the zero-boundary return. Vertices retain original quad/index order, source
coordinates, float record ordinal and `9 + 200` static shape. The static compiler
does not apply the ordinary late-camera `+100` rule.

The original static compiler resets semantic alpha to1 and has no branches for
opacity, geometry/opacity-mask or blend commands. This route retains those
commands without applying them. Inner binary clips are retained as source data,
but the static Hatch execution does not apply their saved drawcall clip: it keeps
the outer actual scissor. `PreparedStaticHatches::draw(pass,None)` preserves that
scissor; an explicit physical clip is checked against the target before encoding.
The dedicated pipeline retains original straight SrcOver and no generic mask.

`StaticHatchEngine::upload` admits every buffer size before GPU allocation and
publishes one immutable generation. A weak lookup reuses only that exact live
generation. `UploadedStaticHatches` owns all source/GPU storage; expired lookup
entries do not retire live owners. `prepare` creates a separate224-byte viewport
snapshot and five-binding group, binding the original192-byte shader header.
`StaticHatchViewport` preserves the original zoom/pan/screen-center expression;
target preparation composes explicit-camera times placement, keeps View identity,
and supplies the actual projection independently. Source static zoom retains the
original finite-positive/default1 normalization. Per-format1/4-sample pipelines
use unchanged canonical Hatch WGSL and default compiler options. Forced CPU or
compute execution rejects; this is a GPU fragment path.

Draw performs no source callbacks, uploads or submissions. A prepared target
retains its pipeline even after engine drop; preparing another target cannot
rewrite earlier uniform/resources. `retain_submitted` extends that owner through
the caller's actual queue completion. Metrics distinguish upload work, target
uniform work, resident bytes, empty batches and weak-cache hits.

Remaining boundary: mixed static DXF vector/text/texture/ACIS/line/spline and
external extension providers reject atomically here. This API does not claim a
complete DxfStaticBuffer replacement or a Scene static-extension0 adapter.
Original shaders and source scopes were inspected; no build, test, browser,
benchmark or pixel validation was run, per implementation-first instruction.

Original SHA-256:

| Source | SHA-256 |
|---|---|
| Scene/Compositor.cs | `a45a99f135806d248fc3792dcbdb5ebe17c094821c6aed080dcdedf4025237cd` |
| Scene/DxfStaticBuffer.cs | `1f150883f9dfa947debc9d1ac1c615eac663c8c08bbf5fc81d770148754da4bf` |
| Scene/StaticCompilationContext.cs | `4b50d72ae384249b2c1ee4dd09f7062387b55a2ecd3a64046222b914335d89be` |
| Scene/Extensions/HatchExtensionPipeline.cs | `c8ac3e1dff8d8329d25514fee31d2c27d76f80ad833872778199811eb6bea68f` |
| Scene/Extensions/StaticDxfExtensionPipeline.cs | `fe89047ec3bfa4755ab934fa369ce305807551bbf824484544f389220ab3fadb` |
| Scene/Shaders/Hatch.wgsl | `9895f6f7f4b5bb1f213f9a95625d8de8603ff0f7e32e2881a4b930d456714ab5` |
| Scene/CompositorOptions.cs | `d2260f91392e163ab1e47909bed6a14033d38fe184370ab7640fc5ed7ddb9b97` |
