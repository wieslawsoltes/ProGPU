# Original transform resource transport

`IPortableTransformSource` captures one original Matrix, Translate, Scale,
Rotate, Skew or Group resource. Scalars retain their original double bits;
groups copy the ordered child identity list without evaluating children or
combining matrices. Empty groups and repeated references are meaningful. The
older matrix-only interface remains a separate explicit contract.

The WPF counterpart emits the existing native typed packets, capturing each
source identity once per batch. Cycle, depth, list/graph count and finite-value
failures prevent batch publication. Source mutation produces the usual retained
session packet delta; graph topology changes use the existing rebuild path.
The native group writer now validates every child before allocating a packet,
so a late invalid handle leaves its prior bytes unchanged.

This fixes lost source history; it is not a new trigonometric parity promise.
The immutable original source applies double modulo 360, narrows the angle,
calls `XMConvertToRadians`, then `XMMatrixRotationZ` for rotation; skew calls
the original tangent path. Each centered primitive is composed through two
ordered float matrix products. A generic host `sin/cos` or a flattened WPF
double matrix cannot establish that original primitive witness. Native v6
Rotate/Skew source-effect gates remain until the independently specified
construction is implemented and the final original-SDK controls qualify it.

The existing original-only SDK companion now authors ten named construction
observations and five atomic rejection controls. It records all original double
bits, modulo/narrowed angles, SDK radians, complete core/centered matrices and
unchanged PE/compiler/header/source provenance. Independent checks cover capture,
radians and centered composition, not a guessed trigonometric polynomial. The
receipt explicitly qualifies zero product/hardware cases and keeps trig output
observation-only. Existing source inventories and deadlines are unchanged.

Primary construction references are immutable original WPF `381194e1`:
`WpfGfx/core/resources/rotate.cpp` (double modulo, narrowed SDK rotation),
`skew.cpp` (narrowed radians and tangent), `scale.cpp` (centered float products),
and `common/DirectXLayer/XMath/extensions_xm.hpp` (public SDK radians API).
Microsoft documents the scalar sine/cosine as [minimax approximations](https://learn.microsoft.com/en-us/windows/win32/api/directxmath/nf-directxmath-xmscalarsincos),
not correctly-rounded host libm. Its public [radians conversion](https://github.com/microsoft/DirectXMath/blob/main/Inc/DirectXMath.h)
specifies one multiplication by a float conversion constant; the product's old
two-operation generic angle path is not silently promoted to source admission.

Four authored managed controls retain parameter bits/list ownership, atomic
writer failures (late child and count), exact typed packets and untouched bytes.
The WPF child adds five compiler/session controls and two actual built-in source
controls. These are authored counts, not execution results.

No tests, builds or reference captures are executed at this intermediate tip.
Producer qualification and paired source pins remain final-union requirements.

## Independent construction arithmetic

`progpu_native_source_transform_primitive.hpp` now separates the proven original
double-modulo → float-narrow → single float radians product from trigonometric
evaluation. Finite double angles are reduced before narrowing; overflow of an
unreduced diagnostic metadata field is not a source construction rejection.
The centering helper reuses the original ProGPU-owned ordered float matrix
products and is connected to the existing v6 Scale path. Generic transforms and
v1–v5 arithmetic are unchanged. Named source Rotate/Skew remain unadmitted.

The authored controls retain original-SDK quarter-turn/large-angle radians,
signed zero, scale centering, independently observed core-as-input centering and
atomic nonfinite/overflow rejection. These controls have not been executed.
The quarter-turn matrix is a centering input, never a product trig table.

Approximation work is clean-room: only mathematical specifications and owned
black-box observations may inform it. Coefficients must be independently derived
from analytic sine/cosine and an explicit minimax objective, not copied from SDK
implementation/tables or fitted to observed output samples. Degree alone does
not prove interval, constraints, range reduction, evaluation order or bit parity.
Numerical analysis of those independent approximants is separate from deferred
product validation and does not widen the current source gate.

## Actual typed source-effect controls

The shared provider fixture now authors four real typed source graphs, using a
centered Scale(-1,1,3.25,-4.5) and Translate(2,3): Scale alone, Scale→Translate,
Translate→Scale, and nested Group(Translate→Scale) followed by the same original
Translate child twice. Their independently literal six-component matrices retain
translations (6.5,0), (8.5,3), (4.5,3), and (8.5,9), respectively. Reversing order
and deduplicating repeated identity therefore have observable consequences.

Each actual source graph and its separate literal MatrixTransform reference
produces an owned scene. The fixture compares complete raw affine-frame fields,
then retires the original channel before cold, warm and independent-engine replay
in both providers. All 128×64 RGBA bytes are compared against the literal scene
and independent dyadic UV-squared/28.4 clip equations, with explicit nonempty
coverage and unchanged per-replay submission/draw/uniform-upload expectations.
No expected frame or pixel uses the product centering helper.

The prior seven MatrixTransform cases and both Rotate/Skew rejection controls are
unchanged. These four new cases are authored only: no compilation, test, syntax,
GPU, VM or CI execution occurred at this intermediate stacked tip. Original SDK
and final complete source/provider qualification remain separate requirements.
