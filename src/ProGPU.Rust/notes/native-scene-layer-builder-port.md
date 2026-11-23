# Native owned layer builder

The original layer producer is connected to `NativeSceneBuilder`: all eight mask
families, composite auxiliary arenas, effect chains, tile composition, and shader
resource versions 1–6. Descriptors preserve original canonicalization and raw
reserved-field gates; source geometry clips remain explicit private metadata.
Picture-mask recording checks only the original nested header, leaving complete
nested admission to the scene-stream stage. Shader picture references keep their
earlier resource identity and exact v5/v6 input/sampler lattice requirements.

`push_layer_with_source` retains independent opacity, identity-effect, local-cache
and opacity-mask input contracts, while plain `push_layer` preserves the original
unspecified policy. Layer/composite-state/cache dependencies, stack/materialized
depths and source-hit records are admitted before publication. Failed candidates
do not append resources or commands; backing capacities may be retained.

`prepare_antialiased_clear_layers` preserves exact source AA-clip identity, including
bitwise bounds/transform equality, nearest ordinary owner, historical child peaks,
and open-child identity. All checks precede allocation-free background-flag and
isolation publication. It does not count active AA children twice or reinterpret
an ordinary effect/mask group as a source AA clip.

Borrowed mask predicates reuse the existing native scalar and brush/path gates.
Shader builders reuse the version-aware owned bytecode reader; that reader allocates
program/WGSL storage whereas the original native descriptor gate used scratch-only
admission. Wire serialization remains explicit little-endian safe Rust. Actual
native-stream execution/provider/target and source qualification remain separate.

| Original source | SHA-256 |
| --- | --- |
| `src/ProGPU.Native/src/Scene/Builder/progpu_native_scene_builder_layer.cpp` | `7436958d7065afd34b6b602cf480a211e9705184db6db62fed6df28653df3077` |
| `src/ProGPU.Native/src/Scene/progpu_native_semantic_layer_mask.cpp` | `6c3b0b22d2e7aac8a1e9d17b4d774d1dc7c8ae090f90766653581bc1003a370a` |

Implementation and source formatting only; no build, test, browser, benchmark or
shader validation was run.
