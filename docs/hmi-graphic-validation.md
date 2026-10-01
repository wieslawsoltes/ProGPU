# Graphic conventions and WYSIWYG validation

Runtime/designer implementation revision: `163152fe551f9a7bd63ca45fb3720f15a9af9e94`.
Matched-physical-origin framebuffer fixture: `b4a982eca03b5f6700830301c3b06ef32a4ee5a6`.

The implementation contains 48 original retained-vector component types, Process/HighPerformance/Schematic presentation, explicit instrument annotations and normal display bands, contextual formatting and transactional native caption editing. The coverage and limitations are documented in [graphic conventions](hmi-graphic-conventions.md). No complete standards database, certification or physical-GPU performance claim is implied.

## Local executed evidence

The exact source-only patch was built and exercised on Linux using .NET SDK 10.0.401. The focused regression suite completed with **448 passed, 0 failed and 0 skipped**, explicitly disabling reflection-based JSON. This includes the existing model, diagram, waypoint, Modbus/MQTT and reviewed-command tests as well as the new graphics and inline-editor regressions.

The CoreCLR serialization probe passed with reflection disabled, including all 48 symbols, profile/annotation/band configuration, routes/pins and asynchronous project persistence. The full native visual/input probe completed independently in Light, Dark and HighContrast using **SwiftShader software Vulkan** through native WebGPU and the real retained compositor.

The native probe renders all 48 controls and tests caption-free glyph ink, existing equipment silhouettes, bounded drawing regions, quality/state/orientation, fitted runtime commands, semantic connection creation, waypoint capture/rollback and exact undo. Additional checks exercise profile/fault pixels, F2, Unicode input, Enter, keyboard undo, actual caption double-click and Escape. A controlled fault-state fixture compares the complete design and runtime crops byte for byte under identical font, physical placement, scale and adapter. The test exposed and verified the fix for stale grid drawing after `DesignerCanvas.ShowGridLines` changed.

IME completion, Tab/Shift+Tab navigation, focus loss, foreign document changes, cancellation, oversized native paste, retained identity, format isolation and reentrant focus ownership are covered by the focused tests. These are not a substitute for hardware input-method and assistive-technology qualification on each OS.

## CI execution and framebuffer contract

[Graphic conventions integration run](https://github.com/wieslawsoltes/ProGPU/actions/runs/36707610445) applied and committed the integrity-checked source before testing. Its artifact `commit.txt` records implementation revision `163152fe551f9a7bd63ca45fb3720f15a9af9e94`; the workflow trigger commit is a separate transfer revision.

The run passed **448 focused tests and 24 OPC UA real-server tests**, the CoreCLR serialization probe, standalone sample and actual desktop-gallery portable-host builds, actual reflection-disabled gallery-page construction, real Linux NativeAOT publication/execution and all six package builds. Both application builds reported zero warnings and zero errors. It did not pass the complete visual stage: its first Light fixture compared different physical framebuffer origins, `(364,204)` and `(100,120)`. Mesa readback differed by one channel level at four pixels (eight channel bytes), while the local SwiftShader run happened to be exact at both origins.

The corrected fixture positions the independent runtime view at the designer's actual physical origin and asserts that their integer-pixel origins agree before comparison. It retains strict byte equality over the entire 260 by 240 crop: no tolerance, image normalization, pixel masking or renderer fallback was introduced. This qualifies design/runtime visual identity under the same display conditions, not universal translation invariance or equality between unrelated adapters. The production graphics were not altered to make this test pass.

The permanent **HMI visual and pointer regression** workflow runs the complete probe separately in all three palettes using Mesa software Vulkan. Each artifact records the actual source revision, SDK and Vulkan ICD alongside PNGs and logs. Its individual stages and final conclusion are authoritative; a queued, skipped or failed stage is not a passing result. Temporary source-transfer files and the one-off integration workflow have been removed.

The permanent HMI model/protocol matrix independently tests Windows, Linux and macOS; those platform results are separate from Linux native visual validation. NativeAOT qualification applies to the UI-independent serializer/router, not the entire desktop UI or third-party transport stack.

## Reproduce

```sh
dotnet test tests/ProGPU.Hmi.Tests/ProGPU.Hmi.Tests.csproj -c Release
dotnet test tests/ProGPU.Hmi.OpcUa.Tests/ProGPU.Hmi.OpcUa.Tests.csproj -c Release
dotnet run --project tests/ProGPU.Hmi.SerializationSmoke/ProGPU.Hmi.SerializationSmoke.csproj -c Release
dotnet build samples/HmiDesigner/HmiDesigner.csproj -c Release
dotnet build src/ProGPU.Samples.Desktop/ProGPU.Samples.Desktop.csproj -c Release -f net10.0 -p:ProGpuSamplesDesktopPortableOnly=true
dotnet run --project tests/ProGPU.Hmi.GallerySmoke/ProGPU.Hmi.GallerySmoke.csproj -c Release
```

With an explicitly configured Vulkan adapter and an installed font:

```sh
PROGPU_WGPU_BACKEND=vulkan dotnet run --project tests/ProGPU.Hmi.VisualSmoke/ProGPU.Hmi.VisualSmoke.csproj -c Release -- /path/to/font.ttf artifacts/hmi-visual Dark
```

Repeat the complete visual command in a separate process for Light and HighContrast. The optional `--graphics-only` fourth argument selects only the new conventions/inline-editor fixture for focused debugging; CI deliberately runs the full default probe, not that subset.

Software-Vulkan timing is not physical Metal/Retina or DirectX performance. No production endpoint, external-write authorization, controller interlock, protocol configuration or NuGet publication is changed by this graphics work.
