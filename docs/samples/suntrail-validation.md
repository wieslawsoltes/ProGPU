# Suntrail validation

Validation date: 2026-09-05. This is a new sample with no predecessor benchmark;
these are absolute measurements, not a claimed speedup. The visuals are original
stylized procedural artwork. “AAA” is a subjective production-quality target, not
a certification established by a shader test or FPS counter.

## Correctness and platform execution

| Check | Result |
| --- | --- |
| Sample Release tests | 40 passed in the current revision, including eight surface completions and eight vault entry/traversal/return tests |
| Main `ProGPU.Tests` Release suite | 3,809 passed, including shader resource and rendering regressions |
| `ProGPU.Tests.Headless` Release suite | 240 passed |
| Desktop Release | Built and ran on macOS arm64 / Apple M3 Pro |
| Browser Release | WebAssembly AOT publish; Chrome WebGPU rendered title, scrolling gameplay, collectibles, stage progression, retry, and pause |
| iPhone 15 Pro Max | Corrected Release AOT package installed; 250 WebGPU exports verified; attached console passes renderer startup; user confirmed display and gameplay; slow FPS investigation below |
| iOS simulator | Release simulator build installed and launched on iPhone 17 Pro / iOS 26.4; visually checked landscape rendering and safe-area layout |

The simulator uses the interpreter. iPhone installation/launch is verified; Windows, Linux, and mobile-browser performance are not inferred
from these results. Computer Use could
capture Simulator output but rejected coordinate interactions after focus changes;
host touch gestures therefore have a narrower verification claim than browser input.
Held-button cancellation/focus loss is covered by the sample regression tests.

Native GPU tests render all eight biomes, assert no validation errors, compare exact
pixels on unchanged replay, and assert no extra upload bytes on that replay. Native
and browser execute one canonical shader body; the native compatibility loader only
removes the unsupported diagnostic-control directive. Captures are under
`artifacts/suntrail/`: `title.png`, `playing.png`, `biome-1.png` through `biome-8.png`,
and `phone-title.png`, `phone-playing.png`, `phone-paused.png`.

## Initial artwork baseline

MacBook Pro, Apple M3 Pro (11 CPU cores), 18 GB memory, macOS 26.6, .NET 10.0.201,
Xcode 26.4. Three launches of the same initial Release executable, each with 240 warmup
frames and 1,200 recorded frames, 1,440 × 900 logical window, normal Retina rendering.
The input-only pilot uses the same physics/collisions/damage as manual input.
No game deaths occurred in these measured windows. Browser gameplay was paused and
the iOS game process was stopped before these runs.

| Metric (ms) | Run 1 p50 / p95 / p99 | Run 2 p50 / p95 / p99 | Run 3 p50 / p95 / p99 |
| --- | --- | --- | --- |
| Frame interval | 8.165 / 17.007 / 20.788 | 8.216 / 16.790 / 20.516 | 8.326 / 16.834 / 22.336 |
| Compositor wall time | 0.378 / 0.894 / 1.230 | 0.402 / 0.875 / 1.345 | 0.360 / 0.809 / 1.236 |
| Simulation + artwork | 0.011 / 0.030 / 0.062 | 0.011 / 0.027 / 0.054 | 0.010 / 0.029 / 0.069 |

First-frame wall time: 1,051 / 707 / 649 ms. Worst frame intervals: 48.3 / 30.2 /
30.9 ms. These results support responsive rendering on this host; they do not claim
an uninterrupted 120 FPS on every device. `cpuMs` in raw benchmark output is the
host frame's elapsed wall time, including presentation/waiting; it is not sampled
CPU busy time or a GPU-duration measurement.

The app allocates 1.25–1.32 MB across each 1,200-frame measurement, including the
existing WinUI/host and occasional HUD text changes. The isolated warmed simulation
and artwork-batch regression allocates **zero bytes** across 1,000 iterations.
Do not conflate these two scopes. Active artwork uploads average 5,983–5,997 bytes
per frame. The extension issues one instanced artwork draw; UI draws are additional.

All three runs retain 18 native buffers and four textures; reported Metal residency
is 48,168,960 bytes, managed heap approximately 9.4 MB, and process working set
136–170 MB. PrivateBytes reports zero on this runtime and is treated as unavailable.
The initial sprite buffer was 98,304 bytes plus a 96-byte uniform; the final
material revision uses a 288-byte uniform for three positional lights and bounded ground occlusion. Stable paused replay
has no artwork uploads. Process footprint and Metal residency describe different
ownership domains and must not be compared as interchangeable totals.

