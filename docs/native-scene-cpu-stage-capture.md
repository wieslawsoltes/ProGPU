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
