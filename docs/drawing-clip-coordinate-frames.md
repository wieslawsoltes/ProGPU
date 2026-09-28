# Drawing clip coordinate frames

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
