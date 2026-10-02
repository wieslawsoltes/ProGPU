# Retained final-device ShaderEffect sampling

This implementation branch continues the local-frame contract without changing
the published version-1 through version-4 paths. It is not yet wired into the
source scene compiler and is not a new capability or qualification claim.

The first coherent primitive retains a positive-axis original float matrix,
extracts the scale, computes a sparse full 4x4 cofactor inverse and retains the
complete residual including its homogeneous coordinate. Scale-space allocation
still floors minima and ceils maxima independently. The transformed unit quad
owns a separate final integer output lattice; fractional placement never changes
the input extent or crops UV normalization.

The new shared owned shader binding receives an existing same-engine retained
picture as input. Its separate program/layout identity includes the new vertex
contract. A six-vertex unit quad carries the full residual to the hardware raster
stage and interpolates original UVs at final samples. There is no intermediate
evaluation followed by texture resampling. The original bytecode translator,
sampler policy, premultiplied values, texture leases and submission retirement
are reused. Legacy programs retain their original 528-byte uniforms and three
vertices; the new primitive uses its own 576-byte block and six vertices.

Arithmetic is an independently derived diagonal cofactor reduction, not copied
DirectXMath source. The actual original-SDK reference draft is committed at
`390dada4cc906eef99b5b2f9c42f54eb84d27455`; its installed SDK comparison has not
executed. Original hardware `ShaderEffectsVS.fx` keeps the full homogeneous
position and passes the unit UV; SoftwareOnly extracts affine inverse terms and
does not supply equivalent evidence for nonunit homogeneous behavior.

Current source tests author the 1.25 reciprocal discriminator, nonuniform and
near-ULP residuals, independent capture/output extents, fractional final origin,
and atomic invalid-frame controls. They have not been executed. Intermediate
commits carry `[skip ci]`; per user direction, validation belongs to the final
integrated tip, not these source checkpoints.

The additive version-5 wire now retains the original source frame and DPI,
independently derived capture/output lattices and full homogeneous unit quad,
plus a physical output clip. Its mandatory input picture and optional secondary
sampler picture are earlier same-scene resources with exact capture dimensions
and zero-origin unit-DPI presentation. All old readers reject the new payload
rather than discard either picture or frame. Generated managed layouts are
synchronized from the C authority; malformed/atomicity controls are authored.

Remaining implementation in this branch: actual source subtree capture,
shared layer/output allocation and retained cache
integration, original output/source clip and anti-alias coverage, both-provider
full-pixel controls, and final source/SDK/package qualification. No missing
contract is redefined as a dyadic-only final feature.
