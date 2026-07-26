# ProGPU Avalonia replacement package

This `Avalonia` package is a source-built, API-compatible replacement for
official Avalonia 12.0.5. It is intended for a private or isolated local NuGet
feed where it replaces the official package with the same package identity.
Do not publish it to NuGet.org.

The package is built from official Avalonia commit
`fee9c561ce036e8a3e8cee2397c75ca599b4790d` plus the reviewed clean-room ProGPU
compositor seam. Its public assembly identities and runtime API are checked
with strict .NET ApiCompat rules, and its packed reference facades are compared
with the official 12.0.5 package before the artifact is accepted.

The replacement keeps Avalonia's public API contract while allowing the
strongly typed `Avalonia.ProGpu` renderer to retain and replay compositor draw
lists. There is no runtime reflection, detouring, IL weaving, or dynamic
assembly substitution. Stack packaging inspects the final
`Avalonia.ProGpu.dll` and `Avalonia.SilkNet.dll` metadata and rejects runtime
reflection, emit, dynamic activation, assembly-load-context, and unsafe-accessor
type references. The exact-source renderer accesses the pinned image-brush
contract directly through Avalonia's signed friend assembly.

The retained compiler uses bounded, typed local scene pages and 4 KiB
dirty-range GPU buffer uploads by default. Set
`PROGPU_AVALONIA_INCREMENTAL_SCENE_PAGES=0` before process startup to disable
both optimizations for an exact-binary comparison.

The exact-source native lane can retain Avalonia's existing platform windowing
and use WebGPUSharp/Dawn only for rendering:

```bash
PROGPU_AVALONIA_BACKENDS=source-progpu-native \
PROGPU_AVALONIA_PAGE_FILTER='^Buttons$' \
./tools/profile-avalonia-controlcatalog.sh
```

On macOS, `tools/build-avalonia-native-dawn.sh` compiles the pinned native
source in temporary Xcode DerivedData and installs a current-architecture
dylib whose CAMetalLayer drawable is IOSurface-importable by the pinned Dawn
binary. The strict benchmark performs direct IOSurface rendering plus Metal
shared-event synchronization.

On Windows, Avalonia Win32 continues to own the window and supplies its typed
`HWND`; Dawn selects a compatible D3D12 adapter and presents directly to that
window. On Linux, Avalonia X11 supplies its typed `XID`; ProGPU owns the Xlib
display connection needed by Dawn's Vulkan surface. Both lanes acquire the
Dawn swapchain texture and render into it directly. They do not use Silk.NET
windowing, a CPU framebuffer, cross-device texture sharing, or a presentation
copy. The strict profiler rejects any result whose `PresentationPath` is not
the expected `DawnD3D12HWND`, `DawnVulkanXlib`, or
`DawnMetalIOSurface`.

