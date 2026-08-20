# Dawn Linux VM rendering investigation

Date: 2026-08-20  
ProGPU baseline: `eab6754b` (`0.1.0-preview.53`)  
WebGPUSharp: `0.5.5`, Dawn aligned with Chromium M150

## Outcome

Dawn is a viable native WebGPU backend for ProGPU, and it removes the need for
a locally modified wgpu-native binary. It does not, however, provide hardware
acceleration on the measured Linux ARM64 virtual machine. Dawn selects the
Vulkan CPU adapter (`llvmpipe`) because the VM exposes no hardware Vulkan
adapter. The virtual OpenGL adapter cannot be used by the current package or by
the Core WebGPU renderer:

- WebGPUSharp's Linux Dawn binary is built with Vulkan enabled and desktop GL
  and GLES disabled;
- Dawn's GL backends support WebGPU Compatibility mode, whereas ProGPU's
  renderer currently requires Core mode;
- current Dawn requires desktop OpenGL 4.4 or OpenGL ES 3.1, while this VM
  exposes virgl OpenGL 4.0 and OpenGL ES 3.0.

The investigation did find and fix a real Dawn presentation lifetime defect.
After the fix, the bounded sample renders and presents correctly for the entire
measurement instead of losing its surface after the first frame.

## Test system

| Component | Observed value |
| --- | --- |
| Guest | Ubuntu 24.04 ARM64, kernel 7.0.0-29 |
| VM | Parallels on Apple silicon |
| Session | GNOME Wayland with XWayland presentation |
| Virtual OpenGL | virgl, desktop GL 4.0, GLES 3.0 |
| Vulkan | Mesa `llvmpipe`, CPU adapter only |
| Guest logical processors | 4 |
| Bounded process RSS | 327-346 MB |

No long renderer suite was run in this constrained desktop session. Validation
used single-threaded project builds and one 60-frame warmup plus 120-frame
measurement process with a 30-second hard timeout.

## Runtime and package audit

The WebGPUSharp 0.5.5 Linux ARM64 native object is
`webgpu_dawn.so`. Its dynamic runtime closure includes:

- `libc++.so.1`;
- `libc++abi.so.1`;
- `libunwind.so.1`.

