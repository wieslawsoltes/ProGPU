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

Integrated execution found that the shared mask renderer evaluated the retained
target-space brush matrix with local shape coordinates. Its private mask fragment
entry now maps physical pixel centers through the actual crop origin and per-axis
presentation before brush evaluation. Shape coverage retains its local frame;
the immutable brush/stops, alpha order, uniform size and submission graph remain
unchanged. The same pass handles ordinary masks and shader-input masks. All eight
gradient states pass exact stock Metal comparisons against the independent
ordinary drawings, including translated and DPI2 captures; Windows and the
complete producer/package gates remain required.

Ordinary DrawingBrush fills retain local analytic/path material coordinates;
their separate geometry transform places coverage in the target. Applying the
target inverse to those same material points shifted captured gradients. The
shared fill compiler now keeps that local brush frame. A VisualBrush inside a
shader-input opacity mask also retains its own content-to-viewport traversal,
separate from the containing input's proven source pushes. Actual input
descendants, unsupported 3D and recursive-resource rejection retain their gates.
All 27 ImageBrush/DrawingBrush/VisualBrush opacity states pass exact stock Metal
cold, warm and independent-engine comparisons. Both vector brush families record
directly into the owned mask picture: cold ordinary replay submits that picture
and its target, while a shader input adds its own picture. This local evidence
does not replace Windows, Dawn, package or original WPF qualification.

Explicit linear-byte source-layer opacity recovers the stored RGBA bytes before
scaling them, then applies the separate geometric mask. Its uniform opacity uses
a flat varying; interpolation of the same constant or scaling normalized bytes
before recovery can move an exact half-byte boundary. The vertex buffer and
uniform layouts, image addressing and ordinary layer shader entries are unchanged.
The complete sampled-opacity families still pass on local Metal and Vulkan;
the Intel Metal and Windows integration runs remain required.

The stock native GPU executable accepts `--sampled-opacity-only` or
`--sampled-opacity-software` for a focused run of the unchanged three source
families, nine states and cold/warm/independent engines. Default execution calls
the same helper and still runs its complete corpus. A failed comparison logs the
total changed pixels, maximum channel difference and a small original/subject
neighborhood; no expected bytes, tolerances, counters or deadlines change. The
shared fixture provides the same failure diagnostics for Dawn.

At the integrated `1eb07c48e` checkpoint, hosted Intel Metal still differs at
DrawingBrush state 3, pixel `(34,19)`, red 32 versus ordinary 33. Windows x64's
complete GPU test passes, while Windows ARM64 reaches the later Direct2D phase
before the unchanged aggregate 900-second deadline. The new focused helper and
complete default GPU corpus pass on local Apple-silicon Metal. These observations
do not qualify the remaining platform, producer-package or application gates.

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
