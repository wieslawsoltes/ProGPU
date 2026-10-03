# Original shader sampler transform animation controls

This additive original Microsoft WPF reference exercises actual animated
`ImageBrush.Transform` and `ImageBrush.RelativeTransform` source objects. It joins
the native sampler-transform implementation in the same product change. It does
not construct native packets or use ProGPU's transform resolver to obtain an
expected matrix or pixel. Every pre-existing reference family, assertion and the
shared 60-second deadline remains unchanged.

## Retained source and independent inputs

Each property route retains one original effect, shader, receiving visual, brush
and frozen 96-DPI 2-by-1 red/green bitmap through twelve states. The effect content
and final source clip are `(8,10,32,24)` inside a 64-by-64 opaque black target.
Brush opacity is `.5`; viewport and viewbox are the full relative rectangle.
Both the actual visual bitmap-scaling field and shader sampler request nearest
sampling. No attached-property inference substitutes for actual source state.

The twelve literal physical mappings are:

| Source state | Current mapping | Color interval / split |
| --- | --- | --- |
| matrix at origin | identity | `[0,32)` / 16 |
| matrix translation | X +8 | `[8,32)` / 24 |
| matrix mirror | X -1, offset 32 | `[0,32)` / 16, reversed |
| typed translation | X +8 | `[8,32)` / 24 |
| typed translation current change | X -8 | `[0,24)` / 8 |
| typed centered scale | X .5 about 16 | `[8,24)` / 16 |
| typed centered mirror | X -1 about 16 | `[0,32)` / 16, reversed |
| ordered group | translate 8, then scale .5 | `[4,20)` / 12 |
| group current-value change | translate 4, then scale .5 | `[2,18)` / 10 |
| reordered group | scale .5, then translate 4 | `[4,20)` / 12 |
| repeated child identity | translate 4 twice, then scale .5 | `[4,20)` / 12 |
| detached matrix animation | original base identity | `[0,32)` / 16 |

Intervals are local to the 32-wide secondary capture; final X adds 8. The
relative route expresses translation and center X in units of 32. The source
fixture stores independently specified matrices and intervals, not matrices
derived from rendered output or animation evaluation. Group tests retain the
same child objects, including the deliberately repeated translation object.

Real `MatrixAnimationUsingKeyFrames` and `DoubleAnimation` clocks belong to a
controllable four-second `ParallelTimeline`. The fixture observes an actual
paused/active timing tick before using exact seeks at zero, one or two seconds.
Discrete matrix values and dyadic interpolation avoid an unrelated numeric
approximation policy. Before and after every render it requires exact root and
child times, zero global speed, source object identities, six double matrix bits,
base values and actual dependency-property animation attachment. Detaching the
matrix clock must reveal the unchanged identity base. Named Rotate and Skew are
not part of this reference or qualified by these cases.

## Complete-frame evidence

Each of the 24 property-route/state configurations has two retained-object
renders and one fresh-source render using the independent literal matrix:
72 complete-frame replays. Every replay must match the literal nearest-color
oracle exactly, including empty columns, black exterior, opacity 128 and opaque
final alpha. Equivalent typed/group/matrix mappings must match earlier complete
rasters; absolute and relative routes must also match. Neither comparison is a
replacement for the independent per-byte assertion.

Inputs, source pixels, PNGs, raw BGRA, exact matrix bits, clock and source identity
metadata, architecture, original assembly/producer identities and SHA-256 hashes
are written with `CreateNew`. Pixel failures retain a separate failed receipt
before failing the application. The existing ARM software-unavailability case
requires opaque black output and reports zero qualified shader cases.

## Original contract and provenance

The original ProGPU-owned reference application at
`634a037af807ce9813b2d982e00256c3722adbfc` supplies the existing effect bytecode,
bitmap scene, capture utilities and unchanged deadline. The paired authored
native cases are in `progpu_native_shader_sampler_transform_fixture.hpp` at
`5464d04189b6ce3217f78d52a78480b74aa725d2`; their literal intervals do not call
the product transform resolver.

Immutable original WPF `381194e`,
`src/Microsoft.DotNet.Wpf/src/WpfGfx/core/resources/ShaderEffect.cpp`,
`CMilShaderEffectDuce::DrawIntoIntermediate` (794–868), supplies the observable
secondary-capture frame contract: brush sizing bounds start at zero and use the
implicit-input width/height, the separate intermediate is cleared, and the brush
is drawn through that context. This is why an absolute center 16 corresponds to
a relative center .5 even though the final receiving visual starts at X=8.
No foreign implementation is copied or adapted.

Public source contracts:

- [MatrixAnimationUsingKeyFrames](https://learn.microsoft.com/en-us/dotnet/api/system.windows.media.animation.matrixanimationusingkeyframes)
  defines the actual matrix keyframe animation.
- [TransformGroup](https://learn.microsoft.com/en-us/dotnet/api/system.windows.media.transformgroup)
  owns the ordered child transforms.
- [ClockController.SeekAlignedToLastTick](https://learn.microsoft.com/en-us/dotnet/api/system.windows.media.animation.clockcontroller.seekalignedtolasttick)
  and [Animatable.ApplyAnimationClock](https://learn.microsoft.com/en-us/dotnet/api/system.windows.media.animation.animatable.applyanimationclock)
  define immediate source-time evaluation and attachment/removal.

The reference deliberately remains `SoftwareOnly`. Its established integer-origin
software shader UV phase is not a hardware pixel-center equivalence claim. These
nearest, integral-boundary controls do not change that distinction or admit other
capture, filtering, frame or trigonometric contracts.

## Status

Authored only: no build, syntax check, test, verifier, original Windows execution,
GPU/VM operation or CI dispatch was performed. Actual original reference and both
native-provider execution remain mandatory at the final consolidated tip; no
source-host, package or desktop qualification follows from this authored fixture.
