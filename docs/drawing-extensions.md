# Application drawing extensions

Applications and libraries can implement custom GPU drawing using only the
`ProGPU.WinUI` NuGet package and its transitive dependencies. No ProGPU source
changes, signing key, friend assembly, reflection, or registration in a built-in
extension table are required.

The low-level `ICompositorExtension`, `Compositor.RegisterExtension(int, ...)`, and
`DrawingContext.DrawExtension(int, ...)` APIs were already public. The typed API
adds application identity, window lifecycle registration, local bounds and payload
type checking while using those same compilation and GPU submission paths.

## Register once, record many times

Declare a shared definition in the application or extension library:

```csharp
public static readonly DrawingExtension<MyBatch> MyDrawing =
    new("My application artwork", static () => new MyPipeline());
```

`MyPipeline` implements the existing public `ICompositorExtension` contract.
`MyBatch` is an application-owned reference type containing retained drawing data.
Using a class avoids boxing or copying a struct payload for each command.

Register on every window that can display the drawing, before calling `Activate`:

```csharp
var window = new Window();
window.RegisterDrawingExtension(MyDrawing);
window.Activate();
```

Registration can also occur after activation, outside a compilation or rendering
callback. Re-registering the same definition is idempotent. Its factory runs once
per compositor, never per command or frame. Registration before activation does
not create a GPU device. When a mobile host suspends and recreates the window's
renderer, the window registers a fresh instance automatically.

Record from a control's existing `OnRender` override or a `DrawingContext` extension
method in the application:

```csharp
context.DrawExtension(MyDrawing, new Rect(0, 0, width, height), batch);
```

An optional final `Matrix4x4 transform` argument has the same semantics as the
legacy command. Recording emits one ordinary `DrawExtension` command containing
the definition's integer identity, bounds, original payload reference and transform.
It does not invoke the factory, resolve a service, copy an array, create a wrapper,
upload a buffer, or cross a native boundary.

For standalone/offscreen use, call
`compositor.RegisterDrawingExtension(MyDrawing)`. To configure an existing instance,
use `compositor.GetDrawingExtension(MyDrawing)` and cast to your pipeline type.

## Ownership and invalidation

- Share definitions, not pipeline instances. A factory must return a fresh instance
  for each compositor, and that instance must reject resources from other devices.
- Create shaders, pipelines and buffers lazily in the instance. Retain and reuse
  them across frames. The compositor calls the existing lifecycle hooks and
  disposes instances implementing `IDisposable` when its renderer is disposed.
- The command retains the payload reference. Keep it stable from recording through
  submission. Advance its generation and invalidate the owning visual whenever
  pixels change. Do not mutate it inside `OnRender` or behind a retained cache.
- `Compile` prepares CPU command metadata. `TryPrepareDrawCall` can prepare GPU
  resources before the active render pass. `Render` encodes into that existing
  pass. Keep preparation/submission batching and resource leases in the extension.
- Use the compositor's physical target dimensions, render format and current
  sample count. Offscreen targets, transforms, clipping, opacity and masks remain
  the extension author's responsibility under the existing callback contract.
- Definitions are process-local identities, not portable serialized IDs. Numeric
  IDs generated for the typed API must not be reused with the legacy API.
- Register on the owning UI/render thread outside callbacks. This is a startup
  operation; definitions cannot be unregistered while retained commands reference
  them. Dispose the compositor/window to end their instance lifetimes.

## Shader resources from a package consumer

Put static WGSL in the application's `Shaders` directory and embed it explicitly
in its project. Repository-wide build properties are not needed:

```xml
<ItemGroup>
  <PackageReference Include="ProGPU.WinUI" Version="YOUR_PACKAGE_VERSION" />
  <EmbeddedResource Include="Shaders/*.wgsl"
                    LogicalName="$(AssemblyName).Shaders.%(Filename)%(Extension)" />
</ItemGroup>
```

Load once with `ShaderResource.Load<MyPipeline>("Artwork.wgsl")` into a static
readonly string. Shader files must document their algorithm and time/space costs.
The [unsigned consumer fixture](../eng/fixtures/drawing-extension-package-consumer)
contains a complete original shader and pipeline. Its verification script copies
it outside the repository and restores only NuGet packages, proving that no source
checkout properties or privileged assembly access are involved.

## Design, provenance and backend applicability

This is an original wrapper over existing ProGPU-owned contracts in
`ICompositorExtension.cs`, `Compositor.cs`, `RenderCommand.cs`, and WinUI `Window.cs`.
It preserves their callback interfaces, command representation and resource
ownership. No foreign implementation was copied or translated.

Primary-source design review (2026-09-05):

