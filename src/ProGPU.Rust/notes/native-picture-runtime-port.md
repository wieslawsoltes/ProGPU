# Native retained nested pictures

Implementation only. No compiler, tests, GPU/browser execution, fixtures or benchmark validation ran.

`engine_resources::pictures::NativePictureRuntime` is the actual shared image/mask/shader source cache. `NativePictureExecutorFactory` and `NativePictureExecutor` require the owning native ordered renderer: preflight consumes a complete `NativeSceneResourcePlan`; execution receives that same plan, exact physical frame, actual `NativeRenderAttachment`, ordered same-device external image owners and an independent nested runtime. Success requires a real child submission. There is no default renderer, fabricated pixel source or CPU raster fallback.

The source-selected parent format is preserved for **all** captures. Original `create_semantic_picture_binding` creates both image-output and mask attachments with `engine.target_format`; it does not hardcode RGBA8. Native image page and shader resolver contracts now pass that actual format. Standalone `NativeImagePageEngine::new` retains its existing explicit RGBA8 domain; parent scene construction uses `new_with_target_format`.

Image history retains the original eight-entry / 64MiB FIFO, exact descriptor/presentation/engine flags, complete external table view identities, full nested bytes and append-only admission. Actual suffix reuse copies the previous GPU texture into a fresh owned backing, submits that seed copy, then renders only the original command suffix with PreserveTarget. Exact full captures return their old texture without compilation or submission. Mask history retains the independent 64-entry / 64MiB FIFO and original generation-independent scene comparison. Both histories reject external dependencies recursively and preserve original descriptor cost of four bytes per pixel plus owned stream bytes. Actual output allocations additionally report their selected-format physical byte count. Shared pure eligibility/comparison helpers were extracted from the existing mask cache; external resolvers keep that cache, while this runtime declares ownership to avoid retaining the same raster twice.

Each child retains its exact installed immutable stream and resource scope. A distinct capture increments scope only through the existing original collision helper; exact snapshots retain their identities. The complete CPU resource candidate is reused for an unchanged child frame and metadata. Read-only preflight snapshots retain those identities without mutating renderer state. Successful rendering publishes child state and cache entries last; failures preserve the previous source snapshot/scope and cache entries. Seed/output/external owners survive actual queue completion, including a renderer failure after submission. Eviction only drops this cache lease and never destroys independently retained captures.

`NativePictureImageCapture` reports actual preparation work, replacing prior per-source preflight estimates in the image page. `NativePictureMaskCapture` supplies actual newly allocated output bytes separately from additional child metrics. Shader/image preparation includes newly allocated output once; all preflight callbacks exclude output already budgeted by their consumer. Retained warm sources report zero allocations/uploads. Full child `FrameMetrics`, render/compute work and live-blur work remain distinct. `begin_work` / `work_metrics` retain a separate invocation subtree diagnostic; `mask_work_metrics` identifies the exact direct-mask subset already carried by outer mask metrics, and `output_metrics` accounts only this runtime's capture attachments. This does not add child draw/brush/style/coverage fields to the original parent `FrameMetrics` contract. The original child submission delta (including an append seed copy) remains available separately for queue accounting.

The runtime uses the original bounded nested-source policy and actual same-byte cycle guard; no provider callbacks occur under its short registry lock. All texture/view/source owners are safe Rust values; no pointer emulation or unsafe was added. The actual root/child renderer implementation is integrated independently by the parent task. Source arithmetic, pixels, browser/native providers, package/application gates and performance remain unqualified until the later validation phase.

## Original SHA-256

| Source | SHA-256 |
| --- | --- |
| `src/ProGPU.Native/src/Scene/progpu_native_semantic_picture_mask_resources.cpp` | `ad9f661c3dd6cac4d612a28ff8b24124833b1b15af2f81d32c6e82c96106432a` |
| `src/ProGPU.Native/src/Scene/progpu_native_semantic_update_execution.cpp` | `ef4cb61079f0367f98dc11940277fff810728a54a6058b13215bd3e016cfa4d5` |
| `src/ProGPU.Native/src/Scene/progpu_native_semantic_replay.hpp` | `daa885dc4c4238e9bcd4ce8df811a61030319b07a0186bd099f167005dce876b` |
| `src/ProGPU.Native/src/Scene/progpu_native_semantic_identity.cpp` | `5087c7ce66ee7201cadd1617eb90c01801b0d1e7dee5a35e87c2fd7c503e52ba` |
| `src/ProGPU.Native/src/Scene/progpu_native_semantic_layer_mask_resources.hpp` | `2c221b1bf9714f8d8557dea7600ad5ba3c2cf305fd35c822cc99e0fbf7cbd4c5` |
| `src/ProGPU.Native/src/Scene/progpu_native_semantic_state.cpp` | `2b801e9cc4673fa49092bdee28ac7c6275672c000f635798ba5767d759b30dd5` |
| `src/ProGPU.Native/src/Backend/progpu_native_child_engine.hpp` | `479eeaa671ac8e38ece17ae2f09b51b36ebb57ba927e5dac3f39be0dab08aaa6` |
| `src/ProGPU.Native/src/Backend/progpu_native.cpp` | `e124d2f9c53fe67c9fc8ab3b17094514dda00dcb28888678a8eae3fcd331c989` |
