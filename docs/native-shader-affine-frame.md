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

This first checkpoint authors resource validation, shared renderer transport,
and exact quarter-turn/mirror/atomic-rejection arithmetic controls. Actual source
traversal, independent installed-SDK matrices and paired source pixels are being
connected in the same child. RotateTransform/SkewTransform primitive constructors
are not proven by arbitrary MatrixTransform support and remain an explicit next
contract. No tests, builds, verifier or CI runs were executed; validation is
reserved for the final integrated tip. This is not numeric Windows qualification.
