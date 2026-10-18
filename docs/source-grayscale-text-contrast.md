# Explicit source grayscale text contrast

`GlyphCoverageTransfer.GdiPlusGrayscale(0..12)` is an immutable paint policy.
Default keeps the existing ProGPU transfer. The explicit policy is valid only
with grayscale glyph coverage; it does not select shaping, metrics, glyph
placement, font execution, spatial sampling, GDI quality or ClearType.

The original Microsoft observation used 936 frames in 8.949943 seconds, with
no input or desktop windows (run 39117886245, commit
`d33cf935b401f1d63039a9d521bcefb917ec7179`). Its artifact 12132528187 has SHA256
`42347ace23d41cc7f7d947ee42d3b1265bd09f7b8073c1c35f49c276a4aa1702`.
All 3,354 distinct black-on-white transfer tuples agree with
`round(255 * (originalBackgroundByte / 255) ** (1 / (1 + contrast / 10)))`.
For the non-ClearType observations, complementary black/white pixels and
transparent alpha locate the transfer before foreground composition. This is an
inference from those original observations, not a documented Microsoft algorithm
or proof that ProGPU's spatial coverage is the same. SystemDefault/ClearType
observations have separate channel behavior and remain excluded.

## Recording and execution

Positioned and prepared hinted draw APIs retain the exact value. Snapshot,
clone, compact command storage, static replay and native style identity keep it.
Version 10 picture archives append its raw encoding; version 1 through 9 reads
retain default, and writes to an older version reject a nondefault policy.
Invalid raw encodings and contradictory rendering modes reject before publication.

The shared WGSL transfer operates on coverage before color, brush opacity and
masks. The existing glyph instance flags retain it in multiples of 16 without
changing instance size or spatial raster cache keys. Both native providers use
the same shader. Native 32-byte text styles and 96-byte direct-paint records
retain the encoding in their former reserved word. Fixed-width native path
fallback records retain it above the sample-grid byte; only grids 4/8 admit
explicit grayscale transfer. Raster dispatch receives the original low-byte
grid, and paint receives the independent transfer. Clip records remain separate.
The ordinary vector and retained-glyph paths share the transfer;
analytic rectangle and wavefront shortcuts cannot drop it. Explicit source-physical
path packets retain their independent original grid-only admission.

New native semantics require matching rebuilt producers. Layout stability does
not qualify an older producer or allow a consumer to claim the new capability.
Old native consumers reject these previously reserved nonzero values.

## Design references

* [Microsoft Graphics::SetTextContrast](https://learn.microsoft.com/en-us/windows/win32/api/gdiplusgraphics/nf-gdiplusgraphics-graphics-settextcontrast): adopt the original bounded paint property; do not infer a complete rasterizer from its API description.
* [Skia text design](https://skia.org/docs/dev/design/text_overview/), [HarfBuzz shaping](https://harfbuzz.github.io/what-does-harfbuzz-do.html), [Parley concepts](https://raw.githubusercontent.com/linebender/parley/main/doc/concept.md): preserve the separation of reusable layout and paint-time coverage.
* [Win2D rendering parameters](https://microsoft.github.io/Win2D/WinUI3/html/T_Microsoft_Graphics_Canvas_Text_CanvasTextRenderingParameters.htm), [DirectWrite rendering parameters](https://learn.microsoft.com/en-us/windows/win32/api/dwrite/nn-dwrite-idwriterenderingparams), [WebRender font options](https://doc.servo.org/webrender_api/font/struct.FontInstanceOptions.html): adapt explicit retained presentation identity; reject cross-API enum aliases as parity evidence.
* [Vello architecture](https://raw.githubusercontent.com/linebender/vello/main/ARCHITECTURE.md): keep the transfer in GPU paint. No CPU font bitmap fallback, render-time readback, per-frame shader generation or source-layout mutation is introduced.

## Validation status

The expanded managed run passed 116 focused cases, including original integer-origin
regressions, archive/clone retention, native wire admission and atlas/vector cold
and warm frames. The native internal contract suite also passes, and the source
Forms consumer passes 15 coverage controls. The initial build exposed a missing test namespace, which was
fixed; its original log is retained. These controls do not establish Microsoft
font pixels, native provider execution, public-package or source-application
qualification. Full renderer/headless and Svg.Skia gates, both rebuilt native
providers, original 72-case Forms images and final package/application checks
remain required before release. Forms issue 174 remains open.
