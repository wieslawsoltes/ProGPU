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
The shader additionally rejects late-MVP and ClearType/color geometry. Certified
non-axis triangles use the separately described affine address below; their GPU
qualification is not inherited from the original axis-only evidence. Scene-cache reuse retains existing viewport/DPI
ownership and additionally keys the actual physical framebuffer dimensions, so a
window-only framebuffer change cannot retain a stale certificate. Stable replay
does not add a per-frame uniform write.

## Affine physical coverage

Exact parent `e4ab2a7b9` package checks reached the italic/shear bounded-texture
case after passing the original canonical controls. Windows stock/Dawn x64/ARM64
reported 11 differing bytes (maximum one), first pixel `(48,38)` green 79 versus
78; native Metal ARM64 reported 24 bytes (maximum one). No failed Build runtime
was staged to investigate this result.

The axis-only helper left this occurrence on two different address calculations:
ordinary Text interpolated atlas UVs over its glyph triangles, while bounded
texture paint inverted separately interpolated image coordinates. The candidate
correction extends the *existing actual-pass certificate* to each finite,
nonsingular original physical triangle. It retains independently rounded corners
012 and 023, computes each signed inverse in physical coordinates, and evaluates
the same barycentric-to-atlas expression from the actual fragment center in Text,
material and texture paint. It never reconstructs corner 3 as a parallelogram.

Each bounded image copy carries one constant original glyph-triangle mapping;
its own image diagonal does not switch that mapping. Separate copies, original
paint UVs, logical half-open edge tests, contribution order, derivatives and
filter/gamma/alpha policy remain unchanged. The qualified positive-axis expression
is unchanged. Gate zero, late-MVP, unsupported text modes and non-finite/singular
physical inverses retain the original path. Six flat frame floats and one mapping
tag fit the existing paint pipeline's limit: 16 user locations, 41 components.
No public/native record layout, target certificate, fixture or tolerance changes.

Independent rational controls exercise the actual four corners, reflected and
rotated signs, folded triangles with distinct contributions, origin/DPI placement
and singular rejection. These controls and host shader compilation are not proof
of original hardware interpolation or package pixels. Same-head affine gate-zero
and certified observations, followed by the unchanged authentic full-RGBA fixture,
remain required; matching two changed shader paths alone is not qualification.

The bounded sampling probe keeps every historical source profile and adds an
exact separately pinned physical-triangle profile. Its `--native-frame
--affine-frame` control reuses the original derived padded tile and synthetic
nonuniform coverage, applying the unchanged package fixture's italic/shear basis,
skew and relative-position arithmetic. An independently packed 192-byte control
binds those input bytes. Run once without `--canonical-frame` and once with it in
fresh output directories; the original `--fallback` remains an explicit Windows
diagnostic choice. The old axis-only receipt is not affine gate-zero evidence.

This affine control saves the existing raw texel/sample/gamma/alpha and cold/warm
RGBA comparisons without inventing a CPU gamma or hardware-sampler tolerance.
`--binary-oracle` remains the unchanged axis-only independent dyadic control and
rejects combination with `--affine-frame`. Production shader source is never
rewritten for these input variants. At this checkpoint, 23 math/source controls,
193 device-free probe controls and both shared modules/all 16 Metal pipeline
validations pass. No draw/dispatch/font or ProGPU-native renderer ran locally;
the shader check used the existing four-byte WebGPU initialization queue probe.
Actual affine pixels and authentic package qualification remain pending.

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

At exact correction head `4a5c93b08397f8ec6ea532b1e4722299db8d3648`, the
[Windows shader/oracle run](https://github.com/wieslawsoltes/ProGPU/actions/runs/36889398905)
passed on SilkNative/D3D12/FXC, Microsoft Basic Render Driver. All 144 saved
output hashes were verified. The uncertified control's 48 outputs exactly match
immutable run `36884526788`, including the original 65-byte single-occurrence and
111-byte overlap differences. The certified nonuniform and binary controls have
zero RGBA, sampled-coverage or final-alpha differences at both target sizes.
All 24 independent binary-oracle captures match exact coordinates/coverage.
The remaining coordinate-only differences at the second glyph's right/bottom
edge retain its original zero-coverage per-path boundary ownership.

The [fresh original native consumer](https://github.com/wieslawsoltes/ProGPU/actions/runs/36889403967)
also passed at that exact head (7m35s): prepared hinted layouts, all four original
solid DPI/overlap cases, and the previously failing bounded-texture cold/warm and
same-count texture-replacement assertion. Both providers compiled, but this run
executes stock Windows x64 wgpu-native only. Its artifact `11176057366` contains
logs/data, not native runtime DLLs. The affine/readback/source-stack integration
is later work and requires its own exact-head validation; these runs do not
qualify those later changes or release packages.

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
