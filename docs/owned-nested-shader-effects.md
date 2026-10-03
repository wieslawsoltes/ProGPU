# Ordered owned nested ShaderEffect preparation

`OwnedShaderEffectSource` owns an immutable original source capture/rebase and
one typed `IShaderEffectPreparation` recipe. The recipe records source data before
rendering; it does not rediscover mutable source UI at draw time. Its `Prepare`
method receives the actual `ShaderEffectPreparationContext`: compositor/device,
stored projection, normalized physical viewport, target dimensions, semantic DPI
and derived complete implicit-input frame. Returning successfully transfers one
`OwnedShaderEffectParameters` generation to that preparation transaction.

`DrawingContext.DrawOwnedShaderEffect` retains a picture clone and an independent
source reference, then records one existing `DrawVisual` command at the exact
stream position. It does not attach children after the source commands, flatten
the subtree on each replay, or change ordinary borrowed `DrawVisual` ownership.
The picture's existing retained-resource machinery carries these references
through recorder completion, picture clones and parent recordings. The source
and caller picture may be disposed after successful recording.

## Actual target and ordering

Preparation occurs in the picture effect-task prepass or at the embedded visual
boundary, before consumer geometry/command encoding. Sampler pictures can be
realized on the GPU during this preparation; it is not an arbitrary callback
from a shader draw. Recursive sources and depth beyond 256 fail explicitly.
Each child's preparation observes its own actual offscreen target. An outer
window's projection/DPI is never substituted for a physical brush or raw cache
target. Raw caches keep their independent source-to-raster policy. The managed
source capture's historical affine basis remains unchanged; this is not a claim
of original Microsoft affine or fractional pixel qualification.

The prepared result snapshots shader source/key, constants and sampler register
metadata. Implicit input remains compositor-owned; an explicit `Texture` on the
parameter snapshot is rejected. Null sampler placeholders remain implicit.
Nonnull samplers must provide genuine owned leases, and must belong to the
actual device before capture starts. No borrowed constructor is reclassified as
owned. `OwnedShaderEffectTexture` transfers one completed fresh texture to the
existing context retirement coordinator; `FromOwnedTexture` and `CloneOwned`
retain exact generations independently. Neither factory freezes mutable source
pixels or permits callers to keep writing a published texture.

## Isolation and retirement

Each recording/actual-target pair has a private visual, immutable parameter
snapshot and separate input texture. Preparing the same recording for a second
target cannot mutate the first draw's parameters or resize its referenced
texture. Keys retain exact source identity, compositor/device identity, complete
target mapping, semantic DPI and the resolved capture frame. A candidate is
published only after preparation and its implicit-input capture both succeed;
failed candidates also roll back newly introduced nested variants without
discarding earlier generations. Cleanup never replaces the original failure.

Completed frames keep only target variants actually used in that frame. This
uses the compositor's existing main/offscreen frame boundaries rather than an
unbounded resize history. Independent draw/compiled-frame leases can outlive
cache eviction and source recording disposal. Existing submission-aware texture
retirement still owns GPU completion; no counter or timeout is treated as a
fence. Source disposal is explicit and normal picture-retirement work, never a
new finalizer callback. Owned sampler finalizers only enqueue, and the existing
creating-thread shutdown registry retires their textures even if parameters
survive. Old cache-sampler and borrowed texture constructors keep their policy.

## Architecture research and scope

The implementation reuses ProGPU's own retained picture leases, embedded visual
ordering, source capture frame, effect task prepass and context retirement.
The following primary architecture references inform separation of ownership,
recording and frame preparation; no foreign implementation was copied:

- [Skia canvas layers](https://api.skia.org/classSkCanvas.html): ordered saved
  transform/clip state is distinct from allocation bounds. Do not turn bounds
  into a source clip or reorder effect scopes as appended children.
- [Direct2D layers](https://learn.microsoft.com/en-us/windows/win32/direct2d/direct2d-layers-overview)
  and [Win2D command lists](https://microsoft.github.io/Win2D/WinUI3/html/T_Microsoft_Graphics_Canvas_CanvasCommandList.htm):
  reusable storage/retained device resources do not erase each layer's state.
- [WebRender](https://firefox-source-docs.mozilla.org/gfx/RenderingOverview.html):
  retained scene input and target-dependent frame preparation are separate,
  including their spatial/clip state.
- [Vello scenes](https://docs.rs/vello/latest/vello/struct.Scene.html): ordered
  scene resources guide this retained approach; copying an entire child stream
  per draw is not the chosen ownership mechanism.

Text/shaping policy and the primary text references listed in
[managed source capture](managed-source-effect-capture.md) remain unchanged.
There is no new font fallback, variable-font policy, per-glyph callback, CPU
effect implementation, renderer fallback or worker/device-selection policy.
Recording clones shared command storage and adds bounded resource references;
target preparation is lazy and reused only for identical immutable input and
actual target. No startup, latency, allocation, residency or throughput benefit
is claimed without final binary measurements.

## Authored controls

`OwnedShaderEffectTextureTests` contains nineteen authored configurations for
metadata snapshots, partial-candidate cleanup, picture-clone ownership, off-thread
drains, actual context shutdown and queue-only abandoned owner/parameter release.
The real-device configurations use both existing owned providers; pure controls
do not claim GPU behavior.

`OwnedShaderEffectRecordingTests` adds eight configurations: four pure lifetime,
stream-order and atomic-failure facts, plus four actual-provider configurations
covering successful two-target use and deliberately reused parameter ownership.
One retained command is drawn directly at XY(2,1) and in a legacy offscreen target
at XY(1,1), with unchanged semantic DPI and independently specified complete
128-by-64 pixels. Cold/warm replay requires exactly two preparations. The invalid
reuse case retries the earlier target after rejection to detect destructive
rollback. Existing source, sampler and scalar controls remain unchanged.

All controls remain unexecuted. No build, syntax check, verifier, probe, GPU/UI
run, VM or CI request was performed. Full provider/package/source and original
application gates, including numeric/affine comparison and performance
qualification, remain open.
