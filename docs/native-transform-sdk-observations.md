# Focused original transform SDK observations

The user authorized a narrow CPU-only reference probe during implementation;
product builds, tests, GPU/UI checks and final qualification remain deferred.
These observations execute the unchanged `OriginalTransformPrimitiveMath`
reference from source commit `9f944a64b0defd9f63c79ccaddd6bbd17480fc5a`, not a
ProGPU transform implementation. They do not identify the toolchain or SDK
headers originally used to build the installed `wpfgfx` module.

The existing Windows ARM64 VM ran both a verified ARM64 PE and a verified x64 PE
(the latter under emulation). Ubuntu remained suspended; no VM was started.
The compiler was MSVC `19.51.36257.0`, the consumed Windows SDK was
`10.0.26100.0`, and the recorded DirectXMath version was `319`. Both builds used
`/O2 /fp:strict /MD` without a product library, GPU device or UI. Actual compiler,
backend, linker, source, binary and consumed-header hashes are in the receipts.

All ten named cases returned results, with all forty raw float lanes equal
between the two architectures. Each architecture's five invalid requests
returned rejection without changing either output buffer. This classifies the
reference invocation and its atomicity only, not product correctness.

The named positive 90-degree rotation produced radians
`1.5707963705062866`, cosine `-1.1920928955078125e-7`, and sine
`0.99999988079071045`. Its centered translation at `(3.25, -4.5)` was
`(-1.2499990463256836, -7.75)`. Consequently, an exact quarter-turn shortcut is
not the observed original constructor. The public
[XMScalarSinCos contract](https://learn.microsoft.com/en-us/windows/win32/api/directxmath/nf-directxmath-xmscalarsincos)
specifies degree-eleven sine and degree-ten cosine minimax approximations;
neither these ten samples nor that degree specification alone establishes a
portable implementation's coefficients, range reduction or operation order.
No SDK implementation or coefficient table was copied into the product.

The probe also exposed two launcher defects: compiler dependency reports
lowercase Windows header paths, and ambient command discovery can return more
than one compiler. The reference launcher now uses Windows filename comparison
and the exact selected native host/target tools while retaining actual header
hashes, distinct-path rejection and PE architecture checks. Its new offline
controls are authored but have not run.

## Retained evidence

The local evidence directory is `original-transform-sdk-probe.AA7QAf` on the
external development volume. Each architecture subdirectory retains the full
bit inventory, provenance, compiler dependencies and original-only executable.
The original reference source SHA-256 is
`3cc1bfc42ed350b700d4cd015fc88b920f3c5072938876e96ee67aea6adcaee0`.

| Architecture | File | SHA-256 |
| --- | --- | --- |
| ARM64 | `observations.json` | `624212705ca71e7fb8203c79ccb466803bf9a134a178a544ac5d6197970d76a2` |
| ARM64 | `provenance.json` | `f25075114f77596cadc6dde2d67f1b677d52db7e5cdcb7b5f6ff9869e9fc8890` |
| x64 | `observations.json` | `1351d7468658357be495863eb7b8943b8b339129ed6ad05fbd2096f5170e5e68` |
| x64 | `provenance.json` | `86915b223dda817a2ef2a4ff0891c827a8f4c3eebc0d8c85539d6c04eb25d632` |

Named rotation/skew source witnesses still require their own implementation.
No native, package, hardware, original-WPF rendering or source application gate
is satisfied by these CPU observations. Qualified downstream pins and automatic
source policies remain unchanged.
