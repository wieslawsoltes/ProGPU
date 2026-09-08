# Representative CAD rendering audit

Audit baseline: ProGPU `0a7233c9`, ACadSharp submodule
`64f4fedab7431dd723cc0f84427f89a3cb5d1ec5`.

This audit uses the dependency's `samples/sample_AC1032_ascii.dxf` and
`samples/sample_AC1032.dwg`, not just the small generated sample scene. Each file
was loaded with `CadDocumentStore`, captured with `CadSnapshotCompiler` using
`CadFontManagerTextResolver(InterFontFamily.Regular)`, and recorded through
`CadPlanSceneCompiler`. No external raster-image resolver or standalone SHX
catalog was supplied. These are compilation results, not pixel-fidelity or
round-trip-certification results.

| Result | DXF | DWG |
| --- | ---: | ---: |
| Source entities | 163 | 163 |
| Expanded entities | 573 | 573 |
| Snapshot unsupported entities | 13 | 13 |
| Snapshot invalid entities | 3 | 4 |
| Recorded drawable entities | 556 | 556 |
| Recorded commands | 584 | 585 |
| Unsupported linetype count | 4 | 4 |
| Unresolved raster images | 1 | 1 |
| Deferred modeler surfaces | 3 | 3 |

## Rendering-first implementation priorities

- HATCH handles `371` and `376`: gradient fills are explicitly rejected by
  `CadSnapshotCompiler.Hatch.cs`. Implement their gradient-space semantics
  through existing retained ProGPU brushes and analytic boundaries; do not
  replace them with a solid fill or flatten their curves.
- MTEXT handles `3EC`, `3F6`, and `779`: the DXF path rejects nonpositive column
  heights; the DWG path rejects nonpositive column counts. Inspect the authored
  column records and ACadSharp reader output before deciding whether the fix
  belongs to IO or layout. Do not silently turn columns into one unbounded line.
- LWPOLYLINE `35B`: mixed zero/nonzero-width segments require the filled outline
  and skinny stroke to coexist. The current explicit rejection avoids incorrect
  centerline-only output but leaves visible content missing.
- Vertical MTEXT `3F5` and MULTILEADER `B22`/`B23` remain unsupported. These need
  shaping/orientation semantics, not a rotated horizontal-text approximation.
- The DWG path additionally rejects SPLINE `434` for degree/control/knot data.
  Compare the paired file records before relaxing validation.

Empty attribute definitions, host font substitutions, the missing standalone
SHX resolver, and the absent image resolver are separate diagnostic categories.
They must not be counted as evidence that the corresponding renderer is missing.
In particular, running without a TrueType resolver initially produced 96
unsupported entities; with the host-style resolver it produced 13. Neither
configuration establishes exact font fidelity: substitutions remain reported.

## Zoomed text finding

The browser screenshot raised a possible softness issue but does not by itself
prove a rasterization defect. The source audit found:

- `CadPlanSceneCompiler.RecordText` records shaped glyph ranges with
  `useVectorGlyphRendering: true`, including the entity transform.
- `Compositor.CompileGlyphRunCommand` sends that flag through vector fallback;
  `CompileVectorGlyphPath` supplies DPI-dependent raster scale to the path atlas.
- `Compositor.RetainedPictures.cs` includes the complete global transform and
  DPI in its retained-picture key. A zoom-independent cache-key omission was not
  found in this path.

No text algorithm or quality threshold was changed on the strength of a resized
screenshot. A matched device-pixel capture and managed/native differential are
still required before identifying or fixing the cause. The existing shaping /
drawing separation is consistent with the primary contracts reviewed in
[Skia's text overview](https://skia.org/docs/dev/design/text_overview/),
[DirectWrite's natural-layout discussion](https://learn.microsoft.com/en-us/windows/win32/directwrite/introducing-directwrite),
and [HarfBuzz's scope](https://harfbuzz.github.io/what-is-harfbuzz.html).
This preliminary audit is not a completed cross-engine design gate for a new
rendering implementation.

The full CAD objective remains open. These findings order the rendering-first
work; they do not redefine unsupported output as acceptable completion.