Raw CSV, benchmark stdout, and exported tables reside in
`artifacts/suntrail/performance/`. Instrumented runs are recorded separately and are
not used to claim FPS improvements. The Time Profiler run completed normally and
contains 3,004 samples, including runtime startup, system/event-loop work, and native
rendering. JIT frames require the companion .NET trace for managed attribution.

The companion EventPipe trace uses `dotnet-sampled-thread-time,gc-verbose` and ends
with process exit 0. Its wall-stack report attributes 87% inclusively to the host
frame and 12.6% exclusively to surface-texture acquisition; the compositor is 4.95%
inclusive. This agrees with the much smaller explicit compositor/animation counters
and shows why host frame wall time must not be called busy CPU time. Full-launch
samples also include GLFW and Fluent resource initialization.

Time Profiler and Metal System Trace completed with benchmark output. Their resource
snapshots agree with the normal runs (18 buffers, four textures, 48,168,960 Metal
bytes). Both Allocations launch and attach attempts returned “Failed to attach to
target process.” Their failure logs are retained as such; they are **not**
evidence of allocation stability. No system security or debugger permissions were
changed. Managed allocation counters, the GC trace, stable native resource counts,
and bounded replay tests provide the available memory evidence, with that native
allocation-profiler limitation stated explicitly.


## Artifact cleanup

At the user's request, raw Instruments bundles, EventPipe traces, and derived ETLX
trace caches were deleted after exporting useful tables and summaries. Only the
small measurement reports and review screenshots belong in Git. The earlier
25-second rich-world Metal capture was terminated during finalization because its
temporary store consumed several gigabytes; it is not a successful GPU-duration
measurement. Its incomplete bundle and process-owned temporary store were removed.

## Final eight-world build: conservative ground occlusion

The same final Release executable was run in alternating off/on, on/off, off/on
order, with 240 warmup and 1,200 recorded frames per run. The explicit framebuffer
was 2,880 × 1,800 pixels for a 1,440 × 900 logical window. The toggle changes only
background shading behind opaque ground; exact-pixel GPU tests cover all eight
worlds both before and after scrolling.

| Pair / occlusion | Frame interval p50 / p95 / p99 (ms) | Compositor p50 / p95 / p99 (ms) | Simulation + art p50 / p95 / p99 (ms) | Deaths |
| --- | --- | --- | --- | --- |
| 1 / off | 10.084 / 16.917 / 21.151 | 0.411 / 0.761 / 5.783 | 0.013 / 0.028 / 0.071 | 0 |
| 1 / on | 9.621 / 17.111 / 20.440 | 0.261 / 0.885 / 5.095 | 0.009 / 0.030 / 0.054 | 0 |
| 2 / off | 8.318 / 17.315 / 24.203 | 0.476 / 1.240 / 9.561 | 0.013 / 0.040 / 0.071 | 0 |
| 2 / on | 8.690 / 17.781 / 23.645 | 0.308 / 0.823 / 5.143 | 0.010 / 0.025 / 0.071 | 1 |
| 3 / off | 8.305 / 17.335 / 25.037 | 0.301 / 1.145 / 10.467 | 0.010 / 0.034 / 0.067 | 0 |
| 3 / on | 8.371 / 16.987 / 20.177 | 0.510 / 1.015 / 5.169 | 0.015 / 0.037 / 0.076 | 0 |

The spread does not establish a statistically reliable frame-rate improvement.
The pilot reacts to host frame timing, so these are equivalent traversal workloads,
not identical frame-by-frame trajectories. One run hit an obstacle and retried;
all eight fixed-step route-completion regressions pass without deaths. Manual play
uses the same collisions and damage rules. No invulnerability or teleporting was
introduced to make the benchmark complete.

Enabled runs report 0.56–0.62 seconds to the first frame, 1.38–1.90 MB total managed
allocation across 1,200 frames, 6.0–6.34 KB artwork upload per active frame, 18 retained
native buffers, and four textures. Reported Metal residency varies from 48.2 to
69.8 MB; a fixed resource count alone is not proof of constant byte residency.
Stable paused replay has zero artwork upload; the isolated warmed simulation and
batch test has zero managed allocation. Frame elapsed time includes presentation
waiting and is explicitly named `hostFrameMs` in the final recorder.

