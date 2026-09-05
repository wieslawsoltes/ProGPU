# ProGPU CAD retained material and texture research

Date: 2026-09-01

This record covers the clean-room design of retained CAD scalar materials and
diffuse images in the managed C# and native C++ renderers. No third-party
implementation source was copied or translated. The implementation provenance
is the existing ProGPU-owned `Mesh3DExtensionPipeline`, native semantic external
image resources, `Native3D.wgsl`, and CAD raster-image resolver.

## Primary contracts consulted

- Autodesk's [common entity group codes](https://help.autodesk.com/cloudhelp/2021/ENU/AutoCAD-DXF/files/GUID-3610039E-27D1-4E23-B6D3-7E60B22BB5BD.htm)
  define group 347 as an optional hard pointer to a material and define its
  absence as ByLayer. Autodesk's [LAYER record](https://help.autodesk.com/cloudhelp/2015/ENU/AutoCAD-DXF/files/GUID-D94802B0-8BE8-4AC9-8054-17197688AFDB.htm)
  gives a layer its own group-347 material pointer, while
  [Apply Materials](https://help.autodesk.com/cloudhelp/2025/ENU/AutoCAD-Core/files/GUID-62385979-8E5E-4F20-AA5D-5820AE49EB6B.htm)
  confirms that ByLayer is the object default and applies the layer material.
- Autodesk's [MATERIAL DXF schema](https://help.autodesk.com/cloudhelp/2024/ENU/AutoCAD-DXF/files/GUID-E540C5BB-E166-44FA-B36C-5C739878B272.htm)
  is authoritative for ambient/diffuse/specular factors, gloss, opacity,
  translucence, self illumination, diffuse image source/blend, projection,
  tiling, transform, and auto-transform flags.
- Microsoft's [Direct2D API overview](https://learn.microsoft.com/en-us/windows/win32/direct2d/the-direct2d-api)
  and [resource-domain guidance](https://learn.microsoft.com/en-us/windows/win32/direct2d/resources-and-resource-domains)
  separate CPU/device-independent identity from expensive device-dependent
  bitmap/brush resources and require same-device use. Win2D's
  [device-loss guidance](https://learn.microsoft.com/en-us/windows/apps/develop/win2d/handling-device-lost)
  requires resources and all references to the old device to be recreated.
- Skia's public [`SkImage` contract](https://api.skia.org/classSkImage.html) and
  [`SkParagraph` overview](https://skia.org/docs/user/modules/skparagraph/)
  were checked for immutable image identity and for separation from reusable
  text layout. Material images belong to rendering resources; they must not
  invalidate or duplicate unrelated shaping/layout results.
- WebRender's [rendering overview](https://firefox-source-docs.mozilla.org/gfx/webrender/webrender.html),
  its [upstream repository](https://github.com/servo/webrender), and the
  [interning/resource profiler categories](https://github.com/servo/webrender/blob/main/webrender/src/profiler.rs)
  informed the split between immutable scene identity, interned image identity,
  visibility/batching, and render-time GPU resource caches.
- Vello's public [scene image API](https://github.com/linebender/vello/blob/main/vello/src/scene.rs)
  and maintainer-authored [image resource design](https://github.com/linebender/vello/issues/176)
  separate cheap CPU image identity from render-time atlas/binding policy.
- The [WebGPU specification](https://www.w3.org/TR/webgpu/) was used for device
  ownership, bind-group resource lifetime, validation, and device-loss rules.
- [Parley](https://github.com/linebender/parley) and
  [HarfBuzz](https://github.com/harfbuzz/harfbuzz) were examined as required.
  They own text layout/shaping and provide no material-image contract, so no
  material behavior was adopted from them. This change leaves text caches,
  fallback, shaping, glyph upload, DPI, and subpixel policy untouched.

## Mandatory cross-engine architecture audit

- Startup and lazy initialization: Direct2D/Win2D device resources, Skia
  images, WebRender resources, and Vello images all support keeping cheap CPU
  identity apart from device realization. ProGPU adopts that split: snapshot
  and scene compilation do no image I/O or GPU initialization, and a typed
  source realizes a lease only for submission.
- Shaping/layout reuse, fallback fonts, variable-font state, DPI, subpixel
  positioning, and glyph upload: SkParagraph, Parley, and HarfBuzz keep these
  in the text domain. A diffuse image does not participate in any of their
  cache keys, so ProGPU deliberately leaves every text result and glyph cache
  reusable and unchanged.
- Retained display/scene reuse and visibility: WebRender and Vello retain scene
  image identity separately from render resources. ProGPU interns material and
  image identity in the immutable CAD generation, resolves it before batching,
  and retains the resulting managed records or native page. Existing scene and
  viewport culling remain authoritative; invisible content does not acquire a
  per-primitive interop path.
- Cache keys, eviction, and demand-driven upload: the snapshot key is bounded
  CPU material/image metadata, while the source owns decode and device-cache
  policy. ProGPU neither creates another unbounded bitmap cache nor evicts a
  host-owned source. Texture generation and WebGPU device domain are validated
  when acquiring/binding a lease, and unchanged bindings retain their native
  page and bind groups.
- Worker preparation and GPU organization: a host may decode/warm a source off
  thread under its existing contract, but scene compilation never initiates
  asynchronous work. Changed generations compile in bounded batches and cross
  the native boundary once; fragments add at most one diffuse sample to the
  fixed three-light shader rather than introducing per-face calls or compute
  dispatches.
- Device loss: Direct2D resource domains, Win2D loss handling, and WebGPU device
  ownership require device-dependent objects to be discarded. ProGPU keeps the
  immutable material/image identity, releases old leases/views/bind groups and
  sentinel resources, and reacquires from the replacement `WgpuContext`.

## Adopted ProGPU design

- Resolve material identity once during immutable CAD snapshot compilation:
  explicit material, otherwise ByLayer, and ByBlock inherited through an
  insert-like expansion; unresolved special values fall back to Global.
- Intern normalized scalar material records and image identities. The snapshot
  stores only bounded CPU metadata and a source path/handle; it never decodes an
  image, performs file I/O, or creates a GPU object.
- Resolve an image through `ICadMaterialTextureSourceResolver` before retained
  scene replay. A missing image fails soft to the scalar diffuse material. A
  resolved image uses an `IProGpuTextureLeaseSource`; the submission path owns a
  typed lease and validates the consuming `WgpuContext` device domain.
- Compile scalar color/factors, opacity, gloss, self illumination, authored or
  bounded generated Planar/Box/Cylinder/Sphere UVs, entity-extents scaling,
  diffuse transform, and Tile/Crop/Clamp policy once per changed scene
  generation. Stable replay performs no CAD lookup or file work.
- Managed replay consumes `ProGpuTextureMaterial`. Native replay adds one
  pointer-free external IMAGE resource per interned texture and references its
  scene-local resource index from the fixed-size mesh record. The C++ renderer
  resolves `(resource_id, generation)` to a retained same-device view and owns
  material bind groups until the page is invalidated.
- Replacing the external-image table invalidates only cached image/3D bindings
  and render bundles. Device loss releases the bindings, sentinel resources,
  pipelines, and retained page, while the immutable CPU scene remains usable
  for recreation on a new device.

## Rejected alternatives

- File paths, strings, decoded pixels, process pointers, or texture handles in
  the stable scene mesh record: these violate the pointer-free ABI and device
  ownership contract.
- Per-mesh/per-face P/Invoke or per-frame image resolution: this violates the
  one-update/one-render boundary and stable-replay allocation contract.
- Storing a GPU texture in the CAD snapshot: this couples immutable document
  content to one device and prevents correct loss/recreation.
- Silently accepting an out-of-range, non-image, embedded-image, missing, stale,
  or cross-device native reference: validation rejects it transactionally.
- Replacing the existing managed/native three-light model with a reduced
  texture-only approximation: both backends preserve the same scalar material,
  visual-style, opacity, self-illumination, addressing, and diffuse blend rules.

## Complexity and validation contract

Snapshot and scene compilation are `O(E + M + T + V + I)` time and bounded
storage for entities, interned materials, texture identities, vertices, and
indices. Native binding creation is `O(B)` per changed 3D page for `B` mesh
batches. Stable replay retains the scene buffers and bind groups, performs one
filtered texture sample per textured fragment, and performs no managed upload
or allocation when the generation and external binding table are unchanged.

Matched regressions cover explicit, ByLayer, and nested ByBlock resolution;
scalar factors; texture interning; authored UV transform; generated face UVs;
missing-image fallback; native external-image resource typing; mesh reference
validation; fixed ABI size; shader resource audit; managed crop/image pixels;
lease invalidation/disposal; and real native WebGPU pipeline creation.

The final Release binaries were measured locally on macOS 26.6/.NET 10.0.5
with 256 retained Mesh3D batches, 3 warmups, and 24 camera-only frames. The
modified record/shader replay produced p50 1.3579 ms, p95 7.2055 ms, and p99
16.4248 ms with zero managed allocation in camera update, render, validation,
and compositor breakdowns. Stable replay visited no models, uploaded no
geometry/records/indices, uploaded one 144-byte uniform, and issued one command
buffer/submission. The ignored raw JSON is reproducible with:

```sh
dotnet run --project src/ProGPU.CAD.Benchmarks/ProGPU.CAD.Benchmarks.csproj -c Release --no-build -- --mesh3d-replay-batches 256 --warmup 3 --iterations 24 --output-json artifacts/progpu-cad-material-mesh3d-replay.json
```

This work adds functionality rather than claiming a performance optimization,
so no before/after Instruments improvement claim is made. The GPU image fixture
uses explicit source-texture readback and output pixels; the native build also
created the final Metal pipelines and passed its live sample and nine-test C++
suite.
