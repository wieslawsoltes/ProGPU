# Native retained-page identity

`NativeScene::content_hashes` ports the complete original family dependency walk.
Brush/style remaps retain global command ordinals; geometry pages retain only
their own consumed commands, effective state and currently active layers. Closed
or trailing scopes do not perturb unrelated pages. Resource references preserve
stable scene-owner/resource-ID/generation identities while excluding serialized
ordinals, payload offsets and scene generation. Original source coverage frames,
styled/material glyph dependencies, 3D material tables and every family seed are
retained. The private resource-scope epoch participates only when nonzero.

`picture_capture_changes_resource_scope` uses the actual owner and existing
generation comparison, without changing public wire identity. Presentation hashing
retains the original full-target/uniform-DPI fast path and otherwise appends all
six original viewport/DPI words, including exact float bits. It does not resolve
or infer a target frame.

`append_only_suffix` compares complete original resource/command prefixes after
the narrow native offset/size normalization. Only brush/style tables may extend.
External images and every 3D command reject color-only retained-prefix reuse;
exact prior payload prefixes and record bits remain authoritative. It consumes
two already admitted, balanced `NativeScene` generations.

Byte equality uses safe `wide::u8x16` on the same native NEON/SSE2 and browser
SIMD128 targets, with a bounded short tail. Other targets use the standard
optimized slice routine, matching the original fallback. Only the public API
declarations of the existing reviewed `wide` dependency were consulted; no
third-party implementation was copied. FNV is the original serial dependency
chain, with no allocation and wrapping unsigned multiplication. Hashing uses
fixed stack wire serialization, not Rust struct memory, float equality or casts.

Time is O(C*D+R+P) for command count C, bounded active depth D, resources R and
hashed payload bytes P. Additional storage is O(D), independent of scene size.
Actual renderer cache selection will consume these keys in the execution port;
this increment does not claim cold/warm upload or runtime qualification.

| Original source | SHA-256 |
| --- | --- |
| `src/ProGPU.Native/src/Scene/progpu_native_semantic_identity.cpp` | `5087c7ce66ee7201cadd1617eb90c01801b0d1e7dee5a35e87c2fd7c503e52ba` |
| `src/ProGPU.Native/src/Scene/progpu_native_semantic_identity.hpp` | `af028628f0ac255d89f5d7d554ef2d403e7187af90a51524152adf24564f9d61` |

Source formatting only. Original hash/byte/error/append/collision/SIMD/browser/
lifetime/performance qualification remains required after the implementation pass.
