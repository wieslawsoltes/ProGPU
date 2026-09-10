# ProGPU.CAD unbounded construction-linetype research record

## Scope and clean-room sources

This checkpoint defines and implements the missing model-space phase contract
for non-continuous `RAY` and `XLINE` entities. It is an original ProGPU
implementation. No third-party source text, helper structure, lookup encoding,
or control flow was copied or adapted.

The authoritative persisted-format and observable-behavior sources were:

- Autodesk's [LTYPE DXF contract](https://help.autodesk.com/cloudhelp/2020/ENU/AutoCAD-DXF/files/GUID-F57A316C-94A2-416C-8280-191E34B182AC.htm),
  which defines A alignment, total pattern length, signed element lengths, and
  complex-element records;
- Autodesk's [simple custom-linetype contract](https://help.autodesk.com/cloudhelp/2020/ENU/AutoCAD-MAC-Customization/files/GUID-EF1DF0A9-2088-487C-8085-16FEE6425405.htm),
  which requires A-aligned open paths to begin and end with at least half of
  the first dash; and
- Autodesk's [`setIsScaledToFit` contract](https://help.autodesk.com/cloudhelp/2027/ENU/OARX-RefGuide/files/OARX-RefGuide-AcDbLinetypeTableRecord__setIsScaledToFit_bool.html),
  which distinguishes A alignment from scaled-to-fit alignment.

Those sources do not specify a viewport-independent phase origin for an
unbounded entity. ProGPU therefore makes that missing contract explicit rather
than inferring phase from a temporary clip edge.

## Cross-engine architecture audit

