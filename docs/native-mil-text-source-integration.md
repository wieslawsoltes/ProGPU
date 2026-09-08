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
positions and line ranges; the future WPF TextLine adapter must preserve them
for rendering, selection, caret navigation, wrapping and trimming, including
source-run styling and actual end-of-paragraph semantics.

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

This closes the missing interaction export, not the WPF formatting connection.
The source TextLine adapter, styled font runs and cluster metadata producer remain
required; `SimpleTextLine.CreatePortableFallback` is still an open core blocker.

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

Still required: connect source WPF formatting to this existing C++ pipeline through
typed interop, resolve/cache source font identities and variations, adapt mixed
styled runs and source clusters to WPF line/glyph contracts, and remove the empty
fallback without replacing missing content with nominal unshaped glyphs. This
ownership change alone does not close that application feature or Windows SDK
admission. Managed portable rendering must retain its independently selected mode.
