# Explicit Dawn system WARP adapter

This is an optional Windows startup capability, not a new default or an automatic
renderer/compute fallback. `DawnGpuContext.CreateSystemWarpOffscreen()` selects
Microsoft's actual system WARP adapter and returns the ordinary managed Dawn
context only after verifying D3D12, CPU/software identity and the exact adapter
LUID. It performs one adapter request and one device request, without a hardware
retry. `SystemWarpAdapterLuid` exposes the verified typed identity, not a native
handle. Generic `ForceFallbackAdapter`, compiler defaults and existing factories
remain unchanged.

The companion/header preparation, package controls and source controls are
authored but have not been executed. Neither original-header compilation,
NativeAOT, actual WARP device creation, GPU readback/completion, package consumers
nor application behavior is qualified by this source checkpoint.

## Original public contract and provenance

The companion calls the public Microsoft
[IDXGIFactory4.EnumWarpAdapter](https://learn.microsoft.com/en-us/windows/win32/api/dxgi1_4/nf-dxgi1_4-idxgifactory4-enumwarpadapter)
and consumes original Dawn
[D3DBackend.h](https://dawn.googlesource.com/dawn/+/01249a97332468dbdd6cf5edb8dd7bae77875de5/include/dawn/native/D3DBackend.h):
the actual `RequestAdapterOptionsLUID` constructor and the owned
`GetDXGIAdapter` result. No extension enum/layout, C++ constructor, COM vtable or
FFI extension record is recreated in ProGPU. The small ProGPU-owned C boundary
borrows original WebGPU handles/callback descriptors and returns a future ID and
typed LUID components. The source-generated managed imports are startup-only.

WebGPUSharp 0.5.5's Windows DLLs were independently matched byte-for-byte to
[dawn-m150-01249a9](https://github.com/EmilSV/webgpu-dawn-build/releases/tag/dawn-m150-01249a9),
produced by the original successful eight-job
[Build 28411907227](https://github.com/EmilSV/webgpu-dawn-build/actions/runs/28411907227).
The full Dawn revision is `01249a97332468dbdd6cf5edb8dd7bae77875de5`; the original
builder revision is `a63b42357c8c081cf6a1ace312670b2bb61680c6`.
[inputs.json](../eng/dawn-system-warp/inputs.json) pins each original ZIP's asset
ID, size and SHA-256, plus the actual DLL/import-library hashes and machine types.

| Original provider | DLL bytes | Actual PE/COFF machine |
| --- | ---: | --- |
| `win-x64` | 10,779,648 | `0x8664` (x64) |
| `win-arm64` | 11,457,536 | `0xAA64` (pure ARM64) |

Both original generated C headers have SHA-256
`32f8063fa2aa5977da27d8a39479581ac834e6cb0cf39f2f9bd6de928a998e5d`
and CRLF line endings. The release has the C header and architecture-specific
import library, but not the complete public C++ header closure. Preparation obtains
the exact original source and invokes its
[dawn_json_generator.py](https://dawn.googlesource.com/dawn/+/01249a97332468dbdd6cf5edb8dd7bae77875de5/generator/dawn_json_generator.py)
with `headers,cpp_headers`. The generator's actual target is `headers` despite
its help text listing `dawn_headers`. The original
[DEPS](https://dawn.googlesource.com/dawn/+/01249a97332468dbdd6cf5edb8dd7bae77875de5/DEPS)
pins Jinja2 `c3027d884967773057bf74b957e3fea87e5df4d7` and MarkupSafe
`4256084ae14175d38a3ff7d739dca83ae49ccec6`; those repositories are the Python
package roots. The script checks them out under their original import names and
passes those roots to the original generator. It rejects any generated C header
whose bytes differ from the successful original release. Original generated C++,
chained-struct and proc-table headers are required; none is locally reconstructed.

Headers/generator dependencies remain private external build inputs, not ordinary
ProGPU implementation or distributed runtime assets. Import-library machine checks
follow Microsoft's [PE/COFF archive and import-header specification](https://learn.microsoft.com/en-us/windows/win32/debug/pe-format).
The wrapper is original ProGPU startup code using public dependency contracts;
no third-party renderer implementation is incorporated.

## Explicit preparation and packaging

`eng/build-dawn-system-warp-windows.ps1` is a standalone Windows CI
preparation entry point, independent of the existing native renderer CMake/jobs.
It requires PowerShell 7.2+, Python, CMake 3.28+, Ninja, clang-cl with the target MSVC
ABI, and the matching Windows SDK/MSVC link environment. It checks committed,
unchanged owned source inputs; it does not modify upstream headers or fetch/build
the entire Dawn dependency tree.

For each RID, provide two fresh, absolute, nonoverlapping external directories:
`-BuildDirectory` for private original inputs/intermediates and `-OutputDirectory`
for the new `<payload-root>/<rid>/native` publication. Neither directory may be
inside any source checkout, a drive root, an existing directory or a reparse-point
path. Use one volume so final directory publication can remain a move. No recursive
cleanup, system/package-cache writes or same-name overwrite occurs. Failed inputs,
generation, compilation, architecture checks or publication retain private evidence
but cannot publish a successful payload.

The original ZIP is length/hash-checked before its exact three entries are read.
The C header, DLL and import library are hash-checked; every real import/object
member and each DLL must have the selected machine. Only successful companion
compilation and actual output PE validation can create the new publication. It
contains exactly `progpu_dawn_system_warp.dll`, `progpu-dawn-system-warp.json` and
`Dawn-LICENSE.txt`. The manifest records the companion bytes/hash, original pins,
full source revision and explicit unqualified status. It is a corruption/provenance
receipt, not a signature or permission to load untrusted native code.

Ordinary Dawn packing excludes optional native payloads. Explicit
`ProGpuIncludeDawnSystemWarp=true` with an absolute
`ProGpuDawnSystemWarpPayloadRoot` requires both RID publications; the read-only
validator rejects missing/extra files, receipt/hash/machine mismatches and missing
notices before NuGet packing. The published target defaults
`ProGpuEnableDawnSystemWarp=false`. When explicitly enabled with `win-x64` or
`win-arm64`, it copies only those three selected files for build/publish, keeps
native files outside single-file bundles and rejects caller-owned same-name items.
It never replaces WebGPUSharp's native asset or chooses an adapter. No WARP DLL,
DXC runtime, original provider DLL, import library or generated header is packaged.

## Runtime ownership and remaining gates

Explicit availability performs bounded startup receipt/PE/SHA-256 checks, loads
the original provider and companion, and checks the actual executing companion's
imported provider module identities against the inspected modules. P/Invoke/module
references remain process-pinned; borrowed module identity handles are never
released. A failed binding freezes this optional capability and cannot unload a
cached import or cause fallback. Missing optional files do not change other paths.
`IsSystemWarpNativeLibraryAvailable()` is therefore a native startup operation,
not a pure file-existence query and not device/runtime qualification.

The real original LUID extension selects WARP with generic fallback **false**:
this original Dawn D3D backend rejects generic forced fallback before processing a
LUID. The selected original adapter must independently report D3D12/CPU; its owned
DXGI adapter must be software and match both the captured and freshly queried
system WARP LUID before a device is requested. New adapter/device request userdata
holds separate managed/native uses through success or shutdown cancellation. A
failed wait ends only the managed use; callback userdata cannot be freed while
native work still retains it. The creating factory keeps device-loss userdata
until complete instance shutdown, drains all acquired owners and preserves the
first setup error. Existing waits and deadlines are not lengthened or bypassed.

Actual original-header compilation/linking on both Windows architectures and the
actual managed FFI/provider binding, failed-request cancellation, device-loss and
NativeAOT paths still require runtime evidence. Existing independent package/RID,
ordered-query, DX12, full-capacity/raw-result, deadline and application assertions
remain mandatory. A selected system WARP adapter or a successful companion Build
would not prove GPU readback completion or permit default-policy changes.

The `Dawn system WARP` workflow now schedules separate native Windows x64 and
ARM64 jobs. Each builds the companion from the pinned original headers and then
publishes independent JIT and NativeAOT consumers. Each consumer creates and
disposes two devices, verifies retained D3D12/CPU/LUID identity, and clears a 7x3
RGBA target to two independently specified colors. All pixels and 17 untouched
caller-tail bytes must match after actual mapped readback. A 120-second child
process bound cannot turn an incomplete callback into success. These authored
controls have not yet produced a passing receipt. They use project references
and explicitly copied companion files, not the optional NuGet asset-selection
path, and do not replace the complete package or application gates.

The companion's imported-provider check is distinct from proving a host's custom
WebGPUSharp P/Invoke resolver. Custom resolver/load-context binding and failure
cancellation remain open integration gates; a normal-runner readback cannot
qualify those configurations.
