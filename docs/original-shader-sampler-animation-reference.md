# Original ImageBrush sampler animation controls

This additive reference family uses **original Microsoft WPF only**, through the
existing `eng/WpfShaderEffectReference` application. It does not call ProGPU's
renderer or construct native animation packets. The prior 25 arithmetic, 28
sampler, padding, local-frame and transform families and their expectations remain
unchanged. The existing shared 60-second deadline and workflow are not extended.

## Actual source objects and time

One live `DrawingVisual`, `ShaderEffect`, `PixelShader`, `ImageBrush` and frozen
96-DPI 2-by-1 red/green `BitmapSource` survive the entire nine-state sequence.
The receiving content and clip are both `(8,10,32,24)` in a 64-by-64 target.
The wider clip is deliberate: it exposes viewport-only changes that the older
central clip can hide. Both the actual protected visual bitmap-scaling field and
the shader sampler request nearest sampling. An inert attached scaling property
is not used as evidence of source MIL state.

A controllable `ParallelTimeline` owns real `DoubleAnimation` and `RectAnimation`
clocks. A retained `CurrentTimeInvalidated` observer supplies the timing consumer
before any clock is attached to the brush, including the original detached
baseline. At least one actual notification is required and the observer is
removed during disposal. A temporary `CompositionTarget.Rendering` subscription
schedules actual composition in the offscreen reference, which has no window.
The shared startup helper removes that subscription before capture, including
on failure, and retains its positive notification count in the receipt.
It is paused through the public controller; a bounded dispatcher frame
observes actual paused/active state. It does not assume that waiting an arbitrary
duration has paused the clock. `SeekAlignedToLastTick` selects exactly zero or two
seconds in a four-second duration **after** changing clock attachments. A newly
attached paused clock otherwise retains its previous property snapshot until
the immediate seek notifies that consumer. Before and after every render, the reference
requires exact root/child times, paused state, zero global speed, current-property
bits, base-property bits and actual animation attachment. Failure is explicit;
there is no metadata-only replacement for a live source clock.

The independent expected current values are literals, not obtained from
`AnimationClock.GetCurrentValue` or product helpers:

| State | Current opacity | Viewport | Viewbox | Attached clocks |
| --- | --- | --- | --- | --- |
| baseline | 1 | full | full | none |
| opacity | .5 | full | full | opacity |
| viewport | .5 | (.25,0,.5,1) | full | opacity, viewport |
| viewbox | .5 | (.25,0,.5,1) | (.5,0,.5,1) | all three |
| seek origin | 1 | full | full | all three |
| hidden base update to .25 | 1 | full | full | all three |
| detached base | .25 | full | full | none |
| reattached at two seconds | .5 | (.25,0,.5,1) | (.5,0,.5,1) | all three |
| detached again | .25 | full | full | none |

Null `ApplyAnimationClock` removes the animation influence, revealing the actual
base value. Reattachment uses the original clock objects and the original brush;
it does not recreate the receiving visual. Source pixels, DPI, shader policy and
object identity are checked independently of the expected final pixels.

## Pixels and evidence

Each state renders twice with the retained objects and once with independent
objects whose brush properties are set directly to the literal current values:
27 complete-frame replays. Every replay also meets an independent nearest-color
oracle with integral viewport boundaries, original black background and opaque
final alpha. Removing an animation must restore the earlier exact raster;
rewinding and reattaching have explicit complete-frame equivalence controls.
The oracle does not infer unknown repeated-linear precision or a new effect UV
phase from animation values.

Every input, source bitmap, replay PNG, raw BGRA array, exact current/base double
bits, clock metadata and SHA-256 is retained. Pixel failures produce a failed
receipt and fail the application. Existing native ARM64 software unavailability
remains a distinct negative control: no sampler color and zero qualified shader
cases. `SoftwareOnly` captures do not establish hardware, native-provider,
package or source-host parity.

## Primary contracts and clean-room provenance

Only observable API/state contracts were used; no foreign implementation was
copied or adapted. The bitmap, identity sampler bytecode and reference utilities
reuse original ProGPU-owned `ImageSamplers.cs` at product authoring base
`44d97e9eddfc91b1ca01e29638541029a8c40c78`.

- [ClockController.SeekAlignedToLastTick](https://learn.microsoft.com/en-us/dotnet/api/system.windows.media.animation.clockcontroller.seekalignedtolasttick)
  supplies the immediate current-time/current-value boundary.
- [ClockController.Pause](https://learn.microsoft.com/en-us/dotnet/api/system.windows.media.animation.clockcontroller.pause)
  and [Timeline.CreateClock](https://learn.microsoft.com/en-us/dotnet/api/system.windows.media.animation.timeline.createclock)
  define actual controllable clock ownership.
- [Clock's timing-consumer contract](https://learn.microsoft.com/en-us/dotnet/api/system.windows.media.animation.clock)
  requires an event observer or animated property for a clock to progress.
  Reading properties from a separate dispatcher timer is not such a consumer.
- [CompositionTarget.Rendering](https://learn.microsoft.com/en-us/dotnet/api/system.windows.media.compositiontarget.rendering)
  supplies a public composition callback after animation and layout. Only the
  bounded startup frame subscribes; pixel captures use the unchanged source.
- [Animatable.ApplyAnimationClock](https://learn.microsoft.com/en-us/dotnet/api/system.windows.media.animation.animatable.applyanimationclock)
  defines property attachment and null removal, not source-base replacement.
- [Brush.Opacity](https://learn.microsoft.com/en-us/dotnet/api/system.windows.media.brush.opacity),
  [TileBrush.Viewport](https://learn.microsoft.com/en-us/dotnet/api/system.windows.media.tilebrush.viewport)
  and [TileBrush.Viewbox](https://learn.microsoft.com/en-us/dotnet/api/system.windows.media.tilebrush.viewbox)
  define the independent bounded brush inputs.
- Immutable original WPF `381194e`, `PresentationCore/System/Windows/Media/Animation/Clock.cs`
  confirms that pause is pending until a timing tick and immediate seek notifies
  animation storage. `PresentationCore/System/Windows/Media/Generated/ImageBrush.cs`
  retains separate opacity, viewport and viewbox animation resource handles in
  the original MIL packet. This source is behavioral evidence, not an imported
  implementation or a guarantee about packets emitted by another source host.

## Status

The integrated original Windows runs `37764831728` at `5024aa0e2` and
`37768049325` at `40d26c090` exhausted the shared deadline during clock startup.
A local public-API probe on original Microsoft WPF .NET 10.0.12 measured the
stopped clock with a timing observer alone and the active, paused clock after a
composition subscription. It also measured the stale property after attachment
and the exact current value after the subsequent immediate seek.

The repaired original animation and transform-animation families passed in the
local Windows guest: x64 qualified all 33 states and 99 full-frame replays in
3193 ms; ARM64 passed the distinct unavailable-software controls in 1472 ms.
All current/base/time and pixel assertions remain unchanged. These selected
family diagnostics do not replace the complete hosted original workflow or
qualify either native provider, source application or package.
