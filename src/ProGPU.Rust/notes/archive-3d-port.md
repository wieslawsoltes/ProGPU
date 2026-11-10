# Original 3D picture producers and camera frames

Implemented `ArchiveThreeDDraw::{from_picture,from_command,line,acis}` and
`Scene::draw_archive_three_d`, preserving the complete raw command, immutable
picture float/edge arrays, source pen, model and semantic opacity. Inline versus
provider selection follows the called source helper: ordinary `DrawLine3D`
passes no provider, explicit extension 2 selects the first six declared provider
floats, and ACIS selects the actual provider range whenever a provider exists.
Typed `line` owns the original DataParam pen independently; this source factory
is not claimed portable through the archive's forbidden external DataParam slot.

CPU compilation retains the original four line vertices, six-index order,
32-byte ACIS edges, 96-byte records, exact edge/record ordinals and raw pen widths.
The original no-op ClampToClip remains a no-op. Raw DrawLine3D/DrawAcisSolid remain
Vector ranges. Only explicit extensions 2/1 select the unchanged canonical
Line3D/AcisSolid shaders. These dedicated extensions preserve original straight
SrcOver, actual binary scissors, no depth attachment and no generic mask/blend
inputs. They do not invent extrusion in the original Line3D shader or replace
ACIS records with generic stroke geometry.

Actual renderer targets retain the last source-selected camera, matching the
original compositor's shared frame uniform generation. Model and camera remain
independent, with +100 vertex flags and the source's different homogeneous
Line3D/ACIS routes unchanged. Prepared scenes own their camera buffer, pipeline,
bindings, full ACIS inventories and all source generations. Actual target format
belongs to each renderer's pipeline cache. No source callback occurs during
encoding. `PreparedScene::three_d_metrics` reports dedicated draws and actual
record, edge and buffer payloads.

Explicit Spline extension 4 now compiles the original spline geometry/material
and any resulting raster work while retaining its source owners and suppressing
GPU draw ranges: its original Render method is empty. Ordinary DrawSpline still
emits Vector draws. This preserves the source batching distinction rather than
promoting either raw or extension commands to another rendering route.

Remaining boundaries: general late-camera text/path and static DXF/Mesh3D
provider protocols are not fabricated. There is no original separate polyline3D
factory in this source inventory; the complete owned ACIS edge list and typed
line source are the available producers. Original line shader degeneracy and
raw-command generic batching remain source behavior, not repaired semantics.
Raw Archive-to-Canvas transport retains complete source bytes; arbitrary inverse
Scene capture of typed external 3D owners remains unsupported. Runtime, GPU,
browser, pixel, package and performance qualification are deferred as requested.
No compiler, test, benchmark or browser execution was performed.

SHA-256 source receipts:

| Original source | SHA-256 |
|---|---|
| `src/ProGPU.Scene/Compositor.cs` | `a45a99f135806d248fc3792dcbdb5ebe17c094821c6aed080dcdedf4025237cd` |
| `src/ProGPU.Scene/RenderCommand.cs` | `dff6b8f8c375dcb8b2a2bae357145416bd48d05a5c12ebdbbbe0710dd98858f8` |
| `src/ProGPU.Scene/Extensions/Line3DExtensionPipeline.cs` | `52a75bcff9891b5894926bedded7d2b6e3c6fb8c2000082d82a82ab545c1bd8e` |
| `src/ProGPU.Scene/Extensions/AcisSolidExtensionPipeline.cs` | `db5258e6b5a146eddac39fcfae1fb60ec912bcbbc77048f8ca01836540ae6651` |
| `src/ProGPU.Scene/Extensions/SplineExtensionPipeline.cs` | `b2c1aade24d8a3758fcd7eda27f433c8c409b69fd05620349f2ad81d8d7ea3c3` |
| `src/ProGPU.Scene/Shaders/Line3D.wgsl` | `a336e636714d9285e2f84339deb4e9c5c0eb3b68282ba8ed52e2920e60c5b4e6` |
| `src/ProGPU.Scene/Shaders/AcisSolid.wgsl` | `4bec38325cdc7c8aea3c38e6b985ed810555202b9a9e2b1de49f0beca895d82f` |
