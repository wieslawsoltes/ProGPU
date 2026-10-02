# Original fractional-local ShaderEffect capture reference

This independent Windows companion extends reference PR276 without changing its
13 padding cases / 39 replays / 38 controls, the preceding 28 ImageBrush cases /
84 replays / 19 arithmetic controls, or the original 25 arithmetic shaders.
It changes no native renderer, WPF source provider, dependency pin or admission.
The native counterpart is the bounded integral-final-placement family stacked
on PR277. Fractional final placement remains deferred.

## Immutable original contract

The source inspected is LibreWPF
`381194e1ffe4d64fb747556fcaf76e1c34fe9df8` (original WPF implementation):

- `PresentationCore/System/Windows/Media/Renderer.cs:55–64` builds the RTB DPI
  root using original double DPI times `1.0 / 96.0` before sending MatrixTransform.
- `WpfGfx/core/resources/matrix.cpp:61–79` narrows every original matrix component
  before composition; `resources/translate.cpp:78–86` likewise narrows offsets.
  `common/MatrixStack.cpp:58–60` prepends each local FLOAT matrix to the previous
  FLOAT top. Its offset path at108–109 performs the original float multiply/add.
  Casting an accumulated double matrix once is not equivalent.
- `common/BaseMatrix.cpp:767–769,804–837` computes axis lengths with `sqrtf`,
  constructs the scale matrix, then inverts it and multiplies the original matrix
  to obtain the residual. Positive axes alone do not prove exact residual identity.
- `resources/ShaderEffect.cpp:172–179` inflates LOCAL float edges with separately
  narrowed original double paddings. `uce/drawingcontext.cpp:4912–4923` supplies
  the original inner visual bounds; the source Visual export retains those inner
  bounds separately from its transform and final clip.
- `uce/drawingcontext.cpp:3695–3733` maps inflated bounds by scale only.
  `common/rtutils.cpp:302–330` allocates floor(min) through ceil(max), not ceiling
  the extent. `drawingcontext.cpp:3398–3420` renders input using scale minus this
  integer origin. `3825–3878` reapplies residual transform plus integer origin.
- `resources/Effect.cpp:37–50` retains the whole effect input independently of
  final clipping. `ShaderEffect.cpp:798–838` realizes an ImageBrush over the full
  zero-origin physical intermediate extent, not the original content/viewbox.

The admitted native proposal therefore needs a retained original float-history
witness, scale-space allocation origin/extent, and an exact integral final-frame
proof. Its input-to-capture mapping is `S*p - A`. Final mapping is `R*(A+q)`;
the first family requires an exactly identity residual linear part and integral
final origin. No epsilon, cast-at-end repair, fabricated inverse, parent-clipped
allocation or `ceil(width)` substitute establishes that proof. Existing generic
and v1–v3 transform behavior must remain unchanged.

## Actual source controls

The new public original drawing is always `Rect(16.75,16.25,15.5,7.5)`, retained
unchanged on a real DrawingVisual. Both actual content and descendant bounds are
verified before capture. Target size is96×64, with source DPI1/2 and a final
physical translation of(2,3). A parent sets actual protected nearest/aliased visual
state so both input and final composition have an explicit source contract.

| Padding(top,bottom,left,right) | DPI | Scale-space allocation(x,y,w,h) |
| --- | --- | --- |
| zero | 1 | (16,16,17,8) |
| (.25,1.25,.5,1.5) | 1 | (16,16,18,9) |
| zero | 2 | (33,32,32,16) |
| (.25,1.25,.5,1.5) | 2 | (32,32,36,18) |

Twenty-one cases execute cold/warm/independently rebuilt captures, totaling63
shader replays and23 separate original ordinary-drawing baselines. Input cases
must equal a separately rendered no-effect original drawing byte-for-byte;
constant output must fill the authored complete allocation, including its border.
Every ordinary-input baseline must independently contain visible white pixels,
only binary black/white RGB and opaque alpha; two empty results cannot qualify
input preservation.
Derivative output uses the full18×9 or36×18 frame after overriding original user
c0. Two-texel ImageBrush output retains actual144/192 bitmap DPI and fills the
entire capture. A separate final clip changes only output. Consecutive zero →
asymmetric → zero captures preserve the same effect, source visual and drawing;
reset must restore every pixel and expansion must change visible border pixels.

UV controls use32×16 captures with fractional local edges, isolating frame and
phase from unrelated non-power-of-two software coefficient/packing behavior.
They preserve the established SOFTWARE integer-origin UV phase. Immutable
`ShaderEffect.cpp:1136–1178` and `fxjit/PixelShader/pshader.cpp:286–306` support
that phase. Original hardware instead sends an unchanged unit-quad UV through
the D3D9 half-pixel projection, giving `(deviceCenter-finalOrigin)/extent`.
These software receipts must not qualify native hardware UV equivalence.

Two nested controls construct an actual outer translation, inner scale2 and
source scale.5. The original source order restores unit scale without scaling
the outer translation; reordering the noncommuting operations is observably wrong.
A separate original-only case uses two MatrixTransforms with scaleX=`1+2^-24`:
each original resource narrows to1, whereas casting their accumulated double
product differs. It records the exact original double transforms and remains a
native rejection control where generic geometry and the source-float witness
disagree, not a reason to alter the generic traversal.

Two further original-only controls place the final quad at fractional offset
(2.25,3.5): constant output and original ps_2_0 `t0.xy*t0.xy`. A separately drawn
ordinary opaque rectangle supplies an independently checked binary aliased
coverage control, never inferred capture bounds. UV-squared colors use the known
software integer-origin coordinates on that authored frame. These inputs expose
the deferred final-device-sample contract; neither is a native candidate. A
future GPU implementation could use an output intermediate only if the shader
is evaluated on the FINAL device lattice and then composed at unit integer
placement, not by filtering results evaluated on the source lattice.

## Evidence and gates

All new input JSON, original padding bits and transform histories, source bitmap,
baseline PNG/BGRA, all63 shader PNG/BGRA and complete receipt use `CreateNew`.
Pixel failures accumulate across the new inventory, write a failed receipt,
qualify zero new cases and fail the process. There are70 device-free independent
controls. Original ARM64 unavailable-software behavior remains a strict negative
control: non-image effects equal the ordinary original input; ImageBrush effects
contribute no color. It qualifies zero software shaders, never native ARM64 pixels.

The old shared60-second stopwatch,90-second process timeout and8-minute job
deadline are unchanged. This implementation is authored pending bounded checks
and actual hosted original Windows execution. No source hardware, native provider,
package, WPF551 integration, fractional final placement or UI qualification follows
from source arithmetic or syntax checks.
