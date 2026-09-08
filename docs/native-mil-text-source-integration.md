# Native text source integration

## Core consumer and existing implementation

LibreWPF's core applications include editable TextBox/RichTextBox content and
Toolkit/AvalonDock text. Source inspection found that unsupported portable WPF
formatting can reach `SimpleTextLine.CreatePortableFallback`, which constructs an
empty paragraph rather than a shaped line. This is an unfinished source-formatting
connection, not acceptable evidence of rendering parity. Do not switch Windows
text services to that path merely to remove an OS guard.

ProGPU already implements retained `NativeTextShapingContext` operations for
font fallback, OpenType shaping and bidi-aware positioned paragraph layout.
`GetParagraphRequirements`/`LayoutParagraph` and their C++ context implementation
are the existing reusable pipeline. Do not add a second paragraph composer in
the WPF bridge. Native output retains glyph/font indices, source clusters,
positions and line ranges; the WPF TextLine adapter must preserve them
for rendering, selection, caret navigation, wrapping and trimming, including
source-run styling and actual end-of-paragraph semantics.

## Source TextLine connection — current implementation checkpoint

The source adapter now exists: `PortableTextLine` consumes the neutral
`IPortableTextFormatting`/`IPortableTextParagraph` contract. A native-MIL host
registers the provider before source formatting. On platforms without Windows
LineServices, a rejected SimpleTextLine now tries that provider before the old
transitional empty fallback. When the registered provider rejects content, the
exception propagates; it cannot become an empty successful line. The established
simple formatter and Windows LineServices selection are not replaced in this
checkpoint, and Windows SDK admission remains guarded.

`NativeTextParagraphSnapshot` in ProGPU owns the reusable editor integration:
UTF-16 input, existing retained C++ paragraph shaping, native bidi resolution,
logical successor-cluster metadata, exact UTF input line ends and native
interaction buffers. The original C++ shaping/layout algorithms are unchanged.
The snapshot rejects truncated/ellipsis layout because synthetic glyphs require
an explicit source collapsing contract. No retained native pointer is exposed.
Requirements allocate owned output capacity; only successful result counts are
retained. Temporary scratch is pooled and always returned.

Source WPF owns physical GlyphTypeface byte/face-index/em-unit snapshots cached by
face, run/paragraph metrics and source character indices. The host weakly caches
the leased native contexts by those immutable font snapshots. Wrapped lines share
their exact rendering TtfFont annotation (same bytes and collection face index),
so replay does not choose another face by URI/family-name fallback. Source WPF
transports that existing opaque glyph-font annotation without inspecting it.
Wrapped lines share immutable paragraph output through cloned typed TextLineBreak continuation state;
disposing a break drops its reference without invalidating an independent clone.
Changing continuation width/source position is currently explicitly rejected.

The adapter creates actual GlyphRuns with source cluster maps, caret stops, bidi
levels and WPF offsets. Native Y-down positions transfer once to the initialized
source GlyphRun and are used by both native-vector and neutral compatibility
exports; they are not reconstructed as unshaped per-character advances. WPF
logical caret navigation uses ordered native cluster boundaries, not the native
API's distinct visual navigation operation. Native hit and selection queries
consume retained buffers. Boolean OpenType typography features cross a typed tag
contract; source DigitState decides whether unsupported digit substitution is
required rather than guessing from a culture name.

This is an incremental **single typography domain** connection, not complete
TextBox/RichTextBox support. Mixed fonts/sizes/cultures/brushes, composite-font
resolution and fallback-face mapping, tabs, document objects/modifiers/hidden
runs, decorations/effects/baseline changes, enum-valued typography alternates,
digit substitution, markers, justification, WrapWithOverflow and collapsing
symbols remain explicit missing connections. Display-mode hinting is rejected;
localization/variations, changed-width continuation, min/max paragraph measurement
and full editing behavior remain unqualified. Close these against the existing
MVP and Toolkit/AvalonDock editor cases; do not replace them with nominal glyphs
or redefine this single-domain checkpoint as the application's finish line.

CPU input expansion has intrinsic BMP and four-surrogate-pair blocks; mixed or
malformed variable-length sequences and the bounded tail use the Rune decoder.
Cluster sorting/search, variable cluster grouping and logical navigation have
data-dependent topology. Owned DTO/font/glyph arrays are formatting lifetimes,
not allocations on each retained replay. These are CPU-owned source/editor
operations, not a GPU fallback, and no full SIMD or speed claim is made. Allocation
and throughput qualification remain required at feature freeze.

