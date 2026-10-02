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

No tests, builds or reference captures are executed at this intermediate tip.
Producer qualification and paired source pins remain final-union requirements.
