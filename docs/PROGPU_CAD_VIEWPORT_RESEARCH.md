# ProGPU.CAD paper-space VIEWPORT research

Date: 2026-08-30

## Scope and clean-room boundary

This checkpoint implements a bounded paper-space output slice: one atomic
model/paper snapshot generation, rectangular and exact closed-polyline/circle/
ellipse/degree-1-through-3 SPLINE active floating `VIEWPORT` boundaries,
orthographic WCS top views, DCS panning, view twist, viewport scale,
per-viewport frozen layers, independently visible viewport borders, paper/model
draw order, managed/native retained replay, and physical inch/millimeter 1:1
`PlotType Layout` printing.

The implementation is clean-room. No third-party renderer source was copied,
ported, translated, or used as an implementation template. Autodesk's public
DXF/ObjectARX contracts define the observable coordinate, clipping, ordering,
and page behavior. ProGPU's existing original snapshot, analytic linetype,
picture, clip, affine replay, native-picture, and print-plan implementations are
the only implementation sources reused directly.

## Primary CAD contracts consulted

- Autodesk's [`VIEWPORT` DXF contract](https://help.autodesk.com/cloudhelp/2025/ENU/AutoCAD-DXF/files/GUID-2602B0FB-02E4-4B9A-B03C-B1D904753D34.htm)
  defines paper center/width/height, active status, DCS view center, WCS view
  direction/target, model view height, twist, frozen layers, clipping flags,
  render mode, and the non-rectangular boundary reference. It also defines the
  paper/model scale as viewport paper height divided by model view height.
- Autodesk's [nonrectangular layout-viewport workflow](https://help.autodesk.com/cloudhelp/2019/ENU/AutoCAD-Core/files/GUID-5272B8FC-88FD-4B58-BC7C-A32C71AA22C2.htm)
  identifies a closed circle or polyline in paper space as the associated
  boundary object. It also specifies that border suppression turns the layer
  off, while freezing that layer does not preserve correct clipping. ProGPU
  therefore retains an off-layer boundary as a hidden dependency and fails a
  frozen boundary explicitly.
- Autodesk's current [`VPCLIP` contract](https://help.autodesk.com/cloudhelp/2023/ENU/AutoCAD-Core/files/GUID-D5FD4D1A-5785-4A8E-B0D1-D12079C0A4FF.htm)
  explicitly lists closed polylines, circles, ellipses, closed splines, and
  regions as valid clipping objects. ProGPU adopts closed SPLINE identity and
  exact curve topology here; REGION boundary topology remains a separate ACIS
  gate.
- Autodesk's [coordinate-system contract](https://help.autodesk.com/cloudhelp/2022/ENU/OARX-DevGuide/files/GUID-01A45BA0-CC4F-4DCA-840E-DCA8802A060A.htm)
  defines the DCS origin as the WCS target and its Z axis as the view direction;
  a viewport is a plan view of that DCS.
- Autodesk's [.NET current-view example](https://help.autodesk.com/cloudhelp/2027/DEU/OARX-DevGuide-Managed/files/GUID-FAC1A5EB-2D9E-497B-8FD9-E11D2FF87B93.htm)
  constructs the DCS plane at the target, applies negative view twist on the
  DCS-to-WCS path, and inverts it for WCS-to-DCS. This establishes positive
  twist in ProGPU's WCS-to-DCS row-vector transform.
- Autodesk's [`PLOTSETTINGS` DXF contract](https://help.autodesk.com/cloudhelp/2020/ENU/AutoCAD-DXF/files/GUID-1113675E-AB07-4567-801A-310CDE0D56E9.htm)
  defines physical media and margins in millimeters, plot origin, paper units,
  rotation, standard/custom scale, viewport-border and draw-order flags, and
  `Layout` as plot type 5.
- ObjectARX's [`plotType` contract](https://help.autodesk.com/cloudhelp/2019/ENU/OARX-RefGuide/files/OREF-AcDbPlotSettings__plotType.html)
  specifies that `kLayout` prints paper from paper-space `(0,0)` to the
  configured printable upper-right corner when no origin offset is applied.
- ObjectARX's [plot-settings method contract](https://help.autodesk.com/cloudhelp/2027/ENU/OARX-RefGuide/files/OARX-RefGuide-__MEMBERTYPE_Methods_AcDbPlotSettings.html)
  states that `DrawViewportsFirst=true` means floating model viewports are
  drawn first and paper-space objects last, and that viewport borders are a
  separate plot policy.