The richer materials cost more than the initial artwork in some measured windows.
There is no claim of sustained 120 FPS, native iPhone frame-rate equivalence, or
photorealistic/AAA certification. Per-world traversal logs and raw frame CSVs remain
locally available under `artifacts/suntrail/performance/`.


The final six-second Time Profiler and Metal off/on recordings reached their time
limit and wrote bundles; xctrace returned 54 because it terminated the launched
workload before the 600-frame recorder completed. These runs are not FPS samples.
The retained final Metal export contains 2,219 app-specific residency records:
48,168,960 bytes at the end, 48,316,416 peak, and 48,021,504–48,218,112 after three
seconds. This corroborates the approximately 48 MB normal-run snapshot for that
window, while the other normal runs' higher residency remains disclosed above.
CPU sampling from the completed earlier rich-world capture contains 2,456 samples;
there is no matched final CPU/GPU speedup claim. Native Allocations attachment was
unavailable. Raw bundles were removed after review/export as requested.

The first signed installation exposed a device-only packaging failure: the native
linker exported WebGPU functions, but the subsequent Release symbol-strip step
removed them because Silk resolves them by name rather than managed P/Invoke.
A live process did not establish successful rendering. Device verification now
includes attached console output and final Mach-O export inspection. No signing
identities, provisioning profiles, device identifiers, or local signing helper are
included in the PR. The final published Browser build was re-opened in Chrome and
visually checked in title and Tidal Kingdom gameplay states.


The corrected device package preserves 250 WebGPU exports and passes the same
export check as the simulator. After reinstalling, attached console output reaches
the wgpu-native renderer initialization without the earlier symbol exception.
The full native executable is 65 MB (108 MB app bundle) with symbols retained;
managed full trimming and AOT remain enabled. The clean device rebuild has four
existing framework/dependency trim warnings and zero errors. Actual phone visual
confirmation remains separate from console and process verification.


## iPhone controls, pipes and GPU investigation (2026-09-05)

The user confirmed the symbol fix displays and plays on the physical iPhone. They
reported poor frame rate and weak touch jumps. Touch now publishes pressed and held
state together before simulation. Targeted tests compare touch/keyboard trajectories
at 120, 30 and 10 Hz; floating/fixed/arrow controls, independent pointers and saved
settings are also exercised. Pipe tests traverse each of the eight vaults through
ordinary input, return to the same surface position, retain coins on re-entry and
restore the surface checkpoint on retry. New vault captures live in the ignored
local artifacts folder; hazard collision shares the animation clock.

The iPhone 15 Pro Max runs a signed, fully AOT Release build at its normal physical
framebuffer resolution. The initial 600-frame run after 120 warmup frames measured
interval p50/p95/p99 50.011/62.434/72.873 ms, simulation 0.053/0.127/0.161 ms and
compositor CPU 1.506/1.866/2.380 ms. Instruments Metal System Trace subsequently
identified Suntrail fragment execution at median 56.065 ms (110 intervals) and
main-thread drawable wait at median 55.128 ms (111 intervals). Native Metal residency
counters are unavailable on this device build: reported zero is not zero residency.

Six canonical shader entry points specialize the largest materials without changing
their equations or quality. Exact specialized/general pixel comparisons pass for all
eight surface worlds. The same installed binary supports `SUNTRAIL_MEASURE=1` and
`SUNTRAIL_GENERIC_SHADER=1` for the bounded input-driven workload:

| Run | Shader | Frame interval p50 / p95 / p99 (ms) | Deaths |
| --- | --- | --- | --- |
| A | Specialized | 33.598 / 56.214 / 66.681 | 0 |
| B | General | 66.560 / 77.248 / 82.328 | 1 |
| C | Specialized | 42.984 / 66.681 / 66.681 | 1 |

A separate specialized Metal trace reported median fragment execution 32.516 ms
(166 intervals) and drawable wait 34.275 ms (164 intervals). This supports continued
work on fragment cost. It does **not** establish sustained 60 FPS: workloads advance
by wall clock, route deaths differ, and thermal conditions were not held constant.
The final pipe/hazard build was subsequently installed with all 250 WebGPU exports
verified. Its normal-play launch is checked separately; the timing runs above belong
to the preceding specialization comparison and are not final campaign performance. No smooth-FPS completion is claimed.

