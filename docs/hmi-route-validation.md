# Route-editing validation record

Implementation: `55aee029576bf85a04d954a6281a45d46e5ede68`.

The source was developed against `eed2e82cdc3b171c07dc55ae634c4574bfacaf39` and transferred as an integrity-checked 24-file patch. Patch SHA-256: `e63d048769c28c096a1d73fdf2332736da02eff8b8572ef45039607d053606f2`. Temporary source-transfer workflows and payloads are removed from the final repository tree. Cleanup changes documentation/workflows, not the tested implementation.

## Executed local checks

.NET SDK 10.0.401, runtime 10.0.12, Linux x64:

| Check | Observed result |
| --- | --- |
| Baseline HMI/component/designer/Modbus/MQTT tests before this extension | 362 passed, 0 failed, 0 skipped |
| Final reflection-disabled HMI regression suite | 394 passed, 0 failed, 0 skipped |
| Reflection-disabled project/ordered-router/file-persistence executable | Passed |
| Native retained visual probe build | 0 warnings, 0 errors |
| Full Light visual/pointer executable | Passed |
| Full Dark visual/pointer executable | Passed |
| Full HighContrast visual/pointer executable | Passed |

The native runs used ProGPU's actual retained compositor and native WebGPU with **SwiftShader software Vulkan**. They are not screenshots of an HTML mockup and are not measurements of a physical GPU. Each run includes all 40 component glyphs, equipment quality/orientation, the designer, runtime input, nozzle creation/link selection, and line feedback/styles. The route extension additionally checks clicked waypoint insertion, captured drag preview and release, no intermediate document/history changes, Escape and capture-loss rollback, recovery of a pin blocked by equipment, real selected-handle pixels, arrow-key nudging and exact undo restoration.

The 32 added regressions include an independent unit-grid Dijkstra oracle for ordered length/bend routing, all nozzle-direction combinations, stable obstacle ordering, exact/collinear pins, blocked points, finite/count limits, legacy/generated JSON, detached copies, clipboard translation, reversal, screen duplication, canceled/foreign revisions, no-op gestures, locked/preview-mode edits, selective rerouting and cache admission beyond the obstacle budget.

These checks exercise the implementation but do not constitute complete accessibility, industrial certification, physical-device performance or deployed equipment qualification.

## CI reproduction

The [source integration run](https://github.com/wieslawsoltes/ProGPU/actions/runs/36689794891) commits the exact implementation before executing tests. It also runs the real OPC UA server regressions, standalone/actual desktop-gallery builds, the reflection-disabled gallery constructor, Linux NativeAOT publication/execution, Mesa software-Vulkan probes and six HMI package builds. Its final outcome must be checked in the run; the local results above do not imply every CI stage passed.

The retained workflows continue validating subsequent edits:

- `.github/workflows/hmi-designer.yml`: Windows/Linux/macOS tests, real protocol fixtures, standalone and desktop-gallery builds, gallery construction and packages.
- `.github/workflows/hmi-visual.yml`: real offscreen retained rendering and pointer checks for all three palettes on Mesa software Vulkan.
- `.github/workflows/hmi-serialization.yml`: CoreCLR reflection-disabled serialization and actual Linux/macOS NativeAOT serializer executables.

NativeAOT qualification here applies to the UI-independent HMI serialization/routing probe, not the entire desktop UI or third-party protocol stack. PLC interoperability, deployed identity/certificate systems, physical Apple Metal/Retina, Windows graphics adapters and touch hardware remain separately qualified concerns. No production endpoint connection, external equipment write, NuGet publication or PR merge was performed as part of this extension.

See [ordered route editing](hmi-route-editing.md) and [semantic diagram connections](hmi-diagram-connections.md) for implementation contracts and current routing limits.
