# Original local ShaderEffect capture frames

Version 4 adds an explicit owned capture frame to the existing native shader
resource. Versions 1–3, the nested program, source padding packets, ordinary scene
transforms and zero/integral capture behavior remain unchanged. This is a bounded
source implementation, not complete fractional ShaderEffect support or provider
qualification.

## Frame and provenance

The actual MIL build request supplies the root DPI. A private traversal witness
narrows each actual source matrix component and visual offset to float before
composition. Offset is pushed onto the parent first, then the local transform.
Each multiplication/addition publishes its float result independently; this
metadata path cannot become an FMA. It does not change generic double scene
geometry. Groups preserve their original child order; unsupported transform
kinds, cache-local frames, scrolling and VisualBrush traversal invalidate the
witness. Scope copies/restoration retain its ownership naturally.

New source admission requires that this independent float history agree exactly
with the existing content-to-device mapping. Local bounds narrow x/y and the
original x+width/y+height separately, then inflate by the narrowed padding.
Scale-only edge minima are floored and maxima ceiled independently. The retained
signed origin A and extent E therefore differ from `ceil(width)` allocation.
Content remains at S*p−A. Residual placement must have exact identity linear part
and an exactly integral final origin; the full E owns implicit input, ImageBrush
capture, hardware UV normalization and derivative registers. No shader or sampling
policy changes are needed for this family.

Output coverage is a separate original aliased clip, not the outward allocation.
Original padded edges pass through the visual's own local matrix, its offset,
then the current ancestor matrix. The traversal retains those separate operands;
admission also requires this exact float edge order to agree with the versioned
edge mapping. An adversarial large canceled translation must not masquerade as
equivalent output bounds. Each mapped edge converts first to 28.4 fixed coordinates
with half-up ties and then to its original aliased pixel boundary. The independent
formula is q=floor(double(float(edge*16))+.5), boundary=floor((q+7)/16).
It differs at both positive and negative half/17-over-32 boundaries from generic
ceil/floor. Intersect that integer rectangle with the existing final clip only;
do not crop input or change E. Anti-aliased fractional output remains unadmitted
until its separate coverage contract is implemented.
The existing logical-float clip transport must also round-trip each physical
integer boundary exactly through original DPI. An exact total scale alone does
not prove this: DPI 1.25 with a compensating source scale can still lose clip-edge
identity on division. Such source input stays rejected until a real physical clip
transport or equally exact mapping is implemented.
After intersecting the existing source clip, every final physical edge must
still be integral. Fractional self/ancestor clips are not converted by the old
generic scissor floor/ceil path, which could expose an extra column; they remain
unsupported in the new family. Raw v4 preflight enforces the same restriction.

The first inverse proof deliberately bounds accumulated positive scales to exact
powers of two in [1/256,256]. This is an internal proof boundary, **not** a new
user-facing dyadic policy or completion of general support. The original full
4×4 inverse cannot be justified for arbitrary floats merely by testing
`(1/scale)*scale==1`. In this bounded family its scale cofactors, determinant and
reciprocal products are exact. Non-dyadic decomposition remains an implementation
contract, alongside fractional final translation. Signed capture/final coordinates
must retain exact float integers; existing 16384-axis and aggregate budgets apply.

## Validation, ownership and remaining gates

The additive wire frame owns original float bounds/mapping, original double DPI,
signed scale-space and final origins, and unsigned complete extents. Its fixed
72-byte layout sits in an exact 648-byte version-4 descriptor. Unknown sizes,
versions, flags, reserved fields and invalid arithmetic reject atomically. Older
resource readers reject the extension rather than dropping its metadata. The
builder owns both descriptor and bytecode; sampler references still require an
earlier same-scene retained picture. Generated bindings describe the same layout.

Shared preflight independently verifies the retained arithmetic, actual layer
bounds/presentation, full uncropped physical extent and final state. It permits
only identity final composite state with the proven rectangle clip. Masks,
cache ancestors/local cache, backdrop, custom mapping, rotation/reflection and
unsupported source opacity isolation remain closed for this new family. The
ordinary old-version preflight remains unchanged. Both providers consume the
same source extent through the existing binding and retirement paths; no separate
compositor or CPU rendering fallback is introduced.

For version 4 the output rectangle clip is mandatory and cannot expose pixels
outside the proven original coverage. Complete descriptor bytes and final state
already participate in retained layer identity through
`progpu_native_semantic_identity.cpp::append_layer`; the new frame is not omitted
from a legacy cache key.

