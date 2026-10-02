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

ImageBrush, DrawingBrush and VisualBrush opacity masks are still explicitly
gated in this source family. They need their separate nested-picture mapping and
ownership connection; this implementation does not silently treat a sampled
brush as a gradient or a final geometry clip. Existing custom mapping, cache,
3D and unproven source-frame gates remain unchanged.

Implementation and controls are authored with `[skip ci]`. Per user direction,
no tests, builds, verifiers, GPU/VM execution or CI are run for this intermediate
child. Original source, provider, package and application qualification belongs
to the final integrated tip.