Those libraries were not present in the clean guest runtime closure. The
current [WebGPUSharp Dawn build script](https://github.com/EmilSV/webgpu-dawn-build/blob/main/build_dawn.ps1)
explicitly selects libc++ and enables only Vulkan on Linux:

```text
DAWN_ENABLE_DESKTOP_GL=OFF
DAWN_ENABLE_OPENGLES=OFF
DAWN_ENABLE_VULKAN=ON
CMAKE_CXX_FLAGS=-stdlib=libc++
```

Consequently, selecting `BackendType.OpenGL` or `BackendType.OpenGLES` cannot
discover an adapter with the distributed Linux binary. Merely changing ProGPU
adapter preference cannot change that result.

Upstream Dawn does build Vulkan, desktop GL, and GLES implementations on Linux
when enabled. Its current GL implementation validates a minimum of OpenGL 4.4
or GLES 3.1 and reports support for WebGPU Compatibility feature level. See
[Dawn's Linux backend defaults](https://github.com/google/dawn/blob/main/CMakeLists.txt)
and [the GL physical-device validation](https://github.com/google/dawn/blob/main/src/dawn/native/opengl/PhysicalDeviceGL.cpp).

## Presentation failure and fix

### Root cause

`DawnNativeWindowSource.CreateXlib` opens and owns a dedicated X display
connection. The desktop context factory attaches a Dawn surface inside a
`using` scope and then disposes the source. Before this change,
`DawnNativeWindowSource.Dispose` immediately called `XCloseDisplay`, even
though the Dawn presentation surface still referred to that display.

The first swapchain acquisition succeeded because it had already been created.
Every acquisition after the first present returned `Lost`, triggering a
reconfiguration attempt on every frame. A debugger changed teardown timing
enough for the process to exit normally, but did not restore presentation.

### Implementation

Each surface creation now acquires an idempotent lifetime lease from its native
window source. Disposing the source prevents new surfaces but defers release of
owned native resources until the last surface lease is disposed. A presentation
surface releases its Dawn surface before releasing the native-resource lease.

This ordering applies to Xlib display ownership and Cocoa layer ownership while
remaining harmless for borrowed Win32, Wayland, Android, and Metal handles.

The Dawn-created `WgpuContext` now also carries the actual adapter name, backend,
type, vendor/device identifiers, driver description, and compatible-surface
requirement. An opt-in `PROGPU_DAWN_PRESENTATION_DIAGNOSTICS=1` trace reports at
most the first 32 acquisitions and configurations, so diagnostics cannot create
an unbounded log in the frame loop.

## Bounded measurement

Both measurements used the same 1280x800 Basic Input page, 60 warmup frames,
120 measured frames, uncapped application scheduling, and Dawn FIFO
presentation.

| Metric | Before lifetime fix | After lifetime fix |
| --- | ---: | ---: |
| Adapter | Vulkan `llvmpipe` CPU | Vulkan `llvmpipe` CPU |
| First acquisition | `SuccessOptimal` | `SuccessOptimal` |
| Following sampled acquisitions | `Lost` | `SuccessOptimal` |
| Measured surface configurations | 119 | 0 |
| Scene-cache hits | 0 / 120 | 115 / 120 |
| Present time | 0.0000 ms (no presentation) | 16.7075 ms |
| Render time | invalid workload | 1.7242 ms |
| Compositor time | invalid workload | 1.7819 ms |
| Total frame time | invalid workload | 18.9176 ms |
| Wall throughput | invalid 686.99 FPS | 49.92 FPS |
| Process RSS | 327.4 MB | 346.0 MB |
| Exit | timing-dependent failure/false completion | normal, exit 0 |

The pre-fix throughput is explicitly invalid: almost every frame skipped GPU
rendering after swapchain acquisition failed. The post-fix value is the first
valid Dawn result on this VM. FIFO present dominates the steady frame time, and
all rendering is performed by a CPU Vulkan implementation.

## Downstream WPF presenter audit

The consuming WPF presenter had two independent backend-integration defects:

- it called the concrete `Wgpu` object for acquire, view creation, present, and
  release instead of the context's backend-neutral `Api` abstraction;
- it released the acquired texture view but never released the acquired surface
  texture handle.

The downstream Dawn branch routes those calls through `IWebGpuApi` and balances
both the texture-view and texture references, including the non-success
acquisition path. This removes a per-frame native reference leak for the current
backend and makes the hot presentation path compatible with `DawnWebGpuApi`.
It intentionally does not add Dawn as a shipping WPF dependency while the Linux
native runtime closure and hardware-adapter gate remain unresolved.

## Can Dawn improve GPU rendering in this VM?

Not with the VM's currently exposed graphics capabilities.

| Candidate | Result | Reason |
| --- | --- | --- |
| Distributed Dawn Vulkan | Functional but CPU-rendered | Only `llvmpipe` Vulkan is exposed |
| Dawn desktop GL enabled upstream | Adapter still unsuitable | VM GL 4.0 is below Dawn's 4.4 minimum; GL is Compatibility-only |
| Dawn GLES enabled upstream | Adapter still unsuitable | VM GLES 3.0 is below Dawn's 3.1 minimum; GLES is Compatibility-only |
| ANGLE over guest Vulkan | CPU-rendered | Guest Vulkan resolves to `llvmpipe` |
| Hardware Vulkan exposed by VM | Preferred future path | Dawn can select a Core hardware adapter without renderer changes |

The bottleneck is therefore the guest graphics contract, not the managed
WebGPU bridge. Dawn can improve correctness, diagnostics, portability, and
packaging, but no adapter-selection policy can synthesize the missing guest GPU
features.

## Recommended work

### ProGPU

1. Keep the surface lifetime lease and actual adapter diagnostics.
2. Add an explicit hardware-adapter policy at application integration points:
   reject `AdapterType.Cpu` when hardware acceleration is required, then use the
   application's supported software renderer instead of silently running a
   complex GPU renderer on `llvmpipe`.
3. Keep the renderer on WebGPU Core. A Compatibility renderer would require a
   separate shader/resource design and should only be considered after a
   representative feature and performance study.
4. Allow FIFO/Immediate/Mailbox selection only after querying supported present
   modes. The measured 16.7 ms FIFO wait is expected presentation throttling,
   not renderer CPU cost.

### WebGPUSharp / Dawn packaging

1. Make the Linux native package self-contained or use the standard distro C++
   runtime. The current undeclared libc++ dependency prevents clean deployment.
2. If Compatibility workloads are desired, publish Linux binaries with desktop
   GL and GLES enabled and generate the
   `RequestAdapterOptionsGetGLProc` binding. This does not unblock ProGPU Core
   on the measured VM.
3. Record the exact Dawn commit and CMake feature matrix in package metadata so
   backend availability is auditable without reverse engineering the binary.

### VM qualification

The useful acceptance gate for future VM updates is:

1. `vulkaninfo` reports a non-CPU adapter;
2. Dawn adapter diagnostics report `Vulkan` and an integrated/discrete/virtual
   GPU rather than `Cpu`;
3. 180 bounded frames complete with zero unexpected surface reconfigurations;
4. warmed scene-cache hits are retained;
5. matched wall time and RSS improve over the supported software-renderer
   fallback.

Until the first two conditions pass, additional renderer micro-optimization
cannot produce real GPU acceleration in this guest.
