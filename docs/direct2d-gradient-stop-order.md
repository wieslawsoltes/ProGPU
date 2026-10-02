# Direct2D gradient-stop order and content extent

Portable `CreateGradientStopCollection` now owns finite stops in their original
caller order, including positions outside [0, 1]. Public readback is not sorted
or clamped. Drawing validates the complete captured collection and stable-sorts
only its private render snapshot; equal-position low/high stops retain source
order. The Windows command-sink translator does the same with genuine
`ID2D1GradientStopCollection1::GetGradientStops1` values. Original finite/color,
count, factory, interpolation/alpha and coordinate-transform gates remain intact.
The old descending-input rejection fixture is replaced by a nonfinite-input
rejection at the same checkpoint; descending inputs now have explicit positive
creation, readback, lifetime and rendering controls.

Microsoft's [gradient-stop contract](https://learn.microsoft.com/en-us/windows/win32/api/d2d1/ns-d2d1-d2d1_gradient_stop)
permits arbitrary position order and outside-range stops while specifying stable
duplicate order. Its [extend-mode contract](https://learn.microsoft.com/en-us/windows/win32/api/d2d1/ne-d2d1-d2d1_extend_mode)
clamps the gradient axis/ellipse's content, not the collection's first/last offset.
Therefore source Clamp with outside-range stops selects the additive
`PAD_UNIT_INTERVAL = 4` spread mode. Shared 2D and 3D GPU consumers clamp only
the parameter before the unchanged stop/gamma interpolation. No stop/color is
invented on the CPU. Native and managed wire validators admit this mode only for
linear/radial gradients, reject incompatible high-bit flags and unknown values,
and keep the 256-byte brush record unchanged. Hatch-set family counts retain
their distinct existing field interpretation. Existing spread values 0–3,
outside-color/conical flags and the spread mask remain unchanged; in-range
Direct2D collections retain their previous serialized spread. Managed material
shader consumption is synchronized without changing any ordinary source default.

Provenance is the original repository collection/snapshot translation, sorted
semantic brush storage and shared gradient shaders. No foreign implementation
was copied. Sorting is O(N log N) over the existing bounded owned snapshot, with
the standard stable sort's bounded temporary allocation handled by the original
exception/error path. There is no extra native crossing, rendering algorithm,
GPU readback, submission, fallback or claimed performance improvement.

## Authored controls and pending qualification

Twelve linear/radial, gamma, spread and opacity variants compare unsorted input
with an independently enumerated stable input. They require original collection
readback before/after drawing and release source resources before replay. Both
native providers require exact full RGBA bytes on cold/warm/independent engines
and one submission per draw. An independent pixel-center hard-edge oracle catches
duplicate reordering; it does not use the producer's sorted output. The Windows
D2D/WIC lane independently executes both source orders and requires full-byte
agreement with native output and the absolute oracle, with no border exclusions
or pixel tolerances. Genuine Windows collection1 controls also exercise direct
callbacks and actual command-list streaming, preserving original readback and
checking retained sorted payloads after source release.

A separate original {-1:black, 1:white} source checks every byte inside, at and
outside the linear axis, including the interpolated gray left edge. Both GPU
providers pair it with a deliberate legacy raw PAD=0 stream: that stream must
still extrapolate to black on the left while mode4 remains gray. Neither test may
pass through self-equivalence alone. Native/managed negative controls cover
unknown modes/bits, illegal flags/kinds and unchanged outputs/storage; source
controls preserve null, zero-count, nonfinite and stop-budget failures.

Base is main `0cb73960567e142720de2a110f56230c5afddb5d`; no dependency on #275.
This major implementation is committed before focused checks. Original getter
order, Windows pixels and provider behavior are authored gates, not observed
parity claims. No native/renderer/full graph build, GPU/VM execution or runtime
staging has occurred. Generated C# structure verification and bounded source
syntax checks follow the commit; exact hosted whole-Build/package/provider and
original Windows gates remain mandatory.

Post-commit checks: Apple Clang 21 strict C++20 syntax-only compilation passed
the portable target, shared brush builder, semantic brush validator, complete
portable compatibility test TU and explicitly instantiated pixel fixtures, with
`-Wall -Wextra -Wpedantic -Wshadow -Werror` and 45-second process bounds. LLVM
Clang 22.1.8 also precompiled the actual module and syntax-checked its unchanged
import path plus the new public constant control under those flags and the
actual Xcode SDK. The repository generator (identical cached tool source) verified
the generated C# structures against the changed header: no structure regeneration
was needed. The separate public C/managed spread enums were updated together.
The three changed/new C# files passed Roslyn syntax parsing only, not type
compilation or test execution. The memory guard passed 93 owned fields/seven
excluded identities and `git diff --check` passed. No native object/library,
provider, shader pipeline or GPU work was built or executed. The eight original
Windows direct/streamed linear/radial clamp/mirror cases explicitly require the
new/unchanged serialized mode as well as every original stop.

## Hosted failure diagnosis and correction

Build 37039004155 at `5de5fa787` reported the same first mismatch in the
Linux ARM and GCC lanes: radial variant 1, original unordered input, pixel
(29,16), expected RGBA (255,0,0,255), observed (0,0,255,255). The analytic
rectangle carries its draw transform in positioned vertices but evaluates paint
from original local center plus local SDF coordinates. Direct2D had also put
inverse draw translation into that local brush snapshot, removing the fixture's
two-pixel Y translation twice. The original horizontal linear case could not
observe that Y error. This is a coordinate-contract defect, not a tolerance,
duplicate sorting, opacity or gamma adjustment.

Portable rectangle/ellipse/equal-radius rounded-rectangle gradients now explicitly
use the analytic local frame (inverse brush only); paths, line geometry, unequal
rounded-rectangle fallback and layer masks retain target-frame translation.
Both draw and brush transforms still pass the original inverse/finite gate.
The real Windows command sink uses the same analytic-rectangle distinction and
includes it in immutable brush-cache identity, so a rectangle/line/rectangle
sequence cannot reuse the wrong frame. No public brush/readback is changed.

The authoritative `RegisteredMaterialCommon.wgsl` prefix, used by managed and
both native Vector/hinted-material shaders, also now consumes mode4. The prior
change reached Hatch/3D consumers but missed this shared 2D prefix. All four
spread helpers and paired native-builder/native-validator/managed-validator paths
are accounted for; legacy PAD and illegal mode/kind/flag gates remain unchanged.
An embedded-resource/CMake guard checks that actual registered consumers retain
this prefix. The existing [-1,1] absolute/legacy-PAD differential remains strict.

Every original twelve-variant input and exact pixel assertion is retained. A
thirteenth vertical linear case, also original-Windows paired, independently
observes the draw-Y transform. New source controls inspect all three analytic
families against line and unequal-radius path fallbacks under translation,
nonuniform scale and shear. The eight original Windows collection observations
remain, with 24 additional direct/streamed mixed-frame cache cases. These fixes
and controls are committed before focused source checks. Hosted original
Windows/provider execution, not this source diagnosis, remains the final judge.

The older Windows semantic-stream fixture recorded a transformed rectangle but
expected target-frame inverse-draw coefficients. Its four serialized-coordinate
expectations now require the exact original inverse-brush matrix (no tolerance),
matching that primitive's local evaluator. Original source operations, resource
counts, stops, clips and all independent original pixel assertions are unchanged.

Post-fix checks passed strict C++20 syntax for the complete portable target,
complete portable compatibility test TU and instantiated shared gradient pixel
fixtures (`-Wall -Wextra -Wpedantic -Wshadow -Werror`, 45-second bounds). The new
managed embedded-resource guard passed Roslyn syntax parsing only. Windows-only
source, actual embedded-resource test execution and all native/GPU behavior remain
hosted gates. No local object/library, renderer build or GPU execution occurred.

## Exact gamma endpoints

Build 37042541888 at `a0296c4f3` passed the earlier coordinate case, then its
MSVC native GPU job 110955930140 reported variant 6, unordered input, (4,4):
expected (128,0,0,255), observed (127,0,0,255). This is the original gamma-1.0
half-opacity constant-red interval. The f32 expression `1.055f - .055f`
produces `0x3f7fffff` (0.9999999403953552), not exact one; half opacity then
produces 0.4999999701976776 and an UNORM product of 127.49999237060547.
Opaque output masked this endpoint loss by still rounding to 255.

The four actual gradient linear-to-sRGB helpers now return only exact input
zero/one unchanged before their original formula. There is no interval snapping,
new clamp, HDR change, gamma policy change or revised expected pixel. This
preserves the mathematical endpoints of the existing
[linear-gamma interpolation](https://learn.microsoft.com/en-us/windows/win32/api/d2d1/ne-d2d1-d2d1_gamma)
contract. Every other input retains the original conversion, including the old
negative-value policy. Embedded-resource controls require that exact equality
guard and the unchanged remainder in all four consumers.

All thirteen original source variants remain unchanged. An added single-frame
five-band original gradient uses exact sRGB channels 0, 1/128, 1/2, 127/128 and 1
at half opacity over black. Its independent absolute bytes are 0, 1, 64, 127 and
128: near-endpoint bands must not be snapped to the endpoints. Both native
providers require full-frame cold/warm/independent equality with unchanged
submission counters; original Windows D2D/WIC executes the same public source
operations and must match the independent bytes and ProGPU in full. Existing
engines are reused by the provider fixture, with no new timeout or tolerance.

The separate Windows ARM job 110955930834 stopped at three ClangCL `/WX`
SDK-union aggregate-initializer warnings. Those matrix values now use the
already-established `D2D1::Matrix3x2F` constructor, preserving every coefficient
and strict snapshot assertion. Windows compilation and all new pixel controls
remain hosted gates; this checkpoint precedes bounded source checks.

Post-commit checks passed strict C++20 syntax for the instantiated shared gradient
fixtures and complete portable compatibility test TU, under the existing warning
flags and separate 45-second bounds. The changed managed test passed Roslyn
syntax parsing; four offline source guards checked the actual WGSL helper bodies
against their exact endpoint guard and unchanged remaining formula. These were
source checks, not managed type/test, shader or GPU execution. `git diff --check`
passed. No native object, dependency or renderer build was performed.
