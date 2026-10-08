# Original affine ShaderEffect frames

Implementation child of PR297. Version 6 is additive: the full original six-float
source affine matrix, independent scale-space input lattice and final output
lattice, and complete homogeneous XY quad are retained. The source-frame prefix
is not a version-5 promise; all older resource readers reject its distinct size.
Derived values are revalidated before publication. Earlier input/sampler picture
dependencies, exact engine ownership, target-local clip/mask and submission
retirement are unchanged.

The independent sparse algebra extracts the lengths of the two transformed unit
vectors, retains the full diagonal cofactor inverse of that scale, then composes
the complete residual. Full XY coefficients continue through actual-parent
projection, ordinary six-vertex single-sample rasterization and all four selected
derivative-register values. Negative determinant mirrors do not reverse source
storage or crop the input. No evaluated shader texture is resampled.

The affine template has its own program/layout cache identity and 608-byte block;
legacy 528/592-byte uniforms and v1–v5 resource layouts remain unchanged. Existing
shader bytecode translation, GPU sampler policy and premultiplied blending are
reused by both providers.

Actual MatrixTransform, ScaleTransform, TranslateTransform and TransformGroup
source resources now create a six-component float witness at each traversal push.
Old positive-axis histories keep their old helper and version selection. New
affine histories retain their own provenance even if later transforms cancel
their off-diagonal terms. Captured descendants start from the real scale-space
target, then continue witnessed pushes there. Unknown leaf constructors, cache
input and other previously unproven histories do not invent a witness.

Original output bounds use the own-local transformed AABB, visual offset, then
the ancestor bound transform in that order; physical output clipping does not
shrink allocation or UVs. A genuinely axis-aligned transformed rectangle can
retain the original rectangle clip. Other transformed source clips use the
existing typed polygon/curve mask, with all six logical/device coefficients
proved against source history before admission.

Seven paired-provider source cases are authored: quarter-turn constant/nonlinear
UV, mirror UV, swapped-axis image/derivatives, nested mirrored matrices and a
sheared constant-output quad. All use actual C source packets and retire the
channel before cold/warm/independent-engine replay. Assertions retain every
pixel, exact command/submission/draw/pass/upload counts and full v6 source
identity. Separate raw controls cover old-reader rejection and malformed-frame
atomicity. The optional affine parameters on the existing source fixture do not
change any older invocation or expected result.

The original Windows reference now authors an independent installed-SDK export
and eight exact matrix inventories: quarter-turn, mirror, swap, shear, oblique
matrix, mixed DPI, noncommuting parent and canceled history. Each records nine
complete 4x4 matrices plus allocation/source values and original input bits;
the reference compares an independently reduced sparse float algebra, retains
both signs of structural zero in the receipt, and has four atomic rejection
controls. The SDK library/compiler/header/PE/hash provenance is the existing
original-only companion, never a product library. The workflow requires the
complete new receipt while retaining every older reference inventory and the
same time limits. Original SDK agreement is not an original hardware pixel or
wpfgfx build-header claim.

RotateTransform/SkewTransform primitive constructors are not proven by arbitrary
MatrixTransform support and require their independent primitive contract.

## Integrated original arithmetic reference

The first integrated original ARM64 capture exposed two one-bit discrepancies
in inverse-unit translation: oblique Y and mixed-DPI X. The checker had assumed
separate multiplication/addition on both backends merely because the x86
`_XM_FMA3_INTRINSICS_` flag was absent. The documented
[DirectXMath compiler directives](https://learn.microsoft.com/en-us/windows/win32/dxmath/ovw-xnamath-reference-directives)
identify ARM NEON separately. The companion now records the actual configured
intrinsic backend, including explicit rejection of scalar/unknown combinations.

The independent sparse checker retains fused signed translation cofactors for
NEON and separate publication for SSE. This is a bounded arithmetic model
inferred from the original captures, not a claim about undocumented SDK
implementation or universal bit stability. An additive original-only export
calls the public [multiply-add](https://learn.microsoft.com/en-us/windows/win32/api/directxmath/nf-directxmath-xmvectormultiplyadd)
and [negative-multiply-subtract](https://learn.microsoft.com/en-us/windows/win32/api/directxmath/nf-directxmath-xmvectornegativemultiplysubtract)
APIs with exact binary cancellation inputs of both signs. Those controls must
match the declared backend independently of all eight original matrix captures.
Four additional bad-input/count controls require untouched output and traits.

Every original matrix still compares bit for bit, except the existing signed
structural-zero equivalence. The checker also rejects a one-bit mutation at
every one of the 156 captured positions, totaling 1,248 rejection controls.
No recorded float, product uniform, source constant, tolerance or input matrix
is repaired. Local replay of the immutable failed ARM64 receipt passes all eight
matrix checks and all 1,248 corruptions; the fresh installed-SDK arithmetic
export and complete original Windows run still require hosted CI. This does
not qualify native uniforms, hardware pixels, packages or either source host.