Authored fixtures compare UTF-16 expansion to the Rune oracle, and use a typed
source paragraph fixture for cluster maps, RTL glyph metadata, selection,
logical navigation and cloned continuation after line disposal. That fixture
does not execute native shaping. The existing native host harness now creates
its host before drawing, draws a mixed-direction/combining-mark FormattedText,
requires positive text width and native font bindings, and retains its bitmap,
geometry and device-recovery gates. Its existing dual-assembly diagnostic public
API reflection is not product reflection; remove it when the harness binds the
source WPF assembly directly. All fixture/application execution is deferred.

## C/.NET interaction stage

The existing native cluster/caret/hit/selection algorithms are now exposed through
six C exports and `NativeTextInteractionInterop`: requirements, build, hit test,
caret lookup, visual caret movement and selection rectangles. Inputs and outputs
are borrowed caller-owned spans, with no device, retained handle, per-glyph call,
buffer allocation or glyph repacking in this adapter. Managed output references
are pinned for the complete native call, including references into managed objects.

Cluster ends and resolved bidi levels are required **per positioned glyph**, not
per input scalar. Consumers must preserve original UTF input offsets and real
ligature/cluster boundaries after shaping and visual reordering; sequential glyph
indices or guessed direction are not substitutes. Requirements are capacities;
publish only the returned counts after success because caret deduplication can
reduce output. Buffers must be aligned and nonoverlapping. Invalid input or
insufficient output capacity fails closed, with zero published counts.

The original ProGPU interaction implementation at `8a51aa05` was moved into a
private record-templated implementation shared by the existing C++ API and new
C ABI. C records and C++ records have different layouts and are consumed directly,
not reinterpreted as one another. Valid-input algorithms are retained; finite
extent, bidi-level and boolean-flag validation is strengthened. These synchronous
CPU-owned editor queries are not a rejected-GPU fallback. This connection does
not claim complete SIMD qualification or measured performance improvement.

The new public C header generates its C# records through the existing contract
generator; generation and verification scripts include it. A standalone C ABI
fixture target builds without a WebGPU provider and compares C/native cluster and
caret output, RTL affinity, selection capacity failure and malformed metadata.
Managed fixtures cover ABI sizes and borrowed-buffer source contracts. These are
authored fixtures, not executed evidence. Native text targets and the managed
backend/fixture project compile; full renderer, runtime and application validation
remain deferred until feature freeze.

This closes the missing interaction export. The source connection above consumes
it; styled font runs and the remaining editor contracts are still core blockers.

## Retained context ownership connection

The managed wrapper previously read a raw pointer and then entered native code;
Dispose/finalization could destroy it during the call. Concurrent native calls
could also modify retained plan clocks, plans and fallback-font vectors without
synchronization. Every context operation now acquires the same stack-only use
scope, serializing the entire native call, not just the pointer read.

Disposal from another thread waits for an active operation. Reentrant disposal
marks the owner closed and defers destruction until the final nested use exits.
New acquisitions after closing throw; destruction occurs once. Finalization uses
the same owner, and failure to allocate the managed owner releases a successfully
created native context. Independent contexts remain independently executable.
The scope must stay on its acquiring thread and is internal; callers of the public
synchronous context APIs do not manage it or receive raw handles.

Requirements and execution remain separate operations, not an atomic sequence.
Fallback-font changes between them can change output requirements; consumers must
check execution status/counts and must not publish partially filled glyph data.
C/C++ users still own external context synchronization and borrowed input lifetime.
This change does not add native locks, change the C ABI or affect independent
stateless shaping/layout APIs.

No glyph arithmetic, CPU fallback, shader or algorithm is replaced. Existing
native implementations remain authoritative; no scalar substitute is introduced.
Synchronization overhead and contention are not benchmarked, so no speed claim
is made. Scope acquisition does not create a heap lease or per-call delegate.

## Authored evidence and remaining core work

Owner fixtures cover nested/reentrant disposal, exactly-once release, exclusive
concurrent use and disposal with an operation in flight. They isolate managed
ownership with a typed release callback; they do not execute a native shaper or
prove paragraph/glyph parity. Source guards require all public context operations
to hold the scope. Compilation is the current checkpoint; execution, native stress,
application images/caret/selection checks, performance and CI await feature freeze.

Still required: finish the source connection's explicitly listed application
dependencies above and qualify them against native Windows. The ownership and
single-domain adapter changes do not close the application feature or Windows SDK
admission. Managed portable rendering retains its independently selected mode.
