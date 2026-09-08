# Gradient hatch data preservation

The rendering-first audit exposed enabled gradient hatches in representative
drawings. Rendering them remains an explicit gap. Basic copy and save must not
corrupt their authored data while that gap is open.

## Contract and implementation

The primary serialization reference is Autodesk's
[HATCH DXF group-code contract](https://help.autodesk.com/cloudhelp/2024/ENU/AutoCAD-DXF/files/GUID-C6C71CED-CE0F-4184-82A5-07AD6241F15B.htm).
Adopt its gradient metadata and two-stop data representation, including radians,
shift, single-color dialog state, tint, and name. Preserve authored final colors;
do not recompute them from dialog metadata or approximate a gradient renderer.

Changes are contained in the reviewed ACadSharp dependency:

- `HatchGradientPattern.Clone` owns a new list and cloned color entries. Clearing
  the shallow clone's shared list previously erased the source colors too.
- DXF hatch output writes the previously omitted gradient records for AC1018+
  and rejects older output versions instead of silently dropping the gradient.
- DXF group 421 now replaces the approximate indexed color with the authored
  RGB value. DXF packs red in bits 16–23, unlike ACadSharp's internal packed color
  representation; decode the channels explicitly.

No third-party implementation source is copied into ProGPU. Clone work is O(C)
time and owned storage for C stops; gradient serialization is O(C), with no new
render-frame work. These are shared object-model and file-I/O fixes, not managed
or native renderer algorithms, shaders, or ABI changes. Both renderers receive
the same unchanged snapshot contract. No C++ counterpart applies.

## Validation and limits

Six focused dependency tests pass in Release on .NET 10: independent clone
ownership; all nine names, RGB/indexed colors and single/two-color state through
ASCII/binary DXF; rejection of pre-gradient DXF versions.

Three ProGPU integration cases cover copy, undo, redo and save/reopen through
ASCII DXF, binary DXF and DWG. The complete local Release CAD suite passes
1,545 tests. This is data-preservation evidence, not external writer certification
or gradient pixel-conformance evidence. Gradient rendering remains unsupported
and reported by the snapshot compiler.
