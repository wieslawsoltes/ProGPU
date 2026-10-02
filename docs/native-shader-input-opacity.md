# Native ShaderEffect gradient input opacity

This child of PR284 connects original spatial gradient opacity masks to the
version-5 owned input capture. It reuses the existing typed brush-mask compiler
and shared layer renderer; no new compositor, native ABI, shader translator or
GPU pixel evaluation path is introduced.

Original ordering is `Clip > Effect > OpacityMask/Opacity`. The mask therefore
belongs to the scale-space source picture, before bytecode evaluation. Its
relative material coordinates use the original **unpadded** visual bounds,
while its transform is the input capture's `S*p-A`. The picture still owns the
complete padded extent, including transparent border. Neither fractional final
placement nor final output coverage changes that mask frame. Original brush
opacity is retained in the typed brush; visual opacity is applied once by the
existing inner layer.

Linear and radial gradients retain the existing gradient stop, mapping,
transform, spread, interpolation and validation contracts. The immutable nested
scene owns the brush records and stops; the existing picture/binding caches and
submission leases own the GPU resources. No source handles or mutable brush data
are borrowed after scene serialization. Invalid gradient resolution fails before
scene publication. Successful older wire paths remain unchanged.

Construction adds the existing bounded stop snapshot, O(S) storage/work for S
stops. Cold realization uses the shared GPU brush mask over the input area;
unchanged input replay retains that mask and picture. There is no extra managed
crossing, CPU pixel evaluation, source geometry rewrite or second compositor.
The source semantic evidence is the original WPF visual push ordering at
immutable `381194e1ffe4d64fb747556fcaf76e1c34fe9df8`; implementation provenance is
ProGPU's existing `add_visual_opacity_mask`, typed brush-mask builder and shared
`create_semantic_brush_mask_binding`, not foreign implementation code.

ImageBrush, DrawingBrush and VisualBrush opacity now connect through the existing
`add_spatial_opacity_mask` sampled-brush path. Its original MIL rectangle compiler
uses the unpadded visual material bounds and `S*p-A` transform in the DPI-1 capture
frame. The resulting picture mask owns a complete nested scene, original image
bytes/drawing/visual resources, sampling, tile address mapping and brush opacity.
The original active-resource set and bounded recursion depth cross this capture;
self-referential VisualBrush content cannot bypass graph rejection. No final
residual transform or viewport offset is added to the input mask. This is not a
gradient substitution or a final geometry clip. Existing custom mapping, cache,
3D, unsupported brush contracts and unproven source-frame gates remain unchanged.

The paired-provider authored fixture uses eight generations on one actual MIL
channel: relative linear alpha, reversed stops on the same brush, zero alpha
feeding a constant-output shader, separate brush/visual opacity, radial alpha,
equivalent absolute mapping, an original brush transform, and DPI2. Every scene
owns its nested mask/input bytes after that channel is destroyed. Structured
controls require unpadded bounds and the capture's exact scale/origin on the
typed inner brush mask, with no opacity mask on the final effect layer.

Nearest input sampling at a quarter-device-pixel final offset is compared with
an independently compiled ordinary no-effect drawing at the corresponding
integer offset. This deliberately avoids a texel tie and does not invent an
ideal gradient or UNORM precision promise. The zero-alpha/constant-output case
instead asserts every byte over the full padded output coverage; applying the
mask after bytecode would erase that output and fail. Cold, warm and independent
engine replays retain exact capture submissions and effect upload/pass counters.
The original pre-connection ImageBrush rejection control is superseded by the
stacked sampled-brush source fixture, which is being authored for all three
source families, ownership, input ordering and rejection atomicity. Intermediate
integration is not a validated final candidate.

Applicability: both native providers execute the same compiler/resource path and
the same authored fixture. The managed generic `Visual` compositor currently
captures effects with `includeRootVisualState:false` and applies its root
composite scope afterward. Actual source adapter topology and an explicit
source-only mask-before-effect connection are a required paired follow-up; this
native child does not claim the generic managed ordering was already equivalent
or change unrelated managed effects.

Implementation and controls are authored with `[skip ci]`. Per user direction,
no tests, builds, verifiers, GPU/VM execution or CI are run for this intermediate
child. Original source, provider, package and application qualification belongs
to the final integrated tip.
