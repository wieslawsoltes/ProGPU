# Retained final-device ShaderEffect sampling

This implementation branch continues the local-frame contract without changing
the published version-1 through version-4 paths. The source and retained renderer
are connected; this remains unqualified implementation work, not a new advertised
capability or a completed hardware precision claim.

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
vertices; the new primitive uses its own 592-byte block and six vertices,
including physical output clipping after translated derivative evaluation.

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

The shared layer cursor now consumes the physical output lattice directly.
Target clipping changes only the actual output viewport, while both source
pictures preserve the complete capture. The shared renderer prepares both
retained pictures through the existing engine path, accounts for both in its
bounded memory budget, and holds their leases in the submitted shader binding.
Memory diagnostics enumerate both, deduplicating aliases. Legacy uniform
budgets and byte counts stay unchanged.

Actual source visuals use the existing render-data, glyph, child-visual and brush
compiler to produce the owned scale-space input. Captured descendants continue
the original float transform history in that physical frame. Source output bounds
retain own-local, visual-offset and ancestor operations separately; rectangular
source clips retain physical float edges rather than reconstructed logical clips.
After intersection the original signed 28.4 boundary conversion publishes one
integer output clip. Successful old source paths remain unchanged; v5 is selected
only after an old capture is explicitly unsupported and no layer was pushed.

Original hardware source inspection at `381194e1ffe4d64fb747556fcaf76e1c34fe9df8`
establishes that the final unit quad is ordinary single-sample triangle coverage:
`ShaderEffect.cpp` 224–293, `hwsurfrt.cpp` 3140–3181 and `d3ddevice.cpp` 6576–6643.
Original non-Aliased output bounds inflate by one physical pixel and floor/ceil
before intersecting the current clip (`dirtyregion.cpp` 96–109,
`drawingcontext.cpp` 1522–1560); that clip still reaches the same aliased integer
surface conversion. There is no extra analytic AA factor. Source AA mode is
retained as provenance, not used to invent fractional clip coverage.

Remaining implementation in this branch: exact actual-target projection float
order and direct destination sampling, nonrectangular source-mask contracts, both-provider
full-pixel controls, and final source/SDK/package qualification. No missing
contract is redefined as a dyadic-only final feature.
