# Glyph coverage coordinate frame

## Evidence and correction

The original Windows bounded-texture assertion in PR #242 remains exact. Fresh
native diagnostic run `36882608788` reproduced 62 differing RGBA bytes (maximum
delta two), after all four original solid-paint controls passed. Its data-only
receipt retains actual caller input, loaded-library identity and output pixels;
it does not claim to capture prepared atlas storage.

Shared-shader run `36884526788` then used that original geometry with explicitly
synthetic coverage. At framebuffer pixel (10,12), ordinary Text supplied atlas
U=7.374999523162842 and sampled coverage 0.44105392694473267; bounded paint supplied
U=7.375 and sampled 0.4430147111415863. Both supplied V=6.125. The same discrepancy
occurred at 96- and 128-pixel target sizes. This is observed finite sampler/address
sensitivity, not a claim about a universal Direct3D precision rule.

Ordinary Text and painted glyphs now share the same coverage-address helper:

`atlasMin + (fragmentXY - physicalGlyphOrigin) * (atlasSpan / physicalGlyphExtent)`

The physical frame comes directly from the original four logical glyph corners
and DPI, never an inverse clip-coordinate calculation. The fragment position is
the framebuffer position defined by [WGSL](https://www.w3.org/TR/WGSL/#position-builtin-value).
Flat metadata is identical across both original glyph triangles. All four exact
positive-axis corners must agree; a diagonal or an epsilon is insufficient.
The original image triangles and paint UV interpolation do not change. Neither
sampler filtering, half-texel clamps, derivatives-before-discard, gamma, contrast,
source alpha nor fixed-function blending changes. No texel-load substitution,
coordinate snapping, bias, tolerance waiver or additional GPU readback is added
to production rendering.

## Actual-pass ownership

The existing private uniform tag at byte 204 carries an exact `-1f`
certificate. Existing positive `1f`/`2f` bounded-image-source/ROP tags stay unchanged;
the negative certificate cannot select the image shader's positive-tag branches.
Uniform size remains 224 bytes; public scene and frame ABIs do not change.
Zero retains the original interpolation. CanvasSize alone cannot certify
the mapping: managed and native providers give that field different meanings.

Native root glyph/semantic passes certify their authored orthographic projection,
identity transforms, zero origin, exact DPI relationship and actual full viewport.
Both native providers use these shared sites. Translated layer and destination
sampling passes remain uncertified. Managed root composition checks its original
projection, logical/physical dimensions and the normalized viewport actually
encoded by the pass. Multisampling and frames with GPU transforms remain on the
original managed path. Offscreen and mask uniforms default to zero; bounded source
scratch keeps its existing positive texture tag and is not glyph-certified.
The shader additionally rejects late-MVP, ClearType/color and non-axis geometry.
Those paths remain separate qualification work, not newly admitted by a padding
field or successful source test. Scene-cache reuse retains existing viewport/DPI
ownership and additionally keys the actual physical framebuffer dimensions, so a
window-only framebuffer change cannot retain a stale certificate. Stable replay
does not add a per-frame uniform write.

## Design references and preserved contracts

This is original ProGPU code, informed by public architectural contracts, not
copied renderer code. [Skia's text stages](https://skia.org/docs/dev/design/text_shaper/),
[DirectWrite/Direct2D integration](https://learn.microsoft.com/en-us/windows/win32/direct2d/direct2d-and-directwrite)
and [Win2D's retained layout](https://microsoft.github.io/Win2D/WinUI3/html/T_Microsoft_Graphics_Canvas_Text_CanvasTextLayout.htm)
support keeping layout/glyph identity independent from target rendering policy.
The correction therefore leaves shaping, source indices, placement and retained
font ownership intact. [HarfBuzz buffer positions](https://harfbuzz.github.io/harfbuzz-hb-buffer.html)
remain glyph-placement data, not a replacement coverage-address oracle.
[WebRender font-instance policy](https://doc.servo.org/webrender_api/font/struct.FontInstanceOptions.html)
reinforces retaining explicit rendering-mode and synthetic-style distinctions.
[Vello's renderer architecture](https://github.com/linebender/vello/blob/main/ARCHITECTURE.md)
separates shared representation from execution strategies; this change likewise
keeps one shared coverage helper across providers without replacing their
submission/resource ownership. It does not redesign layout caching, font fallback,
variation axes, workers, culling, cache eviction, uploads or device-loss recovery,
and makes no new performance claim about those systems.

## Focused validation and remaining gates

- Device-free managed/native certificate tests reject changed projection,
  shifted/non-full viewport, invalid DPI/dimensions and unsupported pass state.
- Source and rational-coordinate controls preserve shared shader wiring,
  original image geometry, four-corner admission and fixed uniform ABI.
- `hinted-canonical-frame` runs the actual Windows shared shaders with gate zero,
  the exact negative certificate and a separate binary-atlas oracle. The oracle independently derives
  texel addresses and exact dyadic four-tap weights from original physical
  rectangles. It checks raw coverage without a manufactured CPU gamma tolerance.
  Paired RGBA8 remains exact. Gate-zero output can be compared to immutable run
  `36884526788`; common changed-shader agreement alone is not independent proof.
- `hinted-native-input` builds a fresh unqualified native renderer in its own
  ephemeral Windows job and runs the original package-consumer fixture. Only data
  and logs leave the job. A passing focused run does not qualify package staging.

The original full renderer/package/NativeAOT matrix, managed GPU paths, both
native providers and architectures, affine text, source applications and platform
UI gates remain required. No merge, dependency update or release is justified by
source checks or synthetic coverage alone.
