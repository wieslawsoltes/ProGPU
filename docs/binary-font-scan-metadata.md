# Binary font scan metadata — implementation batch

The prepared font-batch path retains binary scan metadata independently of
antialiased vector geometry. Both renderers consume one shared GPU implementation.
The original authored-font comparisons pass on local macOS ARM64; system-font,
source application and complete package qualification remain open. Existing
source consumers remain unchanged until that qualification.

`NativeHintedCoverage.Monochrome` applies only to captured, already-positioned
TrueType font batches. The original contour writer produces the same physical
lines and quadratics. Cubics, unsupported flags, malformed topology and unknown
policies reject before publication. Existing coverage modes and record layouts
remain unchanged. Old producers reject the new mode.

Every emitted segment carries its actual contour's descriptor:

- `Pad0`: marker bit 8, raw resolved SCANTYPE in bits 0–2, original contour
  override marker in bit 9, and the original admitted outline flags in bits 16–31.
- `Pad1`: contour start relative to this outline's segment range.
- `Pad2`: the complete contour segment count.

Raw SCANTYPE aliases are retained. A marked contour start replaces the current
mode; singleton contours still update that state without manufacturing a segment.
The original batch remains owned, including no-ink descriptors and raw tags.
Generic paths continue to reject nonzero reserved words. Managed import validates
the complete contiguous contour partition, closure, uniform descriptor, original
flags and source bounds. Copies preserve caller tails and atomic rejection.