Full fractional final placement requires evaluating bytecode at final device
samples. Filtering an already evaluated effect texture is not equivalent. This
implementation does not claim that missing contract, generic inverse admission,
source WPF integration, or automatic capability advertisement.

## Independent basis and authored controls

Observable arithmetic/order was researched from immutable original WPF source
`381194e`: `resources/matrix.cpp:61–79`, `translate.cpp:78–86`,
`common/MatrixStack.cpp:58–60,108–109`, `Renderer.cs:55–64`,
`drawingcontext.cpp:3695–3733,4859–4880`, `rtutils.cpp:302–330`, and
`BaseMatrix.cpp:767–837` with the full inverse boundary in
`DirectXLayer/XMath/matrix_xm.hpp:155–176`. No foreign implementation is copied.
The separate output coverage derives from `precompctx.cpp:563–596`,
`drawingcontext.cpp:1522–1559,4779–4788,4842–4847`, and the documented numeric
rules in `BaseRT.cpp:234`, `rtutils.cpp:55–110`, `rtutils.h:109–126`,
`real.h:467–490`. The initial Windows reference failure exposed this allocation/
visibility distinction; its original inputs remain unchanged.

The independent public Microsoft WPF companion is PR280, immutable
`7b0695136ebdb206922a40e57e6a2975926cf7bc`: 21 source cases, including eighteen
integral-final candidates, two deferred final-translation cases and one original
separately narrowed history. Its source-derived output-clip correction is
`e611aae3fdf3366085475094d11156676f4141a3`; original Windows reference workflow
[37045784511](https://github.com/wieslawsoltes/ProGPU/actions/runs/37045784511)
passed on x64, with the separate ARM64 unavailable/negative control passing.
This is original source evidence, not execution of these native fixtures.
SoftwareOnly's integer UV phase is not substituted for original hardware's
pixel-center convention.

The paired native fixture uses one actual channel, advancing source generations,
then retires it before GPU replay. Eighteen scenes cover source-input preservation
against ordinary drawing, constant/UV/derivative/ImageBrush outputs, DPI 1/2,
asymmetric fractional padding, final clip, zero/expand/reset and nested
noncommuting transform order. Both providers check every 96×64 RGBA byte,
cold/warm/independent frames, original command/submission/cache/pass/uniform
counters. Three independently authored old-version wire controls retain rejection
before any submission. Padding fixture variant 9 now exercises actual new source
admission; its original input pixels/counters remain required.

CPU controls cover exact retained frame/layout, all reserved fields, malformed
metadata, output atomicity, immutable snapshots after source retirement, real
fractional final translation, separately narrowed float-history disagreement,
non-dyadic scale and power-of-two neighbor/range rejection. No deadline, assertion
tolerance, adapter/compiler/default policy or old sampler cases are changed.

This branch starts from PR277 source union `41a5cba0570497babf2230b44c4f541f3acab420`,
which includes unqualified source dependencies PR267/270/271. This commit is
authored implementation and fixtures before focused checks; no native build,
GPU/VM execution, runtime staging or WPF source repin has been performed.

## Bounded post-commit checks

Major implementation `f9e2e414c5d50f5e15072a7b4dea43f74e3564b0` was committed before
checks. Read-only peer review identified two additional clip-transport boundaries:
`d594623f6` proves original-DPI round trips and `12fad81a8` proves the final
intersection still has integral physical edges. `ab6d0c1da` adds a raw fractional
subset-clip rejection to both provider fixtures. None changes generic old clips.

Strict Clang C++20 syntax with warnings as errors passed for the actual MIL
implementation/tests, actual scene validator and builder, shared renderer under
the existing cached Dawn ABI header, and a fully instantiated new GPU fixture.
The generated C# contract also parsed without errors (not managed type compilation).
Protocol/coverage freshness, native ownership inventory, all seven generated C#
contracts, both Unicode verifiers and three inline-array generator tests passed.
The existing cached generator source exactly matches this tree (Program.cs SHA256
`32ea7debdc2bd3a864bc6440a45e4f34d19bffaa27bebddfbbd559bf286f021a`).
Whitespace checks passed. No native library/test executable was built or run.

These are source/layout checks only. Native CPU and both-provider GPU fixtures,
Windows/package execution and the exact whole producer Build remain required.
Non-dyadic/full inverse, fractional final placement, fractional source clips,
anti-aliased output coverage and actual WPF source integration remain explicit
implementation/qualification gates; this first family does not redefine them.
