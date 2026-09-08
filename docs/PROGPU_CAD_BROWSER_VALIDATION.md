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
On Linux the software lane now explicitly selects Vulkan SwiftShader and
Chromium's headless Vulkan presentation flags. The previous ANGLE-only flag
selected an OpenGL driver, not necessarily the WebGPU adapter. Earlier local
"with SwiftShader" results in this document mean that flag was enabled; they
do not establish software WebGPU execution. Each run now saves `gpu.json` with
Chrome's GPU report and launch arguments so the actual driver can be audited.
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
