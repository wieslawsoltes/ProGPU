# Original source ShaderEffect transport

The native MIL bytecode renderer is not reachable from LibreWPF merely because
its C++ packet decoder exists. The source compiler needs the original shader,
execution intent, float registers and complete sampler brush state in its own
retained batch. This change supplies that typed transport; source wiring and
qualified producer/package execution remain separate requirements.

`NativeMilBatchBuilder.ShaderEffects.cs` writes the original commands 108, 109
and 112 against the existing native reader at
`0bc0f1eab8b1cbc0ca60a14de166f0b329a47b02`,
`src/ProGPU.Native/src/Mil/progpu_native_mil.cpp`. Original fixed headers are
20/28/80 bytes; float register indices are packed Int16 followed immediately by
their float4 values. Only the full command receives DWORD framing padding.
Validation completes before append. Encoding is O(B + R) for B original bytecode
bytes and R at most 32 original float registers, with no native call or GPU work.
The native translator remains the sole instruction/register semantic validator.
No foreign implementation or second bytecode compiler is introduced.

Original source DTOs retain nullable shader execution intent: absence is unknown,
not Auto. They may retain the original typed pixel-shader identity for source
ownership/invalidation, but immutable captured bytes still own compilation.
An additive Image sampler overload retains the actual ImageBrush alongside its
image, preserving opacity, transform and tile mapping for native compilation.
It does not promote image-only descriptors into complete source brushes.
Existing managed shader registry and original constructors remain unchanged.

The admitted native family keeps one sampler, zero padding, float constants and
optional original derivative-register selection. Software-only rendering,
integer/Boolean constants and broader bytecode/capture semantics remain explicit
native gaps, never ignored source state. Original sampler enum values are mapped
explicitly, not cast from the distinct portable DTO or scene-sampling enums.

This implementation reuses the original MIL renderer's
[architecture and primary-source research](native-mil-shader-effects.md#research-and-provenance)
without changing shader compilation, capture, GPU batching or cache ownership.
Authored transport/source controls and exact-head hosted producer plus WPF
package/application execution are required; transport success is not UI parity.
