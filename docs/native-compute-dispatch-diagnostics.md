# Native compute dispatch diagnostics

Set `PROGPU_NATIVE_TRACE_COMPUTE=1` before loading a native provider to record
actual engine-owned compute pipeline selections, direct workgroup dimensions,
indirect dispatch presence and queue-submission boundaries. The immutable
process opt-in is disabled for every other value. Disabled tracing performs no
formatting, clock reads, pipeline scans or per-dispatch environment reads.

Each engine emits at most 4,096 events followed by one truncation notice.
Events contain its address, installed scene/generation and submission count.
Addresses may be reused after engine destruction; they are not persistent IDs.
An `encoded` event is command encoding, **not** submission or GPU completion.
The count is submissions already issued at that instant. An encoder may later
be discarded. A `submitted` event follows the original engine submission and
increments that count; it does not assert fence completion or successful pixels.
No event reads GPU data or adds a wait, submission, timestamp query or fence.

`encoded-indirect` reports `groups=0/0/0` because the actual arguments remain
GPU-owned and are deliberately not read back. These zeroes do not mean no work.
`hit_query` slots are point/bounds/ellipse in their existing order. For
`ordered_hit_query`, slots 0/1 are collect/merge and the remaining slots are the
existing `2 + kind * 10 + family` pipeline array, including the separate clip
pass. Path keys distinguish the original linear, single, Boolean, signed and
split stages. Glyph, blur, shadow and browser query-readback dispatches retain
their own identities. Unknown pipeline handles are reported as `unknown`, never
guessed from platform, compiler, adapter or command name.

Both wgpu-native and Dawn compile the same original ProGPU dispatch sites.
The managed renderer does not call these engine methods and is not instrumented
by this native-specific switch. Shader bytes, bindings, dispatch arguments and
order, resource ownership, encoder lifetime, compiler/adapter defaults and all
deadlines are unchanged. The small internal header-compatible trace value has
no public/module/C ABI surface or GPU resource ownership; it permits device-free
tests of opt-out, sink lifetime, field formatting and bounded truncation.

## Why this is needed

WPF Build 36411654709's ARM64 failure-only replay shows an active UI loop while
surface configuration waits for actual queued work. Five native MIL submissions
were encoded; no sixth frame was submitted after resize. Its 40,047 ms dump
matches Microsoft WARP compute execution, not compilation. Generated instructions
are readable, but the referenced resource heap is absent. Those instructions
alone do not identify a ProGPU shader or prove an infinite loop/driver defect.

This trace supplies missing dispatch attribution for a subsequent bounded
reproduction. It does not fix or qualify the failed WPF resize. A full successful
producer Build, exact downstream package graph and original application tests
remain required before release; diagnostic payloads cannot substitute for them.

Implementation provenance is the original ProGPU engine and dispatch sites at
`d679346ae5adf565fc471345edf34a64b44a6bb1`. No third-party implementation was copied;
this is diagnostic instrumentation, not a rendering or pipeline architecture change.

Local Release validation rebuilt both native providers and passed the five
selected CTest suites with tracing disabled and enabled: internal, native ABI,
Dawn contract, MIL and Direct2D WebGPU. The ordinary run emitted zero trace
events; the enabled run emitted 464 events with no unknown pipeline or truncation.
The Metal pixel fixtures exercised glyph, single/linear/Boolean path, horizontal
and vertical blur and shadow dispatches. This is not Dawn GPU, Windows WARP,
indirect-query or complete package/application qualification; those retain their
independent CI/runtime gates. The internal test independently exercises disabled
and missing sinks, field identity, indirect encoding and the 4,096-event limit.

Build `36449160844` rejected three diagnostic identifiers under MSVC C4458
because they shadowed the engine's `pipeline` member. The correction names the
owned map entry and selected dispatch pipeline explicitly, without suppressing
warnings or changing the selected handle, dispatch arguments or trace behavior.
The original failed producer remains ineligible for downstream package staging.
