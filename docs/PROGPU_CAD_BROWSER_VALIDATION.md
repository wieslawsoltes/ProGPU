# CAD browser AOT runtime validation

## Fixes found by runtime smoke

The CAD browser host was missing the diagnostics elements required by the shared
`src/ProGPU.Browser/BrowserAssets/progpu-browser.js`. Startup failed in JavaScript
before .NET launch. The HTML now provides those IDs and an initially hidden
diagnostics panel, following the existing ProGPU browser host contract.

A real trimmed/AOT launch then failed constructing ACadSharp's `AppId` table
record. ACadSharp's file-IO object model uses reflection without complete linker
annotations. The browser project now roots that dependency assembly; this is a
correctness/size tradeoff, not a claim of trim-warning-free operation. It adds no
reflection to rendering, scene preparation, or frame submission.

The sample raster IMAGE also had no required rectangular boundary corners. The
writer rejected that invalid fixture, leaving only 14 of 15 model-space entities
after reopening. The sample now supplies the two corners for its 1x1 image.
Matched DXF/DWG tests compare the complete entity-type inventory after roundtrip
and verify that the raster definition and boundary survive.

## Reproduce

```sh
dotnet test src/ProGPU.CAD.Tests/ProGPU.CAD.Tests.csproj -c Release
dotnet workload install wasm-tools --skip-manifest-update
dotnet publish src/ProGPU.CAD.Sample.Browser/ProGPU.CAD.Sample.Browser.csproj -c Release -p:ProGpuForkPackage=true -p:RunAOTCompilation=true
npm ci --prefix src/ProGPU.Native/browser
npx --prefix src/ProGPU.Native/browser playwright install --with-deps chromium
PROGPU_CAD_BROWSER_USE_SWIFTSHADER=1 node eng/progpu-test-cad-browser.mjs
```

For an installed Chrome, set `PROGPU_CAD_BROWSER_CHANNEL=chrome`. Omit the
SwiftShader environment variable to exercise the available hardware adapter.
On Linux the software lane explicitly selects SwANGLE, Vulkan SwiftShader, and
the WebGPU SwiftShader adapter, while keeping Vulkan surfaces enabled. The previous ANGLE-only flag
selected an OpenGL driver, not necessarily the WebGPU adapter. Earlier local
"with SwiftShader" results in this document mean that flag was enabled; they
do not establish software WebGPU execution. Each run now saves `gpu.json` with
Chrome's GPU report and launch arguments. `presentation.json` additionally records
the actual WebGPU adapter and independently verifies canvas contents and page
presentation; the ANGLE report alone does not identify the WebGPU device.
The non-Linux launch configuration is unchanged.
`ProGpuForkPackage=true` selects the dependency's net10.0-only source build and
avoids requesting unrelated multi-target WebAssembly workloads.

Do not disable `MSBuildEnableWorkloadResolver`: a successful publish with the
resolver disabled is not proof of AOT compilation. The smoke requires the CAD
assembly's native AOT object in addition to the published site. The CI job uses
a fresh checkout, performs native AOT linking, and runs the published application.

The smoke checks startup errors, submitted frames/commands, visible scene pixels
before the initial capture, tool expansion, drawing-only pixel changes after wheel
zoom and middle-button pan, DXF download, preservation of the 16-entity inventory,
reopen and resave, and physical framebuffer resize at device scale 2. Screenshots
and DXF evidence are uploaded by `.github/workflows/cad.yml` from the ignored
`artifacts/progpu-cad/browser-smoke/` directory. This is a focused runtime gate,
not an AutoCAD pixel differential, performance benchmark, external raster-file
packaging test, or DWG browser certification.

The current fixture includes two-column MTEXT. Both save and reopen/resave must
retain its content, two column heights, and column count; the paired desktop
tests cover DXF and DWG. Pan, like zoom, waits for a bounded visible pixel change
because software GPU presentation can finish after browser animation callbacks.
The assertion still fails if the drawing never changes.