Implementation provenance: the existing ProGPU contour writer in
`progpu_native_hinted_outline.cpp`; public
[OpenType scan-conversion rules](https://learn.microsoft.com/en-us/typography/opentype/spec/tt_instructions#scantype)
and [FreeType outline metadata](https://freetype.org/freetype2/docs/reference/ft2-outline_processing.html).
No foreign renderer implementation was used.

This uses the existing [cross-engine font architecture research](native-font-hinting.md)
and [retained presentation-policy research](source-grayscale-text-contrast.md).
Adopted: immutable prepared layout, explicit raster identity, lazy device pipelines,
batched uploads and owned submission retirement. Adapted: the original ProGPU atlas
and native raster page carry the additional scan buffer. Rejected: CPU bitmap
fallback, source reshaping, per-glyph interop calls and cross-API mode aliases.
The algorithm comes from public scan contracts and independently observed original
outputs; it does not copy another rasterizer's source or implementation structure.

`BinaryGlyphRasterizer.wgsl` uses one 256-thread workgroup per scanline and two
ordered dispatches. It sorts at most 512 crossings from 256 original line/quadratic
segments, accumulates winding, and writes packed binary R8 coverage. The second
pass reads a separate completed horizontal buffer, avoiding races between dropout
decisions and their output. Pixel boundaries, raw mode aliases and the bounded
bitmap extent are explicit. Vertical gap admission preserves the original bitmap
row direction in the contour's Y-up frame. Continuation counts distinct square
boundary contacts, retains actual orthogonal caps and exact cap contacts, and
keeps with-stubs contour-vertex contacts separate from positive winding intervals.
Quadratic endpoints use their first nonzero one-sided coefficient; an interior
curve extremum does not become an endpoint. Exact nondegenerate quadratic
boundaries retain closed coverage, while straight and flat boundaries retain
their separate rule.
No epsilon, source-coordinate adjustment or AA threshold substitutes for a binary
decision. The independent original system-font comparisons remain required.

Smart curved dropouts resolve only the two selected original roots, using at
most 32 de Casteljau subdivisions each in increasing scan-axis direction. The
one-26.6-unit control-hull bound selects the lower-parameter endpoint's unit;
exact endpoint and dyadic roots retain their coordinates. Their midpoint uses
the nearest 26.6 unit with lower-unit half ties. Line-only intervals retain their
original arithmetic. Analytic winding, gap detection and source coordinates do
not change. Public [Microsoft scan-conversion algorithm prose](https://patents.google.com/patent/US6529197B1/en)
describes selective subdivision for curved dropout transitions. The precise
arithmetic here is an original implementation checked against the retained
observations; that source does not by itself establish broad parity.

Both renderers use the same shader and preserve actual submission ownership.
Managed work uses the existing uniform/output rings, including oversized glyphs;
native work retains both buffers and bindings in its existing raster lease. The
extra buffer participates in native memory inventory, staging metrics and semantic
resource budgeting. Managed `GlyphAtlas.CoverageStagingBytes` reports both allocated
coverage rings after binary demand; ordinary demand keeps its original reservation
and warm binary replay reuses both rings. This allocated-reservation metric remains
separate from native per-operation staging bytes. Pipelines remain lazy; binary-only
demand does not compile the ordinary vector rasterizer. Immutable source bytes participate in raster identity.
Shared command admission requires aliased integer-origin replay and the complete
bounded outline inventory; the inventory maximum is retained once per generation,
including selected views. Unsupported execution paths reject instead of selecting
AA or a CPU bitmap substitute.

The checked-in fixture contains all 880 original Windows observations of two
ProGPU-authored fonts: five scan modes, eight outline families, eleven phases and
two gasp tables. Both original GDI+ and GGO_BITMAP outputs agreed, and all 1,760
captured hinted/unhinted GDI contours matched the authored source. The fixture
digest is `6d13e632b5f260217bc17d9c86b61e77506f463644eeee923280400a52f3b092`.
The owned font inputs are 20,952 bytes each; the tests verify their original
SHA-256 values before execution. No system-font binary or foreign renderer code
is included.

The additional `BinaryGlyphCurveOriginals.json` fixture retains all 880 cases
from eight independently authored curved ribbons, including transposed pairs,
five modes, eleven phases and both gasp tables. Original run `39156744403`
captured 2,640 full frames in 3.899 seconds without input or desktop windows.
All 1,760 hinted/unhinted outlines matched the authored geometry and all 880
GDI+/GGO_BITMAP pairs agreed. The diagnostic PR was closed without merging.
The fixture digest is
`b30470feae7d095adad5e549595adeaed929058e3040d94640f0ca214313e2b8`;
its two original authored fonts are 23,812 bytes each. Both fixture sets run
through the same managed and native checks, preserving the earlier inventory,
full frames, padding, cold/warm reuse and original font-digest validation.

Both captured JSON files are byte-preserving Git assets. The first Windows
package runs at `dcaac6c7` rejected their unchanged SHA-256 checks before rendering:
the default text checkout changed the final LF into CRLF. An isolated checkout
with `core.autocrlf=true` reproduces both altered digests; explicit `-text`
attributes retain each original blob byte and its original digest. No fixture,
font, expected pixel, digest assertion or renderer changes in this repair. The
failed runs remain retained; complete corrected-head CI is still required.

The subsequent Windows wgpu package runs exposed an FXC compiler failure: the
two-root loop wrote dynamically indexed vector components. The pinned Naga
translation reports that exact write at HLSL line 163, followed by forced loop
unrolling errors in the enclosing contour/dropout paths. Each quadratic root now
uses the same scalar helper, in the original order, before constructing the
two-lane result. Derivative arithmetic, endpoint admission, rejected-root
sentinels, stable root calculation and every scan/dropout operation are unchanged.
The helper changes no wire layout or dispatch ordering.

Local validation passes all 36 managed binary/shader controls and all six original
880-case executions through each rebuilt native provider, including unchanged
cold/warm pixels and ownership assertions. A first fixed-component-write
candidate did not return from FXC within its diagnostic's remaining 42 seconds;
that failed attempt remains retained and is not compiler qualification. The
scalar-helper candidate then exceeded independent 60-second compiler-only bounds
for both entry points, without a returned HRESULT or bytecode. Those failures
remain retained; compiler optimization behavior and complete package CI are
still unresolved. The local pixel passes do not qualify Windows compilation.
Original consumer progress now distinguishes render from submission wait, and
Windows failure receipts retain the full native exit code before shell narrowing;
these observations change no inputs, assertions, workload sequence or deadlines.

The owned Naga workgroup-initialization repair subsequently resolves that FXC
optimizer stall; see `native-windows-shader-compiler.md`. At `686a34bb`, all x64
package lanes pass, while ARM64 FXC, DXC and Dawn reach the first original frame
and fail during submission wait. A bounded original DXC capture retains a preceding
write access violation in WARP-generated code: `mov w28,#0x31a4; add x28,xzr,x28;
str w4,[x28]`. The shifted-register ADD uses the zero register rather than SP,
exactly constructing the captured invalid address. The later return-address
failure is in `PixelJITProcessor::ExecuteJIT`. The capture reached its eight-dump
cap; it remains incomplete diagnostic evidence, not a successful product gate.

Each entrypoint now visits its two original dropout indices through one bounded
loop: start at the invocation's lane, add 256, stop below 512. For every lane
0–255 this preserves the exact ordered pair, helper arithmetic, early returns,
shared-memory contents, atomics and subsequent vertex dropout. Both renderers and
both native providers consume this same shader. No compiler flag, adapter default,
native dependency, resource layout, dispatch count or pixel expectation changes.

Matched native ARM64 release compilations retain the loop. Horizontal/vertical
FXC bytecode falls from 81,296/64,088 to 50,484/37,524 bytes; DXC falls from
28,900/23,528 to 19,776/15,728 bytes and its local allocations fall from 16 to 8.
FXC temporary-register counts are mixed, so this is not a uniform register-pressure
or runtime-speed claim. All eight compiler cases pass. Local validation passes
36 managed binary/shader controls and six complete 880-case comparisons through
each rebuilt native provider with zero differing pixels. Actual ARM64 WARP
execution and whole package qualification remain required before calling this a
runtime fix or advancing consumers.

That loop revision, `b7c191f2`, passes 75 of 76 whole-head checks. Its default
FXC and Dawn ARM64 package lanes pass; explicit DXC still fails. The independent
bounded six-group runtime checks also pass on FXC in 44.12 seconds and Dawn in
59.07 seconds. DXC fails at its first frame in 2.46 seconds. Its complete native
exception capture records a smaller `0x2060` JIT frame but the same invalid
address construction: `mov w28,#0x2054; add x28,xzr,x28; str w4,[x28]`.
The actual write target is `0x2054`; the final fail-fast subtype is `0x49`.

The remaining eight DXC local arrays hold two-component root vectors addressed
by dynamic indices. The shader now selects the original scalar components
directly: the continuation loop visits only indices 0 and 1, and smart-curve
selection retains the original order modulo 2. Both roots are already fully
initialized immutable values. This changes neither arithmetic nor component
values, the three-edge visit order, early returns, sample positions, workgroup
storage or dispatches. It does not interpolate between components or change
compiler defaults.

The scalar-selection revision passes the same 36 managed controls and all six
880-case comparisons on each rebuilt local native provider, with zero differing
pixels. All four native ARM64 compiler cases pass with the exact retained compiler
identities and original flags: DXC's eight local arrays become zero in each entry
point. FXC's loop counts remain 7/5 and its temporary-register counts increase
from 26/24 to 27/25, so this is not a uniform register-pressure claim. Actual
ARM64 runtime execution must still establish whether it removes the remaining
generated-code failure; compilation and local pixel agreement do not qualify
the source or package.

The scalar-selection source at `1f81f86e6` subsequently passes all six original
ARM64 groups on default FXC in 40.49 seconds and Dawn in 52.90 seconds. Explicit
DXC still fails during the first frame wait after 3.10 seconds with exit code
`0xC0000409`, before completing a comparison group. Removing DXC's local arrays
therefore does not establish a runtime repair. The exact generated header, two
native DLLs, original package closure and failure receipt remain retained; a new
bounded capture independently records a first invalid store to `0x2074` in an
`0x2080` JIT frame. Five events complete in 6.53 seconds without exhausting the
eight-event cap. The observed shifted-register ADD uses XZR, not SP; removing
DXIL arrays did not repair the generated address.

The next source revision shares the two original smart-curve endpoint evaluations
through one bounded loop. Existing admission proves `index < end < 512`; stepping
by `end - index` visits exactly those two endpoints in order. Each result is assigned
to its own scalar, preserving the single original addition and subsequent midpoint
arithmetic. There is no zero-seeded accumulation, interpolation, reordered edge or
new raster policy. The helper's original 32-step bound, all early returns, barriers,
atomics, shared memory and dispatches are unchanged. Both managed and native
renderers consume this same canonical shader. All 36 managed controls and all six
original 880-case groups in each local native provider still pass with zero pixel
differences. All four original Windows release-flag compiler cases also pass.
Both DXIL entries now retain one 32-step subdivision body inside the endpoint
loop, with five square-root sites instead of six and no local arrays. FXC
temporary counts remain 27/25. These measured compiler changes do not establish
actual Windows runtime safety; the original three runtime routes remain required.

The rejected global midpoint quantizer regressed earlier diagonal controls and
was removed. Its failed captures remain retained. The bounded curved candidate
passes all original and new cases through native wgpu and Dawn, including actual
font captures under interpreters 35 and 40. A separate diagnostic also compares
all 128 retained system-font contours without pixel differences; this is not the
final original source-application, 72-frame text pair or package qualification.

Local validation on 18 October 2026:

- All 880 complete authored coverage cases match exactly in the managed atlas,
  native wgpu and native Dawn; every cold/warm pixel remains identical.
- Both native providers also pass all 880 cases from actual captured font batches
  under both TrueType interpreters, after disposing the public source owners.
- All 72 focused shader/atlas/integer-origin tests pass. The original native
  contour controls and package-consumer device/font/paragraph ownership controls
  pass with binary preparation included.
- The full native build succeeds for both providers. The first full renderer run
  passes 6,352 tests, retains 12 platform skips, and fails one source-text audit
  that still expects the former no-argument compute-pass signature. The audit now
  requires both the typed selector and its actual call; the failed run is retained.
- After correcting that source audit, the complete renderer passes 6,353 tests
  with the same 12 platform skips and no failures. Generated native contracts and
  the 101-field native memory inventory verification also pass.
- All 230 headless tests pass with zero skips.
- After the curved endpoint/boundary/dropout fix, the full renderer passes 6,354
  tests with the same 12 platform skips; all 230 headless tests still pass. Each
  native provider passes six complete 880-case executions (two original fixture
  sets through raw contours and both font interpreters), with zero differing
  pixels and unchanged cold/warm ownership checks. The managed suite executes
  both original fixture sets with the same atlas and ownership assertions.

The JIT/NativeAOT package scenario groups add both binary provider cases while
preserving every original MIL, text and EDIT scenario. The separate DX12 package
and NativeAOT lanes include the original binary check as well. Whole producer
CI, original system-font pixels, final source wiring, packages and application
acceptance are still required. Authored controls do not establish broad Microsoft
parity or release readiness.
