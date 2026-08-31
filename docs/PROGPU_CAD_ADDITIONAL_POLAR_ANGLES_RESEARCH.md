# ProGPU.CAD additional polar angles research record

## Scope and primary sources

This slice adds the bounded additional-angle portion of AutoCAD-style polar
tracking to shared desktop/browser MOVE and COPY point prompts. It does not
invent last-segment-relative measurement for commands that have no previously
authored segment, and it does not add object-snap tracking or 3D polar paths.

The implementation was designed clean-room from these public contracts:

- Autodesk's [Polar Tracking tab reference](https://help.autodesk.com/cloudhelp/2024/ENU/AutoCAD-Core/files/GUID-D7CBB7B0-9140-4C53-88EF-08EAA09FA9D7.htm)
  defines additional angles as absolute rather than incremental, caps the list
  at ten, and separates absolute-UCS from last-segment measurement.
- Autodesk's [POLARADDANG reference](https://help.autodesk.com/view/ACD/2026/ENU/?caas=caas%2Fdocumentation%2FACDLT%2F2014%2FENU%2Ffiles%2FGUID-73162BAB-C98D-4159-A653-E4C7D4CB38C3-htm.html)
  defines the semicolon-separated profile string and states that its values do
  not generate multiples.
- Autodesk's [POLARMODE reference](https://help.autodesk.com/cloudhelp/2025/ENU/AutoCAD-Core/files/GUID-D91628CC-9975-4DBF-8D02-10B23A6F3ED5.htm)
  defines registry/profile bit 4 as additional-angle enablement and bit 1 as
  relative measurement.
- Autodesk's [Polar Tracking and PolarSnap overview](https://help.autodesk.com/cloudhelp/2027/ENU/AutoCAD-Core/files/GUID-7EC3C63D-EA4E-4E65-A676-C3A3627E3F19.htm)
  defines current-UCS/angle-convention orientation and the distinction between
  angular tracking and along-path PolarSnap distance.

No third-party implementation source was used. Approved provenance is the
existing ProGPU-owned polar query, immutable profile settings, point-prompt
state machine, object-snap arbitration, PolarSnap query, direct-distance
resolver, and shared control shell. No foreign helper, naming, control flow,
lookup encoding, comment, or source text was adopted.

## Adopted profile and acquisition contract

`CadPlanPolarAdditionalAngles` stores zero through ten normalized radians in
ten inline value slots. Its cold parser accepts the documented semicolon list
as invariant decimal degrees; blank text is a valid empty list, while empty
items, non-finite numbers, more than ten values, or more than 256 input
characters fail without changing the retained list. Periodic values normalize
into one turn. List order is retained.

Additional-angle enablement and contents are profile/session state. They never
enter the ACadSharp document, DXF/DWG output, snapshot generation, or edit
history. The shared controls update a pending point immediately without a new
pointer event. Invalid text cannot enable the list; making an enabled list
invalid disables it instead of continuing to use hidden stale values.

For measured pointer angle `q`, positive incremental angle `a`, and `A <= 10`
additional absolute angles `e[i]`, acquisition first computes the existing
incremental candidate and then performs one bounded scan:

```text
k = round-away-from-zero(q / a)
c = k * a
error = wrapped-absolute-angle(q - c)

for i in [0, A):
    candidateError = wrapped-absolute-angle(q - e[i])
    if candidateError < error:
        c = e[i]
        error = candidateError
```

The incremental candidate wins exact ties; otherwise the earliest strictly
closer additional candidate wins. The selected angle uses the existing
ANGBASE-adjusted UCS basis and ANGDIR direction, then the pointer is projected
onto that path. Additional values never create multiples. Pointer activation
still uses the existing fixed 10-device-pixel perpendicular aperture.

Query work is `O(A)` and storage is `O(1)` with `A <= 10`, so both are bounded
constant work. There is no managed allocation, table search, document mutation,
scene compilation, upload, or backend call in a warm query.

Object snap remains first. An acquired additional path can feed PolarSnap, but
PolarSnap changes only its along-path distance. Bare direct-distance input uses
the acquired additional direction and preserves the typed length exactly.
Typed coordinate forms continue to bypass pointer constraints.

## Last-segment fidelity boundary

The subsequent LINE slice implements POLARMODE bit 1 from an actual accepted
LINE segment. It passes that finite nonzero direction explicitly to the polar
query; before such a segment exists, relative incremental tracking fails closed.
Additional angles remain absolute. MOVE/COPY still cannot infer a segment from
selection geometry, a previous displacement, or cursor history. POLYLINE remains
a later command-state consumer of the same explicit-reference overload.

## Rendering and managed/native applicability

Additional angles change only which transient polar guide and existing fixed-
device prompt marker are positioned. They add no retained production primitive,
shader, texture, upload, cache key, C ABI record, managed/native crossing, or
backend algorithm. The committed edit continues through the same typed command
and scene compilers. The paired renderer applicability audit therefore requires
no C++ or shader change and committed managed/native output remains equivalent.

The mandatory cross-engine rendering/text gate is not triggered by this input-
only profile/query slice. No rendering, scene compilation, font, text, glyph,
path, image, startup, worker, DPI, device-loss, or GPU-pipeline contract changed.

## Verification and remaining gates

Core regressions cover bounded parsing, periodic normalization, absolute non-
incremental selection, enablement, clockwise orientation, and 1,024 warm
queries over all ten slots with zero managed allocation. Shared interaction
regressions cover live reevaluation without pointer motion, invalid-list disable,
no drawing generation, object-snap precedence, PolarSnap composition, exact
MOVE, and exact direct-distance separation.
The complete macOS arm64 Release ProGPU.CAD suite passes 1,075/1,075.

Last-segment-relative measurement is now covered by real LINE authoring and
remains pending for POLYLINE. Object-snap tracking/acquired points, 3D paths, cross-session
profile persistence, arbitrary-camera rays, visual goldens, and dense-drawing
p50/p95/p99 evidence also remain.
