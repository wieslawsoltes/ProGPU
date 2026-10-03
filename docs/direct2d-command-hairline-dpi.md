# Direct2D command-stream hairline DPI

The Windows command sink owns a copy of the actual target's two DPI values,
alongside its DIP extent. `create_for_target` copies the immutable descriptor;
surface-backed command-list translation reads the original context DPI while
holding the surface access lock. No caller memory or mutable context is retained
by the sink. Source command coordinates remain DIPs: pixel-unit callbacks are
still rejected.

Hairline intervals and dash offset use the same owned SIMD implementation as the
portable target: each physical length is multiplied by `96 / actualDpi` once.
Hairline bounds use the original portable per-axis float arithmetic. Normal and
fixed strokes retain their existing width, transform and targetless behavior.
Unknown target DPI and unequal-axis hairlines fail explicitly before geometry or
brush publication; a failed hairline cannot fall back to DPI-unaware `Widen`.

`HasTargetDependentStrokes` (bit 11) reports retained hairline geometry separately
from `HasTargetDependentMasks`. The C constant feeds the generated native managed
contract; the public result preserves that flag. A new DPI requires a new
recorder/scene generation, not reuse of the converted dash lengths. Full-target
Clear removes the dependency with the discarded scene, without resetting the
original translated-draw/callback accounting. Existing descriptor and result
layouts are unchanged.

Implementation provenance is ProGPU `77b60ae78c416851858527bf355a947a8752ee25`,
`progpu_native_direct2d_render_target.cpp::scale_hairline_dashes` and the adjoining
device-stroke bounds. This is a shared source-adapter correction, not a new
rasterizer, managed rendering algorithm, pipeline or fallback. Both native
providers and managed rendering consume the unchanged semantic scene/shaders.

Primary contracts: [stroke transform types](https://learn.microsoft.com/en-us/windows/win32/api/d2d1_1/ne-d2d1_1-d2d1_stroke_transform_type)
separate transformed geometry from the one-device-pixel pen;
[dash arrays](https://learn.microsoft.com/en-us/windows/win32/api/d2d1/nf-d2d1-id2d1strokestyle-getdashes)
retain stroke-relative lengths; [target DPI](https://learn.microsoft.com/en-us/windows/win32/api/d2d1/nf-d2d1-id2d1rendertarget-setdpi)
owns the DIP/pixel mapping. The existing cross-engine architectural comparison
in `DIRECT2D_WIN2D_COMPATIBILITY.md`, “Portable stroke-transform parity”, remains
applicable: retained source geometry, no per-dash submission, no text/cache change.

Authored Windows source controls cover 18 explicit-target combinations
(96/192/384 DPI, normal/fixed/hairline, line/cubic), two targetless normal/fixed
controls, three failed-frame atomicity controls, full-target Clear accounting,
and two actual command-list/surface-DPI generations. They read literal scene
bounds and every odd dash interval/phase, mutate the caller descriptor after
creation, and read back unchanged original SDK style values. Prior positive
hairline tests now supply explicit 96-DPI metadata; their assertions remain.

All new controls are authored only. No compilation, source verifier, original
SDK execution or native/provider pixel qualification has run for this stack.