Failure evidence includes console messages, page/browser identity, framebuffer
and submission counters, a page screenshot, and a direct canvas PNG snapshot.
The latter two distinguish a page-composition problem from blank canvas content.
These are failure-only diagnostics, not production rendering work.

## Applicability and remaining work

Earlier local validation on 2026-09-08, using the 15-entity fixture:
1,539/1,539 Release CAD tests passed; the
normal-workload-resolver AOT publish completed native linking; Chrome with
hardware rendering and with SwiftShader passed the complete smoke, including all
15 model-space entity types
after reopen/resave and a 2880x1800 physical framebuffer. Screenshots were
visually inspected. Submitted-frame/command counters are liveness evidence only,
not performance measurements.

The DOM and linker changes are browser-host/dependency packaging fixes, not
renderer changes; the native C++ renderer has neither boundary. The corrected
sample document is shared by desktop and browser and feeds both rendering paths.
No renderer, shader, ABI, cache, or GPU algorithm was changed. Source provenance
is the original ProGPU browser-host contract and CAD sample; no third-party
implementation was copied.

Remaining milestone gates include representative real drawings, desktop
interaction, basic edit workflows, and green checks on the final PR commit.
The shared sample now uses the compact workspace described in the focused
delivery scope. Existing linker warnings remain visible. None of these checks establish
full CAD or writer certification.

CI run `34190851775` completed CAD tests and real AOT linking, then failed the
strengthened blank-frame assertion on Linux. Its console-error and console-log
arrays were empty. That remains unresolved; local browser success must not be
reported as green Linux CI. The subsequent columned-text sample builds with
real AOT, and all 1,552 CAD tests pass with CI's existing runtime settings.
The updated local smoke passes on pinned Chromium 151.0.7922.34 with SwiftShader:
all 16 model-space entity types and the MTEXT column fields survive save/reopen,
pan and zoom change drawing pixels, and resize produces a 2880x1800 framebuffer.
The initial screenshot was inspected and both text columns are visible. This
does not close the Linux-only failure or establish zoomed text-quality parity.

