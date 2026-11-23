# Native scene updates and retained cache identities

Implementation only. No compiler, tests, fixtures, browser, GPU execution or
benchmarks were run; no build/capture artifacts were generated.

| Original owned source | SHA-256 |
| --- | --- |
| `src/ProGPU.Native/src/Scene/progpu_native_semantic_update_execution.cpp` | `ef4cb61079f0367f98dc11940277fff810728a54a6058b13215bd3e016cfa4d5` |
| `src/ProGPU.Native/src/Scene/progpu_native_semantic_render_execution.cpp` (presentation/family/bundle selection and output keys) | `c0325d3d72f30d79f386cfc0619f84213090b67a3a203e9e69ac4e4add4a9dae` |
| `src/ProGPU.Native/src/Scene/progpu_native_semantic_effect_cache.hpp` | `4334b792e2b3562b76182599ae70e9c32477d21cd67668efb8eafd3e1e537667` |
| `src/ProGPU.Native/src/Scene/progpu_native_semantic_effect_cache.cpp` | `a0d006807459e2887e1f2944722b052834a1a0935d2dd269c12165ac9b6a2a4d` |
| `src/ProGPU.Native/src/Scene/progpu_native_semantic_identity.cpp` (existing original Rust identity producer reused) | `5087c7ce66ee7201cadd1617eb90c01801b0d1e7dee5a35e87c2fd7c503e52ba` |
| `src/ProGPU.Native/src/Scene/progpu_native_semantic_identity.hpp` | `af028628f0ac255d89f5d7d554ef2d403e7187af90a51524152adf24564f9d61` |

`NativeSceneCache` owns one actual device/resource scope, immutable scene snapshot
and actual external-image table. Scene preparation first applies original exact
byte equality, then full native admission and same-scene/resource generation
rules. Reused snapshots preserve the original metrics flag; generation rejection
preserves the successful candidate metrics and changes only its generation/error
offset diagnostics. Raw whole-stream FNV retains zero, while original family and
presentation hashes keep their separate policies. Candidate owner/epoch checks
prevent a stale publication after another committed source update.

External candidates reuse `NativeExternalImages` whole-table admission and exact
ordered resource/generation/role/view/extent comparison. An identical table
publishes nothing; a changed table returns the original bundle/glyph-paint/image/
3D invalidation set and retains the new actual views. Existing leased generations
remain alive. Device loss is terminal for GPU reuse/external updates, but a later
CPU snapshot update does not release the retained old GPU objects; terminal owner
destruction remains separate. Rust exclusive mutation supplies the owned cache
boundary, without foreign handles or callbacks under a registry lock.

`NativeReplayCacheIdentity` carries the independent original brush, text-style,
analytic, path, glyph, image and 3D identities. Ordinary page keys retain target
size/DPI, draw counts, path Boolean count and the full glyph style/paint/color
inventory guards. The bundle hash appends exactly the original partial-damage
boolean and, only when active, its resolved physical damage rectangle. Required
path/glyph GPU hashes remain independent conditions. Installing a plan computes
these values once; warm lookup and each layer key do not rehash the stream.

`NativeLayerCacheSelection::{push,pop}` consumes that identity, original replay
metadata and an actual `NativeLayerTarget`. Retained content uses its own
presentation-adjusted content revision/cache identity and selected color/effect
generation; other effect output uses the presented full-scene hash and original
effect-operation override. Texture extent/generation come from the actual owned
target. This helper never asserts pixels are ready. The existing
`output_updates` / `record_encoded_output` / `commit_submitted` journal owns hit
selection, successful encoded output and publication after real submission.

The connected one-entry CPU plan/GPU page lease requires the same immutable
scene and plan owners, complete frame/options and actual external bindings.
Prepared pages become reusable only through `commit_prepared_submitted` after
coverage submission; later live image-plane/effect work remains mandatory.
Cold upload metrics are not relabeled warm work. This entry supports exact-owner
warm reuse; cross-generation family rebinding is a separate integration stage,
even when its original family keys compare equal. Changing a combined external
generation retires this combined entry rather than pretending unaffected
subfamilies were independently republished. Provider, source/application, browser,
package, pixels and measured cold/warm performance gates remain independent.
