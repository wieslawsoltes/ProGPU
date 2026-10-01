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
