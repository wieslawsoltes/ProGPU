# Explicit native WebGPU backend selection

`WgpuContext.NativeBackendOptions` selects a backend before ProGPU creates its
owned wgpu-native instance. This addresses the backend-choice portion of
[LibreWPF #187](https://github.com/wieslawsoltes/LibreWPF/issues/187), where a
headless Linux host initializes EGL before requesting an adapter. Selecting an
adapter only after unrestricted instance creation cannot prevent that startup.

```csharp
using var context = new WgpuContext
{
    NativeBackendOptions = new(WgpuNativeBackend.Vulkan)
};
context.Initialize(window);
```

For hosts that construct their own contexts, set `PROGPU_WGPU_BACKEND=vulkan`
before application startup. `WGPU_BACKEND=vulkan` is also accepted when the
ProGPU-specific variable is absent. A present ProGPU variable takes precedence,
including an empty value or `automatic`. Supported values are `automatic`/`auto`,
`vulkan`, `gl`/`gles`/`opengl`, `metal`, and `dx12`/`d3d12`, ignoring case and
surrounding whitespace. Lists and unknown values are errors, never automatic.
This is ProGPU startup configuration, not a claim that wgpu-native reads the alias.

Automatic preserves existing defaults: Windows restricts creation to D3D12,
Android to Vulkan and iOS to Metal; other desktop hosts retain unrestricted
creation. Explicit selection supplies exactly one backend bit. The returned
adapter must match it before device creation; unavailable choices fail without
trying another backend. High-performance/fallback-adapter and compiler preferences
remain separate. An explicit D3D12 compiler cannot be paired with another backend,
and native mobile surface restrictions remain enforced.

Both managed rendering and native C++ MIL hosts consume this same owned context
and device. Shared surfaces inherit their owner's actual device; a conflicting
explicit choice is rejected before acquiring its lifetime or creating a surface.
Borrowed browser/Dawn devices reject new explicit backend selection, because
their creator owns it. Raw C hosts continue to configure their own provider.
No public initialization signature, renderer default, shader, native library or
external device is replaced.

The implementation extends ProGPU's existing native instance-extras descriptor.
Its backend flags and unchanged field layout are the public ABI in the pinned
[wgpu-native header](https://github.com/gfx-rs/wgpu-native/blob/33133da4ec5a0174cb21539ef2d3346f75200411/ffi/wgpu.h).
No foreign implementation was copied. Parsing is startup-only O(value length);
validation, mask selection and adapter comparison are O(1), with no frame work.

## Qualification boundary

The regressions cover parsing, precedence, original platform masks, mobile/compiler
conflicts, exact descriptor fields/layout, actual-adapter checks, shared/borrowed
admission and pre-load rejection. They do not establish Vulkan device availability,
multi-window rendering, EGL thread ownership or native application parity.
The existing Linux multi-window and all Windows/default/package gates remain
required. The reported EGL abort and missing upstream error propagation stay open;
an explicit Vulkan choice is not an EGL repair. Do not close #187 from this change.

Local Release evidence: `ProGPU.Backend` builds with zero warnings/errors; 78
linked actual backend-selection and existing DX12 compiler test cases pass with
zero failures/skips. Logs are retained under `artifacts/native-backend/` and
`artifacts/shader-identifiers/backend-build.log` in the isolated worktree. These
are descriptor/admission tests, not native Vulkan/GL execution or package proof.
