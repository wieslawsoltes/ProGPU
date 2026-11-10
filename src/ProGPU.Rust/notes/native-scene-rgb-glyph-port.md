# Native explicit RGB glyph execution

Original ProGPU-owned sources and SHA-256:

| Source | SHA-256 |
| --- | --- |
| `src/ProGPU.Native/src/Backend/progpu_native_rgb_glyph_execution.cpp` | `adf2e631c6e16a60fead644abab9eefe5a37a91a7aab31b26375533218a647fc` |
| `src/ProGPU.Native/src/Backend/progpu_native_rgb_glyph_execution.hpp` | `6a91e41c1af7850bca6182807a983addc38f1ff39a891e7acd6f181c21aaa800` |
| `src/ProGPU.Native/src/Scene/progpu_native_semantic_rgb_glyph.hpp` | `ad6fd9c66013c53901668f99a1ef7a2eb433fd41c82637e8bc943275230169fa` |
| `src/ProGPU.Native/src/Scene/progpu_native_semantic_render_execution.cpp` | `c0325d3d72f30d79f386cfc0619f84213090b67a3a203e9e69ac4e4add4a9dae` |
| `src/ProGPU.Backend/Shaders/GlyphRgbRasterizer.wgsl` | `715337c9331991578fa4437779d9d1d4fd2e7776a10fb4a303f7343b23998fa7` |
| `src/ProGPU.Backend/Shaders/GlyphRgbComposite.wgsl` | `391b61f092a734dcd5a4959122d868522d06ffeb12bc780d3f900b428a4a7dfe` |
| `src/ProGPU.Backend/Shaders/GlyphRasterizer.wgsl` | `50a30bf25cbfdc410b4350ec5a58b436cd5607f0b3326a391f5f87a1b2318971` |
| `src/ProGPU.Backend/Shaders/TextMaskCommon.wgsl` | `197a780d70286a4ddcccd4edfb522e08a0702298827203098fd768b4e142e2f3` |

`native_scene/engine_resources/glyph_rgb` ports the independent explicit box
coverage model and its full GPU execution. `NativeRgbGlyphPage::compile` retains
the actual command frame and source generation, validates complete outline and
segment inventories, preserves sampling operands, performs the original float
translation before widening, and requires exact two-axis DPI, integral physical
translation and the ordered cursor's actual opaque-target policy. Source opacity
multiplies only each original foreground alpha. The 4096 target/tile domain,
whole-inventory bounds, shader sample-extreme arithmetic, linear RGBA/BGRA-only
policy and CPU fallback rejection remain separate from ordinary text.

The CPU plan preserves source-order shelf packing, 512-byte output alignment,
256-byte row pitch, bounded 16-by-16 composition cells, stable per-cell glyph
references and the exact visible destination rectangle. Clipping affects only
composition membership. Nonempty storage remains allocated for a zero-cell draw.
`coverage_bytes()` is the per-command aligned admission cost, including fragment
mode; the owning scene budget adds completed command costs without carrying
alignment from an unrelated command.

`NativeRgbGlyphEngine` owns named original shader pipelines with independent
immutable mode entries. Original shader bodies and their original prefix order
are included unchanged; compilation defaults remain unchanged. Prepared GPU
resources contain the original 48-byte instances, 32-byte cells, 32-byte frame,
256-byte uniform slots, all outlines/segments, the actual RGBA coverage atlas and
the owned destination snapshot. Compute and explicit fragment routes retain
their original dispatch/pass order. Composition takes the live destination copy,
then issues the three original unblended channel-only draws, retaining destination
alpha and the shader's original per-contribution byte rounding.

The actual native executor constructs the crate-private `NativeRgbTarget` from
its materialized opaque attachment. A root clear or ancestor's policy cannot
create this certificate. `NativeRgbMaskSource` pairs the same immutable page with
the actual shared `NativeMaskBinding` generated from its original source resource
and frame, retaining analytic-chain versus sampled-mask identity. The original
eight source mask families are admitted; this module does not manufacture mask
pixels, reconstruct geometry from envelopes, or substitute grayscale coverage.

`prepare` returns a one-shot `PreparedNativeRgbGlyphPage`; `encode` consumes it
at the occurrence's original ordered position and returns a submission owner.
`retain_submitted` retains the complete actual target, page, mask, snapshot and
uploaded storage until queue completion. Metrics report actual coverage/storage,
texture residency, destination-copy bytes and draw/pass counts. As in the source,
each replay uploads its occurrence data and regenerates RGB coverage; warm
pipeline reuse does not claim warm coverage retention. Mask creation/upload costs
remain with the shared source mask producer, avoiding double accounting.

Integration boundary: the final ordered native executor must supply its actual
materialized target and original source mask generation, break surrounding draw
bundles even for a clipped RGB occurrence, retain aggregate source budgets, and
continue the parent attachment with Load after the RGB pass. The typed factories
are crate-private specifically to keep arbitrary external textures or opacity
booleans from masquerading as that producer. Source/provider/Windows pixel and
package qualification in `docs/native-rgb-glyph-coverage.md` remain independent.

Implementation and touched-source formatting only. No compilation, tests,
shader execution, fixtures, browser runs, benchmarks or qualification performed.