Run `34192175781` also failed blank-frame validation: six submitted frames,
17 dispatches, a 2560x1600 framebuffer, and no console messages. The page was
white and the direct canvas snapshot was blank. This motivates testing the
explicit Vulkan configuration; it does not prove that configuration is the cause.
The flag selection follows Chromium's
[SwiftShader driver documentation](https://chromium.googlesource.com/chromium/src/+/main/docs/gpu/swiftshader.md)
and Chrome's [headless Linux WebGPU configuration](https://developer.chrome.com/blog/supercharge-web-ai-testing).
No application rendering behavior, quality threshold, or input assertion is
relaxed. `SystemInfo.getInfo` is a test-only browser diagnostic; no GPU readback
or new dependency is added to production rendering.

Run `34193459492` still failed. The browser GPU report selected Mesa llvmpipe
for ANGLE, and failure screenshot capture timed out before DOM state was saved,
masking the original smoke exception. The software lane now explicitly selects
both SwANGLE and `--use-webgpu-adapter=swiftshader`, following the separate
adapter switch documented by [Dawn's CTS runner](https://dawn.googlesource.com/dawn/+show/HEAD/webgpu-cts/README.md).
The original exception is saved first in `failure.json`; DOM/canvas and page
captures are independently bounded and cannot replace it. Linux success remains
unproven until the new run completes; no screenshots or pixel checks are skipped.

## Linux presentation isolation: 2026-09-08

The final local AOT publish at ProGPU `11b87761` succeeds. Its macOS browser smoke
passes with 78 frames, 100 dispatches, all 16 model entity types through save/reopen,
and a 2880x1800 framebuffer after resize. These counters describe workflow
liveness, not rendering speed.

A running Ubuntu ARM64 VM with Google Chrome `151.0.7922.137` and Node `24.20.0`
reproduced the blank drawing using the existing CI launch arguments. The app
submitted 90 frames / 101 dispatches with no JavaScript errors, but the page
remained blank. This local VM differs from the x64 CI runner and does not replace
the required CI result. No VM configuration or desktop settings were changed.

An independent 64x64 WebGPU clear, with no ProGPU renderer or shaders, isolated
the failure. With `--disable-vulkan-surface`, direct canvas readback contains all
4,096 expected red pixels while the page screenshot contains zero. Removing
only that flag produces all 4,096 red pixels in both outputs, with the actual
WebGPU adapter still reporting `google / swiftshader`. Other tested driver
combinations either failed device creation or failed presentation; they were
not adopted.

The full CAD smoke with that single flag removed passes on the VM: 147 frames,
169 dispatches, visible initial and expanded-workspace screenshots, pan/zoom,
16 entity types through DXF save/reopen/resave, and 2880x1800 resize. The initial
image was inspected, including the two MTEXT columns. Thus the observed blank
page was not sufficient evidence of missing CAD geometry or an atlas defect.

The launch correction preserves separate ANGLE and WebGPU adapter selection
from the primary Chromium/Dawn references above. The headless example's
surface-disabling flag is rejected for this measured Chrome configuration.
No application algorithm, shader, raster quality, resource cache, managed/native
contract, or screenshot threshold is changed.

`eng/progpu-webgpu-presentation.mjs` now checks the clear in an independent
browser process before the CAD smoke. It retains `presentation.json`, a direct
canvas PNG, and a page PNG, and requires complete red coverage in both. The
independent process keeps its device/cache lifetime out of the application's
cold-start workload. A same-page probe experiment passed qualification but a
subsequent Linux app screenshot timed out; that experiment is not claimed as
a passing full smoke. An isolated-probe follow-up also timed out on a full-page
capture after drawing pixels were visible, so probe isolation alone is not
claimed to resolve screenshot stability.

The Linux launch argument now also preserves `CDPScreenshotNewSurface` alongside
Vulkan, matching the pinned Playwright runner's default screenshot feature
([upstream launch contract](https://github.com/microsoft/playwright/blob/main/packages/playwright-core/src/server/chromium/chromiumSwitches.ts)).
Adding a Vulkan-only enabled-feature argument must not remove that contract.
Test-only readback and one clear pass add fixed bounded
work; they are not production rendering or a performance measurement.

CI run `34196455411` initially stopped on
`WarmModernMeshSubobjectQueryAllocatesNothing` (7,312 bytes). Its isolated local
check passes, and the failed job was rerun with the zero-allocation assertion
unchanged. The retry passes CAD tests and AOT publishing, then reproduces the
blank-frame assertion with the old surface-disabled configuration. This does
not prove the intermittent allocation failure is permanently resolved.
Both allocation stability and green Linux browser CI on the final
commit remain required; a local pass does not satisfy those gates.

The final isolated-probe smoke passes on macOS (75 frames / 97 dispatches) and,
with the combined Vulkan/screenshot feature argument, on the Linux VM
(114 frames / 136 dispatches). Both complete all drawing, interaction,
save/reopen/resave, MTEXT column, and 2880x1800 resize assertions. The preflight
records 4,096 direct red pixels and all 16,384 physical screenshot pixels at
device scale 2. Linux uses RGBA8; macOS uses BGRA8. These are measured workflow
passes on the same final AOT app, not a claim of x64 CI completion or performance.

## Subsequent x64 CI screenshot stall

Run `34200318584` on `ebf4f0ee` passes CAD tests, AOT/native linking, and the
independent presentation preflight (4,096 direct / 16,384 page red pixels), then
times out at the first CAD page screenshot after 30 seconds. No browser console
error is recorded. The actual command line selects full Playwright Chromium
revision 1234 (151.0.7922.34), SwiftShader, enabled Vulkan surfaces, and
`CDPScreenshotNewSurface`. This is not evidence that the earlier blank-surface
configuration is still selected, nor that the full CAD canvas is correct.

The failure-state diagnostic previously combined DOM reads with
`canvas.toDataURL`. A stalled GPU readback could therefore discard useful DOM
state and did not prove the page's ordinary JavaScript was unresponsive. Capture
DOM state before the first screenshot, record `Browser.getVersion`, and persist
failure DOM state separately before attempting bounded canvas readback and page
capture. Preserve the original smoke failure in every case. The rendering pixel
assertions, browser selection, GPU flags, and screenshot timeout are unchanged.
This improves diagnosis; it does not resolve or waive the x64 screenshot gate.

The updated diagnostics pass syntax checking and the complete local Chromium
SwiftShader smoke against the unchanged final AOT application: 84 frames /
106 dispatches, 16 saved/reopened entity types, and a 2880x1800 framebuffer.
`pre-screenshot-state.json` and the browser version are present. The initial
state has six frames / 17 dispatches while the title still says the viewer is
starting; the title alone is not an application-ready contract. No performance
claim or screenshot-threshold change is made.

## Cold software capture deadline (2026-09-08)

Run `34202061035` passes the isolated allocation tests and AOT build, but still
times out at the first page capture. The new DOM-only diagnostics succeed:
both before capture and after the timeout they report six frames, 17 dispatches,
521,776 command bytes, and a 2560x1600 canvas. Ordinary page JavaScript is
responsive; the absence of GPU completion/readback is a separate observation.

To distinguish a browser-version problem from insufficient capture time, the
same pinned Playwright Chromium 151.0.7922.34 was staged in the existing Ubuntu
ARM64 VM, together with the final AOT application and smoke script. No VM or
system package configuration changed. CPU affinity below applies only to the
test process and its browser children. These are diagnostic workload controls,
not matched renderer performance benchmarks.

| Experiment | Result |
| --- | --- |
| Pinned Chromium, four available CPUs | Full smoke passes: 111 frames / 133 dispatches. |
| Same browser, two-CPU affinity | Full smoke passes; first drawing capture takes 28,305 ms. |
| Same browser, one-CPU affinity, original capture timeout | First screenshot fails at 30,000 ms; later canvas readback contains the drawing. |
| Same one-CPU startup, remaining 120-second deadline | First screenshot completes in 56,648 ms with 27,669 bright drawing pixels and 1,286,337 dark background pixels. |

The last experiment stops after initial pixel assertions; it does not certify
the full one-CPU editing workflow. Both complete runs retain the 16-type
save/reopen/resave checks and 2880x1800 resize gate. The canvas from the timed-out
run was visually inspected and contains the geometry and both MTEXT columns.

The first-drawing loop already declared a 120-second readiness budget but did
not pass its remaining time to
[Playwright's screenshot operation](https://playwright.dev/docs/api/class-page#page-screenshot-option-timeout).
The observed 30-second per-call default terminated that loop prematurely.
Pass only the remaining budget for each initial screenshot; never reset the
deadline on a retry. Keep all pixel counts, image quality, GPU flags, presentation
preflight, and file assertions unchanged.
`startup-capture.json` records elapsed time and the actual pixel counts.

A complete two-CPU run of that startup-only correction recorded a 37,315 ms
first capture, then failed the separate ten-second presentation-callback wait
after opening More tools. A subsequent two-CPU run with a 30-second callback
budget passes its 37,283 ms startup capture but times out at the following
full-page screenshot. The delay is therefore not confined to the first capture.

Use an explicit software-renderer correctness-test profile: when
`PROGPU_CAD_BROWSER_USE_SWIFTSHADER=1`, visual operations have a bounded
120-second budget; other runs retain 30-second screenshot/interaction budgets.
The existing 120-second startup budget applies to both profiles. Presentation
callbacks have both an in-page timer and an outer deadline, so unresponsive page
JavaScript cannot leave the test hanging. Pan and zoom each share one deadline
across presentation and screenshot retries. Keep two animation-frame callbacks,
all pixel/file checks, and the existing file-event and ten-second failure-capture
budgets; do not require extra GPU submissions from an idle scene. `result.json`
records the selected visual budget. These are explicit bounded correctness-test
waits, not interactive-performance acceptance thresholds.

This resolves a reproduced smoke-test timeout mismatch, not a production
rendering optimization or proof that all x64 CI stalls have this cause. Final
CI qualification remains mandatory, and a capture that cannot complete within
its bounded budget must still fail. Only the browser test driver changes:
managed/native renderers, canonical shaders, ABI, production startup, and scene
submission behavior are unchanged, so no paired rendering algorithm change
applies. No third-party implementation was copied.

Final-driver local validation against the unchanged final AOT application:

- macOS Chrome / SwiftShader: full smoke passes, 69 frames / 91 dispatches,
  6,774 ms initial capture, and the 120,000 ms visual profile recorded in results.
- macOS Chrome / Apple M3 Pro Metal: full smoke passes, 1,365 frames / 1,393
  dispatches, 184 ms initial capture, and the 30,000 ms visual profile recorded.
- Ubuntu ARM64 / pinned Chromium 151.0.7922.34 / SwiftShader, two-CPU process
  affinity: full smoke passes, 117 frames / 139 dispatches, 47,886 ms initial
  capture, and the 120,000 ms visual profile recorded. Initial drawing and both
  text columns were visually inspected; pan, zoom, file round trips, and resize
  pass without extending the file-event deadlines.

All three runs preserve 16 model-space entity types and the two-column MTEXT metadata
through save/reopen/resave, and finish at a 2880x1800 physical framebuffer. Frame
counts and capture times are diagnostic observations, not comparable throughput
measurements. No VM configuration was changed; the Parallels guest-execution
workflow reuses the existing shared validation stage.

### New-head CI remains unreliable (2026-09-08)

CAD run [34205114167](https://github.com/wieslawsoltes/ProGPU/actions/runs/34205114167)
passed on `bd442c45`, but the next run
[34207854625](https://github.com/wieslawsoltes/ProGPU/actions/runs/34207854625)
failed on the fit-export commit `1723c256`. Tests and AOT publish passed. The
first drawing capture completed in 100,018 ms with 27,669 visible pixels and
1,286,337 background pixels; the immediately following full-page initial
screenshot then exceeded 120 seconds. Failure-time canvas readback also timed
out. DOM state remained readable at 18 frames / 29 dispatches, 627,184 command
bytes, and a 2560x1600 framebuffer.

This contradicts treating the prior successful run as reliable Linux CI
qualification. The artifacts are retained locally under
`artifacts/progpu-cad/ci-1723-browser/` and in the workflow upload. No further
deadline increase, pixel-threshold reduction, or skipped screenshot has been
introduced. The software presentation/performance blocker remains open and
must be investigated independently of the file-serialization corrections.

### Current basic-workflow Linux qualification (2026-09-08)

The published `f78f2543` AOT app completes the full browser smoke on Ubuntu
ARM64 with pinned Chromium 151.0.7922.34 and SwiftShader, restricted to two CPUs
by process affinity. This run exercises line creation, selection, move/copy,
delete, undo/redo, save/reopen, malformed polygon-clip rejection, and recovery
by opening the valid file again. It preserves all 16 expected model-space
entity types and finishes at 2880x1800 with `malformedClipRecovery: true`.
The final screenshot was inspected: drawing geometry, both MTEXT columns,
the WIPEOUT, and the added/moved line remain visible.

Observed counters are 198 frames / 236 dispatches. The first cropped capture
takes 31,088 ms; the following full-page capture takes 18,520 ms. These are
diagnostic observations, not throughput measurements or proof of x64 CI
reliability. Capture deadlines, pixel thresholds, geometry quality, and the
independent presentation preflight are unchanged. The staged driver only logs
capture start/end times. No VM configuration or security setting was changed.
Evidence is retained under the existing shared validation directory
`cad-render-stall.2nnKTc/artifacts/progpu-cad/browser-smoke/`, with the host log
under `artifacts/progpu-cad/linux-current-two-cpu.log`; generated files remain
outside Git.

### Startup trace separates compilation from presentation waits

Two separate startup-only diagnostic runs use Chromium's documented
[startup tracing switches](https://www.chromium.org/developers/how-tos/trace-event-profiling-tool/recording-tracing-runs/).
The second also enables `disabled-by-default-gpu.dawn`, the category exposed by
the [pinned Chromium Dawn platform](https://github.com/chromium/chromium/blob/151.0.7922.34/gpu/command_buffer/service/dawn_platform.cc).
These probes deliberately terminate after the initial captures and are **not**
full smoke passes. Their traces are retained in separate `startup-trace/` and
`startup-dawn-trace/` evidence directories beside the successful smoke run.

The detailed trace contains 117,505 events. Two compute-pipeline creation calls
total 4,050.912 ms; four render-pipeline creation calls total 973.495 ms. The
longest enclosing WebGPU command-buffer flush takes 8,815.865 ms wall time but
only 19.389 ms of GPU-main-thread time. Its queue submission finishes near the
start; serializer-return events occur about 8.79 seconds later. Nested trace
durations overlap and must not be added as independent CPU costs.

This evidence distinguishes startup compilation from a later wait, but does
not identify the wait's exact cause or establish a performance improvement.
The [pinned WebGPU decoder](https://github.com/chromium/chromium/blob/151.0.7922.34/gpu/command_buffer/service/webgpu_decoder_impl.cc)
has a synchronous staging-buffer readback in its shared-image fallback; that
is a hypothesis to correlate with submission completion, not a demonstrated
diagnosis of this run. The next diagnostic should distinguish queued shader
execution from presentation/readback. Do not change raster quality, introduce
application readback, remove captures, or extend deadlines on this evidence.
Only public diagnostic contracts and observed trace behavior are used; no
third-party implementation is copied into ProGPU. Managed/native rendering,
canonical shaders, ABI, and application host scheduling remain unchanged.

### Pixel-based interaction readiness

The next x64 run,
[34216503431](https://github.com/wieslawsoltes/ProGPU/actions/runs/34216503431),
passes CAD tests and AOT publishing and gets through the initial/expanded
captures, but fails in the zoom pixel poll. Initial drawing capture takes
83,682 ms with the expected visible/background pixels. After wheel input,
waiting for two animation callbacks consumes 37,266 ms of the 120,000 ms
interaction budget; the following screenshot exhausts the remaining 82,734 ms.
Failure-time DOM counters are 54 frames / 74 dispatches, with no browser errors.

The interaction driver now starts its existing bounded pixel poll immediately
after input, without the preceding two-callback wait. The poll itself waits for
observable output, so an unchanged capture still retries and eventually fails.
Zoom and pan compare decoded RGBA pixels rather than compressed PNG bytes,
preventing an encoding-only difference from satisfying the assertion. All
captures, the 30/120-second profiles, the shared per-interaction deadline,
first-drawing thresholds, and file/edit/recovery checks remain in place.
Toolbar and final-resize presentation waits remain unchanged. This is a test
readiness correction, not a renderer performance fix or CI qualification.

An independent worker-side queue probe confirms that the published app uses
worker execution: page-only instrumentation sees no submissions. In the worker,
later batches of three render-only submissions take roughly 5.5 seconds to
report completion without repeated compute dispatches. Completion promises
cover earlier queued work and are not individual GPU timers. This further
motivates profiling steady rendering/presentation separately from startup
pipeline compilation; the instrumentation remains outside production source.

Focused validation of the revised driver: the hardware browser completes the
full smoke with 2,379 frames / 2,428 dispatches, 16 preserved entity types,
malformed-clip recovery, and a 2880x1800 final framebuffer. Two independent
negative controls omit only wheel input or only the middle-button drag; each
fails its corresponding unchanged-pixel assertion as required. `node --check`
passes. Software-rendered full runs and new-head CI remain pending; these
results do not close the Linux x64 rendering gate.
