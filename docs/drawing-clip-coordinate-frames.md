# Drawing clip coordinate frames

For admitted frames whose float determinant or reciprocal overflows, clip queries
now compute the capture-to-current affine mapping directly in double, narrowing
only its final coefficients. Ordinary finite float arithmetic is unchanged.
Never reject a representable relative mapping merely because an intermediate
inverse overflows, or manufacture an identity for an unrepresentable result.
This is shared System.Drawing capture/query ingress; both managed and native
renderers retain the same source geometry commands, with no renderer algorithm
change. See [recursive SVG evidence](svg-recursion-reference.md) for the independent
Windows comparison, the confirmed recursive-scale rejection contract, and the
remaining whole-workflow qualification of this follow-up.

`Graphics.SetClip` captures geometry in the current world/page/container/host
mapping. Later transforms change drawing coordinates, not that captured coverage.
Combining the old region's raw coordinates with new world coordinates incorrectly
emptied the second WinForms menu item's text clip: the item clip was at Y=40,
then item painting translated by 40 and text intersected a local Y=0 rectangle.

## Shared implementation

The original ProGPU `Graphics`, `Region` and retained geometry APIs are the source
of this implementation. No third-party implementation is copied or adapted.
Each owned clip retains its device transform separately from the existing
`GetContextInfo` saved-context transform. Before a Boolean combination or explicit
clip translation, map the old geometry by `captured * inverse(current)`. Keep the
original curves and Boolean tree, not their bounding boxes. Public clip snapshots,
bounds and visibility use the current world frame. Unchanged-frame queries keep
their existing direct path. A failed inversion rejects the combination before
replacing the old clip; no epsilon or identity substitute is added.

Actual Microsoft GDI+ probing also distinguishes Display from scalable page
units: Display retains the `PageScale` property without applying it. Pixel and
physical units apply the stored scale. The shared page mapping follows that
policy; do not use default Display units as a scaled-pixel reference.

World matrix assignment, `TransformElements`, multiplication, scale and rotation
validate the candidate before publication. `Matrix3x2.Invert` can return true for
NaN components, so inversion alone is not admission. Reuse the original
`Matrix.MultiplyWithGdiPlusOverflow` arithmetic, finite-component checks and the
existing representable inversion predicate, without an arbitrary small-scale
epsilon. Rejection preserves the previous world matrix, clip and recorder.
This is fixed-work, allocation-free validation for mutators; matrix-property
assignment retains its existing caller-owned clone. Both renderer consumers see
the same unchanged recording after rejection. Standalone Matrix semantics are
unchanged. Actual Microsoft probing shows `TranslateTransform` has a distinct
policy and accepts non-finite offsets; it remains separate, not silently clamped
or treated as permission to accept invalid matrix assignments.

Saved states retain both transforms. Restore and flush push the clip with its
captured transform, including finite-universe construction for symbolic infinite
regions. Parent container clips remain separate enclosing scopes. The cumulative
`GetContextInfo` policy is unchanged. Rectangle-only Boolean region bounds reuse
the existing exact scan engine: operand bounds are not the result bounds of a
difference. Curved Boolean bounds retain their existing conservative policy.

Intersecting two proven rectangles produces their exact rectangle (or empty set)
before lowering. Recognition requires one filled closed four-line contour, exact
axis alignment, a closing endpoint and all four unique corners. SIMD min/max
intersects both axes; overflowed extents or endpoint-rounding changes retain the
original Boolean expression.
No tolerance-based near-rectangle classification or envelope approximation is
allowed. Curves, shears and all other Boolean operations retain their existing
geometry. This capture-time identity is shared by managed and native consumers.

Capture adds one fixed-size matrix per current/saved clip. Same-frame clones stay
O(1); a frame conversion visits the retained region geometry once, O(S) time and
storage for S segments/nodes. Boolean rectangle bounds use the existing bounded
scan algorithm, up to O(X * Y * S) for X/Y distinct edges, with its one-million-cell
limit. No shader, GPU submission, CPU readback or shaping operation is added to
production recording or transform changes. Pixel readback is test-only.

Both managed `Compositor` and native `GpuPictureNativeSceneCompiler` consume the
same corrected `PushGeometryClip` geometry/transform commands. Their raster
algorithms and the native C ABI are unchanged. The native stream differential
compares complete output against an independently framed clip; it is not a claim
of native application or package qualification.

## Design references

