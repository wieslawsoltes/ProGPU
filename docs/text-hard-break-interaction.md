# Hard-break row interaction

Managed horizontal `TextLayout` retains an empty row as explicit caret metadata
from its actual layout-writer frame. It does not add a drawable glyph or invent a
shaping cluster. The metadata includes the original UTF-16 position, resolved
paragraph bidi level, alignment, top and line height. An insertion index merges
these carets with visual cluster edges in writer order, without sorting rounded
positions or independently resolving bidi.

LF, CR and CRLF delimit horizontal hard segments. CRLF is one break spanning two
original UTF-16 units. Neither its code units nor any other source text are
rewritten; a break is not passed to the glyph shaper as a visible character.
Cluster ends are bounded by their actual hard segment, so a visible character
cannot absorb intervening breaks or empty rows. Selection containing only a break
therefore has no glyph rectangle.

An empty row owns its half-open vertical band for pointer placement even when
the pointer is far from its zero-width caret. It reports no inside-glyph hit.
Exact shared row boundaries belong to the following row. Owned interaction
snapshots retain this metadata independently of later source glyph mutation or
regeneration, and ordinary visual movement visits the actual empty-row stops.

## Implementation checks

All 18 new hard-break regression cases fail against parent `6b78259ff`. With the
implementation, the complete linked shaping/interaction/source-guard project
passes 161 cases with zero failures/skips. The Drawing retained-layout/font-
quality selection passes 42 cases, including two public Drawing CRLF/empty-row
cases. Logs remain under `artifacts/text-interaction` in the implementation tree.
The source guard now requires retaining source bounds and paragraph level when
alignment updates a row, in addition to every original glyph-buffer invariant.

This change depends on the owned Drawing interaction API in PR #201. Full CI,
canonical editor integration and native application validation remain required.
It does not claim vertical writing, all Unicode line separators, visual-line
Home/End or up/down navigation, newline selection caps, IME, or native C/C++ text
ABI qualification. Those contracts are not replaced by zero-width glyphs or
source-side approximations.
