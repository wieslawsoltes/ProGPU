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
contract and actual destination format. A six-vertex unit quad carries the full
residual to the hardware raster stage and interpolates original UVs at final
samples. The actual parent target's origin and viewport participate in original
float projection composition; a small effect-output projection is not substituted.
The original D3D9 half-pixel raster convention converts explicitly to WebGPU's
half-integer samples after that retained matrix. There is no intermediate
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
Both source pictures preserve the complete capture. The shared renderer prepares both
retained pictures through the existing engine path, accounts for both in its
bounded memory budget, and holds their leases in the submitted shader binding.
Memory diagnostics enumerate both, deduplicating aliases. Legacy uniform
budgets and byte counts stay unchanged. The v5 wrapper owns no content pass or
evaluated-output texture: its empty source command scope is validated, and its
existing pop-layer operation draws directly onto the current parent target with
premultiplied source-over. It restores ordinary viewport/scissor state afterward.
Only a one-pixel bookkeeping layer slot remains in the bounded existing pool.
The input picture/program/binding are retained on warm replay; the effect draw
still executes, so v5 never reports an evaluated-output cache hit it did not use.

Actual source visuals use the existing render-data, glyph, child-visual and brush
compiler to produce the owned scale-space input. Captured descendants continue
the original float transform history in that physical frame. Source output bounds
retain own-local, visual-offset and ancestor operations separately; rectangular
source clips retain physical float edges rather than reconstructed logical clips.
After intersection the original signed 28.4 boundary conversion publishes one
integer output clip. Existing dyadic integral source paths remain unchanged.
An original witnessed non-dyadic scale, fractional residual, float-history
divergence or fractional source clip selects v5 explicitly, even if a rounded
legacy bound happened to look integral. Other unsupported old captures can also
select it before any source draw or layer is emitted. Old wire versions retain
their complete original validation and execution contracts.

Original hardware source inspection at `381194e1ffe4d64fb747556fcaf76e1c34fe9df8`
establishes that the final unit quad is ordinary single-sample triangle coverage:
`ShaderEffect.cpp` 224–293, `hwsurfrt.cpp` 3140–3181 and `d3ddevice.cpp` 6576–6643.
Original non-Aliased output bounds inflate by one physical pixel and floor/ceil
before intersecting the current clip (`dirtyregion.cpp` 96–109,
`drawingcontext.cpp` 1522–1560); that clip still reaches the same aliased integer
surface conversion. There is no extra analytic AA factor. Source AA mode is
retained as provenance, not used to invent fractional clip coverage.

Both provider fixtures now author sixteen actual C-source cases with three
replays each: non-dyadic DPI/scale, fractional final placement, a nonlinear
UV-squared shader, original source/ancestor rectangle clips, AA output-bound
inflation with ordinary quad coverage, and genuine nested visual-opacity
targets. The nested cases retain nonzero target origins and non-power-of-two
viewport extents, draw the shader inside that parent, then apply parent opacity.
Separate padded-input, original ImageBrush and selected derivative-register
cases check full capture texels and dimensions before final fractional placement.
Input and secondary sampler ownership are distinct; their cold submissions are
counted separately, with one ordinary retained submission on warm replay.
They check every pixel, independent-engine replay, original channel retirement,
input ownership, exact submissions and effect uploads/passes. These controls
are authored only; no execution or result is claimed.

Completely clipped parent/output extents retain the validated v5 command
identity without an invented one-pixel shader viewport or unused input capture.
The empty wrapper remains balanced and a later visible source generation
acquires the real input and binding. The fixture moves one live source outside
the target and back, retaining exact no-effect-pass/no-uniform-upload controls
for the hidden generation and ordinary cold/warm controls after restoration.

Nonrectangular source clips now use the existing typed vector/Boolean mask
resource. The original path segments, fill rule, curve topology and intersection
chain remain owned by the immutable scene. The existing shared mask rasterizer
creates its retained R8 coverage at the actual parent target extent; the final
shader uses one integer coverage load at that same device pixel and multiplies
premultiplied output once, after bytecode evaluation. It does not clip the input
capture, filter evaluated shader pixels, replace a shape with its envelope, or
create a separate mask compositor. Existing span/submission retirement owns the
mask texture, uniforms and bind group. Program cache identity includes masked
versus unmasked layout, and the unmasked path does not initialize mask resources.

Source admission requires every inherited vector clip to retain a proven basis:
the existing path's logical-to-device transform must equal the traversal-owned
original float transform. Unproven history stays unsupported rather than being
reconstructed from rounded final bounds. Raw v5 accepts only the complete typed
vector clip chain at opacity one; coverage-bitmap, analytic, brush, picture and
composite masks retain their separate contracts. Legacy effect wires are unchanged.
Geometry operands retain the existing typed path compiler's admission and
rasterization contracts; connecting this mask is not new numeric Windows parity
evidence for the curve approximation or every source geometry-transform history.

The paired provider fixture authors eight actual source clip generations:
self ellipse, ancestor ellipse, Boolean difference/hole, two-curve intersection,
nested-opacity target, DPI2, mutation of the same ellipse resource, and a single
half-ULP transform component that narrows to identity in both paths. Every
effect replay is compared byte-for-byte to the corresponding ordinary original
drawing without an effect, including nontrivial fractional curve coverage.
Cold/warm/independent replays retire the C source channel first. Structured
controls require retained curve and Boolean records, while source-float mismatch
and raw non-unit vector-mask opacity remain rejected before GPU submission.
The rejection now uses two non-dyadic transform pushes whose ordinary double
product differs from the original float composition. Mutated raw controls advance
the same scene/resource owners, so they reach render preflight rather than stale
generation rejection. Integrated stock Metal passes these controls; the complete
provider/package and original Windows qualification remains pending.

The UV-squared fixture copies `t0.xy` to `r1.xy` before multiplying `r1` by itself.
The earlier authored `mul ..., t0, t0` violated the documented
[ps_2_0 register read limit](https://learn.microsoft.com/en-us/windows/win32/direct3dhlsl/dx9-graphics-reference-asm-ps-registers-ps-2-0).
Both native and original-WPF fixtures retain this corrected bytecode; validation
still rejects the two-read form. The production translator and source constants
are unchanged.

Spatial gradient source opacity is connected by the child described in
`native-shader-input-opacity.md`, inside the input picture before bytecode.
Remaining implementation: sampled/picture spatial opacity masks, vector
clips with unproven source-float mapping, and final source/SDK/package qualification.
No missing contract is redefined
as a dyadic-only final feature. Original-reference authoring is separately owned
on `test/original-shader-axis-evidence`; this product branch does not alter its
unexecuted observation-only UV/input/derivative claims.
