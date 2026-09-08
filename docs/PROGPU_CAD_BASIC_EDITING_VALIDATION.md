# Basic editing and persistent image references

2026-09-08; focused delivery milestone, not advanced drafting certification.

The published browser smoke now exercises Line, selection, Move points, Copy
points, Delete, Undo, Redo, and Save/Open through ordinary mouse/keyboard input.
Each editing stage saves a DXF for an independent group-code oracle. It checks
entity counts, unchanged original entity tags, exact undo/redo and reopened
coordinates, and the translated line/copy. Pointer unprojection uses the host's
float viewport, so its expected displacement permits at most 1/256 logical pixel
of rounding; save/reopen geometry still requires exact equality. No application
test hook changes the document, and no screenshot substitutes for file checks.

## Reproduced file-integrity defect

After creating one line, an untouched IMAGE changed its group 360 reactor from
`62` to `64`. The IMAGEDEF persistent-reactor group contained both handles, but
only reactor `64` was written. `CadWriterBase.Write` calls
`CadDocument.UpdateImageReactors` on every save; the old implementation removed
reactors from the object registry and created replacements without removing
their definition backlinks. Repeated saves accumulated dangling references.
Four new dependency regressions failed before the correction.

The primary contracts are Autodesk's
[IMAGEDEF](https://help.autodesk.com/cloudhelp/2020/ENU/AutoCAD-DXF/files/GUID-EFE5319F-A71A-4612-9431-42B6C7C3941F.htm)
persistent references, one per image instance, and
[IMAGEDEF_REACTOR](https://help.autodesk.com/cloudhelp/2021/ENU/AutoCAD-DXF/files/GUID-46C12333-1EDA-4619-B2C9-D7D2607110C8.htm)
associated-image handle. The correction is original object-graph reconciliation
inside the existing reviewed dependency, not copied CAD implementation code.

## Correction and applicability

Save preparation retains valid image-owned reactors and their handles, adopts
reader-resolved owned reactors that are not registered yet, and creates reactors
only for missing/invalid ownership. It rebuilds each definition's image
backlinks, preserves other reactor types, and unregisters obsolete reactors.
Copying an image does not reassign the original's shared reactor; deleting or
retargeting an image removes its old backlink. Reconciliation is expected
`O(N + R)` for N document objects and R definition backlinks, with `O(I + D)`
temporary storage for I images/reactors and D definitions. Backlink removal uses
one linear list compaction, not repeated removal for each shared image.

This changes DXF/DWG document metadata preparation, not rendering, scene
compilation, image decoding/upload, resource identity, shaders, ABI, or GPU
submission. Both managed and native renderers keep consuming the same CAD
snapshot. There is no separate native CAD file writer to update. No rendering
performance improvement or external AutoCAD interoperability certification is
claimed. Other existing writer preparation (such as class/dimension updates)
is outside this correction; this is not a claim that all saves are mutation-free.

The dependency correction is committed as `c1b69797`. Four focused dependency
cases and all 1,625 Release CAD tests pass, including strengthened repeated-save
checks on the representative scene. Dependency net9.0 tests use Major roll-forward
on the installed .NET 10 runtime, not native .NET 9 certification. Desktop Release
build and browser Release AOT publish complete.

Before the clip-closure correction below, the extended hardware browser smoke
passed every edit/save stage through redo of deletion, including unchanged
original entity tags, but stalled while opening the final edited file and
attempting the next save. A native stack capture showed
the Chrome renderer's main thread busy in unsymbolized generated code. The same
saved DXF loads, compiles to a snapshot/plan, and publishes through
`CadSampleCanvas.LoadAsync` in an independent desktop-side probe. This narrows
the investigation but does not establish a browser root cause or a passing full
edit/reopen workflow. Stage diagnostics are being used to localize it.

Further instrumented AOT runs reach the new session's capture lock, enter
`CompileSpace`, complete LINE `49`, and resolve WIPEOUT `4A`'s style, material,
and persisted frame setting. The six-vertex clip loses one closing vertex, then
reaches the collapsed-edge rejection on retained vertex 4. The browser stalls
at that exception path, before resource preparation or retained recording.
Inspecting the desktop probe's diagnostics corrects the earlier implication:
its load completes **with WIPEOUT rejected as CADSNAP002**, not with intact
rendering. All temporary stage logging was removed after this diagnosis.

CAD CI run `34211146562` on `dbabd6fe` also fails: the initial screenshot times
out after 120 seconds, before the edit sequence. This repeats the Linux
presentation blocker rather than qualifying the final browser workflow.

## Repeated polygon-clip closure

The DXF image writer appended the first polygon vertex unconditionally, whereas
the reader retained every serialized vertex. The sample starts with four
corners, saves five vertices, reopens five, then saves six. The additional
closing vertex is a collapsed edge, not additional polygon geometry. The
original basic file smoke checked retained entity types and missed this loss.

Autodesk's [WIPEOUT group-code contract](https://help.autodesk.com/cloudhelp/2023/ENU/AutoCAD-DXF/files/GUID-2229F9C4-3C80-4C67-9EDA-45ED684808DC.htm)
defines the vertex count and sequential polygon vertices, with exactly two
opposite corners for rectangles. The writer now adds a polygon closing vertex
only when the boundary does not already end at its first vertex. Group 91
matches the emitted count, input lists remain unchanged, and two-corner
rectangles keep their representation. This does not repair previously malformed
lists or weaken the renderer's collapsed-edge validation.

This original dependency IO correction adds O(1) closure detection to the
existing O(V) write for V vertices, with O(1) extra storage. DXF ASCII/binary
share the writer; DWG has no unconditional closing-vertex append and is
unchanged. Both renderers receive the same corrected immutable clipping
geometry; no renderer algorithm, shader, native ABI, cache, or quality change
is involved.

Eight polygon cases fail before the correction, covering IMAGE/WIPEOUT,
ASCII/binary, and initially open/closed lists over chained save/reopen cycles.
All 12 clipping cases plus four existing reactor cases pass after it. Three
CAD-level regressions compare exact WIPEOUT primitives and clip arrays after
four representative edit/save/reopen cycles, including DWG as an unchanged
control. The five focused CAD round-trip tests pass. Final CAD and uninstrumented
browser AOT results follow below; malformed-input browser exception handling
remains an explicitly separate investigation.

The dependency correction is `7ee9001b`. All 1,628 Release CAD tests pass after
the fix. The browser file oracle now checks IMAGE/WIPEOUT group 91 against the
actual coordinate pairs, two-corner rectangles, and single polygon closure
without collapsed adjacent edges, in addition to unchanged original entity
tags at every editing stage. Desktop Release builds with 65 existing warnings
and no errors, and the browser Release AOT publish completes.

The uninstrumented Chrome hardware smoke passes the complete basic editing
sequence, final edited-file reopen and resave, and resize to 2880x1800 physical
pixels. It reports 1,791 frames and 1,840 dispatches; these are diagnostic counts,
not performance comparisons. The edited screenshot was inspected and shows the
retained drawing, WIPEOUT, both text columns, and newly authored/moved line.
The same final AOT binaries also pass the full macOS SwiftShader smoke (93
frames, 117 dispatches, 2880x1800); no entity, coordinate, or pixel check is
relaxed for software rendering.
This closes the reproduced valid-file edit/reopen workflow locally, not the
Linux CI gate or the separate malformed-input exception path.

The mouse-input call inside the existing bounded file-event loop could itself
wait indefinitely. It now shares that operation's deadline, and file-input
delivery is explicitly bounded. These checks report stalls rather than restart
the app or bypass the edited-file reopen assertion. Profiler attachment/stop
also stalled during diagnosis and is not part of the final smoke implementation.

The broader dependency image/document test run remains active in its existing
large-object deletion stress test; no full-suite result is claimed. The new-head
Linux screenshot failure is tracked separately in
[browser validation](PROGPU_CAD_BROWSER_VALIDATION.md#new-head-ci-remains-unreliable-2026-09-08).
Generated DXFs, screenshots, stacks, and logs remain ignored under
`artifacts/progpu-cad/`.
