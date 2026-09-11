# Retained floating paragraph snapshots

## Application dependency

The unchanged LibreWPF RealApplicationRunHarness first-window measurement needs
Figure/Floater events to survive the native text provider. `CreateWithFloats` now
connects the batched floating transport to the existing paragraph snapshot, not a
second paragraph composer. Automatic WPF anchor admission remains guarded.

The snapshot owns UTF-16 event records and their source-ordered native placements.
An ordered O(S+A) merge maps source boundaries into the existing decoded scalar
array, preserving repeated sibling positions and the terminal boundary. Negative,
unordered, out-of-range and surrogate-interior offsets reject. The native shaping
transport remains responsible for rejecting events inside actual shaped clusters.
The merge has a forward dependency; UTF decoding retains its existing intrinsic
implementation. There is no per-row managed callback or extra shaping pass.

Native fragment frames continue driving glyph boxes, carets and inline-object
placement. `FragmentLayout` describes parent text; `FloatingLayout` independently
retains child-inclusive extents. Original event arrays are copied once into owned
generation storage. Subsequent caller mutation cannot change retained positions.
Empty source with floats uses the native explicit source-metric row and emits no
synthetic glyph or caret. Source editor empty-row caret policy remains downstream.

This reuses original ProGPU NativeTextParagraphSnapshot inline/excluded ownership
and the floating transport at 04395737. The existing
[text ownership research](native-mil-text-source-integration.md)
and [native floating row policy](native-mil-floating-row-events-2026-09-11.md)
remain applicable: reusable shaping, source clusters distinct from glyph indices,
and shared native layout/interaction. No foreign implementation is introduced.
Both WPF renderer modes will consume the same optional source text provider;
this CPU snapshot adapter is not an alternative renderer implementation. Both
native libraries already share the underlying floating fitter and C transport.

## Evidence and remaining work

Release consumer compilation and the local MIL-only project-reference consumer
pass, covering native text/inline ownership, source origin, sibling and terminal
events, separate extents, and the explicit empty row. UTF-16 mapping regressions
cover surrogate boundaries and repeated events: all 11 focused snapshot tests
pass (zero skipped). These are local retained-contract
checks, not exact-head packaged application or platform qualification.

Next connect the optional neutral floating provider, original WPF hidden-edge
mapping, hard-segment continuation and owned child placement/drawing/input. Native
intrinsic width output is not exposed by this new floating factory; its separate
contract must be implemented before a consumer requires it. Actual application,
package, Windows/macOS/Linux, CI and deferred compatibility gates remain open.
