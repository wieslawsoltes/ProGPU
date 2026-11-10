# Original bounded opacity masks (implementation only)

`SourceMask::bounded` retains actual source recording, declared XYWH allocation
bounds and complete source placement. `bounded_brush` uses the original retained
CompileRectCommand producer; archive picture and path/pen branches retain their
own clip, recorded pen frame and source geometry. Existing `SourceMask::new`
keeps its separate full-target capture policy. Geometry-mask fallback now uses
the same original bounded R8 route; unavailable geometry bounds retain the
original full-target R8 allocation fallback.

The renderer ports original four-corner Vector3 arithmetic, pairwise bounds,
two-pixel padding, outward rounding, active-mask intersection, one-pixel empty
fallback and 16-pixel allocation rounding. Independent corner lanes use SIMD;
ordered mask-stack accumulation remains scalar. Source bounds only select storage:
they never replace transformed source clips or input geometry. Actual parent
physical frame, target-local scissor, storage origin, mask sampling and source
matrix remain separate. Analytic owners retain their original allocation bounds
instead of reconstructing them from inverse shader uniforms.

The original first solid mask fast path retains exact source alpha normalization,
float Matrix3x2 inverse, offscreen-depth and empty-prior-mask gates. Other sources
draw directly into R8 using owned canonical Vector/Text/HintedGlyphPaint/Texture
functions and per-draw premultiplied blend state. Alpha is not extracted from a
completed RGBA image. Additive continuation entries only compose the already
owned Rust mask-chain sampling with those exact alpha functions. Source compilation
retains the original parent offscreen depth: ResetStateForMaskCompilation only
resets opacity/blend stacks. Public final-target image capture is not bypassed by
altering scene metadata.

Finished bounded outputs retain exact physical origins through normal drawing,
mask composition, managed shader and backdrop consumers. Whole active allocation
admission uses bounded source sizes; source state/pipelines survive failed
candidates. Existing source text capture isolates and restores the actual outer
atlas owner. Submission uses the renderer's complete image/source-owner retention
and cache-completion hook; no callback executes during encoding.

The direct mask encoder admits the four source families actually handled by
original ExecuteMaskRenderPasses and fixed per-draw blends. Other unresolved source
operators explicitly reject; no alpha-capable Series/Hatch/custom-provider shader
is invented. Existing affine/source/device/provider gates remain independent.
No builds, checks, tests, fixtures, browser runs or performance claims; touched-file
formatting only. No build/results/trace artifacts were generated.

| Original ProGPU-owned source | SHA-256 |
| --- | --- |
| `src/ProGPU.Scene/Compositor.cs` | `a45a99f135806d248fc3792dcbdb5ebe17c094821c6aed080dcdedf4025237cd` |
| `src/ProGPU.Backend/Shaders/Vector.wgsl` | `6d2d92d59341ea91dc33f851c08467f4b4dcd7f270b5f1f638b9f45006e481ed` |
| `src/ProGPU.Backend/Shaders/Text.wgsl` | `432bcabab2d670297ceba2a720de8ca2351ffb4609bd619f62ead1fd7cf84d4a` |
| `src/ProGPU.Backend/Shaders/Texture.wgsl` | `5f9c663cdc9eac166506c93a1add5ec058c3d68f9a766253aee895afe4adda51` |
| `src/ProGPU.Backend/Shaders/HintedGlyphPaint.wgsl` | `1afaa272899c9f498602a9e21e9101540497afa37a8ac0cd44343b1f050014d2` |
