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

The replacement keeps Avalonia's public API contract while a context-owned
internal `ICompositionServerBackend` gives the strongly typed
`Avalonia.ProGpu` renderer persistent ownership of each target's retained
scene. The strict smoke requires a nonzero
`RetainedCompositionServerBackendRenderCount`, at least one retained scene,
and zero flattened fallback nodes. There is no runtime reflection, detouring,
IL weaving, or dynamic assembly substitution. Stack packaging inspects the
final `Avalonia.ProGpu.dll` and `Avalonia.SilkNet.dll` metadata and rejects
runtime reflection, emit, dynamic activation, assembly-load-context, and
unsafe-accessor type references. The exact-source renderer accesses the pinned
image-brush contract directly through Avalonia's signed friend assembly.

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
./integration/ProGpuAvaloniaPackageSmoke/run.sh replacement
```

The stack contains the exact nine-package ProGPU runtime closure used by the
renderer. The isolated consumer restores into a fresh NuGet package directory
from the replacement feed, while the packer verifies package content, public
ABI, and assembly identity. The native smoke then requires rendered frames, a
retained composition scene, nonzero typed server-backend renders, a real
presentation path, and zero flattened fallback nodes.

## NativeAOT lifecycle gate

The same isolated consumer can publish and execute the replacement stack as a
fully trimmed self-contained NativeAOT application:

```bash
PROGPU_PACKAGE_SOURCE=artifacts/avalonia-replacement \
PROGPU_REUSE_REPLACEMENT_STACK=1 \
PROGPU_INTEGRATION_NATIVE_AOT=1 \
PROGPU_INTEGRATION_SMOKE=1 \
./integration/ProGpuAvaloniaPackageSmoke/run.sh replacement
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

Current package, NativeAOT, test-count, and runtime measurements are generated
by the qualification workflow rather than embedded here. This prevents a
historical executable size or test count from being mistaken for evidence
about a newer build. Temporary packages, compiler intermediates, and publish
output are removed by the runner's bounded exit cleanup.

Multi-window shared-device ownership remains covered by the typed Silk.NET
contract and renderer tests. The package-only smoke intentionally stays a
minimal single-window consumer so restore, NativeAOT, presentation, and
retained-compositor failures have a small diagnostic surface.
