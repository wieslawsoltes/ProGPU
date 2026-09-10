# Native exclusion-band text fitting

## Acceptance dependency and implementation

The unchanged LibreWPF Showcase Figure/Floater path requires text to fit around
anchored source subtrees. The previous interval resolver is now consumed by
try_fit_text_exclusion_band in the same native text implementation. This is
logical fitting, not yet final paragraph placement or source anchor admission.

The fitter calls the existing scan_line shared by ordinary logical paragraphs.
It returns original shaped glyph ranges for each available interval, in actual
paragraph reading order. RTL chooses the rightmost interval first. Mandatory
breaks end the candidate band. Tab origins incorporate the interval's displacement
from the paragraph leading edge, preserving the existing grid rather than
restarting it beside each anchor.

Intervals too small for indivisible shaped content are skipped, not expanded
through an anchor. A fully constrained band returns no consumed glyphs and the
existing next-Y retry hint. An unconstrained full-width band retains ordinary
overflow behavior. Trimming and starts inside shaped clusters fail explicitly.
Band height remains caller-resolved: actual measured ascent/descent must be
checked before accepting the result. No conservative oversized line height or
silent clip is substituted for that missing placement connection.

## Reuse, cost and parity

Original ProGPU source provenance is scan_line in progpu_native_text_layout.cpp
at 535dfcce, including shaping-safe break selection, cluster preservation and
incremental tabs. There is no copied external implementation or second composer.
The [interval design and research](native-mil-anchor-intervals-2026-09-10.md)
remain applicable: source ownership, caches, fonts, retained rendering, GPU work
and device-loss behavior are unchanged.

The API is exported by both the installed header and the existing named module.
It has no C ABI/PInvoke addition: this fitting work belongs inside the retained
native paragraph operation. Both native providers share this text library.
The original managed text implementation has no equivalent anchored fitter;
neither renderer has newly admitted Figure/Floater support from this change.

It allocates nothing and uses E scratch intervals and E+1 output slots. It
validates scales through the existing SIMD metric path. Sorted interval union,
tab accumulation and shaping-safe break selection are ordered dependencies.
Cost includes O(G) scale validation and O(E log E) interval processing; an
indivisible prefix may be rescanned for each interval. The eventual paragraph
consumer must avoid repeating whole-paragraph validation for each row.
No benchmark or fastest-path claim is made.

## Evidence and remaining work

Release native text compilation and the complete include-based text suite pass.
New cases exercise LTR/RTL multi-interval fitting, original source ranges,
hard breaks, indivisible clusters, oversized constrained content, full-width
overflow, next-Y progress, paragraph-relative tabs and rejected trimming.
The updated named-module consumer also builds and runs against the same text
and compression archives using Homebrew LLVM and the installed Xcode sysroot.

Logs: artifacts/anchor-band-build.log and artifacts/anchor-band-tests.log.
Module outputs remain in artifacts/anchor-text-module.FxeqPf.

Still required: measured-height acceptance with bounded retries; common-baseline
fragment emission through the shared native writer; retained fragment-aware
interaction/transport; native anchor sizing/positioning; real source subtree
ownership and interaction; unchanged application and final package/platform/CI
qualification. Existing source rejection and all release gates remain intact.
