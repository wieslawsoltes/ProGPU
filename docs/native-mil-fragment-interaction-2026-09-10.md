# Native fragment text interaction geometry

## Acceptance and implementation

The unchanged LibreWPF Showcase Figure/Floater path requires selection, caret
geometry and text hits to match the retained paragraph fragments. This batch adds
try_build_fragment_text_interaction to the installed native header and named
module, using the existing shared interaction builder rather than another map.

The shared builder accepts explicit text_fragment_placement metadata. It reads
each fragment's real top instead of summing fragment heights, retaining same-row
geometry and vertical clearance gaps. Cluster boxes and caret stops retain the
fragment index; the placement record maps that index to its row.

Validation requires matching fragment/line counts, contiguous row identity,
finite positive interval widths, ordered nonoverlapping intervals within a row,
equal top/height/baseline for same-row fragments and nonoverlapping row extents.
Invalid metadata fails before box/caret publication. Ordinary and measured
prefix-based entry points keep their existing contracts and default behavior.

Existing point-hit, source-affinity caret lookup and selection-rectangle helpers
consume these boxes directly. Selection does not fill the space between separate
fragments, and clearance/anchor gaps do not become inside-text hits. Nearest-text
hit results outside content remain explicitly outside.

## Scope, reuse and evidence

Original ProGPU provenance is progpu_native_text_interaction_impl.hpp and the
excluded paragraph flow at 86afa6df. The
[paragraph design and research chain](native-mil-excluded-paragraph-2026-09-10.md)
remain applicable. No foreign implementation is introduced. Both native providers
share this builder; no managed renderer is replaced by a reduced alternative.
Both WPF modes still await the same retained native fragment transport.

The change allocates nothing and retains no pointers. Row validation is one
ordered metadata pass; cluster grouping, caret generation and queries retain
their original shared algorithms. It adds no GPU work, source text copy, font
initialization, new cache or upload. No performance qualification is claimed.

The Release native text suite and named-module consumer pass. Both native wgpu
and Dawn libraries build. Focused integration coverage uses actual excluded
paragraph output, checking both fragments on a row, the following row, original
source positions, source-affinity caret coordinates, four distinct selection
rectangles, outside-text gaps and invalid metadata without output publication.
RTL output also preserves the rightmost fragment's original source affinity,
caret position and hit result without treating its fragment index as a row.

Logs: artifacts/fragment-interaction-build.log,
artifacts/fragment-interaction-tests.log,
artifacts/fragment-interaction-providers-build.log.
Module artifacts remain in artifacts/anchor-text-module.FxeqPf.

Remaining: cross-fragment visual/vertical caret movement, retained C ABI and
neutral transport, native anchor sizing/positioning, original WPF subtree
ownership and interaction, nonconvergent layout semantics, unchanged application
acceptance, final package/platform and PR CI qualification. No WPF anchor
admission or release gate was changed.
