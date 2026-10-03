# CFF vertical origins from retained contours

The private prepared-original-font consumer can derive an absent CFF/CFF2 VORG
origin from the actual retained design curves and the original vmtx top side
bearing. It uses the same selected font, captured axis instance and top/FD matrix
as outline decoding. No source callback, second glyph decode or source-local
font substitution is introduced. Existing explicit VORG values retain their
precedence, and TrueType's stored/varied bounds and phantom points remain separate.

## Arithmetic and ownership

The new private `read_outline` metric helper reads the existing strict base
metadata, then reduces each retained line/quadratic/cubic's actual Y extrema.
Endpoints and interior derivative roots participate; unused control fields do
not. Derivative degree reduction uses exact zero, not an epsilon. The cubic
quadratic formula uses a sign-selected numerator and root product to retain
the smaller root. Evaluation uses double Bernstein interpolation. Float input
coordinates, coefficient products and subnormal values fit the double range.
Nonfinite selected points and unsupported kinds fail without partial output.

The original top bearing is added to the double maximum before narrowing the
retained source origin to float. There is no integer rounding, int16 clamp,
control-envelope substitution or tolerance-based source admission. This is
an explicit unrounded natural-outline implementation; it does not establish
DirectWrite's own intermediate numeric decisions. Empty contours retain their
real advance without inventing an ink origin.

The completed origin joins the existing per-font/per-instance vertical cache.
All new metric and contour entries remain unpublished until the complete run
succeeds. CFF2 without VORG uses its already-varied outline maximum; a VVAR
vertical-origin map requires its genuine VORG base and cannot be added to a
contour-derived origin a second time. VVAR advance-height handling stays paired
with the same immutable instance. Missing vhea/vmtx synthesis remains separate.

This cold metric operation is bounded O(S) time and O(1) scratch in retained
segments. Scalar derivative degree/root decisions and interpolation have
per-curve dependencies; they do not run again on a warm glyph. Existing SIMD
placement, GPU rasterization, atlas ownership, draws/submissions and device-loss
policy are unchanged. Both native renderers consume the same source geometry.
No managed shaping/layout contract, C ABI or public COM slot changes.

## Source contracts and controls

[OpenType vmtx](https://learn.microsoft.com/en-us/typography/opentype/spec/vmtx)
defines the maximum-plus-bearing origin. The
[VORG specification](https://learn.microsoft.com/en-us/typography/opentype/spec/vorg)
explicitly distinguishes direct origins from bounds calculated with different
data types and rounding decisions. [CFF2](https://learn.microsoft.com/en-us/typography/opentype/spec/cff2)
defines its line/cubic outline representation. The implementation derives the
Bernstein derivative algebra directly; no foreign engine implementation was
used. Existing ProGPU Direct2D path bounds at parent fd53fdbeb were inspected,
but their epsilon degree reduction and immediate float bounds were not adopted.
The retained outline/run-transform separation and cross-engine applicability
remain those recorded in [sideways placement](direct2d-sideways-glyph-placement.md).

Independently authored original CFF fonts contain two cubic arches whose control
points extend beyond their real maxima. One maximum is300 or301.5, the other280;
their bearings produce origins380/381.5 and240. Literal source geometry checks
actual prepared cubic endpoints/handles, explicit/null advances, an empty middle
glyph and warm owner reuse. Separate algebraic controls retain quadratic and
linear derivatives, both stable-root branches, a repeated root, endpoint roots,
subnormal geometry and late malformed input. A zero-bearing control ensures
the subnormal result is not hidden by ordinary bearing addition.

All controls are authored, not executed. Original SDK metric/outline observations
and exact original/provider full-frame comparisons remain required before this
source family is qualified. The focused font observer is not authorized to run
yet. Builds, tests, verifiers, CI and GPU/UI execution remain deferred; no original
rounding or application/package parity is claimed.
