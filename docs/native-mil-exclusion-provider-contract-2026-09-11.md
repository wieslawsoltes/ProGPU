# Source exclusion provider contract

Acceptance action: first presentation, reflow and caret movement in ShowcaseApp's
Figure/Floater-containing document. The source paragraph currently rejects
AnchoredBlock; the WPF provider exposes ordinary and measured inline paragraphs,
but has no explicit exclusion capability. Removing that source guard would
discard child layout and source positions, so admission stays closed.

`IPortableExcludedTextFormatting` adds an explicit optional capability over the
existing inline provider. It takes source-resolved half-open paragraph-DIP
exclusions, source style/object metrics and an explicit native attempt budget.
Providers that implement only the existing interfaces remain unchanged and
cannot silently accept this request. The contract does not measure anchor
children or turn paint envelopes into exclusion geometry.

`IPortableExcludedTextParagraph` retains native fragment frames, content/measured
extents and caret stops. Lines and fragments share indexing, distinct from rows.
The native double fragment top remains available separately from float glyph
coordinates. Existing line-local selection and hit conventions remain intact.
Caret motion is indexed within one retained generation, with physical direction,
paragraph-DIP preferred X and provider-owned paragraph direction. Invalid input
and unsupported policies fail; no default implementation discards exclusions.

The neutral project builds with zero warnings/errors, recorded in
`artifacts/excluded-neutral-contract-build.log`. This is a contract addition, not
an implemented WPF provider or source anchor admission. Next connect the existing
WPF adapter to the native excluded snapshot without ordinary line-height prefixes,
then actual anchor subtree measurement/placement and original source mapping.
Application/package/platform/CI qualification remains required.

## Workspace metadata repair

Inspection also found that recursive submodule synchronization had rewritten
shared core.worktree paths for the primary LibreWinForms and ProGPU checkouts
relative to their linked worktree Git directories. Primary `git status` failed;
source files and linked-worktree commits were intact. Restored each common
core.worktree to its verified absolute primary checkout path using Git's explicit
config-file operation. Primary status again reports only its pre-existing dirty
submodule identities; the prepared WPF checkout is clean and its pinned heads
remain unchanged. No source checkout, index reset or user edit was overwritten.
