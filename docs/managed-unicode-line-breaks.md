# Managed Unicode line-break resolver

`UnicodeLineBreakResolver` is an **internal, currently unconsumed prerequisite**,
not a Windows EDIT word-selection policy or a change to managed text wrapping.
It exposes no public Drawing API and adds no native runtime dependency. No
`TextLayout`, `DrawingTextLayout`, native resolver or native ABI behavior changes.

## Owned source and contract

The implementation is a faithful managed port of ProGPU's own
`src/ProGPU.Native/src/Text/progpu_native_unicode_line_break.cpp` at
`4b0064a9c949e07a20fcbe21a98203fc95cb86ed`, including its rule order and
context walks. Internal class/kind/error values follow `progpu_native_text.hpp`
at that same commit. No third-party implementation source was used.

Both implementations use the original Unicode 17 generated property data:
`UnicodeLineBreakData.Generated.cs` (line-break, quotation, mark, East-Asian and
unassigned ranges) and `UnicodeGraphemeData.Generated.cs` (extended pictographic
ranges). `eng/generate-native-unicode-tables.py` already generates the C++ data
from these files. The port adds no separate Unicode classifier/table and uses
neither runtime Unicode categories nor regexes.

`TryResolve` borrows disjoint scalar input, resolved-class scratch and break
output spans for one synchronous call. Results are indexed by original scalar,
describing the boundary **after** it; the final nonempty boundary is mandatory.
The immutable managed scalar value retains the caller's original source-unit
index/length. This core, like its C++ counterpart, resolves scalar order without
interpreting or rewriting that metadata. UTF-16 decoding and transport admission
are separate contracts; this is not a native wire-record layout.

Capacity failure precedes scalar validation and writes nothing. Invalid Unicode
scalars (surrogates or values above U+10FFFF) are rejected before any break result
is written; an earlier resolved scratch prefix may remain, matching the native
core. Empty input succeeds without writes; unused caller tails remain intact.
The original context-dependent scans remain sequential and bounded by the input
length. Generated static C# tables initialize on first use; subsequent calls
allocate nothing. Worst-case work remains O(N² log R) for N scalars and R property
ranges; caller workspace is O(N), with O(1) additional per-call storage besides
the existing O(R) shared generated tables.
No measured performance improvement is claimed.

## Authored checks and remaining qualification

`UnicodeLineBreakResolverTests` contains 24 authored managed cases. The existing
native `unicode_line_breaks_feed_native_layout_without_allocation` case retains
its original assertions and gains matching complete default-boundary fixtures
and failure/tail controls. These cover CR/CRLF, combining marks, numeric context,
glue, CJK, Hangul, quotes, regional indicators, emoji modifiers and ZWJ, source
metadata preservation, invalid scalars, empty input and capacity/error ordering.
They are focused conformance scaffolding, not a claim of complete Unicode test
corpus coverage or an executed managed/native differential.

The integrated managed project builds in Release, and all 24 authored managed
cases pass on macOS arm64. The matching native cases compile in the complete
stock/Dawn `--build-only` payload; they have not been executed locally.
Native production source, generated data and ordinary wrapping are unchanged.
Full managed/native conformance and normal upstream gates remain required before
qualification. This private dependency must not be published as a completed
source feature without its actual qualified consumer.

Default UAX #14 is deliberately not tailored to the stock EDIT observations.
For example, its NBSP and narrow-NBSP glue behavior is the same, and both snowman
and grinning-face use the ideographic class. The recorded EDIT selections differ.
Those observations do not justify a general compatibility profile, literal
exceptions, or treating unobserved source interiors as an oracle. EDIT tailoring,
owned generation metadata and Forms selection integration remain unfinished.
