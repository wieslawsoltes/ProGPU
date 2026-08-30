# ProGPU.CAD Ortho point-acquisition research record

## Scope and primary sources

The initial slice adds exact plan-view Ortho acquisition to the shared
desktop/browser MOVE and COPY second-point prompts. The isometric continuation
uses the active Left/Top/Right isoplane pair and composes the same grid and
direct-distance paths. It does not add 3D Z-axis acquisition, temporary keyboard
overrides, or arbitrary-camera point acquisition.

The implementation was designed clean-room from public behavior contracts:

- Autodesk's [ORTHOMODE reference](https://help.autodesk.com/cloudhelp/2022/ENU/AutoCAD-Core/files/GUID-CF142B68-675B-452F-B3A8-7831DDB71BD0.htm)
  defines persisted drawing state and constrains horizontal/vertical movement
  relative to the current UCS and grid rotation angle.
- Autodesk's [ORTHO command reference](https://help.autodesk.com/cloudhelp/2026/ENU/AutoCAD-Core/files/GUID-128AC5D7-72B0-498F-958D-7F619A73EC5F.htm)
  defines the two-point pointing-device contract and identifies UCS X/Y as the
  plan-view horizontal and vertical axes.
- Autodesk's [orthogonal-locking behavior](https://help.autodesk.com/cloudhelp/2022/ENU/AutoCAD-MAC-Core/files/GUID-2ADD1754-3FD1-43B4-BF59-B05FFC59A587.htm)
  specifies nearest-axis rubber-band behavior, coordinate and object-snap
  overrides, isometric-snap priority, direct-distance composition, and mutual
  exclusion with polar tracking.
- Autodesk's [ActiveX Ortho behavior](https://help.autodesk.com/cloudhelp/2024/CHS/AutoCAD-ActiveX/files/GUID-E16359AE-88A0-4FA6-87CA-66510937DFA4.htm)
  independently confirms that Ortho applies to activities requiring a second
  point, follows the nearest axis, and is ignored for typed coordinates,
  object snaps, and perspective views.
- Autodesk's [ISOPLANE command reference](https://help.autodesk.com/cloudhelp/2020/ENU/AutoCAD-Core/files/GUID-9B1EEA63-BEC1-413E-B69F-541B5865F1A1.htm)
  defines Left as 90/150 degrees, Top as 30/150 degrees, Right as 90/30
  degrees, and requires Ortho to use the active pair.

No third-party implementation source was used. The exact approved source
provenance is the ProGPU-owned point-prompt, object-snap, grid-basis, viewport,
snapshot, and retained-overlay implementation plus the in-repository ACadSharp
`Header/CadHeader.cs` `OrthoMode` and `Tables/VPort.cs` isometric contracts at
the pinned ProGPU feature commit `592e5f1c`. No foreign source text, helper
shape, naming, or control flow was copied.

## Adopted contract and precedence

`CadSnapshotCompiler` captures persisted `ORTHOMODE` into the immutable
snapshot. A fresh shared view initializes its session toggle from that value;
the toggle remains a bounded interaction-session override and does not edit or
republish the document. Ortho is enabled only for a pointer-supplied second
point after the exact base point has been accepted.

For accepted base `B`, pointer point `P`, and the active rectangular or
isometric unit axes `X,Y`, the query computes:

```text
D   = P - B
dx  = dot(D, X)
dy  = dot(D, Y)
ex2 = max(0, dot(D,D) - dx*dx)
ey2 = max(0, dot(D,D) - dy*dy)
axis = X when ex2 <= ey2, otherwise Y
P'  = B + axis * dot(D, axis)
```

The squared perpendicular distance works for both orthogonal and oblique axis
pairs; the deterministic exact tie chooses X. Because the basis already
composes the active UCS with SNAPANG and SNAPISOPAIR, Ortho follows all three
without duplicating their normalization or validation. Non-finite or malformed
state fails closed. Work and storage are O(1), and a warm query allocates no
managed memory.

Pointer precedence is exact object snap, then Ortho, then grid snap, then raw
pointer position. Object snap returns immediately and therefore ignores Ortho,
as specified. Typed absolute and relative Cartesian/polar coordinates bypass
the pointer constraint pipeline. With grid and Ortho both enabled, the nearest
rectangular or Euclidean-nearest triangular grid point is projected onto the
selected axis through the accepted base. An off-grid base obtained from object
snap therefore cannot be pulled off its Ortho line. The exact double-WCS result
is committed without a float screen round trip. Direct-distance entry consumes
that same axis result. Polar tracking remains mutually exclusive with Ortho.
F5 or Ctrl+E changes drawing-persisted SNAPISOPAIR through one reversible edit;
the resulting immutable snapshot supplies the new active pair to the next
Ortho query without a duplicate direction table or mutable hover state.

## Rendering and managed/native applicability

The existing second-point rubber band is itself the Ortho guide: its endpoint
uses the constrained point and follows the selected axis. Grid composition
continues to reuse the existing fixed-device marker. Hover changes no document,
generation, immutable scene, upload, cache key, or GPU resource.

The required rendering/text architecture gate was rechecked against
[Skia's staged text model](https://docs.skia.org/docs/dev/design/text_shaper/),
[DirectWrite/Direct2D separation](https://learn.microsoft.com/en-us/windows/win32/direct2d/direct2d-and-directwrite),
[Win2D retained text layout](https://microsoft.github.io/Win2D/WinUI3/html/T_Microsoft_Graphics_Canvas_Text_CanvasTextLayout.htm),
[WebRender's rendering pipeline](https://firefox-source-docs.mozilla.org/gfx/RenderingOverview.html),
[Vello's retained-scene vision](https://github.com/linebender/vello/blob/main/doc/vision.md),
[Parley's reusable layout model](https://github.com/linebender/parley/blob/main/doc/concept.md),
and [HarfBuzz shape plans](https://harfbuzz.github.io/shaping-and-shape-plans.html).
The applicable common principle is to retain semantic results and keep
lightweight interaction state outside resource and scene caches. This slice
changes no shaping, layout, fallback, glyph/path/image cache, batching, upload,
DPI/subpixel, startup, worker, or device-loss behavior.

The native renderer consumes the same committed retained picture after the
MOVE/COPY edit. Ortho is shared host-side input policy and adds no native scene
compiler, shader, GPU resource, wire record, C ABI crossing, or backend-specific
algorithm. A second C++ constraint implementation is therefore not applicable;
managed/native committed-scene behavior remains identical.

## Verification and remaining gates

Focused tests cover nearest-axis selection and deterministic ties, rotated
bases, rectangular grid composition with an off-grid base, all active isometric
pairs, non-finite rejection, persisted snapshot capture, 1,024 zero-allocation warm queries, second-point-only
shared-shell behavior, exact MOVE commit, object-snap override, and shared
desktop/browser toggle propagation.

3D UCS Z acquisition, F8 and temporary overrides, persisted ORTHOMODE editing,
arbitrary-camera rays, interaction image goldens, large-drawing
p50/p95/p99 evidence, and DXF/DWG ORTHOMODE round-trip fixtures remain before
the broader Ortho/tracking feature can be called complete.
