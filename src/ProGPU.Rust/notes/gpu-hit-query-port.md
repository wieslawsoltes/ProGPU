# Original retained GPU hit queries

| Original ProGPU source | SHA-256 |
| --- | --- |
| `src/ProGPU.Vector/GpuHitTesting.cs` | `8435e814c26761b3a3454fdf773792cdfbf31b4490f65543fd1800641b9d7411` |
| `src/ProGPU.Vector/GpuOrderedHitQueries.cs` | `a34e3c0c7c270c8df414f413d8dcce5752a5d177fac52db9f6c176f40b79bb99` |
| `src/ProGPU.Backend/GpuHitTestExecution.cs` | `0c1fa66ebe914cef0c41cd67b57af4cd252245bbe27945f495f30b934c6d03ec` |
| `src/ProGPU.Backend/GpuBuffer.cs` | `59c6f3f69a5f72221977745890d5716cb6c2c7dc21e54eaadaf74e023afa05ad` |
| `src/ProGPU.Vector/Shaders/GpuHitTesting.wgsl` | `1dbf5e9bab657323460d680e5417b6aa1f29b23656efca210520b1c4a23b3f4b` |

`GpuHitIndex` realizes one immutable `ArchiveHitIndex` on the explicitly supplied
device/queue pair. Exact 128-byte primitives, 32-byte nodes, four-byte references
and 48-byte path segments upload once; absent segments retain one original zero
record. Query records stay 40 bytes, results 32 bytes, and list storage retains
257 records for the original maximum of 256 returned hits plus summary. Empty
indices reject actual device-index construction. An empty requested result list
returns the source's default summary without dispatch.

All point, rectangle and ellipse geometry executes the unchanged canonical WGSL.
Best-point queries use flags zero and remain distinct from a one-slot list.
List queries preserve source result insertion, ties, duplicate-owner handling,
total participating primitive counts and actual traversal/precise-test counters.
The complete raw result, including unused slots, remains accessible. Copying
hits changes only the actual returned prefix of caller storage. Query coordinates
are never adjusted to manufacture a hit or replace a precise test with bounds.

Typed `HitQueryExecution` preserves Automatic, SinglePass and OrderedStages.
Automatic selects ordered stages only from the creator's explicit actual owned
D3D12/FXC or D3D12/DXC provenance. Unknown/borrowed devices keep the original
automatic single-pass behavior; callers can select either path explicitly.
OS, adapter name and compilation success are not compiler provenance. Optional
environment parsing retains original spellings and invalid-value rejection;
the engine never changes process environment, drivers or compiler defaults.

Ordered resources retain the original 32-byte header plus eight bytes per index
reference, a separate twelve-byte indirect buffer and exact binding 7. Collection
runs once in original traversal order, copies dispatch arguments from byte 16,
then runs the clip family and only actual primitive families before ordered merge.
Every dependency has its original separate compute pass. GPU arguments stay on
the GPU; no intermediate geometry readback or CPU result sort is introduced.
The original candidate overflow marker rejects before publishing any output.
Whole index/storage/workgroup limits reject insufficient ordered devices without
fallback. Original private stack, helper arithmetic and family-specific shader
entry points are not changed.

The original per-index query/output/scratch generations are reused in submission
order. Rust pending work has an independently owned readback buffer and retains
the exact index and source owners. Later queries cannot overwrite its copied
result. Shader and pipeline creation is lazy, including only selected modes and
families; failures do not publish a cached invalid pipeline. Typed metrics expose
actual index/query upload bytes, buffer sizes, passes, submissions, pipeline
creations and readback bytes. The original constant-sized stage schedule is a
stack array; warm dispatch does not allocate a CPU candidate list.

Asynchronous completion yields to browser mapping. Native hosts can drive their
actual device or request the original thirty-second synchronous mapping bound.
Abandoned work never polls, finds a global context or invokes a source callback.
The readback copy is encoded after the query in the same submission, adapting
the original separate readback submission without changing GPU dependency order.
The index/cache owns resources independently of returned views and submissions.

Architecture references are the primary links in `DESIGN.md`: Direct2D/Win2D
device and host ownership, retained Skia/WebRender boundaries and independent
Vello/Parley/DirectWrite/HarfBuzz geometry/text stages. Only ownership boundaries
inform the Rust adaptation; original ProGPU supplies all executable query logic.
No external query or geometry engine is incorporated.

Implementation only. No compilation, shader checks, tests, browser runs,
benchmarks or package validation have been performed for this port. Full 256-hit
raw output/counter/unused-slot controls, duplicates/ties, source command input,
all query modes/primitive kinds, cold/warm costs and automatic compiler policy
still require original qualification. Successful source translation is not
Windows/WARP stack safety, runtime completion or source/package parity evidence.
