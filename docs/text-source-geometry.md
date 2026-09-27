# Retained source-row and position queries

`TextInteractionSnapshot` owns the actual horizontal writer's row starts beside
its existing cluster boxes and carets. Wrapped rows retain the logical candidate
boundary selected before visual reordering; first rows keep their original hard
segment start, including hidden formatting characters. No query scans source
newlines, reshapes text, resolves bidi again or reconstructs rows from rounded Y.

`RowCount`, `GetRowSourceStart` and `GetRowIndexFromTextPosition` describe original
UTF-16 source ownership. Both units of CRLF belong to the preceding row. The
shared source position at a soft wrap belongs to the following row, whereas
`GetCaretRowIndex` keeps the existing leading/trailing affinity and can identify
either physical row at that boundary. Empty and trailing rows keep real writer
identity, even at zero height. Empty managed text keeps its existing sole empty
caret row; this is not a change to native empty-input row contracts.

`GetSourcePositionPoint` uses the owning cluster's logical leading edge. Interior
UTF-16 units of a combining/supplementary cluster do not acquire invented stops.
Hard delimiters use their own row's retained logical end, not the nearest caret
on the next row. Positions are advance geometry, not glyph ink offsets. The
Drawing facade applies the same layout alignment offset as drawing and the
existing caret/selection methods.

All source-row arrays are captured with the snapshot; later mutation or
regeneration of the original glyph collection cannot change their ownership.
Source indices outside `[0, TextLength]` and invalid row indices are rejected;
caret lookup retains the existing nearest-stop/affinity policy. Vertical writing
and nonempty legacy layouts without writer-owned row metadata are not admitted
as horizontal source geometry.

Storage adds O(R) integers per owned generation. Row-start lookup is O(1), source
row lookup O(log R), and source-point/caret lookup uses the existing O(C) captured
cluster/caret data. Queries do not allocate new paragraphs or initialize a GPU.
The original implementation provenance is ProGPU's `TextLayout` horizontal
writer, `TextInteractionSnapshot`, and `DrawingTextLayout`; no third-party source
implementation was imported.

Local macOS ARM64 validation passes all 208 linked shaping/interaction/source-
guard cases and all 27 retained Drawing-layout cases, zero failures/skips.
Evidence is under `artifacts/source-geometry`. Tests cover all source units,
hard/soft boundaries, hidden formatting characters, bidi edges, zero-height rows,
snapshot ownership, argument rejection, Drawing alignment and original draw
contracts. Full exact-head CI and source editor integration remain required.

This provides the geometry needed for WinForms character/physical-line APIs; it
does not itself replace their USER32 dispatch, qualify platform editor behavior,
change native C/C++ text ABI admission, or enable WPF Display hinting.
