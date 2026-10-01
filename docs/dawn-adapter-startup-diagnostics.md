# Dawn adapter startup failure diagnostics

Build [36811268913](https://github.com/wieslawsoltes/ProGPU/actions/runs/36811268913)
compiled both Windows package consumers without warnings or errors. Their first
JIT Dawn paragraph selector failed before device creation, at the original
offscreen adapter request: `Unavailable. No supported adapters`.
The [ARM64 job](https://github.com/wieslawsoltes/ProGPU/actions/runs/36811268913/job/110213085530)
and [x64 job](https://github.com/wieslawsoltes/ProGPU/actions/runs/36811268913/job/110213085631)
subsequently rendered their independent stock path probes with Microsoft Basic
Render Driver. This is distinct from hinted outline/coverage or paragraph pixel
validation; those Dawn fixtures had not started. It does not identify a missing
compiler DLL or prove that Dawn admits the stock provider's adapter.

Only the existing offscreen adapter-failure branch captures a diagnostic. The
original exception category, native status and complete original message remain
unchanged. One bounded snapshot is attached to exception data and printed to
stderr; inspection or reporting faults cannot replace the originating exception.
There is no successful-path inspection, retry, second device, additional native
library load, new selector, toggle, compiler choice or changed deadline.

The snapshot reports the exact original request and process architecture. On
Windows it borrows the already process-pinned Dawn module, reports its actual
module path, and reads at most 64 MiB for on-disk SHA-256 and original PE machine
metadata. The hash describes the current file, not a proof that mapped executable
pages are unchanged. Six named runtime modules are observed only through
[GetModuleHandleW](https://learn.microsoft.com/en-us/windows/win32/api/libloaderapi/nf-libloaderapi-getmodulehandlew)
and [GetModuleFileNameW](https://learn.microsoft.com/en-us/windows/win32/api/libloaderapi/nf-libloaderapi-getmodulefilenamew).
These observations do not retain or load a dependency or enumerate all process
modules; another thread may change a borrowed runtime module between observations.
`not loaded` does not mean missing, required or unsupported. Output is limited to
4,096 characters, sanitizes control characters and explicitly marks truncation.
PE machine values follow Microsoft's [PE format](https://learn.microsoft.com/en-us/windows/win32/debug/pe-format)
and are reported unchanged, including unknown or hybrid values; this is not
compiler architecture admission.

The public [adapter request contract](https://webgpu-native.github.io/webgpu-headers/structWGPURequestAdapterOptions.html)
requires the requested backend and fallback and permits no adapter when those
constraints cannot be satisfied. The pinned Dawn [support document](https://raw.githubusercontent.com/google/dawn/710c33013c53ab2700d332c25ff51430251a8cc4/docs/support.md)
does not establish the cause of these specific requests. WebGPUSharp 0.5.5's
published generated declarations lack an instance logging-callback layout;
the separate pinned Dawn public C++ header is not proof of the packaged binary's
native revision/layout. No guessed logging extension, foreign implementation or
vendored/system/package-cache mutation is introduced. Actual backend rejection
diagnostics and fresh unchanged full JIT/NativeAOT gates remain required.

Original controls cover bounded/sanitized formatting, exact exception preservation
even after an output fault, complete/invalid PE metadata and bounded hashing.
They do not create native providers or qualify Windows adapter availability.

## Fresh Windows failure evidence

The bounded 2026-10-01 inspection found the same adapter failure in both completed,
failed Builds below. All four jobs failed their first JIT
`--text-hinted-paragraph-dawn-render-only` selector before device creation or any
hinted rendering assertion, with `Unavailable. No supported adapters` and exit
code 127. The new request diagnostics were present; they did not replace the
original failure.

| Build / exact head | Windows Dawn text jobs |
| --- | --- |
| [36817345502](https://github.com/wieslawsoltes/ProGPU/actions/runs/36817345502), `516e877b315142142286b18c381ba6e1ac2dc054` | [ARM64 110237190543](https://github.com/wieslawsoltes/ProGPU/actions/runs/36817345502/job/110237190543), [x64 110237190552](https://github.com/wieslawsoltes/ProGPU/actions/runs/36817345502/job/110237190552) |
| [36817348725](https://github.com/wieslawsoltes/ProGPU/actions/runs/36817348725), `f9f0d00159e714eac7adfea3803bb6971b67272f` | [x64 110239775703](https://github.com/wieslawsoltes/ProGPU/actions/runs/36817348725/job/110239775703), [ARM64 110239775729](https://github.com/wieslawsoltes/ProGPU/actions/runs/36817348725/job/110239775729) |

Each request reported `backend=D3D12`, `forceFallbackAdapter=True`,
`featureLevel=Core`, `powerPreference=HighPerformance`, and the matching X64 or
Arm64 process architecture. Both `d3d12.dll` and `dxgi.dll` were observed loaded
from `C:\Windows\SYSTEM32`. `d3dcompiler_47.dll`, `dxcompiler.dll`, `dxil.dll`,
and `d3d10warp.dll` were not observed loaded. The snapshot explicitly retains
the distinction between those observations and missing or required dependencies.

The reported original provider files had these identities in both Builds:

| RID | PE machine / bytes | On-disk SHA-256 |
| --- | --- | --- |
| win-x64 | `0x8664` / 10,779,648 | `f8801b2be52bf1ddd536c72e2e8e77f757f32a85846c3505d0215ef19b444f5f` |
| win-arm64 | `0xAA64` / 11,457,536 | `68e388593559c1a5dad9537873b0377f02aaec55d5f30b5eb5246b8df9743dc6` |

These hashes and lengths match the original public runtime LFS pointers in
[WebGPUSharp commit 71899324f14353bc4bb207270ed27c62e226299e](https://github.com/EmilSV/WebGPUSharp/commit/71899324f14353bc4bb207270ed27c62e226299e),
whose message records an update to Dawn Chrome 150. The already cached
WebGPUSharp 0.5.5 package has the same Windows hashes, and its nuspec records
source commit `9a750346ff77a25eb671f630797b62100a9de926`. No package or CI artifact
was downloaded for this comparison. Original package-byte identity is not mapped
image identity, nor a receipt for the precise native Dawn source revision,
compiler feature flags or dependency search configuration. That build-provenance
gap remains; the repository's separate Dawn pin must not silently stand in for
the packaged binary's provenance.

## Pinned-source admission and next capability

At the separately recorded Dawn revision
`710c33013c53ab2700d332c25ff51430251a8cc4`,
[D3D physical-device discovery](https://github.com/google/dawn/blob/710c33013c53ab2700d332c25ff51430251a8cc4/src/dawn/native/d3d/BackendD3D.cpp#L126-L139)
returns an empty list immediately when `forceFallbackAdapter` is true, before
LUID selection, adapter enumeration or feature-level filtering. Its
[D3D12 feature-level predicate](https://github.com/google/dawn/blob/710c33013c53ab2700d332c25ff51430251a8cc4/src/dawn/native/d3d12/PhysicalDeviceD3D12.cpp#L69-L71)
returns true. The empty adapter list produces the observed
[Unavailable callback and message](https://github.com/google/dawn/blob/710c33013c53ab2700d332c25ff51430251a8cc4/src/dawn/native/Instance.cpp#L341-L354).
This is a concrete explanation at that source pin, consistent with the captured
requests; without the native build receipt it is not proof of the packaged
implementation's exact rejection path.

Compiler initialization belongs to
[D3D12 device compiler setup](https://github.com/google/dawn/blob/710c33013c53ab2700d332c25ff51430251a8cc4/src/dawn/native/d3d12/DeviceD3D12.cpp#L273-L279):
the resolved `UseDXC` toggle selects `EnsureDXC`, otherwise `EnsureFXC`. Adapter
failure before device creation is therefore not evidence that a compiler DLL
must be added. No new DLL payload, system/package-cache change, compiler toggle,
hardware retry or changed automatic default is justified by these observations.

The original explicit adapter contract provides a narrower next implementation
path. Dawn documents
[exact D3D11/D3D12 LUID selection](https://github.com/google/dawn/blob/710c33013c53ab2700d332c25ff51430251a8cc4/docs/dawn/features/adapter_options.md#L27-L29),
and its public native header declares
[RequestAdapterOptionsLUID](https://github.com/google/dawn/blob/710c33013c53ab2700d332c25ff51430251a8cc4/include/dawn/native/D3DBackend.h#L41-L45).
Microsoft's
[IDXGIFactory4::EnumWarpAdapter](https://learn.microsoft.com/en-us/windows/win32/api/dxgi1_4/nf-dxgi1_4-idxgifactory4-enumwarpadapter)
obtains the actual WARP adapter. Dawn derives CPU identity from the actual
[DXGI software flag](https://github.com/google/dawn/blob/710c33013c53ab2700d332c25ff51430251a8cc4/src/dawn/native/d3d/PhysicalDeviceD3D.cpp#L81-L92),
not an adapter name.

The locked WebGPUSharp source exposes
[RequestAdapterOptionsFFI and its extension-chain pointer](https://github.com/EmilSV/WebGPUSharp/blob/9a750346ff77a25eb671f630797b62100a9de926/gen/RequestAdapterOptionsFFI.cs),
and [the LUID SType value](https://github.com/EmilSV/WebGPUSharp/blob/9a750346ff77a25eb671f630797b62100a9de926/gen/SType.cs#L33),
but no typed LUID-selection structure or WARP acquisition helper. The current
ProGPU backend does not expose that capability either. The next aligned work is
an additive, explicit, original typed WARP/LUID selection contract with scoped
DXGI ownership, verified native layout and returned D3D12/CPU/exact-adapter
identity, failing closed without hardware or alternate-backend substitution.
The generic `CreateOffscreen(..., forceFallbackAdapter: true)` contract remains
unchanged; clearing its flag merely to admit a hardware candidate would weaken
the existing software gate. The pinned early return also means chaining a LUID
while retaining that same native flag does not admit WARP.

This update records read-only diagnosis only: no wrapper, ABI, default, assertion
or deadline changes, and no local product execution or new CI invocation. The
software capability and unchanged JIT/NativeAOT rendering gates remain unqualified.
Only a fresh exact producer Build that fully succeeds may supply artifacts or
support a merge; successful subsets of either failed Build above do not qualify.
