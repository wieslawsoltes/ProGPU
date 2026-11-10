# Native semantic scene admission

`NativeScene::new` now owns the complete original ordered CPU scene admission:
header framing, resource payload/dependency checks, unique command IDs, command
metadata and selected payload checks, state references, nested pictures, and
balanced ordinary/materialized layers. It preserves original diagnostic offsets,
status categories, header-dependent failure metrics and caller-sized metric writes.
The immutable admitted byte generation retains all raw payloads, inactive fields,
optional unknown records and source identities. Typed resource views decode the
original pointer-free records without casts; sealed `WireRecord` codecs prevent
external record-size contracts from changing their interpretation.

`NativeScene::hit_index` imports the actual admitted hit resource into the shared
`ArchiveHitIndex`/`GpuHitIndex` storage without rebuilding its quadtree. Explicit
field copies preserve original node/primitive/reference order, raw segment kinds,
reserved parameter bits and float bits. The complete actual `NativeScene` owner
survives the parser caller, GPU index and pending readback. No source command or
Visual ownership is fabricated; empty-page/device/execution gates remain with the
existing query engine. This is O(P+N+I+S) cold decoding and owned typed storage,
with no additional hot-query transformation or geometry reconstruction.

The shared header reader precedes each semantic resource loop directly. The raw
`NativeSceneEnvelope` is still a separate transport API and is not used as an eager
semantic prepass: doing so would change original first-error ordering. Resource
generation comparisons retain the original byte/metadata identity rules.

Admission deliberately keeps the original stages: ordinary analytic, path,
geometry, image and related execution payloads do not gain new eager checks that
the native scene validator did not perform. Engine/provider/device/target gates
and a native-stream renderer adapter remain separate work. Typed record inventories
expose incomplete deferred records explicitly; admission is not GPU qualification.
The shader-resource child reuses the existing exact raw v1–v6 parser and retains
its owned bytecode/WGSL allocation, unlike the native validator's scratch-only
operation. Allocation and scalar-family details are recorded in the companion
`native-scene-semantic-payload-port.md` and `native-scene-scalar-admission-port.md`.

Original source SHA-256:

| Source | SHA-256 |
| --- | --- |
| `src/ProGPU.Native/include/progpu_native.h` | `bf31eab3ad25f079e36391c4d3072bbd15eeab8a94f8cbd04bb9e2d15145e441` |
| `src/ProGPU.Native/src/Scene/progpu_native_scene.cpp` | `a9630f05490363aeb82883a3901394187f6d36985d592faad88b1aa30975ee1c` |
| `src/ProGPU.Native/src/Scene/progpu_native_scene.hpp` | `781fc4fcae8bb40576dd9ad15abd353e28fe7e49d32f1e5993c3ee5f3d7fb298` |
| `src/ProGPU.Native/src/Backend/progpu_native_source_path.hpp` | `e0482207d3e5b5cafadb40599d218a5bb85071a5338c6b03086f75c604d9821b` |
| `src/ProGPU.Native/src/Backend/progpu_native_vertex_mesh.hpp` | `ec6cd5443cfbff74b1261e026e030a0b42728b0bc3199d5a495bfaa09abb3861` |
| `src/ProGPU.Native/src/HitTesting/progpu_native_hit_testing_execution.cpp` | `c06ef5424a2fa04d50394095c723a4eb69592d2bc7a9ba7d0455ec212de99a9f` |

Implementation and touched-source formatting only. No build, test, browser,
benchmark, shader validation or source qualification was run.
