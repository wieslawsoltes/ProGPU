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
`ProGpuForkPackage=true` selects the dependency's net10.0-only source build and
avoids requesting unrelated multi-target WebAssembly workloads.

Do not disable `MSBuildEnableWorkloadResolver`: a successful publish with the
resolver disabled is not proof of AOT compilation. The smoke requires the CAD
assembly's native AOT object in addition to the published site. The CI job uses
a fresh checkout, performs native AOT linking, and runs the published application.

The smoke checks startup errors, submitted frames/commands, visible scene pixels
before the initial capture, tool expansion, drawing-only pixel changes after wheel
zoom and middle-button pan, DXF download, preservation of the 15-entity inventory,
reopen and resave, and physical framebuffer resize at device scale 2. Screenshots
and DXF evidence are uploaded by `.github/workflows/cad.yml` from the ignored
`artifacts/progpu-cad/browser-smoke/` directory. This is a focused runtime gate,
not an AutoCAD pixel differential, performance benchmark, external raster-file
packaging test, or DWG browser certification.

## Applicability and remaining work

Local validation on 2026-09-08: 1,539/1,539 Release CAD tests passed; the
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
