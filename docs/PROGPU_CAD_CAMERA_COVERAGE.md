# Late-camera coverage correction

Baseline: ProGPU `ef5a76705575bf22139186b8c2377882e73c8124`.
This is a rendering correctness fix, not a performance optimization or an
expansion of the focused CAD milestone.

## Finding and original-source provenance

`CadSampleCanvas.OnRender` uses `DrawingContext.DrawPicture(picture, camera)`.
The managed `Compositor.CompilePicture` retains local vertices and applies that
camera at GPU submission. However, `CompilePathCommand` selected path-atlas
resolution and fractional phase from only the local transform. CAD's explicit
vector glyph runs therefore enlarged low-resolution coverage during zoom.
Retained-picture invalidation was not the missing contract.

The correction composes the active camera for coverage scale, translation phase,
and text coverage-gamma selection only. Quad placement stays local and the GPU
still applies the camera. It reuses the original ProGPU algorithms in
`src/ProGPU.Scene/Compositor.cs` (`CompilePathCommand`, `CompileVectorGlyphPath`,
`GetTextPathCoverageGamma`) and `src/ProGPU.Vector/PathAtlas.cs`; it does not
introduce third-party implementation text or a new shader.

The change preserves the existing four device phases for vector text, 64 for
ordinary paths, 128 local text phases, quantized vector-text scale, physical DPI
multiplier, atlas ownership/generation, and high-precision winding. Matrix/scale
work is O(1) per path or text command; compilation remains O(G) for G glyphs.
Coverage storage/work now follows the final projected footprint, as it already
does for an ordinary transform. Zooming can therefore cost more raster work and
atlas space than the defective undersampled baseline. No latency, memory, or
throughput improvement is claimed.

## Managed/native applicability

`src/ProGPU.Scene.Native/GpuPictureNativeSceneCompiler.Pictures.cs` already folds
an affine 2D camera into the transform before encoding native path/glyph records.
The native renderer consequently receives the same final transform for ordinary
and late-camera pictures. No C++ correction, C ABI change, generated declaration,
shader fork, upload seam, or additional managed/native crossing is applicable.
The paired regression compares complete native streams, including glyph/path
resources, for the two placement routes using identical scene identity.

This audit covers affine filled paths and explicit vector glyph fallback used by
CAD. It does not certify ordinary glyph-atlas text or perspective/3D cameras.

## Required primary-source research

- [Skia text overview](https://skia.org/docs/dev/design/text_overview/) and
  [SkParagraph public contract](https://skia.googlesource.com/skia/+/refs/heads/main/modules/skparagraph/include/Paragraph.h):
  retain shaping/layout results independently from painting. Adopt that separation;
  do not reshape text for camera changes or transplant paragraph implementation.
- [DirectWrite glyph-run analysis](https://learn.microsoft.com/en-us/windows/win32/api/dwrite/nf-dwrite-idwritefactory-createglyphrunanalysis)
  explicitly includes em size, pixels per DIP, and the rendering transform.
  [DirectWrite overview](https://learn.microsoft.com/en-us/windows/win32/directwrite/introducing-directwrite)
  and [Win2D CanvasTextLayout](https://microsoft.github.io/Win2D/WinUI3/html/T_Microsoft_Graphics_Canvas_Text_CanvasTextLayout.htm)
  separate reusable layout from drawing. Adapt the final-transform raster contract
  to ProGPU's atlas; reject a platform-specific DirectWrite dependency.
- [WebRender rendering overview](https://firefox-source-docs.mozilla.org/gfx/RenderingOverview.html):
  spatial transforms and picture organization permit text rasterization at its
  final scale. Adopt the final-scale invariant without copying its tree or cache.
- [Vello current project overview](https://github.com/linebender/vello),
  [historical 2023 roadmap](https://raw.githubusercontent.com/linebender/vello/main/doc/roadmap_2023.md),
  and [Parley](https://github.com/linebender/parley): keep reusable glyph runs and
  CPU layout separate from GPU coverage. The historical roadmap is design context,
  not evidence of today's implementation. Reject an unrelated renderer rewrite.
- [HarfBuzz scope](https://harfbuzz.github.io/what-is-harfbuzz.html): preserve the
  CPU-produced glyph identities and positions; camera correction is not shaping.

Across these contracts the applicable distinction is reusable logical content
versus target-sensitive rasterization. Startup/lazy initialization, font discovery,
fallback and variable-font state, shaping/layout reuse, visibility culling,
worker preparation, GPU batching, cache eviction, demand-driven upload, and
device-loss/atlas-generation invalidation remain the existing ProGPU contracts.
No engine's unrelated policy for those domains is adopted. Retained-scene reuse
and DPI/subpixel policy are directly exercised; new camera scale/phase demand uses
the existing bounded atlas and same-frame capacity recovery.

## Validation

`GpuCameraCoverageTests` independently records a quadratic filled path or an Inter
glyph and compares actual device pixels for ordinary versus late GPU placement.
The baseline maximum channel error was 175 for the path and 183 for the glyph.
The correction passes the two-level tolerance with integer zoom, fractional
nonuniform scale/translation, and rotated fractional translation. Each of the six
cases also verifies stable pixels, atlas generation/count reuse, and byte-identical
native scene streams. A separate 64-frame fractional zoom/pan stress starts with a
64-pixel atlas and requires visible vector text in every frame.

Release suites: 3,862 core renderer tests and 268 headless tests passed, with
`DOTNET_TieredCompilation=0` matching the existing CAD CI setting.
Local logs/screenshots stay ignored
under `artifacts/progpu-cad/`. Linux CAD browser CI at baseline still produces a
blank canvas despite advancing counters; this independent host/runtime blocker
is not resolved or waived by these offscreen tests. Final browser AOT and PR CI
remain milestone gates.
