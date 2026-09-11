# Native retained-picture copy submission

## Package build blocker

The LibreWPF package Showcase requires the native renderer package, including both
wgpu-native and provider-resolved Dawn binaries. A clean macOS ARM64 build of
ProGPU `95f9049232b0cc37ad23fb170efdccc75797be50` with the pinned Dawn headers
`01addc4ba8a2915a061b7095a6768b512071ab96` failed compiling
`progpu_native_semantic_picture_mask_resources.cpp`: the seed-copy branch called
`wgpuQueueSubmit` directly despite `WGPU_SKIP_DECLARATIONS` in the Dawn profile.

## Connection fix and provenance

Submit the existing GPU texture-copy command through the original ProGPU-owned
`progpu_native_engine::submit` in `src/Backend/progpu_native_engine.hpp`, already
used by the layer/composite/brush-mask paths. That method selects the backend's
existing dispatch adapter, records the latest submission identity, increments its
counter once, and feeds the shared bounded retirement policy. Remove the branch's
separate counter increment. Release the command buffer after submission as before.

No shader, copying algorithm, resource format, ABI or GPU/CPU fallback changes.
This restores the existing submission contract in both C++ backend builds; the
managed renderer is not affected by this C++-only raw-call mistake and continues
using its existing context submission/retirement implementation. No third-party
implementation was copied or new rendering architecture introduced.

## Deferred qualification

`NativeRetainedPictureCopiesUseProviderAwareTrackedSubmission` guards the call,
single counter ownership and release ordering in the source. Native compilation
must include the Dawn adapter as well as wgpu-native; a successful wgpu-only build
cannot catch this missing provider dispatch. After feature freeze, execute the
existing retained-picture/update and submission-retirement tests, Dawn contract
and import/export gates, and package-mode application gates. Compilation alone
does not establish correct rendered output or lifetime behavior.