Exports and compact summaries are under `artifacts/suntrail/performance/iphone-*`.
Both raw device `.trace` bundles were removed after successful export, as requested.
The prior full renderer/headless suite results above belong to the earlier commit;
new sample and shader-resource tests are run for this revision. Format importers,
drag-and-drop editing and full 3D remain explicitly unfinished in the work list.

Current platform checks: signed iOS Release build and installation passed; Browser
WebAssembly AOT publish passed, and Chrome visibly renders the title and the new
touch settings panel. Current shader-resource audit: 19 tests passed.


Joystick correction (2026-09-05): the new phone-root pointer-injection tests failed
before the fix because the hit target was GameSurface instead of TouchStick. Both
floating and fixed layouts now pass movement, simultaneous jump, captured movement
outside the control, cancellation, and visible thumb-position checks. All 42 sample
Release tests pass; the extended phone render test also passes and writes
`phone-stick-center.png` / `phone-stick-drag.png` without advancing simulation between
captures. The focused core pointer/hit-testing suite passes 161 tests, including new
Panel/Grid/StackPanel null/transparent-background regressions. This is an input and
feedback fix, not a claim that the outstanding iPhone GPU bottleneck is resolved.


## Full-precision sky cache experiment

The optional sky cache passes exact RGBA8 comparisons for all eight surface worlds
and all eight vault palettes. Additional tests cover 2×/3× DPI, camera movement,
world/viewport-size changes, retained upload reuse, and disposal. Current sample
suite: 46 Release tests; canonical shader audit: 19 tests. The default is disabled;
Desktop `--sky-cache` enables normal-play review, while `--render-benchmark PREFIX
on|off 600` runs the fixed-input offscreen workload. The installed iPhone version
remains the joystick fix, commit `29366d4f`; device work stopped when the user
disconnected, and this experiment has not been installed or measured on iPhone.

The benchmark uses 932×430 logical pixels, 2796×1290 framebuffer pixels and 4× MSAA
on Apple M3 Pro. Each run warms 120 frames, restarts the simulation, then records
600 frames with exactly two simulation steps per frame. Submission CPU and serialized
GPU completion are separate; completion latency is not display FPS. All runs finish
at tick 1200, position (2977.0835, 552), zero deaths, 4,484,784 uploaded bytes and
177,600 total managed allocated bytes during measurement. Cache-on performs exactly
one sky bake during warmup, with no additional measured upload.

Metal System Trace corroborates resource residency: 83,132,416 bytes without the
cache and 142,508,032 with it, matching the application snapshots. The sky texture
itself occupies 57,709,440 bytes. Trace GPU interval distributions are not frame
durations and the short launch captures include unequal startup windows; they are
not used to claim a GPU speedup. Time Profiler exports contain 685 baseline and 565
cache-on samples. An Allocations launch remained in `AuthorizationCopyRights` with
the target stopped, so it was canceled; native allocation-stack evidence is missing.
The bounded texture and matching counters are not a substitute for that missing lane.

Six same-binary measurements and their environment/hash record are retained as
`artifacts/suntrail/performance/sky-final-*`. The earlier exploratory pairs showed
4–6% lower median latency but mixed p99 results. No sustained smooth-FPS claim or
default enablement follows from this experiment. Static terrain and other large
materials still need optimization and a later iPhone measurement.

Final same-binary results (alternating off/on, on/off, off/on):

| Pair | Sky cache | Serialized completion p50 / p95 / p99 (ms) |
| --- | --- | --- |
| 1 | off | 17.595 / 25.798 / 28.797 |
| 1 | on | 16.250 / 23.934 / 27.335 |
| 2 | off | 17.669 / 26.294 / 30.917 |
| 2 | on | 16.346 / 24.719 / 28.027 |
| 3 | off | 17.485 / 25.302 / 29.922 |
| 3 | on | 16.453 / 23.848 / 29.766 |

Desktop and Browser AOT builds pass. Native Desktop title/gameplay and Browser
title rendering were visually reviewed with Computer Use. This is visual rendering
verification, not a new device-control or phone-FPS claim. Oversized and translated
cache requests are also checked against live rendering. Completed raw Metal/CPU
trace bundles and the incomplete Allocations bundle were removed after export.
The larger GPU XML tables are gzip-compressed with SHA-256 round-trip verification.


