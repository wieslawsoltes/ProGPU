# GPU-first compute fallback policy

Status: glyph coverage implemented and exact on the macOS Metal qualification
lane; Windows D3D12/Parallels and Linux Vulkan qualification remain required
before the policy is used as a release gate for those adapters.

## Decision

A failed or excluded compute shader does not imply a CPU fallback. ProGPU
selects the fastest qualified exact implementation for each workload in this
order:

1. native WebGPU compute shader;
2. an equivalent same-device render, fragment, vertex, mesh, or other shader
   path;
3. an intrinsic-SIMD CPU implementation;
4. the scalar CPU reference implementation.

`Fastest` is the product default. A forced path is a diagnostic and
qualification control, not permission to approximate the result. Resource or
pipeline creation failure in a forced mode fails closed.

A non-compute shader is eligible only when it preserves the workload's
observable algorithm without compute-only workgroup memory, barriers, atomics,
indirect-dispatch behavior, or storage writes unavailable to that stage. It
must stay on the same WebGPU device and operate on the retained destination;
CPU readback, CPU repacking, and per-item command submissions are not accepted
GPU fallbacks.

## Typed configuration

Set `WgpuContext.ComputeExecutionPreference` before constructing workload-owned
resources. The available values are:

| Preference | Resolved path |
| --- | --- |
| `Fastest` | Adapter-qualified native compute or exact GPU-stage fallback |
| `NativeCompute` | Native WebGPU compute only |
| `RasterShader` | Equivalent render/fragment shader only |
| `IntrinsicSimdCpu` | Hardware-vectorized CPU implementation and atlas upload |
| `ScalarCpu` | Scalar reference implementation and atlas upload |

Process-level integration gates can set `PROGPU_COMPUTE_EXECUTION` to `fastest`,
`compute`, `raster`, `simd`, or `scalar`. Aliases are accepted by
`GpuComputeExecutionPolicy`; unknown values throw rather than silently changing
the selected path. `PROGPU_BACKEND_DIAGNOSTICS=1` reports the preference,
resolved path, adapter, and backend.

The C++ engine receives one mutually exclusive typed engine flag for raster,
intrinsic-SIMD, or scalar fallback. No flag means native compute. Managed and
native hosts resolve the same `GpuComputeExecutionPath` before creating their
pipelines and atlases.

## Glyph coverage implementation

The first qualified workload is monochrome glyph-atlas coverage. Its compute
and raster entries share `GlyphRasterizer.wgsl`, the same analytic line,
quadratic, and cubic winding functions, the same 8x8 sample grid, and the same
R8 coverage quantization.

The raster fallback draws one viewport-sized triangle per cold glyph into the
retained R8 atlas inside one load/store render pass. Per-glyph viewport,
scissor, and dynamic uniform offsets replace compute dispatch coordinates. It
does not allocate a coverage storage buffer, copy a coverage buffer, read back
the GPU, or submit once per glyph. `coverage_staging_bytes` is therefore zero
for this path.

Automatic mode selects this raster path for the known Parallels D3D12 adapter
profile whose compute pipeline is not qualified. Other currently qualified
adapters retain native compute by default.

The CPU fallback uses the same analytic crossing algorithm. Managed code uses
hardware-backed `System.Numerics.Vector<T>` lanes. Native C++ uses ARM NEON or
SSE2 lanes. Curve roots and crossing positions are solved once per sample row,
then all eight independent horizontal samples accumulate winding in vectors.
The scalar path remains available as the differential oracle and as the
bounded fallback on architectures without a supported intrinsic target.

## Validation gate

All four modes must produce exact final-frame pixel parity between the managed
and C++ implementations. Run the retained positioned glyph-atlas gate with:

```bash
dotnet build src/ProGPU.Native.Benchmarks/ProGPU.Native.Benchmarks.csproj \
  -c Release --no-restore -m:1 -nr:false

for mode in fastest raster simd scalar; do
  PROGPU_COMPUTE_EXECUTION="$mode" \
  PROGPU_BACKEND_DIAGNOSTICS=1 \
  dotnet run \
    --project src/ProGPU.Native.Benchmarks/ProGPU.Native.Benchmarks.csproj \
    -c Release --no-build -- \
    --glyphs --warmup 0 --iterations 1 --sync
done
```

The gate requires:

- the diagnostic path expected for the requested mode;
- no WebGPU validation or device errors;
- `MaximumChannelDifference == 0` and `PixelsOverTolerance == 0`;
- identical managed/native FNV-1a hashes;
- zero native coverage staging bytes in raster mode; and
- SIMD/scalar differential tests for line, quadratic, and cubic outlines.

The initial Apple M3 Pro Metal qualification produced the identical final hash
`5B6EF4F70536C862` in native-compute, raster, intrinsic-SIMD, and scalar modes.
The raster path reported zero coverage staging bytes. Single-iteration timings
are deliberately not used as performance claims; representative warm/cold
distributions on each release adapter remain required before changing an
automatic selection.

## Extending the policy

Each additional compute-heavy workload must declare the semantics that make a
different shader stage eligible or ineligible, share typed resources and
constants across exact implementations, report the resolved path, and add a
final-output differential gate. CPU work with independent lanes must add an
intrinsic implementation plus a scalar oracle; a whole-buffer scalar loop
requires a documented data dependency and review.
