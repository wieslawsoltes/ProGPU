# Original ShaderEffect padding transport

`PortableShaderEffect` retains the four original padding doubles without
normalization. Previously its transport constructor converted negative values,
NaN, infinity and negative zero to positive zero. That could make an invalid
source effect appear to be a valid zero-padding effect before native admission.
The neutral DTO now preserves every source bit; it does not declare the effect
renderable or turn invalid input into an exception at this transport boundary.

The existing `NativeMilShaderPadding` packet overload remains the numeric
admission boundary: each axis must be finite, nonnegative and representable as a
finite float. Valid values, including negative zero and differences below float
precision, retain their original double bits on the wire. It validates before
appending bytes and does not reinterpret the four sides as their maximum.
Native capture owns the subsequent bounds, lattice and texture admission.

LibreWPF's paired source change removes its blanket nonzero-padding rejection,
validates the actual metadata before resolving dependencies, and uses this
existing four-double overload. Its managed effect mapper must reject invalid
metadata before using the legacy `MaxPadding` accessor or materializing sampler
resources. The accessor alone is not a validity or asymmetric-rendering contract.
Qualified dependency pins remain unchanged until integrated qualification.

## Provenance and authored controls

This is a source-metadata correction, not a new rendering algorithm. It changes
the ProGPU-owned transport at `dd2f1415ac1ee2551ea83321b6581cc52e6fb145` and reuses
the existing `NativeMilBatchBuilder.SetShaderEffect` overload. LibreWPF source
snapshot `bae8f10ede7b0a166e470fad59d722748f85f381` exports all four protected
source properties; the source setter's negative-value check does not reject
NaN or positive infinity. No foreign implementation is copied or translated.
The DTO and packet boundary remain O(1) for padding, with no new allocation,
crossing, callback, shader, GPU resource, submission or dependency.

Authored transport controls retain each axis independently, including signed
zero, subnormal values, sub-float differences, negative values, infinities and
distinct signed NaN payloads. Paired packet controls require unchanged earlier
bytes after invalid source metadata and exact wire bits after valid transport.
The existing original Microsoft padding and local-capture reference inventories
remain unchanged; this transport change does not replace their pixel oracle.

No build, syntax check, test, verifier, probe, VM, renderer or CI execution was
performed. Source, both native providers, packages and final application image
qualification remain required; these authored controls are not passing results.
