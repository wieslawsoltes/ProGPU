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

Independent installed-SDK matrices are being connected in this child.
RotateTransform/SkewTransform primitive constructors are not proven by arbitrary
MatrixTransform support and remain an explicit next contract. No tests, builds,
verifier or CI runs were executed; validation is reserved for the final integrated
tip. This is not numeric Windows qualification.
