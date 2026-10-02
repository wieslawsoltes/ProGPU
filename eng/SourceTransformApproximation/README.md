# Independent source-transform approximation

This is bounded numerical analysis, not a product test or a source admission
mechanism. It consumes no SDK implementation, coefficient table or product
library. The coefficients are derived before any original observation is read.

`derive.py` solves the equioscillation equations using original Decimal Gaussian
elimination, analytic Taylor sine/cosine, and independently located derivative
roots. Pi comes from the Machin arctangent identity. Each candidate records its
objective, fixed coefficients, extrema, residual spread and rounded binary32
coefficients. The root scan and finite series are numerical evidence, not an
interval-certified proof of a global error bound. No runtime policy consumes
these experimental coefficient records.

The public [XMScalarSinCos specification](https://learn.microsoft.com/en-us/windows/win32/api/directxmath/nf-directxmath-xmscalarsincos)
states the polynomial degrees, but does not specify the minimax interval, error
weight, coefficient quantization or evaluation order. [DLMF 3.11](https://dlmf.nist.gov/3.11)
provides the mathematical equioscillation/exchange contract used here. We adopt
those equations, not another implementation's functions, tables or structure.

## Retained observations

Root's separately authorized installed-SDK probe produced 1,210 records on each
architecture, with all original/status/40-float lanes reported bit-identical:

- ARM64 SHA256 `a60b40f533c2346af2ffbda0dee71a7c11a3a11aa06a3f55dbf6b3fe09a14354`.
- x64 SHA256 `1d21c7a236305c20fab977866397d8b3d691a18131f142a3ce9c90792c541c05`.
- Original SDK companion source `9f944a64b0defd9f63c79ccaddd6bbd17480fc5a`;
  the probe's separate provenance retains exact compiler/header/binary identity.
- 1,204 accepted cases and six diagnostic-wrapper rejections. The wrapper stores
  the *unreduced* narrowed angle too; its overflow is not an original resource
  rejection and must not become a product angle limit.

`analytic-derivation.json` is the independently generated coefficient/alternation
receipt and comparison against that immutable ARM64 observation inventory. Every
candidate difference remains reported; nothing edits an original receipt.

The unconstrained degree-ten minimax approximation of `sin(x)/x` on
`[-pi/2, pi/2]`, reconstructed as `x * P(x*x)` with explicit binary32 Horner,
matches all 1,116 accepted rotation sine observations after the documented
candidate periodic/reflection operations. This is not a proof of a complete
SDK rotation constructor or its values outside the inventory.

Direct odd-degree-eleven sine differs at ten observations. Direct even-degree-ten
cosine differs at thirty (eight among the 320 central-interval observations).
Fixing leading coefficients, normalizing `(cos(x)-1)/x²`, or removing its endpoint
root does not establish cosine equality. The separately explored relative
endpoint-root objective also differs. We do not tune coefficients to those
observed mismatches, waive them, or promote the matching sine portion to Rotate.

Independent 70-digit analytic `sin(radians)/cos(radians)`, rounded to binary32,
matches all 176 accepted skew core lanes, including the authored pole neighbors.
That observation alone does not prove the original CRT is correctly rounded for
every input and does not select host `tan` as a portable source implementation.

## Reproduction (numerical analysis only)

The analysis is scalar because exchange iterations and Gaussian elimination have
dependent updates. Dimensions are at most seven, with fixed 30-iteration,
256-bracket and 200-bisection limits; no unbounded coefficient search occurs.
No third-party numerical dependency is required.

```sh
python3 eng/SourceTransformApproximation/derive.py --all-quadrants --observations <owned-original-observations.json>
python3 eng/SourceTransformApproximation/derive.py --analytic-tangent --observations <owned-original-observations.json>
```

No product compilation, tests, renderer execution, VM work or CI ran for this
analysis. The implemented modulo/radians/centering helper is separate from this
experiment; source Rotate/Skew admission remains unchanged pending a complete
clean-room construction and final integrated qualification.
