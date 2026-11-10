# Native device loss and retained CPU recovery

Production implementation is in `native_scene/engine_gpu/recovery.rs`, integrated
with the actual renderer and owned picture-child chain. Source formatting only;
no builds, tests, device-loss injection, browser runs or qualification were run.

`NativeSceneRenderer::mark_device_lost` retains owner-thread admission, terminal
loss, one generation increment per actual engine loss, and recursive notification
of the existing owned child. It performs no poll, submit, cache eviction or GPU
release. Normal render entry points reject a lost or wrong-thread engine before
resource callbacks; `require_live_device` exposes the distinct native status.

`update_scene` and `update_admitted_scene` retain CPU updates after loss, exact-byte
reuse before parsing, whole-source admission before generation checks and atomic
publication. Such updates preserve stale GPU owners until their normal terminal
lifetime. Actual scratch picture engines alone retain the original independent
capture identity reset; roots retain immutable same-ID generations and resource
generation ordering.

`recreate` builds a private renderer on the supplied actual device/queue, enforces
the same target format and copies only original scene bytes/header/metrics/hashes,
CPU brush/style pages and loss generation. The source stays lost. External image
bindings, child engines, frame identities, GPU buffers/textures/atlases, pipelines,
bundles, layer pixels and submission state are fresh. `render_retained` requires
new external owners and an actual target/frame; it repeats complete device-limit
and resource admission. Original brush/text-style family hashes permit immutable
CPU array reuse with the exact new scene owner, followed by fresh GPU uploads.

`NativeSceneRecoveryError` preserves native loss/thread/admission/generation
diagnostics without changing the crate-wide error enum. Existing rendering APIs
continue to report lifecycle rejection as `Error::ResourceDomain`; callers can
query the precise lifecycle status or use the retained-render/recreation methods.
Notification is explicit, as in the source API: no caller-owned device callback
is replaced, and a successful recreation does not prove recovery on any provider.

The original public native constructors admit only RGBA8/BGRA8 and their sRGB
forms. Picture children must share the parent's actual format. Source inspection
found no reachable standalone R8 renderer constructor, so no new R8 admission was
introduced. Dedicated R8 mask/clip/glyph-paint attachments remain independent.

Original SHA-256 provenance:

| Original source | SHA-256 |
| --- | --- |
| `Backend/progpu_native_device_recovery.cpp` | `8b4c4183c8b18fd7642dbe58342aec1c775d4e6e8a202f2782256254c1cccb54` |
| `Backend/progpu_native_device_recovery.hpp` | `68150297f34422e54323da8637e26f728ac9e64f0df2e3ea83ec6630db27cedd` |
| `Backend/progpu_native.cpp` | `e124d2f9c53fe67c9fc8ab3b17094514dda00dcb28888678a8eae3fcd331c989` |
| `Scene/progpu_native_semantic_update_execution.cpp` | `ef4cb61079f0367f98dc11940277fff810728a54a6058b13215bd3e016cfa4d5` |
| `Scene/progpu_native_semantic_picture_mask_resources.cpp` | `ad9f661c3dd6cac4d612a28ff8b24124833b1b15af2f81d32c6e82c96106432a` |
| `Scene/progpu_native_semantic_render_execution.cpp` | `c0325d3d72f30d79f386cfc0619f84213090b67a3a203e9e69ac4e4add4a9dae` |
