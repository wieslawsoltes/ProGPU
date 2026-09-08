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

Follow-up: the [late-camera coverage regression and correction](PROGPU_CAD_CAMERA_COVERAGE.md)
now confirms the defect in the managed GPU-camera path. The initial investigation
below examined the ordinary retained-transform route, which was not the cause.

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

Follow-up: [column IO investigation](PROGPU_CAD_MTEXT_COLUMN_IO.md) confirms
that DWG discarded the persisted column count. That is fixed independently of
layout. The sample's zero DXF / negative DWG final-column heights still need
defined semantics; the baseline entity totals above are not claimed to improve
from the count fix alone.

Follow-up: [mixed straight-width lowering](PROGPU_CAD_WIDE_POLYLINE_RESEARCH.md#2026-09-08-mixed-straight-widths)
now retains `35B` as one CAD entity with a filled taper and connected thin runs.
Against the same fixtures and resolver, unsupported entities fall from 13 to 12
in both files, recorded entities rise from 556 to 557, and recorded commands
become 586 (DXF) / 587 (DWG). Invalid counts remain 3 / 4, and the linetype,
missing-image, and deferred-modeler counts remain unchanged. The baseline table
and original priority list above are historical findings, not current totals.

Follow-up: [exact fit-only cubic lowering](PROGPU_CAD_FIT_SPLINE_RESEARCH.md)
now renders DWG SPLINE `434` using the same analytic controls as the paired DXF.
With the same fixture/resolver, DWG invalid entities fall from 4 to 3, recorded
entities rise from 557 to 558, and commands rise from 587 to 588. DXF remains
557 recorded entities / 586 commands / 3 invalid entities. Both retain 12
unsupported entities; linetype, missing-image, and modeler counts are unchanged.
The fit-only curve preserves DWG round trips, but DXF export is explicitly
blocked pending lossless control/knot serialization (`CADSAVE002`).

Follow-up: [dynamic/manual final-column placement](PROGPU_CAD_MTEXT_COLUMN_IO.md#dynamicmanual-final-column-placement)
now restores MTEXT `3EC`, `3F6`, and `779` without rewriting the persisted zero
DXF or negative DWG heights. Both fixtures now have zero invalid entities and
12 explicitly unsupported entities. DXF records 560 entities / 589 commands;
DWG records 561 / 591. Vertical text, gradient hatches, unresolved resources,
and deferred modeler surfaces remain separate limitations. Six fixture cases
compare managed-positioned/native-serialized scene and print results; six
headless cases compare pixels at two zoom levels. These checks establish this
specific rendering correction, not comprehensive AutoCAD fidelity.
