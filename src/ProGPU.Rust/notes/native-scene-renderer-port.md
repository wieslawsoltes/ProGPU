# Owned native scene renderer

Implementation only. This increment has been source-formatted, without builds,
compiler/lint checks, tests, shader validation, native/browser execution,
benchmarks or new fixtures. All original qualification gates remain open.

| Original owned source | SHA-256 |
| --- | --- |
| `src/ProGPU.Native/src/Scene/progpu_native_semantic_render_execution.cpp` | `c0325d3d72f30d79f386cfc0619f84213090b67a3a203e9e69ac4e4add4a9dae` |
| `src/ProGPU.Native/src/Backend/progpu_native_backdrop_execution.cpp` | `6c516aaa3b38b1e3987d5320d8a6e0c140e1113b5face5df8eb3b0709a0acc90` |
| `src/ProGPU.Native/src/Backend/progpu_native_engine.hpp` | `635ca99c4af1d23bc713ba8ca7fadd4187be15534a1979fa7ef705ac69485396` |

`NativeSceneRenderer` connects native CPU scene/resource admission to actual
immutable family GPU pages, exact layer-slot assignments, ordered replay and
queue submission. The default atlas starts at the original 1024 and retains
actual growth; antialias padding starts at the original 1.5. Engine glyph flags
remain explicit. The main crate's unsafe-code prohibition remains intact.

The CPU replay plan and GPU targets retain the same assignment owner. Complete
family/mask/shader-source preflight precedes candidate preparation. Actual
root/slot frames use the allocation dimensions; source presentation and logical
clip frames retain their independent original values. Pooled RGB attachments
compile a separate physical-size packet without rewriting original positions,
source frame, scissor, no-ink slots or opacity proof. Forecasted cached bodies
omit one-shot RGB allocation; the actual encoder independently selects its
submitted output journal.

Merged vector/path/glyph ranges preserve page-global offsets and first-command
provenance. Image/3D draws share the original ordered clip/depth spans. Actual
bundles retain all page, frame, glyph-paint and mask owners. Layer compositions
bind the original four-vertex slice and shared six indices with zero base
vertex. Root composition uses the separate uncertified frame. Backdrop capture
clears the actual pooled child, copies only the original logical parent region,
optionally fixes only alpha, runs the original filter chain and resolves the
filtered logical extent. Source draw counters remain separate from actual
encoded pass/draw counts.

The pass owner uses wgpu's safe `forget_lifetime` API and denies encoder access
while a pass is active. Pass destruction precedes encoder destruction on every
cancellation path. Clear, RGB destination copies, effects, advanced resolves and
final isolated-root replacement execute in original source order. No source
callback runs during replay encoding. Actual coverage command boundaries remain
separate ordered submissions. Only successful main submission publishes the
target pool/output journal; queue completion retains all actual resource leases.

Nested pictures use this same renderer through the typed runtime executor.
The actual parent's vector pipeline and compatible layouts are shared through
owned WebGPU handles. Child scene/layer/cache state remains independent and
retains the original shared-child admission policy. Captures use the source
selected target format; legacy effect output retains its independent RGBA8
contract. No unrendered attachment, generic scene substitute or C++ rendering
bridge can satisfy a capture request.

The same-owner scene cache retains a submitted immutable page generation.
Warm reuse skips its original uploads and coverage generation, while live image
source effects still encode. Full cross-generation family reuse and retained
bundle/shader/mask parameter reuse remain implementation work; this conservative
whole-page cache does not claim those source performance properties. Explicit
scene update APIs preserve raw snapshot identity, generation failures and
external view ordering independently of pixel output publication.

The complete standalone R8 child-renderer route remains an implementation gap:
native target metadata and raw R8 mask production exist, while the renderer's
eager mask/shader engine constructors currently admit only their color-target
formats. This is distinct from the connected parent-format picture-mask capture
and must not be described as R8 renderer parity.

Original FrameMetrics preserve the source's accounting categories; detailed
page and subtree metrics retain additional physical work separately. Existing
numeric, source-font, binary-scan, original Windows/provider/package/application,
native/browser pixel, device-loss, lifetime and performance gates remain required.
This authored integration is not evidence that those gates passed.
