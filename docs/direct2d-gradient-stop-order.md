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