The implementation is based on the public
[`INativePlatformHandleSurface`](https://docs.avaloniaui.net/api/avalonia/platform/inativeplatformhandlesurface)
contract, Dawn's typed
[native surface implementation](https://dawn.googlesource.com/dawn/+/55623705bef897b77888c3c9410c94cbaa3c1e4e/src/dawn/native/Surface.cpp),
and WebGPUSharp 0.5.5's
[native surface descriptors](https://github.com/EmilSV/WebGPUSharp/blob/9a750346ff77a25eb671f630797b62100a9de926/README.md).
Adapter selection uses the surface compatibility field before device
creation, then format/alpha capabilities are validated before configuration.
The code contains no reflection, symbol probing, dynamic activation, or
private platform-object inspection.

Build and validate the private package with:

```bash
./tools/pack-avalonia-progpu-replacement.sh
```

Build the complete local replacement stack and verify a package-only consumer
with:

```bash
./tools/pack-avalonia-progpu-stack.sh
PROGPU_REUSE_REPLACEMENT_STACK=1 \
PROGPU_INTEGRATION_BUILD_ONLY=1 \
./integration/ProGpuPackageApp/run.sh replacement
```

The stack contains the exact eight-package ProGPU runtime closure used by the
renderer. The isolated consumer verifies the SHA-512 identity of all eleven
replacement-controlled packages (Avalonia, renderer, Silk.NET host, and eight
runtime packages), then the native smoke requires rendered frames, a retained
composition scene, and zero flattened fallback nodes.

## NativeAOT lifecycle gate

The same isolated consumer can publish and execute the replacement stack as a
fully trimmed self-contained NativeAOT application:

```bash
PROGPU_PACKAGE_SOURCE=artifacts/avalonia-replacement \
PROGPU_REUSE_REPLACEMENT_STACK=1 \
PROGPU_INTEGRATION_NATIVE_AOT=1 \
PROGPU_INTEGRATION_SMOKE=1 \
./integration/ProGpuPackageApp/run.sh replacement
```

The first runtime probe proved that ordinary Silk.NET assembly discovery is
not a valid AOT contract: trimming removed both the GLFW window platform and
input-platform discovery paths. ProGPU's Silk.NET bootstrap now calls the
public typed `GlfwWindowing.RegisterPlatform()` and
`GlfwInput.RegisterPlatform()` APIs before Avalonia creates its dispatcher or
any window. The integration packages explicitly depend on the two GLFW
implementation packages, so NuGet and the trimmer see the same concrete
contract. No reflection discovery, private implementation type, linker
descriptor, dynamic dependency, or runtime activation workaround is used.

The clean-room design used only the public contracts recorded by Silk.NET
2.23.0 at commit `94605142f7b7bd6e69c9201e8e721d245c69eb7e`:

- [typed GLFW window registration](https://github.com/dotnet/Silk.NET/blob/94605142f7b7bd6e69c9201e8e721d245c69eb7e/src/Windowing/Silk.NET.Windowing.Glfw/GlfwWindowing.cs);
- [typed GLFW input registration](https://github.com/dotnet/Silk.NET/blob/94605142f7b7bd6e69c9201e8e721d245c69eb7e/src/Input/Silk.NET.Input.Glfw/GlfwInput.cs);
- [.NET NativeAOT deployment contract](https://learn.microsoft.com/dotnet/core/deploying/native-aot/).

Adopted: explicit typed registration at the platform composition root and
host-RID AOT publishing from the already isolated restore. Rejected:
Silk.NET's assembly-attribute discovery, preserving private constructors,
reflection metadata roots, and a build-only result without a rendered-frame
lifecycle check.

On macOS arm64, the exact eleven-package SHA-512 gate published a
22,696,432-byte native executable. It rendered 15 frames, reported one
retained ProGPU composition scene and zero fallback nodes, then exited
normally. NativeAOT analysis still reports upstream warnings in dormant
Silk.NET loader discovery and ImageSharp paths; no ProGPU or Avalonia.ProGpu
assembly produced an AOT warning, and the typed runtime path completed. All
temporary packages, compiler intermediates, and publish output are removed by
the runner's bounded exit cleanup.

The post-change regression gates pass 2,475 ProGPU core tests, 89 Avalonia
renderer/package tests, and 42 Silk.NET integration tests. The final ordinary
package-only refresh restored the exact SHA-512-validated bytes, rendered 21
frames, observed one retained scene, and reported zero fallback nodes. The
packaged renderer and Silk.NET host also pass the runtime-reflection metadata
audit.

## Package-only multi-window lifecycle gate

Run the shared-device owner/borrower disposal-order gate with:

```bash
PROGPU_PACKAGE_SOURCE=artifacts/avalonia-replacement \
PROGPU_REUSE_REPLACEMENT_STACK=1 \
PROGPU_INTEGRATION_MULTI_WINDOW_SMOKE=1 \
./integration/ProGpuPackageApp/run.sh replacement
```

The app uses typed `WindowImpl`/`WgpuContext` diagnostics to prove that each
new window joins the same device domain. It then disposes the original
device-owning window and a later borrowing window in turn. After each actual
`DisposedTask` completion, the survivor must retain an active WebGPU context
and produce new ProGPU frames. The gate also requires one retained composition
scene and zero flattened fallback nodes.

The final exact-package run observed two shared-device pairs and rendered 24
initial frames, 22 frames after owner disposal, and 20 after borrower
disposal. Both survivor-health checks passed without timeout. A bounded
`PROGPU_INTEGRATION_PROFILE_HOLD_SECONDS` option keeps the surviving window
alive for Xcode Instruments without changing ordinary smoke timing; values
outside 1 through 120 seconds are ignored.
