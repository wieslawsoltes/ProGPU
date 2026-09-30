# Graphic conventions and WYSIWYG validation

Implementation revision: `163152fe551f9a7bd63ca45fb3720f15a9af9e94`.

The implementation contains 48 original retained-vector component types, Process/HighPerformance/Schematic presentation, explicit instrument annotations and normal display bands, contextual formatting and transactional native caption editing. The coverage and limitations are documented in [graphic conventions](hmi-graphic-conventions.md). No full standards database, certification or physical-GPU performance claim is implied.

## Local executed evidence

The exact source-only patch was built and exercised on Linux using .NET SDK 10.0.401. The focused regression suite completed with **448 passed, 0 failed and 0 skipped**, explicitly disabling reflection-based JSON. This includes the existing model, diagram, waypoint, Modbus/MQTT and reviewed-command tests as well as the new graphics and inline-editor regressions.

The CoreCLR serialization probe passed with reflection disabled, including all 48 symbols, profile/annotation/band configuration, routes/pins and asynchronous project persistence. The native visual/input probe completed independently in Light, Dark and HighContrast using **SwiftShader software Vulkan** through native WebGPU and the real retained compositor.

The native probe renders each of the 48 controls and tests caption-free glyph ink, existing equipment silhouettes, bounded drawing regions, quality/state/orientation, fitted runtime commands, semantic connection creation, waypoint capture/rollback and exact undo. Additional checks exercise profile/fault pixels, F2, Unicode native input, Enter, keyboard undo, actual caption double-click and Escape. A controlled fault-state fixture compares its design and runtime crops byte for byte under identical font, placement, scale and adapter. The test exposed and verified the fix for stale grid drawing after `DesignerCanvas.ShowGridLines` changed.

IME completion, Tab/Shift+Tab navigation, focus loss, foreign document changes, cancellation, oversized native paste, retained identity, format isolation and reentrant focus ownership are covered by the focused tests. These are not a substitute for hardware input-method and assistive-technology qualification on each OS.

## CI source provenance

[Graphic conventions integration run](https://github.com/wieslawsoltes/ProGPU/actions/runs/36707610445) applies the integrity-checked source patch and commits it before any tests run. Its artifact `commit.txt` records the actual tested implementation revision above; the workflow trigger commit is a separate transfer revision. The job's individual steps and final conclusion are authoritative. A queued, skipped or failed step must not be described as passed.

The CI job runs the complete focused suite and the separate 24-test OPC UA real-loopback-server suite, builds the standalone HMI app and actual desktop-gallery portable host, constructs `VisualDesignerPage.Create()` with reflection disabled, publishes/runs a real Linux NativeAOT serializer/router executable, executes the complete three-palette native visual/input probes with **Mesa software Vulkan**, and packs all six HMI libraries. The artifact contains screenshots, adapter configuration, logs and TRX results; it contains no font files.

The cleanup revision removes only the temporary integration workflow and records this validation guide. It does not change the tested runtime, renderer, designer or tests. The permanent model/protocol, visual/pointer and serialization/NativeAOT workflows remain in place; their separate Windows/Linux/macOS results are not interchangeable with this Linux integration job.

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

NativeAOT qualification applies to the UI-independent serializer/router probe, not the entire desktop UI or third-party transport stack. Software-Vulkan timing is not physical Metal/Retina or DirectX performance. No production endpoint, external-write authorization, controller interlock, protocol configuration or NuGet publication is changed by this graphics work.