- [SkCanvas clip contracts](https://api.skia.org/classSkCanvas.html): capture
  transformed clip geometry independently from later matrix changes. Adopt the
  coordinate separation, not Skia implementation code or API-specific bounds.
- [Direct2D transforms](https://learn.microsoft.com/en-us/windows/win32/direct2d/direct2d-transforms-overview)
  and [Win2D geometry layers](https://microsoft.github.io/Win2D/WinUI3/html/M_Microsoft_Graphics_Canvas_CanvasDrawingSession_CreateLayer_7.htm):
  clip/layer transforms belong to capture. Do not adopt Direct2D axis-aligned
  clip-envelope semantics for arbitrary System.Drawing regions.
- [WebRender rendering overview](https://firefox-source-docs.mozilla.org/gfx/RenderingOverview.html):
  spatial and clip ownership are distinct retained state. Keep that distinction
  without introducing a second display list or invalidation system.
- [Vello layer contract](https://docs.rs/vello_api/latest/vello_api/trait.PaintScene.html):
  the layer transform applies to its clip, not subsequent drawing transforms.
- [Parley layout resources](https://docs.rs/parley/latest/parley/),
  [SkParagraph's public paragraph contract](https://skia.googlesource.com/skia/+/refs/heads/main/modules/skparagraph/include/Paragraph.h),
  and [HarfBuzz shaping](https://harfbuzz.github.io/shaping-and-shape-plans.html):
  retain shaping/layout independently from drawing. No font discovery, fallback,
  variation, glyph-cache, DPI snapping, worker preparation, device-loss policy,
  culling or upload change is needed to correct this clip-coordinate bug.

## Evidence and remaining gates

The initial 12 cases failed 11 times on the parent implementation; Replace was
the independent passing control. Regressions include all six combinations,
caller-region ownership, clip queries, saved state/container restoration, flush,
clip translation, page/host mappings, actual menu-text pixels, a sheared ellipse,
tiny invertible transforms, atomic inversion failure, exact difference bounds and
the native scene stream. Rectangle/image comparisons require every pixel to match
and nonzero reference ink; no tolerance is widened.

Local macOS ARM64 Release verification: 77 selected clip/context/container/state/
coordinate/flush/target-DPI/region cases passed, zero skipped (17 seconds).
The portable 12-case comparison probe also completed, and the Microsoft reference
project cross-built with zero warnings/errors; its actual Windows results are a
separate CI gate, not inferred from that build.

After the Display-unit correction, the expanded local selection passed 78 cases,
zero skipped (45 seconds). An actual Windows ARM64 guest-local diagnostic run
matched Microsoft on all 13 cases, including exact bounds, visibility, coverage
and every RGBA pixel. It used the installed .NET 11 preview runtime explicitly
rolled forward from net10.0; CI's pinned runtime/architecture matrix remains
separate. The earlier shared-folder execution hit its unchanged 120-second
deadline and is not a pass. The guest-local run retained the same deadline and
all cases. Its source-built private DLLs are not staged producer packages.

Hosted Windows WARP subsequently exposed an Intersect pixel difference (368
fully red pixels instead of 384) on both architectures, despite the independent
local Parallels GPU pass. The exact-rectangle identity avoids routing that proven
simple result through the general Boolean mask. Keep the unchanged 13-case
Windows gate; this does not qualify arbitrary curved Boolean rasterization.
Additional source regressions assert exact four-edge output and reject a small
near-rectangle deviation, an ordinary shear, extent overflow and endpoint rounding.
The final local Release selection passed 83 cases, zero skipped (14 seconds).
Hosted run 36425702766 then confirmed Intersect at 384 pixels on both Windows
architectures. Union still produced 890 rather than 896 fully red pixels; the
full pixel gate remains failed, with no tolerance change. Probe receipts now
retain already-read RGBA bytes to locate such differences without another render.

The transform follow-up adds 31 source regressions. Its initial 25-case selection
failed 18 times before implementation; the final expanded Drawing selection
passed all 114 cases, zero skipped (17 seconds). Actual Microsoft Windows ARM64
and portable macOS ARM64 receipts match all 44 transform cases exactly in
acceptance/exception, numeric matrix values and reset-frame clip bounds. The
Microsoft diagnostic used the installed .NET 11 preview; hosted .NET 10 checks
on both Windows architectures remain required. Numeric equality treats signed
zero alike and preserves explicit NaN translation components; there is no epsilon.
The Windows gate validates every transform identity/order before its unchanged
13 bitmap cases. A transformed SVG image previously published an all-NaN matrix,
then failed clip conversion and masked that error during cleanup. Rejecting the
bad scale preserves drawing state; it does not manufacture valid SVG dimensions
or claim that the external SVG renderer's malformed nested image now renders.

Hosted run 36427980081 confirms all 44 transform cases on both Windows x64 and
ARM64 with the pinned .NET 10 runtime. The bitmap gate still fails: six Union
pixels at x=8, y=58..63 are `(255,1,1,255)` instead of `(255,0,0,255)`.
This localizes a one-byte coverage difference, not its cause; no tolerance or
sampling policy is changed. The same run resolves four additional official
Linux x64 cases (noninvertible Multiply/Transform and zero-X/zero-Y Scale), with
3313 passes, 1106 remaining known failures and 34 skips. Remove only those four
measured obsolete expectations; keep ARM64 and all SVG inventories unchanged.

The portable probe additionally captures Union's actual retained atlas slice and
bounded R8 mask after its original bitmap readback. Test-only reflection inspects
owned resources without mutating them. Because production masks intentionally
lack CopySrc, a test-only integer `textureLoad` kernel reads their existing
TextureBinding; no filtering, substitute rasterization or production usage change
is introduced. The original 120-second process bound remains in force. Local
Metal evidence matches every atlas/mask texel against independent rectangle
membership and all 13 final bitmap receipts against Microsoft. This does not
localize the Windows discrepancy until that platform's intermediate receipts run.

The pinned official Linux x64 corpus independently improved from 3297 to 3309
passes, with 1110 remaining known failures and 34 unchanged skips out of 4453.
CI run 36424060755 reported exactly 12 resolved clip failures and no new failures;
remove only those 12 obsolete x64 expectations. The separate ARM64 inventory is
unchanged until actually measured. These remaining known failures are not waived
or claimed fixed by the clip work.

`eng/progpu-verify-windows-drawing-clips.ps1` runs independently built Microsoft
and portable probes in separate processes on Windows x64 and ARM64. It verifies
assembly provenance, architecture and all 13 ordered cases, comparing exact bounds,
visibility, red coverage and hashes of every RGBA pixel. Each process has a
120-second bound. The Build workflow retains both receipts and all old gates.
The Display case must be empty, while its matching Pixel case must have ink;
every other case requires ink. The first Windows run exposed the incorrect
Display scaling assumption in the probe and portable implementation. It remains
failed evidence, not a qualified package producer.

These source/bitmap checks do not close popup issue #197. Actual popup screenshots,
the full unchanged native input scenario (including F10/Down), macOS/Linux UI,
successful whole producer Builds, downstream pins and final releases remain
required. Nested-container mid-scope flush and general curved region query
precision are not newly qualified by these fixtures.

## Exact coverage and native SVG follow-up

Commit `ec4646d7cd40849d4566a7173391b2454590e0a9` implements
[exact pixel-aligned atlas transfer](path-atlas-pixel-mapping.md) in the managed
and native renderers. Both Windows reference jobs in Build `36434866677` pass
all 44 transform and 13 exact bitmap cases, including the six Union pixels.
The remaining full Build/package/application gates are separate requirements.

The native SVG.NET reference was resumed on Windows ARM64 on 2026-09-28, with
the unchanged SVG.NET commit `fd33bed4ff14c803b800214ddec977ca0a2e0f8e` used by
the pinned corpus. Its separate native build depends only on SVG.NET/ExCSS and
Microsoft WindowsDesktop, not the ProGPU Drawing assembly. Under the installed
.NET 11 preview runtime (explicit major roll-forward), `struct-image-16-f`
throws `System.ArgumentException` from Microsoft GDI+ `Graphics.ScaleTransform`
at `SvgImage.Drawing.cs:159`. The portable receipt fails at that same source call.
The .NET 10 hosted reference independently checks invalid scale rejection in the
44-case transform inventory; the SVG-specific native probe is not relabeled as
a .NET 10 run.

The exact fixture SHA-256 is
`7bd31c27ec9031622180ed1b709fd2a9f5a631664a492058744261a76a5600d2`.
Native probe DLL SHA-256:
`181a2f03af9873953346725800f2266cf730e421a24ed83ca3e82fc1293730d1`;
native SVG.NET DLL SHA-256:
`8b90ecd85c862a4f9d8518c8e5a406b7d0eaee2f220971d89e5dab7b6c540beb`.
The original 30-second process bound was retained; stdout/stderr are retained in
the `svg-native-reference.wegcQL` evidence directory. The old interrupted receipt
was not overwritten.

Reclassify only this fixture from an obsolete pixel difference to that exact
expected exception. It is not a rendered-image improvement, permission to accept
invalid scales, or a waiver of the three still-unverified recursive exceptions.
Other resolved image inventories remain unchanged pending actual image review.

The image review of run `36434879988` subsequently compared every retained actual
PNG for eight threshold-resolved fixtures with its pinned reference. The three
SVG-logo fixtures (`embedded-svg`, `external-svg`, and `optimizeSpeed-on-SVG`)
restore the complete logo, reducing error from 0.249319 to 0.016705. The three
`preserveAspectRatio` slice fixtures restore the distinct top/center/bottom crop
and corresponding horizontal alignment: errors are 0.013991 (xMaxYMax),
0.013842 (xMidYMid), and 0.013925 (xMinYMin), previously 0.139442–0.167559.
Remove those six obsolete resvg entries; its measured totals are 1164 passes,
488 threshold differences and 78 exceptions out of 1730 fixtures.

The W3C `filters-example-01-b` restores the complete gray backdrop and smoother
outer ring; `masking-path-03-b` restores the missing inner text and clip regions.
Both were also compared with the parent PR's retained images. Errors fall from
0.172457/0.189438 to 0.089353/0.089429, below the unchanged 0.10 threshold. Remove
these two threshold-difference entries without claiming pixel equality: the
filter example still lacks the reference shadow and has different text metrics,
and the clip fixture retains text differences. W3C totals are 272 passes,
243 threshold differences and 10 exceptions out of 525 fixtures. The earlier
`struct-image-16-f` reclassification is an exception, not one of these eight
rendered improvements. No thresholds or recursive-exception expectations change.
The separate Svg.Skia parity run `36434875731` passed on the same renderer commit.
