# Native scene preflight and resource integration

Implementation checkpoint only. Source formatting ran; no build, compiler, lint,
shader, browser, test, benchmark, fixture or package execution ran for this batch.

`NativeLayerExecutionPlan` retains the original whole-command layer/effect scan:
actual target traversal, cached owner recursion, layer and stable cache budgets,
explicit byte-axis clip exceptions, mapped presentation restrictions, parent-sized
mask costs and all versioned shader capture/input-picture/sample-frame gates.
Original v4 arithmetic remains distinct from v5/v6 input-picture admission.
The complete source generation remains retained; raw bytecode is not regenerated.
Final aggregation preserves separate material, coverage, binary snapshot, effect,
destination, transient and cache costs. Original partial-damage support falls back
only under the original layer/mask/destination/3D rules. The source unsigned
subtraction point remains explicit; public malformed counters reject before sums.

`NativeSceneResourcePlan` connects the complete source frame and layer admission,
independent brush/style identities, ordered typed-draw preflight and whole-scene
budgets before producing large output pages. Original vector, path, glyph, image
and 3D pages retain one scene owner and exact family frames. RGB occurrence slots
remain source indexed; an empty exact clip skips packet creation only after full
preflight, preserving the ordered replay barrier. Raster preparation keeps path
and glyph atlas domains separate and applies no extra glyph opacity.

`NativeScenePageEngine` preflights every ordinary family against the actual device
and external-view inventory, then prepares a single immutable candidate containing
materials and vector/path/glyph/image/3D resources. Hint-paint metadata admission
no longer requires allocating a sentinel first. Existing canonical pipelines and
source resource producers are reused. Coverage encoding returns every original
path split command buffer separately, before the remaining glyph/consumer encoder.
Actual submission remains the outer executor's responsibility; encoding does not
certify coverage residency. Live image Gaussian/plane work remains per replay.
Old prepared pages keep their exact source/material/view owners through completion.

The storage Clear packet retains the original straight color, actual assigned
attachment extent and alpha policy, and independent pixel-center binary clip.
Root-only damage is applied at the original point. The GPU consumer compares the
actual selected attachment and replay alpha state, and reuses the original
TargetClear shader/pipeline, one fresh16-byte uniform per occurrence and unblended
three-vertex pass. Source transforms, opacity, masks or allocation fringes are not
substitutes for storage coverage. `NativeRenderAttachment` retains the actual
base texture/view and optional external producer without destructive teardown.

Frame/material rebinding, layer allocation/cache publication and RGB original
kernels have separate notes. The complete outer ordered replay compiler/executor,
actual nested picture/shader realization and cache/invalidation integration are
still being connected. This checkpoint does not claim a complete native renderer,
source parity, original Windows/WARP safety, browser reliability or package
qualification. Forced CPU glyph modes remain independent unfinished producers;
no forced policy silently selects GPU coverage. All original pixels, numeric,
provider/compiler/source/application/lifetime/performance gates remain mandatory.

## Owned source provenance

| Original owned source | SHA-256 |
| --- | --- |
| `src/ProGPU.Native/src/Scene/progpu_native_semantic_render_execution.cpp` | `c0325d3d72f30d79f386cfc0619f84213090b67a3a203e9e69ac4e4add4a9dae` |
| `src/ProGPU.Native/src/Backend/progpu_native_target_clear.cpp` | `ff98709c7fb03998e23edc0ce5126ef47435d2a71b48250b3360e20ed7229f31` |
| `src/ProGPU.Native/src/Scene/progpu_native_shader_capture_frame.hpp` | `c7ad3ceb49fa6c5a86c31c8a956094aa88ab47878def8a59a3f58497199bb206` |
| `src/ProGPU.Native/src/Scene/progpu_native_shader_sample_frame.hpp` | `6e80a73d4056ccb26ca626156490a3139fca94e4ec9eefe993b627d7a00e3b29` |
| `src/ProGPU.Native/src/Scene/progpu_native_shader_effect_resource.hpp` | `fda73197f0b2e6d1d0f1ea14d19824a30760fd1dc4a458f479da4645ac6e8467` |
| `src/ProGPU.Native/src/Scene/progpu_native_shader_affine_frame.hpp` | `dbc3047321b3c73922e5f4f71c40315099479502de35843fca6544740fd556ed` |