- [Skia `SkDashPathEffect`](https://api.skia.org/classSkDashPathEffect.html)
  exposes phase as an offset modulo the interval sum. This supports retaining
  phase independently of clipping, but Skia's even on/off array cannot express
  CAD complex text or SHX elements and was not adopted as an implementation.
- [Direct2D `ID2D1StrokeStyle`](https://learn.microsoft.com/en-us/windows/win32/api/d2d1/nn-d2d1-id2d1strokestyle)
  and Win2D's documented
  [`CanvasStrokeStyle.DashOffset`](https://microsoft.github.io/Win2D/WinUI3/html/P_Microsoft_Graphics_Canvas_Geometry_CanvasStrokeStyle_DashOffset.htm)
  keep dash phase in stroke state rather than deriving it from a dirty
  rectangle. ProGPU adapts that separation while continuing to lower CAD
  patterns into retained geometry because the shared ProGPU pen grammar does
  not carry complex CAD pattern payloads.
- [Vello's retained scene API](https://github.com/linebender/vello/blob/main/vello/src/scene.rs)
  applies stroke dash offset during CPU scene encoding before GPU path
  encoding. ProGPU adopts the same preparation-versus-replay boundary, but its
  signed CAD interval iterator and complex placements are independently
  designed.
- Firefox/WebRender keeps clips and retained line display items separate; the
  [WebRender display-list binding](https://searchfox.org/mozilla-central/source/gfx/webrender_bindings/WebRenderAPI.cpp)
  submits explicit line bounds and clip state. Its CSS line decorations are
  device/layout decorations rather than model-space CAD patterns, so their
  element rules were rejected as a conformance source.
- [SkParagraph](https://skia.googlesource.com/skia/+/refs/heads/main/modules/skparagraph/include/Paragraph.h),
  [DirectWrite text layout](https://learn.microsoft.com/en-us/windows/win32/directwrite/text-formatting-and-layout),
  [Parley](https://github.com/linebender/parley), and
  [HarfBuzz shaping](https://harfbuzz.github.io/harfbuzz-hb-shape.html) were
  rechecked. They remain applicable to the already shared TrueType
  complex-element payloads, shaping, fallback, and glyph resources, but do not
  define geometric dash phase. No text-layout, glyph-cache, upload, or
  device-loss contract changes in this checkpoint.

## Adopted phase and clipping contract

Every snapshot construction primitive already stores a normalized WCS
direction. Its signed parameter `t` is therefore WCS arclength, with the
authored base point at `t = 0`.

For an A-aligned pattern with first-dash length `D` and scaled period `P`, the
oriented periodic sequence begins at `t = -D/2` and repeats every `P`:

- an `XLINE` evaluates that one sequence for all finite signed `t`; the base
  point is the center of the first dash;
- a `RAY` intersects the same sequence with `t >= 0`, so its authored endpoint
  begins with exactly the positive half of the first dash; and
- a viewport or plot window only intersects the resulting spans. It never
  recenters, stretches, or restarts the pattern.

This is the smallest deterministic extension of Autodesk's documented
A-aligned endpoint half-dash rule to unbounded geometry. Reversing the authored
direction reverses the oriented parameter axis, as expected for a persisted
direction vector. Dot and complex descriptors occur at their exact periodic
parameter; relative complex content uses the projected authored tangent, while
absolute rotation remains absolute.

The iterator seeks directly to the cycle containing the clip minimum with a
floor/modulo calculation. It scans at most one definition prefix plus the
descriptors covering the visible interval; it never walks from `t = 0` to a
distant viewport. Counting completes before proportional path or placement
allocation. Document-wide figure, descriptor-step, source-segment, and complex
placement limits remain transactional and produce `CADCON001` continuous-stroke
fallback diagnostics instead of partial output. Unsupported non-A alignment
uses the same explicit fallback.

A direction parallel to WCS Z has a single-point XY stroke footprint. Simple
patterns therefore retain that exact point footprint without enumerating an
infinite parameter interval. Complex text/shape orientation has no projected
tangent in that case, so ProGPU records the continuous point footprint and
reports `CADCON001`; resolving a view-dependent decoration orientation remains
an explicit later fidelity gate.

## Retention, parity, print, and complexity

Continuous and simple patterned figures with the same resolved style remain
source-order batched into one retained `PathGeometry`. A complex entity flushes
the batch, records its stroke figures, and reuses the existing ProGPU-owned
TrueType/SHX placement recorder. Construction statistics expose lowered entity,
figure, placement, pattern-step, and source-segment counts. Model-space print
compilation uses its exact plot bounds and merges those counters into the print
plan.

For `U` visible construction entities, `E` pattern descriptors, `Q` visited
descriptors in the visible intervals, `F` emitted figures, and `C` complex
placements, compilation is `O(U + E + Q + F + C)` time and `O(F + C)` retained
storage. Continuous construction remains `O(U)`. Stable picture replay performs
no CAD traversal, pattern expansion, upload, or managed allocation.

The managed/native applicability audit finds one semantic lowering path. Both
backends consume the same retained `DrawPath`, `DrawGlyphRun`, and SHX path
commands through `GpuPictureNativeSceneCompiler`; no C++ CAD frontend, C ABI,
generated wire record, shader, device lease, callback, or per-frame crossing is
added. Print reuses the same retained commands under the existing page
transform and clip.

## Verification and remaining evidence

Focused regressions cover centered and panned XLINE clips, the RAY endpoint
half-dash, a far-away viewport under an eight-step traversal budget, complex
TrueType placement phase, vertical projection, print statistics, and managed/
native picture compilation. The publication gates passed on 2026-08-31:

- focused construction tests: 16/16 in Debug and Release;
- complete .NET 10 CAD suite: 1,290/1,290 in Debug and Release;
- Release benchmark build: 0 warnings and 0 errors after the independent
  ACadSharp source warning baseline was already built;
- fresh `ACadSharp.ProGPU` and `ProGPU.CAD` packages at
  `0.1.0-preview.62` passed the two-package content/dependency audit; and
- the isolated package-only consumer restored and built with 0 warnings and
  0 errors, rejected upstream `ACadSharp`, and created an AC1032 document.

The package verifier's repository-wide nonshipping-project scan still reports
the separately user-deleted browser sample project. The requested direct
two-package build, package-content audit, and isolated consumer all passed
without restoring or staging those deletions.

Two sequential processes from one final Apple Silicon/.NET 10.0.5 Release
benchmark binary measured a 10,000-entity simple dash/dot fixture at linetype
scale 20, three warmups, 24 iterations, and one fixed plan/plot window. Both
runs lowered every entity with zero fallback into 200,919 figures and one
style-batched command. Construction p50/p95/p99 was
`113.8119/182.7288/203.5711 ms` and `162.1728/194.4085/244.7878 ms`, with
`45,600,890` and `45,601,325 B/op`; corresponding print planning was
`134.4956/217.7978/224.4152 ms` and `109.0666/138.4019/208.8951 ms`.

The matched 1,000-entity complex TrueType fixture lowered all records with zero
fallback into 11,357 figures and 10,377 placements. Construction
p50/p95/p99 was `17.6564/62.9751/69.0310 ms` and
`15.1876/55.1496/79.6880 ms`, with `9,340,197` and `9,340,260 B/op`; print
planning was `15.4307/45.9684/52.5122 ms` and
`19.9541/81.1369/128.1225 ms`. Construction-query allocation remained zero.
The four ignored JSON artifacts are named in the architecture record. These are
feature-cost baselines from matched final binaries, not an improvement claim or
a replacement for Instruments/GPU traces.

Independent licensed AutoCAD pixel differentials, vertical-projection complex
decoration orientation, visual goldens, and dense construction-pattern
p50/p95/p99 measurements remain future gates. The implementation does not
claim those unmeasured results.