- Autodesk's [Layout plot-scale contract](https://help.autodesk.com/cloudhelp/2022/ENU/AutoCAD-Core/files/GUID-60B37EAD-BBEA-46C0-AA76-137625B93ED5.htm)
  states that a layout is always plotted at 1:1 regardless of the selected
  scale, and the [`ScaleLineweights` property](https://help.autodesk.com/cloudhelp/2024/PTB/AutoCAD-ActiveX-Reference/files/GUID-D0954BC9-C56C-4782-8AA6-6605AAF99418.htm)
  scales lineweights in proportion to plot scale. ObjectARX further restricts
  [setting that property to paper layouts](https://help.autodesk.com/cloudhelp/2027/ENU/OARX-RefGuide/files/OARX-RefGuide-AcDbPlotSettings__setScaleLineweights_Adesk__Boolean.html).
- Autodesk's [`PaperUnits` contract](https://help.autodesk.com/cloudhelp/2024/ENU/AutoCAD-ActiveX-Reference/files/GUID-E4325F20-6258-4F62-93D2-2E1C37C820C9.htm)
  identifies inches, millimeters, or pixels as the layout/plot unit convention
  while clarifying that its Automation media properties remain millimeters.
  The [Page Setup contract](https://help.autodesk.com/cloudhelp/2025/ENU/DWGTrueView/files/GUID-0D72CF75-DA37-4937-9D9A-D93AA9BDF8D3.htm)
  likewise defines plotted units as inches or millimeters on paper and Layout
  output as actual size independent of the stored Scale selection.
- Autodesk's [Page Setup contract](https://help.autodesk.com/cloudhelp/2025/ENU/DWGTrueView/files/GUID-0D72CF75-DA37-4937-9D9A-D93AA9BDF8D3.htm)
  defines Plot Object Lineweights as whether assigned object and layer
  lineweights are plotted. It does not assign a portable replacement width when
  that option is disabled. Autodesk documents [`LWDEFAULT`](https://help.autodesk.com/cloudhelp/2019/ENU/AutoCAD-Core/files/GUID-969FE4A6-C30D-44DE-AFD4-A81B53F175F6.htm)
  as separate registry-backed default-lineweight state and describes its
  [display behavior](https://help.autodesk.com/cloudhelp/2022/ENU/AutoCAD-Core/files/GUID-1BB43E62-DF93-494E-ACF2-55824ACD5130.htm);
  it is therefore rejected as an inferred print width.
- Autodesk's [Plot dialog contract](https://help.autodesk.com/cloudhelp/2021/ENU/AutoCAD-LT/files/GUID-264D3513-0D22-4461-82D6-14F391BC5CDE.htm)
  defines Plot Transparency as whether object transparency is plotted and says
  that it is disabled by default for performance. The separate
  [`PLOTTRANSPARENCYOVERRIDE`](https://help.autodesk.com/cloudhelp/2020/ENG/AutoCAD-Core/files/GUID-38F03A2C-6D36-4AD9-BBE0-9CA574BEF218.htm)
  user setting may force transparency off, honor the page setup, or force it on.
  Because the pinned ACadSharp page-setup surface exposes neither value, ProGPU
  cannot infer the effective output choice from drawing state alone.

## Cross-engine architecture audit

The required rendering and text stacks were checked before design:

- Skia's [`SkCanvas` contract](https://api.skia.org/classSkCanvas.html) exposes
  retained picture replay under matrices plus rectangular/path clips. ProGPU
  adopts the retained-picture/transform/clip composition, not Skia source.
- Direct2D's [axis-aligned clip contract](https://learn.microsoft.com/en-us/windows/win32/direct2d/how-to-clip-with-axis-aligned-rects)
  uses push, draw, pop for efficient rectangular clipping, while its
  [geometric-mask contract](https://learn.microsoft.com/en-us/windows/win32/direct2d/how-to-clip-with-layers)
  uses a path geometry with `PushLayer`, drawing, and `PopLayer`. Direct2D and
  DirectWrite's [interoperation model](https://learn.microsoft.com/en-us/windows/win32/direct2d/direct2d-and-directwrite)
  keeps vector resources and shaped text reusable.
- Win2D's [command-list guidance](https://learn.microsoft.com/en-us/windows/apps/develop/win2d/quick-start)
  records reusable drawing work once and replays it later.
- WebRender's [display-list design](https://github.com/servo/servo/wiki/Design/a88683ec289b53b9f50242d4c27fcc22ddb76039)
  separates reusable display items from spatial transform and clip nodes so a
  view change does not require document layout.
- Vello's [`Scene` contract](https://docs.rs/vello/latest/vello/struct.Scene.html)
  uses `push_clip_layer` with an explicit fill rule, shape, and shape-local
  transform, retaining the clip until the matching pop.
- [Parley](https://github.com/linebender/parley) retains CPU text layout, while
  HarfBuzz's [shaping concepts](https://harfbuzz.github.io/shaping-concepts.html)
  keep Unicode/OpenType shaping independent of drawing replay. No viewport
operation reshapes paper or model text.

For disabled assigned lineweights, Skia's
[`SkPaint::setStrokeWidth`](https://api.skia.org/classSkPaint.html) defines zero
as a one-device-pixel hairline, while Direct2D's
[hairline stroke transform](https://learn.microsoft.com/en-us/windows/win32/direct2d/d1139)
fixes width to one device-dependent unit. Vello's public
[`Style::from_stroke`](https://docs.rs/vello_encoding/latest/vello_encoding/struct.Style.html#method.from_stroke)
rejects zero-width strokes, demonstrating that the final representation is
backend-specific rather than a portable CAD millimeter value. ProGPU therefore
adopts its existing typed `Pen.HairlineThickness` plus fixed-transform contract
as an explicit adapter choice. It rejects silently substituting Autodesk's
separate `LWDEFAULT`, inventing a physical millimeter width, or changing only
the managed renderer.

For retained alpha, Skia's ordered picture/
[`kSrcOver`](https://api.skia.org/SkBlendMode_8h.html) model, Direct2D's
[source-over primitive blend](https://learn.microsoft.com/en-us/windows/win32/direct2d/id2d1commandsink-setprimitiveblend1),
and Win2D's [straight-API/premultiplied-rendering contract](https://microsoft.github.io/Win2D/WinUI2/html/PremultipliedAlpha.htm)
all preserve alpha as paint state and composite in semantic order. WebRender's
[display-list and stacking-context architecture](https://github.com/servo/servo/wiki/Webrender-Overview)
retains transparency through ordered compositing, and Vello's
[`Scene::push_layer`](https://docs.rs/vello/latest/vello/struct.Scene.html#method.push_layer)
accepts an explicit layer alpha and blend mode. SkParagraph/DirectWrite,
Parley, and HarfBuzz retain shaping/layout independently of paint alpha, so
printing must reuse the positioned glyph results rather than reshape text.
ProGPU adopts an explicit preserve-alpha adapter policy over its existing
ordered retained commands and shared native brush table. Rejected were opacity
baking, flattening against an assumed paper color, command reordering, a
managed-only path, and silently treating unavailable page/user state as enabled.

Adopted: immutable display content, explicit affine and clip nodes, reusable
shaped text, and physical fixed-width strokes. Adapted: each unique
case-insensitive frozen-layer set owns one retained model picture shared by all
matching viewports. Rejected: per-viewport model snapshot duplication,
per-frame document traversal, raster flattening, native-only camera lowering,
unclipped replay, turning the boundary into a tessellated rectangle, and
silently accepting unsupported 3D or boundary forms.

For the inch-unit extension, the current Skia canvas/picture and coordinate-
space contracts, Direct2D transform contract, Win2D command-list/DPI contract,
WebRender picture/spatial/device-transform separation, Vello retained Scene,
Parley retained positioned glyph runs, and HarfBuzz shaping contract were
rechecked. They all preserve vector/text content independently of the final
device mapping. ProGPU therefore adopts one late paper-unit-to-pixel affine and
keeps paths, clips, glyph runs, physical strokes, caches, and native scene
records unchanged. Rejected were document-geometry rescaling, reshaping text,
duplicating inch pictures, changing fixed lineweights with the geometry scale,
or introducing a backend-specific unit path.

## Atomic snapshot and transform contract

`CadLayoutSnapshotCompiler` holds one `CadDocumentSession.Capture` lock while
compiling model space and the selected paper block. Both child snapshots,
layout name, and all viewport/frozen-layer state therefore own one generation.
Capture is `O(M + P)` time and storage for expanded model and paper primitives.
Viewport and frozen-layer counts are bounded before proportional storage. A
paper `VIEWPORT` and its referenced boundary are retained as one dependency
unit even when their layer is off or non-plottable. Off/frozen dependency
headers have `IsVisible=false` and are excluded from ordinary drawing and the
spatial selection index; non-plottable dependencies retain their ordinary
screen visibility. Both remain addressable by stable handle for clipping.

For a model-local retained point `l`, model and paper rebase origins `Rm` and
`Rp`, WCS target `T`, DCS center `C`, twist `a`, paper center `V`, paper height
`Hp`, and model view height `Hm`, the implemented row-vector mapping is:

```text
d = rotateZ(+a) * ((l + Rm) - T) - C
p = (Hp / Hm) * d + V - Rp
```

Rectangular viewports are clipped in paper-local coordinates. Their aspect
ratio defines visible model width implicitly, exactly as `Hp/Hm` plus the paper
width/height ratio. Nonrectangular boundaries retain their exact paper-space
closed path: straight and bulged 2D-polyline segments remain analytic lines and
arcs, circles use two analytic arcs, and full ellipses use the unit circle plus
the exact major/minor-axis affine transform. Closed ordinary and periodic
SPLINEs through degree three reuse `CadSplineCanonicalizer` and
`CadRationalBezier`: every non-empty knot span becomes one exact linear,
quadratic, cubic, rational-quadratic, or rational-cubic path segment. A closed
nonperiodic spline uses the source's explicit closing edge; periodic expansion
meets itself without adding a sampled seam. Canonical homogeneous weights are
normalized to the shared unit-endpoint path representation before checked float
retention. Anchor-relative coordinates and the paper rebase preserve float
precision. Degree four through ten remains fail-closed because the shared
filled-path grammar has no above-cubic segment; flattening is not substituted.
Final paper output adds the paper rebase and maps the selected paper convention
to physical output: one millimeter unit maps to `dpi / 25.4` pixels and one inch
unit maps to `dpi` pixels. Persisted paper size, margins, and plot origin remain
millimeters, so offsets are converted independently before the Y flip,
existing rotated-media convention, and printable-area clip.

## Retention, frozen layers, ordering, and linetypes

`CadLayoutSceneCompiler` compiles one paper scene and one model scene per unique
frozen-layer set. Viewports with the same set share the same `GpuPicture` by
identity; every occurrence records only rectangular or geometry push-clip,
transformed-picture, and matching pop-clip commands. Paper commands are
appended after viewport content when `DrawViewportsFirst` is set and before it
otherwise. Paper viewport ID 1 never draws model content or a user viewport
frame. Inactive/off viewports keep their paper frame but emit no model replay.
A nonrectangular boundary entity is the viewport border, so it is drawn at most
once as ordinary paper geometry and is suppressed together with rectangular
frames when `IncludeViewportFrames=false`. `PlotViewportBorders` maps to that
option independently of clipping, including when the border layer itself is
off.

Viewport frames participate in the existing exact A-aligned linetype lowering
as a closed four-segment path. Pattern distance uses double paper width/height,
while retained endpoints remain rebased floats. Per-viewport frozen layers are
matched case-insensitively against effective immutable layer names before scene
recording; no entity or cache is mutated.

For `V` active viewports, `E` model entities, `P` paper entities, `U` unique
frozen-layer sets, and `B` referenced boundary segments, compilation is
`O(U*E + P + V + B)` time and retained storage. Exact spline extraction is
`O(B*D^2)` for degree `D`, with `D <= 3`, bounded stack working storage, and one
retained segment per non-empty span; it is therefore linear in `B` for the
supported grammar. Boundary-handle resolution is `O(P + V)` and duplicate
handles fail as ambiguous rather than selecting an arbitrary entity.
Camera-only replay is `O(Pc + V)` retained commands, where `Pc` is the paper
command count, with no ACadSharp traversal, reshape, raster upload, or
managed/native boundary call.

## Printing, managed/native parity, and measured evidence

`CadLayoutPrintPlanCompiler` accepts only a generation- and name-matched paper
layout setup. This slice requires physical inch or millimeter paper units,
`PlotType Layout`, a defined rotation, explicit wireframe output, enabled
lineweights by default, no
nonempty CTB/STB sheet, no centered-layout policy, and opaque retained styles.
The stored standard/custom scale selection is deliberately ignored because
Layout output is mandated 1:1. `ScaleLineweights` is accepted for paper output
and therefore resolves to the exact multiplier one; the 0.50 mm regression is
five fixed device pixels at 254 DPI. Model-space `ScaleLineweights` remains
`CADPAGE113`. Other page/setup policies return the existing or new `CADPAGE`
diagnostics. The page picture remains one printable clip and one transformed
layout replay.

When the setup disables Plot Object Lineweights, default page-setup lowering
continues to return `CADPAGE112` because the selected output device owns the
thinnest printable width. A caller may explicitly select
`CadDisabledLineWeightPolicy.DeviceHairline`; lowering then carries
`CadPrintLineWeightMode.DeviceHairline` through model or layout print planning,
main geometry, construction geometry, and POINT markers. Every stroke records
the shared ProGPU device-hairline sentinel and fixed transform, so nested
pictures and the native scene compiler preserve the same behavior. Matched DXF
and DWG round trips cover different authored 0.50 mm and 2.00 mm widths becoming
the same hairline only under this policy. The sample preview opts in because it
is explicitly an output adapter; publishing remains fail-closed unless its
adapter makes the same deliberate device decision.

When retained styles contain alpha below one, page-setup output defaults to
`CadPrintTransparencyMode.RejectNonOpaque` and reports `CADPAGE118` because the
effective Autodesk page/user policy is unavailable. An output adapter may
explicitly choose
`CadUnavailablePlotTransparencyPolicy.PreserveRetainedAlpha`. Model and layout
print plans then retain the exact snapshot alpha and source order through nested
viewport pictures and managed/native compilation. Matched DXF and DWG round
trips cover two distinct authored transparency values, while a paper-layout
regression covers transparent model and paper content in one native page scene.
The shared preview makes this explicit choice; direct programmatic printing
continues to preserve alpha by default. Turning transparency off by converting
objects to opaque output remains a separate policy because correct overlap and
background semantics cannot be inferred by changing each brush in isolation.

For inch layouts, lowering records `1 / 25.4` paper-space units per physical
millimeter and `dpi` pixels per paper unit. Media, margins, and plot origin stay
in their persisted millimeter contract. Matched DXF and DWG round trips verify a
10-by-5-inch paper coordinate space over 254-by-127-millimeter media, one-inch
coordinate deltas at 254 pixels for 254 DPI, a 12.7-millimeter origin at 127
pixels, a 0.50-millimeter fixed stroke at five pixels, and native compilation
of the complete page picture.

The layout picture contains only existing ProGPU commands. Managed replay and
`GpuPictureNativeSceneCompiler` consume the same nested pictures, clips,
transforms, paths, glyphs, images, and fixed strokes. Native picture flattening
now folds a finite affine 2D late camera into child geometry exactly, matching
the managed `DrawPicture` page transform; perspective/non-affine camera use on
2D children remains fail-closed and existing 3D camera propagation is
unchanged. No C ABI, shader, GPU algorithm, resource wire record, or per-frame
P/Invoke changed. Focused regressions cover the affine transform product,
perspective rejection, existing 3D behavior, and the complete physical page
picture.

The Release benchmark lane used 10,000 model lines, 1,000 active viewports,
four frozen-layer variants, three warmups, and 24 measured iterations on macOS
26.6 arm64 with .NET 10.0.5. Millisecond `p50/p95/p99` was
`13.455/34.041/37.984` for atomic layout snapshot capture,
`83.435/148.571/156.207` for retained layout-scene compilation, and
`123.183/211.972/220.303` for the physical print plan. An independently owned
layout-picture clone measured `0.000/0.007/0.999` ms and 112 managed bytes per
clone. The retained scene contained 4,000 top-level commands and four shared
model variants; stable compositor replay does not clone and retains its
existing zero-allocation contract. The exact command was:

```text
dotnet run --project src/ProGPU.CAD.Benchmarks/ProGPU.CAD.Benchmarks.csproj -c Release --no-build -- --viewports 1000 --viewport-layer-variants 4 --entities 10000 --warmup 3 --iterations 24 --output-json artifacts/progpu-cad-viewport-benchmark.json
```

This is a baseline, not an improvement claim. No macOS Instruments optimization
claim is made because this checkpoint establishes new behavior rather than
claiming a memory/CPU/GPU speedup.

The paired analytic-boundary lane used the same model/viewports/layer variants,
with one closed three-vertex bulged polyline per viewport. It measured
`28.268/47.171/66.501` ms for atomic capture,
`92.864/148.852/160.377` ms for scene compilation, and
`102.179/164.122/177.437` ms for the physical print plan. Picture clone was
`0.000/0.002/0.193` ms and 112 managed bytes. The paper snapshot contained
2,001 source entities, the retained layout contained 4,000 top-level commands
and four shared model variants, and the exact command was:

```text
dotnet run --project src/ProGPU.CAD.Benchmarks/ProGPU.CAD.Benchmarks.csproj -c Release --no-build -- --viewports 1000 --nonrectangular-viewports --viewport-layer-variants 4 --entities 10000 --warmup 3 --iterations 24 --output-json artifacts/progpu-cad-nonrect-viewport-benchmark.json
```

This is likewise a new-feature baseline, not a comparison or optimization
claim. The canonical benchmark artifact records .NET 10.0.5 on macOS 26.6.

The exact periodic rational-SPLINE lane replaced each polyline boundary with a
compact degree-two periodic NURBS containing four non-empty rational-quadratic
spans. On the same workload it measured `33.578/62.113/63.166` ms for atomic
capture, `131.644/165.708/173.644` ms for scene compilation, and
`132.296/192.316/220.913` ms for the physical print plan. Picture clone was
`0.000/0.004/0.634` ms and 112 managed bytes; the retained layout still
contained 4,000 top-level commands and four shared model variants. The exact
command was:

```text
dotnet run --project src/ProGPU.CAD.Benchmarks/ProGPU.CAD.Benchmarks.csproj -c Release --no-build -- --viewports 1000 --spline-viewport-boundaries --viewport-layer-variants 4 --entities 10000 --warmup 3 --iterations 24 --output-json artifacts/progpu-cad-spline-viewport-benchmark.json
```

This third lane is also a new-feature baseline and makes no optimization claim.

## Dependency provenance

The pinned ACadSharp feature branch commit `c5e7b323` includes the missing DXF code
340 read into its existing `CadViewportTemplate.BoundaryHandle` contract and an
independent DXF identity round-trip regression. ACadSharp already wrote code
340 and its template already resolved that handle; DWG round trips were already
correct. This exact in-repository dependency change is the only dependency
source used by ProGPU. The ACadSharp `master`, `origin/master`, and
`upstream/master` refs remain unchanged at `b469bd1e`.

Spline clipping directly reuses the original in-repository ProGPU algorithms
in `CadCanonicalSpline.cs`, `CadRationalBezier.cs`, and the rational path
fill/clip grammar in `ProGPU.Vector` and `ProGPU.Scene.Native`. No third-party
curve extraction, helper structure, lookup data, or implementation text was
introduced. Focused differential tests prove the same compact periodic NURBS
identity across snapshot, managed clip, native compilation, physical print,
and DXF/DWG persistence.

## Explicit remaining fidelity gates

- Arbitrary orthographic directions, bottom views, perspective, front/back
  depth clipping, hidden-line removal, rendered/shaded output, and visual-style
  overrides require the depth-aware 3D scene and matched image tests.
- Degree-four-through-ten SPLINE and REGION viewport boundaries require an
  exact above-cubic filled-path or ACIS region-topology contract respectively.
  Missing, ambiguous, malformed, open, unsupported, frozen, or
  unrepresentable-weight boundary references remain fail-closed with
  `CADVIEW004`, `CADVIEW009`, `CADVIEW010`, `CADVIEW011`, or `CADVIEW012`.
- Viewport layer overrides beyond frozen membership, annotative scale,
  paper-space UCS, named views, pixel media, centered layout plotting,
  CTB/STB, transparency policy, and device `PaperImageOrigin` need typed
  contracts and differentials.
- Construction-line and nonzero-PDMODE POINT overlays fail with `CADVIEW007`
  and `CADVIEW008`; they need per-viewport camera regeneration before support.
- Licensed AutoCAD pixel differentials across twist/target/view-center,
  overlapping viewport order, asymmetric margins/rotations, DXF/DWG versions,
  malformed/fuzz inputs, browser AOT, and device-loss/residency remain before
  declaring paper-space rendering verified.
