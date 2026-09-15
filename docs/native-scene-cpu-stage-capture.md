# Native semantic-scene CPU stage capture

The C++ semantic renderer can report owner-thread CPU durations for one
installed, immutable scene generation. This is opt-in: the ordinary scene
rendering overloads do not set `CAPTURE_CPU_STAGES`, do not read the clock, and
return zero stage fields. `NativeCompositor.RenderSceneWithCpuStages` selects
capture for a host-owned external target; the existing `RenderScene` overload
remains the fastest default path.

The native frame flag requires the complete extended
`progpu_native_scene_frame_metrics` structure. An older metrics prefix with
the flag is rejected before GPU work; without the flag, old metric callers
retain their established ABI. The managed contract is generated from
`progpu_native.h`. The stage suffix is appended after the existing 104-byte
metrics prefix, so every preexisting upload/submission field retains its
offset. `NativeSceneFrameMetrics` exposes new init-only stage properties
without changing its positional constructor.

| Field | Measured owner-thread work |
| --- | --- |
| `CpuPreflightNanoseconds` | Stream/resource preflight and bounded compilation budgets after the requested scene identity is admitted. |
| `CpuResourceNanoseconds` | Retained GPU resource preparation, including nested picture-image rendering and WebGPU buffer/texture setup. |
| `CpuEncodeNanoseconds` | Scene/pass/layer command encoding and replay up to command-buffer flush. |
| `CpuFlushNanoseconds` | Command-buffer finish and queue submission calls. |
| `CpuFinalizeNanoseconds` | Post-flush clear fallback, retained cache/metrics publication. |
| `CpuTotalNanoseconds` | Sum of the above intervals, allowing a few nanoseconds of clock conversion rounding. |

All stages measure synchronous CPU time inside `progpu_native_engine_render_scene`
on the engine owner thread. GPU queue submission may block in a WebGPU call,
but these fields do not establish GPU execution duration, fence completion,
driver residency, or physical memory. The host must pair this result with its
actual successful frame, engine/scene/generation and recovery identity;
snapshot reads must remain read-only. Captured cost is part of measured host
CPU time.

The motivating Windows 11 ARM64 Parallels Toolkit run completed scene update,
compile and surface acquisition quickly, then spent ~262 seconds inside a
later ProGPU C++ scene render call. The retained log has no native subphase
timestamps. This API makes the next exact-device/package rerun attributable
without managed-renderer fallback or altered scene content. It is a
measurement contract, not itself a fix or Toolkit/runtime qualification.

An exact corrected-head Windows ARM64 runtime trace subsequently measured
`23.815 s` native encoding plus `4.553 s` resources for the first scene
(`287` commands), and `255.427 s` encoding plus `7.754 s` resources for the
second scene (`6,842` commands). Flush was below `2 ms` in each. This
identifies a CPU-side encode bottleneck but does not distinguish the work
within that interval or qualify the full Toolkit application.

## Live encode checkpoints

Set `PROGPU_NATIVE_TRACE_SCENE_ENCODE=1` in addition to using
`RenderSceneWithCpuStages` to print owner-thread checkpoints to standard
error as each expensive phase finishes. `resources` covers the established
resource stage; `prepare` covers subsequent encoder and layer resource
preparation; `bundles` covers retained render-bundle compilation or cache
reuse; `replay` covers pass/span/effect command encoding; `flush` covers
command-buffer finish and queue submission. Each line identifies the source
scene and generation, command count, bundle cache-hit state, retained span
count, phase duration and total elapsed CPU time. The stream is flushed after
each line so a test process stuck in a later phase still leaves its earlier
checkpoints readable.

The environment variable by itself does nothing: the capture flag and full
metrics structure must already be admitted. The ordinary fastest render path
does not read the variable or clock, allocate diagnostic state, or print.
Capture without this additional variable keeps the original phase metrics
without standard-error output. These checkpoints diagnose where encoding is
slow; they do not change bundle ownership, rendering semantics, or make an
unqualified application gate pass.

## Retained-bundle operation profile

The corrected-head Windows ARM64 native runtime for ProGPU #168 established
the coarse cause on the same Toolkit scene: after `80.817 ms` preparation,
the `6,842`-command generation spent `257,745.961 ms` building `327` retained
spans, then only `5.711 ms` replaying and `0.573 ms` flushing. The next
`6,862`-command generation spent `267,411.853 ms` building `340` spans.
Those are source-overlay VM measurements, not package or full application
qualification. The run exited later on a separate Win32 native-popup owner
admission failure. A bundle cache miss is observed; these values do not prove
which internal operation is expensive or that changed spans can be reused.
The runtime was the `progpu-native-runtime-win-arm64` artifact from Build run
`34991253884` at head `1dfda315e4b82c889de1b0a14eee90a2001204fa`;
the installed `progpu_native.dll` SHA-256 was
`f9d913ea13cf6f81d5b9382b23fc75066a398359533d9087754b4326432e41ca`.
Managed WPF host assemblies remained source overlays, so this evidence
isolates the C++ native runtime but not an assembled package closure.

With the same opt-in environment variable and CPU-stage frame flag, the
renderer additionally publishes one `native semantic bundle operations` line
after the `bundles` checkpoint. It reports calls and cumulative CPU time for
render-bundle encoder creation, draw encoding, encoder finishing and release,
semantic mask binding and advanced-blend binding. `otherMs` is the remainder
of the whole bundle phase after these nonoverlapping measured operations; it
includes scene traversal, span assembly, layer/effect planning and any
unmeasured helper. A cache hit reports zero operation counts. The counts do
not equal retained spans because materialized layer/composite operations also
occupy the span table. This profile retains the source
engine/scene/generation and does not sample GPU completion or change replay.
The ordinary default path skips all per-operation clocks and diagnostic
output. Follow-up performance changes must be selected from exact Windows
operation evidence, then pass native MIL input, application and package gates.