## Exact transparent-coverage rejection

The sphere, canopy and mountain materials now evaluate their existing antialiased
coverage before lighting and skip lighting only for exactly zero alpha. The final
sample suite passes 54 Release tests, including 64 differential captures spanning
all eight surface worlds, eight vault palettes and 1×/3× DPI scrolling scenes.
All 450,265,600 compared RGBA8 channels match exactly with the optimization toggled.
The shader resource audit passes 19 tests. This preserves physical resolution,
4× MSAA, derivative coverage, material noise and lighting precision.

The final same-binary all-world benchmark uses the sky experiment's 600-frame
workload, alternating option order by world. These are serialized GPU completion
latencies, not displayed FPS. Files, commands and binary hashes are retained under
`artifacts/suntrail/performance/coverage-final-*`.

| World | Disabled p50 / p95 / p99 (ms) | Enabled p50 / p95 / p99 (ms) |
| --- | --- | --- |
| 1 | 17.112 / 25.143 / 28.952 | 16.357 / 25.074 / 29.076 |
| 2 | 18.010 / 27.627 / 32.122 | 17.545 / 25.598 / 29.093 |
| 3 | 15.961 / 23.978 / 29.262 | 14.770 / 22.230 / 25.556 |
| 4 | 16.395 / 24.068 / 27.511 | 16.077 / 23.925 / 26.861 |
| 5 | 17.295 / 25.449 / 29.261 | 16.268 / 24.668 / 28.199 |
| 6 | 15.297 / 23.273 / 27.299 | 14.737 / 21.479 / 26.011 |
| 7 | 15.239 / 22.858 / 27.230 | 15.357 / 23.384 / 27.966 |
| 8 | 15.254 / 23.079 / 25.789 | 15.043 / 22.831 / 26.759 |

Each option finishes the same route at tick 1200 with zero deaths. Within each
world the upload bytes, draw count, managed allocations (177,600 bytes) and native
Metal residency (83,132,416 bytes) are identical. Median changes range from a 7.5%
reduction to a 0.8% increase; world 7 and world 8 tails need cautious interpretation.
Startup numbers include driver caches and are not cold-start comparisons.

An expanded fourteen-pipeline experiment was rejected: it increased draw switches
and startup work without a consistent latency benefit. It also introduced a small
number of one-step RGBA8 rounding differences. Those extra pipelines and entry
points were removed; the production pipeline count remains six.


Reversed-order confirmation runs for worlds 7 and 8 also finish identical routes:
world 7 off 10.381 / 16.506 / 18.900 ms, coverage 10.160 / 16.306 / 18.805 ms;
world 8 off 11.122 / 17.402 / 19.817 ms, coverage 10.121 / 16.004 / 18.641 ms.
These lower absolute timings show the host's changing performance state, so only
within-pair comparisons are meaningful. The small exact-output optimization is now
enabled by default; sustained phone FPS remains an open gate.

Final Time Profiler and Metal System Trace captures each complete the full 120-frame
warmup plus 600 measured frames and end because the target exits, without truncation.
CPU sample exports contain 1,009 baseline and 1,214 optimized samples; raw stack
addresses limit attribution and these totals do not prove reduced CPU work. Metal
resource snapshots and its allocation-size export agree at 83,132,416 bytes for both
options. GPU interval exports contain overlapping/nested execution intervals, not
one row per game frame; their distributions are retained for diagnosis rather than
reported as FPS. No managed/native ownership changes were introduced. The previous
Allocations authorization failure remains a missing measurement lane.

Useful exports, target summaries and exact profiling commands are retained under
`artifacts/suntrail/performance/coverage-*`. Four completed raw trace bundles were
removed only after XML parsing and SHA-256-verified compression of the GPU exports.
Desktop and Browser AOT builds pass, and Computer Use verifies the Desktop title,
start/pause flow and Browser title rendering. Native review also found a separate
pause-menu defect: after clicking Begin adventure and pressing Escape with the
pointer still over the original button, its label/background remain incorrect until
the pointer leaves. This is recorded for a focused input-state fix; visual review
does not imply every menu state is correct.