| Reference | Decision for this API |
| --- | --- |
| [Direct2D custom effects](https://learn.microsoft.com/en-us/windows/win32/direct2d/custom-effects) and [Win2D offscreen drawing](https://microsoft.github.io/Win2D/WinUI3/html/Offscreen.htm) | Adopt a definition/factory separate from a device-owned instance. Keep initialization separate from drawing; use ProGPU's existing typed callbacks instead of COM or a property bag. |
| [Skia design documents](https://skia.org/docs/dev/design/) and [text architecture](https://skia.org/docs/dev/design/text_overview/) | Keep retained CPU recording separate from GPU resources. Registration does not initialize fonts, shape text or invalidate text caches. |
| [WebRender](https://github.com/servo/webrender) | Retain existing display-list reuse and visibility culling. No extra traversal or worker preparation is introduced for registration. |
| [Vello](https://github.com/linebender/vello), [Parley](https://github.com/linebender/parley), [HarfBuzz](https://harfbuzz.github.io/what-does-harfbuzz-do.html) | Keep rendering and reusable CPU shaping/layout separate. A custom drawing registration is not a new shaping engine or scene compiler. |

The architecture audit covers startup/factory timing, scene reuse, visibility,
cache identity, uploads, workers, GPU batching, DPI/subpixel behavior, font fallback,
variable fonts and device loss. Only application registration/instance recreation
changes. The existing renderer handles the other mechanisms unchanged; extensions
retain the same obligations for target-aware pipelines, generation invalidation and
owned resources. No text, rasterizer, shader algorithm or native boundary changes.

Managed/native applicability: `ProGPU.Scene` owns the managed callback compositor
used by WinUI Desktop, iOS and Browser. The C++ renderer exposes a pointer-free C
scene ABI, not a WinUI window or a managed render-pass callback host. Its
`GpuPictureNativeSceneCompiler` supports specified built-in extensions and explicitly
rejects arbitrary application extension commands; this wrapper does not change that
contract or transport an object/delegate through C. See
`src/ProGPU.Scene.Native/GpuPictureNativeSceneCompiler.cs`,
`NativePictureCommandCapability.cs`, and `src/ProGPU.Native/include/progpu_native.h`.
There is no new GPU algorithm to port, no canonical shader change in the renderer,
and no generated wire declaration change. Native custom callback support would be
a separate ABI feature with ownership and submission requirements of its own.

## Validation

On macOS 26.6 / Apple M3 Pro with .NET 10, the core Release suite passes 3,814
tests, including five new registration/recording/lifecycle/native-rejection tests.
All 240 headless Release rendering tests also pass.
The package-only fixture restores 13 locally packed ProGPU dependencies outside
the source tree and builds unsigned with zero warnings. The optional `--gpu` run
renders the original packaged shader through both APIs: all 16,384 compared RGBA8
channels match, with 661 draws per path and no retained payload uploads.

Three alternating, same-binary Release runs after JIT and GPU warmup:

| Run | Direct recording p50, ns | Typed recording p50, ns | Legacy submit + wait p50/p95/p99, ms | Typed submit + wait p50/p95/p99, ms |
| --- | ---: | ---: | --- | --- |
| 1 | 62.177 | 63.555 | 1.796 / 6.413 / 7.744 | 1.823 / 7.585 / 8.606 |
| 2 | 60.322 | 63.222 | 1.836 / 7.460 / 7.957 | 1.822 / 6.726 / 8.284 |
| 3 | 64.084 | 66.588 | 1.787 / 6.509 / 7.885 | 1.802 / 6.421 / 7.885 |

Each recording path allocates zero bytes, emits one command, copies zero payload
bytes and makes zero native calls. The inlined typed wrapper has a measured
1.4–2.9 ns median recording cost in this fixture. It adds no GPU pass, draw, resource,
upload or per-frame callback. Submission medians differ by at most about 1.5%; tails
vary with host scheduling. These are serialized completion latencies, not display
FPS or an assertion that every timing sample is identical.

The fixture and commands are checked in. Logs, exact-binary hashes and Instruments
exports live under `artifacts/public-drawing-extensions/` in the validation checkout.
Time Profiler captured 3,234 samples and Metal System Trace recorded the complete
alternating workload, with a 6,455,296-byte peak Metal allocation. Both instrumented
runs completed the same pixel/draw checks. Some managed JIT frames remain raw
addresses; the trace is not used to assign nanosecond wrapper costs. Exported tables
were parsed and gzip round trips/hash-verified before removing the two raw `.trace`
bundles at the user's request. No runtime performance logging is added to ProGPU.
`eng/progpu-pack.sh` runs the package consumer gate for portable/all packaging; set
`PROGPU_EXTENSION_GPU_TEST=1` on a WebGPU-capable host to include GPU validation.
