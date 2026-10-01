# HMI desktop host validation

The shared gallery's **Visual Designer → HMI** entry uses the same `HmiDesignerHost` as the standalone sample. The reported reflection-disabled JSON exception is fixed in the HMI serializer with generated metadata; it does not require enabling reflection for the desktop application.

## Portable desktop target without Apple workloads

The desktop host normally advertises both `net10.0` and `net10.0-macos`. A build with `-f net10.0` still restores its multitarget graph, which can discover the optional Apple media project's iOS/macOS workload requirements. For the portable desktop executable, restrict only the host:

```sh
dotnet run --project src/ProGPU.Samples.Desktop/ProGPU.Samples.Desktop.csproj \
  -c Release -f net10.0 -p:ProGpuSamplesDesktopPortableOnly=true
```

`ProGpuSamplesDesktopPortableOnly` is consumed only by `ProGPU.Samples.Desktop.csproj`. It leaves normal target selection unchanged when omitted, does not disable analyzers or JSON trimming behavior, and does not remove the native Apple target from ordinary builds. It selects the existing portable desktop host; it does not add native AVFoundation support to that target.

Do not use a global `-p:TargetFrameworks=net10.0` override as a replacement. That property propagates into dependencies and can retarget restore for the `netstandard2.0` XAML compiler/source generator, causing missing-target errors. The HMI build matrix explicitly checks those dependency target frameworks before building the real desktop host.

## Focused probes

```sh
dotnet run --project tests/ProGPU.Hmi.GallerySmoke/ProGPU.Hmi.GallerySmoke.csproj -c Release
dotnet run --project tests/ProGPU.Hmi.SerializationSmoke/ProGPU.Hmi.SerializationSmoke.csproj -c Release
```

GallerySmoke constructs the actual `VisualDesignerPage.Create()` path with JSON reflection disabled. SerializationSmoke also exercises project round-tripping, all 40 symbols, appearance, runtime, recipes and file persistence. The serialization workflow separately publishes and runs Linux/macOS NativeAOT executables. That small core probe is not a claim that the whole desktop application or industrial protocol stack is NativeAOT-qualified.

See [visual studio and retained rendering tests](hmi-visual-studio.md) and [HMI architecture](hmi-designer.md). Software-Vulkan screenshots and model/protocol tests do not establish physical Apple Metal/Retina performance or plant commissioning.
