# Retained horizontal row navigation

`TextInteractionSnapshot.GetRowBoundary` returns the original source-start or
source-end caret on the current formatted row. `MoveCaretVertically` selects an
existing stop on the adjacent row nearest a caller-owned preferred X, retaining
the requested affinity when distances tie. No adjacent row leaves the original
caret unchanged. Zero direction also leaves it unchanged. Nonfinite X and
vertical-writing layouts are rejected explicitly.

The layout writer now attaches an integer row identity to each interaction box
and empty-row caret. Snapshot caret generation retains these identities beside
the owned stops, including through duplicate removal. Row identity is not
inferred from rounded Y, line height, glyph placement, or source newline scans.
Hard-break source indices, shaped clusters, mixed bidi, wrapped-edge affinities,
zero-height rows and genuinely empty text all remain distinct.

Navigation uses stable sequential candidate reductions, O(number of stops),
without per-key allocations. Snapshot construction owns the row array once.
`DrawingTextLayout` delegates to the same implementation, converting preferred
X out of its alignment frame and returned carets back into that frame. The
caller owns preferred-X retention/reset and source selection policy.

The clean-room behavior uses the public line-navigation concepts in Microsoft's
[keyboard UI guidance](https://learn.microsoft.com/en-us/previous-versions/windows/desktop/dnacc/guidelines-for-keyboard-user-interface-design)
and [edit-control contract](https://learn.microsoft.com/en-us/windows/win32/controls/about-edit-controls).
No external implementation code was used. Local checks pass 176 linked actual
text/shaping/source-guard cases and 45 Drawing/font-quality cases. An initial
wrap fixture incorrectly assumed an unbreakable shaped run would split; the
replacement uses explicit spaces and literal expected source row boundaries.
The failed attempt remains in `artifacts/text-interaction/row-navigation-restored.log`.

This is the managed horizontal layout/Drawing contract, not native C ABI,
platform keyboard/IME, or desktop UI qualification. Native and package gates
remain unchanged, and source editor admission is independently tested.
