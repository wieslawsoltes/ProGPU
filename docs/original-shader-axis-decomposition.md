# Original positive-axis decomposition reference

This original-only reference extends the source inventory in `e611aae3f` and
the unexecuted arithmetic draft `390dada4`. Program now invokes the axis family
after every existing family, and the Windows workflow prepares its private SDK
companion on each native architecture. Existing assertions, the shared 60-second
capture deadline, external 90-second process deadline and eight-minute job limit
are unchanged. Implementation uses skipped-CI draft commits; no new build,
arithmetic, pixel, source or hardware qualification has been executed.

## Files and evidence contract

`eng/WpfShaderEffectReference/OriginalShaderAxisMath.cpp` calls the installed
original DirectXMath API, without copying its implementation or linking ProGPU.
Its private synchronous ABI accepts14 doubles and atomically publishes128 floats
plus5 compiler/architecture traits. The seven full16-float matrices are world,
scale, inverse scale, residual, final capture transform, normalized sampling
transform and its full inverse. Additional slots retain allocation, padded local
edges, extracted scales, determinant and actual float root DPI. Hosted
provenance hashes the compiler, its front/back-end DLLs, output DLL, companion
source and every actual include reported by the compiler. Required DirectXMath
headers must each occur exactly once. `BuildOriginalAxisMath.ps1` uses installed
Microsoft C++ tools in strict C++20/float mode, performs no install/download,
requires the exact checkout and native host architecture, inspects the resulting
PE machine, and refuses to overwrite previous outputs. It does not execute the
companion. The managed loader checks the exact commit, architecture, PE machine
and DLL hash against the receipt before calling its private synchronous API.
The installed SDK's version is not interchangeable with the installed wpfgfx DLL's
unknown build-header provenance.

`ShaderAxisOracle.cs` independently reduces the positive diagonal inverse using
cofactors. It preserves every float multiplication/division as a distinct result:

    sx = sqrtf(fl(mx*mx)); sy = sqrtf(fl(my*my))
    d = fl(sx*sy); q = fl(1/d)
    inverseS = diag(fl(sy*q), fl(sx*q), fl(d*q), fl(d*q))
    Rxx = fl(inverseX*mx); Ryy = fl(inverseY*my)
    Rzz = Rww = inverseW
    Rtx = fl(inverseW*worldTx); Rty = fl(inverseW*worldTy)

These are predictions awaiting direct original-SDK measurements. At1.25/1.25,
the predicted inverseX bits are`3f4ccccc`, not shortcut`1/sx` bits`3f4ccccd`;
Rxx is`3f7fffff`, not identity. Mixed1.25/1.5 predicts Rxx`3f800001`.
At next-float1.25/1.25, inverseW is`3f7fffff`, so translation cannot simply retain
its input bits. Do not infer a portable full general inverse policy from these
unexecuted predictions, or manufacture an identity residual.

`ShaderAxisCapture.cs` authors ten actual original WPF source configurations at
125%/150%, mixed root DPI, nonuniform source scales, adjacent float scales/DPI
and an original double midpoint. Each has constant/input/UV/derivative cases,
three replays and a plain-input baseline. Constant coverage has an independent
28.4 output-clip assertion; input/UV/derivative bytes are expressly observations,
not parity passes. ARM64 retains its original unavailable-software control.
SDK arithmetic, software constant coverage, observation completion and native/
hardware qualification remain distinct receipt fields.

## Immutable source basis

Original WPF source is`381194e1ffe4d64fb747556fcaf76e1c34fe9df8`:

- `PresentationCore/System/Windows/Media/Renderer.cs:55–64`: double RTB root DPI.
- `WpfGfx/core/resources/matrix.cpp:61–79`, `translate.cpp:78–86`: per-resource float narrowing.
- `common/MatrixStack.cpp:58–60,108–109`: local/offset push order.
- `core/common/BaseMatrix.cpp:767–837`: scalar sqrtf, full scale-matrix inverse,
  then multiplication by the original matrix to produce the residual.
- `common/DirectXLayer/XMath/matrix_xm.hpp:155–176`: actual XMMatrixInverse call.
- `core/resources/ShaderEffect.cpp:955–1045,1136–1178`: full intermediate extent,
  normalized sampling matrix and its inverse for software UV/derivative setup.
- `core/fxjit/PixelShader/pshader.cpp:286–306`: integer-origin software coordinates,
  four-lane starting positions and subsequent four-pixel increments.
- `core/uce/precompctx.cpp:563–596`, `drawingcontext.cpp:1522–1559,4842–4847`,
  `core/common/rtutils.h:109–126`: separate original aliased output bounds/clip.

The source opts into header-only DirectXMath; original build props select Windows
SDK10.0.26100.0. The Microsoft-owned SDK header snapshot inspected is
`microsoft/win32metadata` commit`5c5efbc01d4c87f6830ec304d42777991d533154`,
`generation/WinSDK/RecompiledIdlHeaders/um/DirectXMathMatrix.inl:745–939` and
`DirectXMathVector.inl:2908–2932`. Both ARM64 and SSE full reciprocal paths use
division; ARM32's estimate/refinement must not be substituted. This snapshot is
research provenance, not proof of the headers compiled into wpfgfx or any runner.

The workflow requires each of the ten math identities and each of the forty
source/output identities exactly once, complete replay hashes and original
baselines, the original arithmetic/rejection inventories and separate qualification
counters. UV, input and derivative captures remain observations; neither SDK
math nor software constant pixels qualify original hardware output or ProGPU.

Only the final integrated tip will execute this reference. A failed arithmetic
prediction or original constant-coverage assertion must be investigated; do not
weaken it into an observation to obtain a passing receipt. Final native-provider,
package and actual application comparisons remain required. The companion calls
public installed SDK APIs; no SDK or WPF implementation text is copied into it.
