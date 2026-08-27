# GPU-first compute fallback policy

Status: glyph coverage implemented and exact on the macOS Metal, Windows
D3D12/Parallels, and Linux Vulkan/llvmpipe qualification lanes. A physical
Linux Vulkan adapter remains required before making a hardware-wide Vulkan
performance claim.

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

A forced native-compute request for the known-incompatible Parallels D3D12
profile throws `NotSupportedException` after adapter selection and before any
glyph compute pipeline, uniform buffer, coverage buffer, bind group, or command
encoder is created. A diagnostic override therefore cannot turn a qualified
fallback into a WebGPU validation-error cascade or device abort.

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
SSE2 lanes. Curve roots and crossing positions are solved once per subpixel
scanline and reused across the complete glyph row; they are not recomputed for
every destination pixel. All eight independent horizontal samples then
accumulate winding in vectors. The native lane count uses NEON/SSE2 masks
without spilling the winding vectors to temporary arrays, while managed code
reuses pooled crossing storage and precomputed vector lane indices. The scalar
path remains available as the deliberately independent differential oracle and
as the bounded fallback on architectures without a supported intrinsic target.

## Validation gate

All four modes must produce exact final-frame pixel parity between the managed
and C++ implementations. Run the retained positioned glyph-atlas gate with:

```bash
dotnet build src/ProGPU.Native.Benchmarks/ProGPU.Native.Benchmarks.csproj \
  -c Release --no-restore -m:1 -nr:false

for mode in fastest compute raster simd scalar; do
  PROGPU_COMPUTE_EXECUTION="$mode" \
  PROGPU_BACKEND_DIAGNOSTICS=1 \
  dotnet run \
    --project src/ProGPU.Native.Benchmarks/ProGPU.Native.Benchmarks.csproj \
    -c Release --no-build -- \
    --glyphs --warmup 0 --iterations 1 --sync
done
```

The extended macOS/Linux native build runs this matrix automatically. The
Windows lane runs the same forced routes, bounds the deliberately slow scalar
oracle to one glyph in the VM, and requires the known Parallels native-compute
incompatibility to fail with its typed pre-resource exception and no WebGPU
device error. The benchmark project binds its copied native library to
`PROGPU_NATIVE_BUILD_DIR`, so a clean custom compiler build cannot silently be
tested against a stale default artifact.

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

The Windows ARM64 qualification at exact ProGPU commit `a1fd8b2b` rebuilt both
native providers with MSVC strict warnings, passed all 11 CTests, and completed
the entire D3D12 smoke/profile/package lane on `Parallels Display Adapter
(WDDM)`. Automatic mode resolved to the raster shader, the stabilized
managed-picture differential reported zero native and managed allocations and
zero coverage staging on replay, and the independent glyph oracle retained the
exact `5B6EF4F70536C862` hash. The direct raster glyph frame measured 0.8156 ms
native and 0.6884 ms managed with zero coverage staging; the retained SIMD
frame measured 1.8295 ms native and 5.0372 ms managed and necessarily uploaded
247,808 cold coverage bytes. These single-frame values qualify path selection,
not a general performance claim.

Moving curve-root work from every pixel to every subpixel scanline reduced the
same Windows SIMD end-to-end cold qualification from approximately 123 seconds
to 67 seconds (about 45%) while preserving exact pixels. Forced native compute
then failed closed with the typed incompatibility exception and emitted no
WebGPU validation or device errors.

`--rerasterize-glyphs` is the component-performance form of the glyph gate. It
increments the native content revision for every render so each measured frame
must rebuild and upload the 247,808-byte coverage batch; without this option,
the timing interval intentionally measures retained replay after coverage has
already been generated. A representative command is:

```bash
PROGPU_COMPUTE_EXECUTION=simd \
dotnet run \
  --project src/ProGPU.Native.Benchmarks/ProGPU.Native.Benchmarks.csproj \
  -c Release --no-build -- \
  --glyphs --rerasterize-glyphs --warmup 3 --iterations 30 --sync
```

Implementation `516eb3d7` adds an exact conservative Bézier control-hull test
before quadratic or cubic root solving. A scanline outside the segment's
control-point Y range cannot intersect the curve, so the SIMD fallback avoids
unused square-root, power, and trigonometric work while boundary scanlines keep
the original winding rules. On Apple M3 Pro, macOS 26.6, and .NET 10.0.5, four
alternating Release runs per variant (30 rerasterized measured frames after
three warmups) reduced the median of per-run native-submission p50 from
1.8217 ms to 1.3916 ms (-23.6%) and synchronized-frame p50 from 3.6040 ms to
3.0045 ms (-16.6%). Submission p95 fell from 2.9429 ms to 2.3009 ms and frame
p95 from 5.1773 ms to 4.4856 ms. All 240 measured baseline/candidate frames
retained zero pixel difference and hash `5B6EF4F70536C862`. These figures
qualify the CPU fallback on this machine; they do not change the GPU-first
automatic policy.

Intrinsic follow-up `e6ab073e` compiles each quadratic/cubic segment's
control-point Y hull and Y-polynomial coefficients once per rerasterized
frame. The eight subpixel scanlines then retain the same conservative reject,
root solver, crossing order, winding rules, and scalar oracle without
recomputing that invariant curve data. Four alternating pre-change/candidate
Apple M3 Pro Release runs, each with three warmups and 30 forced-SIMD
rerasterized frames, reduced the median of per-run native-submission p50 from
1.1648 ms to 1.0533 ms (-9.6%) and synchronized-frame p50 from 2.7528 ms to
2.5981 ms (-5.6%). Median submission/frame p95 improved from 2.0873/4.3461 ms
to 1.4839/4.0934 ms. Every measured frame and all forced compute, raster,
SIMD, and scalar qualification routes remained byte-exact at
`5B6EF4F70536C862`; the 11-test native/Dawn suite and strict x86_64 SSE2 syntax
compile also pass.

Intrinsic implementation `bf20bd66` collects the eight Y-subscanline crossing
spans for one raster row before visiting X. Each pixel pair therefore builds
its four NEON/SSE2 sample-position vectors once, resets only the integer
winding accumulators between subscanlines, accumulates all 64 coverage samples,
and writes quantized output directly. Crossing order, strict comparisons,
floating-point sample expressions, and the scalar oracle are unchanged. The
single retained crossing arena reserves the conservative eight-scanline,
three-roots-per-segment bound once per frame; the former temporary covered-row
buffer and final output pass are removed.

Four alternating 30-frame Apple M3 Pro A/B runs per variant at 1x DPI reduced
the median-of-run native-submission p50 from 1.0469 ms to 1.0199 ms (-2.6%)
and synchronized-frame p50 from 2.6249 ms to 2.5889 ms (-1.4%). At 2x DPI,
where coverage work is larger, submission p50 fell from 1.9498 ms to 1.7884 ms
(-8.3%) and frame p50 from 3.5588 ms to 3.3814 ms (-5.0%). All 480 measured
baseline/candidate frames were exact at `5B6EF4F70536C862` (1x) or
`706B261418EC5C3B` (2x). The full native/Dawn suite passes 11/11, all five
execution-policy routes remain exact, and strict x86_64 SSE2 syntax compilation
passes. Windows ARM64 MSVC rebuilt both libraries under `/W4 /WX`, passed all
11 tests, and reproduced the full forced-NEON D3D12 hash
`5B6EF4F70536C862`; this cold VM run is correctness, not timing, evidence.

Two subsequent NEON experiments were measured and deliberately rejected. A
four-pixel batch halved crossing broadcasts but increased live-vector pressure
and wasted work on narrow glyph-row tails; in three 120-frame grouped runs its
median submission p50 regressed from 1.0566 to 1.0940 ms at 1x and from 1.7831
to 1.8802 ms at 2x. A lower-pressure packed-byte coverage accumulator deferred
horizontal reduction until all eight scanlines, but its corresponding medians
also regressed from 1.1445 to 1.1821 ms and from 1.9788 to 2.0358 ms. Short
alternating runs had mixed tail-latency wins, so neither candidate met the
no-regression release rule. Every candidate frame remained byte-exact at the
same 1x/2x hashes, the native/Dawn suite stayed 11/11, and the SSE2 syntax gate
passed. The source therefore retains the qualified two-pixel, per-scanline
reduction implementation rather than committing benchmark-negative SIMD code.

The next accepted two-pixel optimization keeps those pressure and tail
properties while reducing the work for each crossing. NEON and SSE2 compare
instructions already produce all-one lanes for covered samples. Because the
winding visitor only publishes direction `+1` or `-1`, the intrinsic path now
subtracts a positive-direction comparison mask or adds a negative-direction
mask directly. This removes the per-crossing direction broadcast and four mask
instructions without changing sample positions, comparison strictness,
crossing order, accumulator width, horizontal reduction, or quantization. The
scalar implementation remains independent.

Four alternating Apple M3 Pro Release A/B runs per variant, each with three
warmups and 120 forced-SIMD rerasterized frames, reduced median-of-run native
submission p50 from 1.3871 to 1.1183 ms at 1x DPI (-19.4%) and from 1.8757 to
1.6719 ms at 2x DPI (-10.9%). Synchronized-frame p50 changed from 3.2270 to
2.6386 ms (-18.2%) at 1x and from 3.4555 to 3.3438 ms (-3.2%) at 2x. Median
submission/frame p95 improved from 2.6408/5.3165 to 1.7334/4.2798 ms at 1x and
from 2.9520/5.5350 to 2.7839/5.2307 ms at 2x. All 960 measured frames remained
byte-exact at `5B6EF4F70536C862` (1x) or `706B261418EC5C3B` (2x). The complete
ten-test local native suite passes and strict Clang x86_64 compilation covers
the paired SSE2 branch. These results qualify the intrinsic fallback on this
Apple adapter without changing the GPU-first automatic policy.

A subsequent empty-scanline branch experiment was exact but rejected. It
skipped winding reset/reduction when a Y subscanline had no crossings. Four
alternating 120-frame runs per variant retained identical current-scene hashes
at 1x and 2x, and improved median submission/frame p50 at 2x from
3.0416/6.5538 ms to 2.8003/6.1728 ms. At 1x, however, submission p50 regressed
from 1.6761 to 1.7931 ms (+7.0%) and frame p50 from 5.3414 to 5.3760 ms
(+0.6%). The predictable branch therefore does not satisfy the cross-profile
no-regression rule, and the source retains unconditional per-scanline vector
reset/reduction.

An ARM64 horizontal-reduction experiment was also exact but rejected. It
replaced the qualified pairwise NEON lane reduction with two `vaddvq_u32`
horizontal additions. Four alternating 120-frame runs per variant retained
zero pixel difference and hashes `5B6EF4F70536C862` at 1x and
`706B261418EC5C3B` at 2x. At 1x, submission p50 improved only from 1.1301 to
1.1172 ms while synchronized-frame p50 regressed from 4.5174 to 4.6368 ms and
both p95 metrics worsened. At 2x, submission/frame p50 regressed from
2.1180/5.9028 ms to 2.4746/6.2609 ms (+16.8%/+6.1%). The source therefore
retains the lower-latency pairwise reduction on Apple M3 Pro; architecture
intrinsics are performance candidates, not assumptions.

The accepted odd-width tail checkpoint stops evaluating a discarded second
pixel at the end of an odd glyph row. Full pairs continue through the
qualified 16-sample NEON/SSE2 kernel; the final pixel uses a dedicated
8-sample intrinsic kernel with identical sample positions, winding tests, and
integer quantization. Four alternating 120-frame runs per variant remained
byte-exact at `5B6EF4F70536C862` (1x) and `706B261418EC5C3B` (2x). Median
submission/frame p50 improved from 1.7587/5.3875 ms to 1.6904/5.0735 ms at 1x
(-3.9%/-5.8%) and from 2.1352/6.1027 ms to 2.0084/5.9048 ms at 2x
(-5.9%/-3.2%). Median p95 also improved in all four comparisons. The complete
native suite passes 10/10, and strict x86_64 compilation covers the paired
SSE2 tail implementation.

A following conservative right-bound experiment was exact but rejected. It
stopped a raster row once the first sample of a pixel pair reached the typed
outline maximum, relying on the already zero-initialized coverage tail. Across
eight 120-frame 1x runs per variant, the hash remained
`5B6EF4F70536C862` with zero pixel difference, but median submission p50
regressed from 1.3922 ms to 1.6106 ms (+15.7%) and synchronized-frame p50
from 4.9143 ms to 5.2848 ms (+7.5%); both p95 medians also worsened. The
source therefore retains the branch-free qualified pair loop.

Two explicitly staged sample-offset experiments were also rejected. The first
precomputed scalar lane offsets once per glyph. With distinct baseline and
candidate dylibs alternated for every process, it remained exact but regressed
2x submission/frame p50 and frame p95. The refined form stored two native
NEON/SSE2 offset vectors per glyph and formed each sample vector with a single
vector add. Across eight alternating 120-frame runs per variant it remained
exact at `5B6EF4F70536C862` (1x) and `706B261418EC5C3B` (2x). Submission p50
improved 1.2428 -> 1.1567 ms at 1x and 1.8251 -> 1.7906 ms at 2x, but 2x
synchronized-frame p50/p95 regressed 5.6951/8.4109 -> 5.8623/8.5459 ms.
Moving arithmetic out of the pair loop is therefore not sufficient evidence;
the qualified construction remains in source. The A/B harness verified the
loaded dylib SHA-256 before measurement to prevent stale native-copy results.

A line-segment metadata experiment was also exact and rejected. It cached each
line's X/Y deltas in the existing curve-metadata table so the eight subscanline
visits retained the original division and edge comparisons but avoided
rebuilding the deltas. Four alternating 120-frame runs per variant retained
zero pixel difference and hashes `5B6EF4F70536C862` (1x) and
`706B261418EC5C3B` (2x). Although 2x submission/frame p50 improved from
1.7558/5.5705 ms to 1.7332/5.0904 ms, 1x regressed from 1.0949/5.1557 ms to
1.1324/5.3494 ms; its frame p95 also worsened from 7.5805 to 7.9913 ms. The
extra metadata traffic therefore does not qualify, and line deltas remain
local to an actual crossing.

Skipping the first redundant winding reset in each pixel-pair and odd-tail
kernel was exact but also rejected. Across eight alternating 120-frame runs,
submission/frame p50 improved 1.1608/4.9743 -> 1.0915/4.7015 ms at 1x and
1.7850/5.9484 -> 1.7218/5.7874 ms at 2x. The 2x frame p95 nevertheless
regressed 7.9814 -> 8.1111 ms (+1.6%). A predictable loop-index branch and
one fewer reset do not satisfy the complete latency gate, so resets remain
unconditional at every subscanline.

The accepted follow-up keeps those control-flow and metadata properties while
folding the NEON lane reduction. The low/high 0-or-1 coverage vectors are
added first, then their 64-bit halves are reduced; this removes one vector add
per pixel without changing integer associativity, sample order, or the SSE2
path. Eight alternating 120-frame runs per variant remained exact at
`5B6EF4F70536C862` (1x) and `706B261418EC5C3B` (2x). Median
submission/frame p50 improved 1.0547/4.6895 -> 1.0211/4.4603 ms at 1x and
1.7792/5.4060 -> 1.6849/5.0955 ms at 2x. Submission/frame p95 also improved
1.6923/7.1415 -> 1.3913/7.1239 ms and 2.7419/7.8718 ->
2.2952/7.7180 ms. The complete local native suite and strict x86_64 SSE2
syntax gate pass.

An exact follow-up replacing NEON compare/invert/shift coverage flags with
integer absolute-value plus unsigned minimum was measured and rejected. Eight
alternating 120-frame runs per variant retained hashes `5B6EF4F70536C862`
(1x) and `706B261418EC5C3B` (2x) with zero channel difference. At 1x,
submission/frame p50 changed 1.0507/5.3452 -> 1.0551/5.1695 ms, but p95
regressed 1.4299/7.2335 -> 1.5391/7.3918 ms. At 2x, submission p50/p95
improved 1.7174/2.7136 -> 1.7046/2.2757 ms and frame p95 improved
8.1648 -> 7.8050 ms, but frame p50 regressed 5.6484 -> 5.8287 ms (+3.2%).
The source therefore retains compare/invert/shift feeding the qualified folded
lane reduction.

A later exact arithmetic candidate derived the second pixel-pair origin from
the first origin plus the existing inverse scale, instead of repeating the
add/subtract/multiply chain. Distinct dylibs were exercised in eight
alternating 120-frame runs per variant and retained zero channel difference at
`5B6EF4F70536C862` (1x) and `706B261418EC5C3B` (2x). Median submission p50
improved only 1.4806 -> 1.4765 ms at 1x and 2.2561 -> 2.2521 ms at 2x, while
synchronized-frame p50 regressed 5.2713 -> 5.3665 ms and 6.1248 -> 6.1985 ms;
1x frame p95 also regressed 8.0932 -> 8.1285 ms. The source therefore retains
the independently evaluated second origin and its established floating-point
edge semantics.

A branchless winding-direction candidate was exact and rejected as well. It
normalized each crossing to a replicated `+1`/`-1` vector and masked that
delta into the paired and odd-tail NEON/SSE2 accumulators. Across eight
alternating 120-frame runs, median submission/frame p50 regressed
1.4165/5.2487 -> 1.4850/5.4465 ms at 1x and 1.7186/5.7809 ->
1.9296/6.0166 ms at 2x; all four p95 medians also worsened. Pixels remained
exact at the established 1x and 2x hashes. The qualified implementation keeps
the cheaper direction branch instead of paying an extra vector mask operation
for every accumulator and crossing.

Exact pushed checkpoint `deb50413` also rebuilt the changed intrinsic source
with ARM64 MSVC/Ninja in the Windows 11 Parallels VM and passed all ten
non-Dawn native CTests. This is cross-compiler and DirectX-host correctness
evidence; performance qualification remains the alternating Apple M3 Pro gate
above.

The same exact `23f6848d` checkout then completed the extended ARM64
MSVC/Ninja D3D12 smoke/package lane. Both native providers built with zero
warnings, all 11 native/Dawn CTests passed, native and managed allocation/
readback samples completed, automatic raster, forced raster, forced SIMD, and
bounded scalar routes remained exact, and forced compute failed at the typed
pre-resource boundary. The SIMD route retained `5B6EF4F70536C862`; GPU Box
blur retained `D77D5DC8AC370BCE`. Microsoft D3D12 triangle/texture oracles,
the cache/effect/mask/clip/text/blend matrix, and runtime staging all passed.
Staged SHA-256 values are
`9D2E6713B9CF8EE97B58B6ED8BB6B73A4C4DF19AED9C5AF5248C0DF522D45266`
for `progpu_native.dll` and
`51BA93113AB6CA6D76DE29BD5DE83C8397808C44EDD21F277244772779B353EC`
for `progpu_native_dawn.dll`.

Exact pushed head `644a8d89` also rebuilt both native libraries with ARM64
MSVC and passed all 11 native/Dawn CTests in the Windows Parallels VM. The
zero-warning benchmark build ran the full 42-glyph forced-NEON D3D12 gate with
247,808 coverage-staging bytes, zero pixel difference, and managed/native hash
`5B6EF4F70536C862`. A separately isolated one-glyph rerasterization A/B also
remained exact at `6C59592F05595EFE` with 5,888 staging bytes. Its process
startup varied from 51 to 133 seconds while measured submissions remained
sub-millisecond, so that VM sample is correctness evidence and is explicitly
not used as a Windows performance claim. Qualified SHA-256 values are
`A9BB8F281F27B332AAACAA0EC35B9E3B26E73D21E839470654D95CB89DDA6A39`
for `progpu_native.dll` and
`97CDBDD4F02442F2D9ACF966C1FF1660C64D7014E9A98FC767B3D9819CB561BF`
for `progpu_native_dawn.dll`.

Exact implementation head `405d139b` then passed the unmodified Windows ARM64
MSVC/D3D12 smoke gate in the Parallels VM. Both native libraries rebuilt, all
11 native/Dawn CTests passed, and forced raster, NEON, and scalar routes
retained exact managed pixels; the incompatible forced-compute route failed
at the typed pre-resource policy boundary. The native and managed samples,
Microsoft D3D12HelloTriangle oracle, MIL guideline/arc deformation, retained
mask/effect/blend families, text parity, bounded differential suite, and
package staging also completed on `Parallels Display Adapter (WDDM)`. The
qualified SHA-256 values are
`C690AED72C3C895778197808C8347656433D6A97DD178F5249A8B4D0C1B56756` for
`progpu_native.dll` and
`552E8CC9441B9A33E89B346758113B52DC13F7A3B1D11F80BF86A3AE90039637` for
`progpu_native_dawn.dll`.

The Linux ARM64 qualification at exact commit `28447de4` rebuilt the 260-object
C++ graph with GCC 13.3 strict warnings, passed all 10 native CTests available
in the wgpu-native lane, verified the export allowlist, and executed the live
Vulkan sample on llvmpipe LLVM 20.1.2. Forced native-compute, raster, SIMD, and
scalar glyph runs all produced exact managed/native pixels with hash
`1F9AE0BB0AC59113`; raster again reported zero coverage staging while both CPU
routes uploaded 247,808 coverage bytes. This is deterministic software-Vulkan
qualification, not physical-GPU performance evidence. It also found and fixed
an old/new WebGPU-header portability defect by normalizing glyph-atlas texture
usage through the typed `texture_usage_flags` compatibility alias.

WPF `BlurEffect.KernelType.Box` now remains GPU-resident instead of falling
through to CPU rendering or being approximated by Gaussian weights. The typed
MIL decoder accepts canonical kernel values 0/1, publishes Box as
`PROGPU_NATIVE_GROUP_EFFECT_BOX_BLUR`, and rejects unknown values. The public
C/C# group-effect surfaces and capability bit expose the same reusable effect
for WPF, WinUI, and Avalonia hosts. Gaussian remains the WPF/default and fastest
path; Box is selected explicitly by the MIL packet or `NativeGroupEffect.BoxBlur`.

Both kernels share the existing bounded two-pass WebGPU compute resources.
Gaussian uses its normalized exponential recurrence; Box uses exactly
`2R + 1` equal samples in each direction, transparent out-of-bounds reads, and
an RGBA8 intermediate. There is no CPU readback or product CPU fallback. The
`--group-box-blur` gate compares the final GPU output against an independent
two-pass integer RGBA8 oracle: Apple M3 Pro Metal is byte-exact at radius 2 and
1x (`22A8BEC63E7C7494`), while 2x is bounded to 1/255 with zero pixels beyond
tolerance and mean absolute error 0.000455 byte/channel. The native 10-test
suite, 84 focused managed interop tests, zero-warning managed builds, and MIL
generator oracle (143 commands/141 complete layouts) pass.

The portable managed compositor selects the same shader branch through
`BlurEffect.KernelType` and `ComputeAccelerator.ApplyBoxBlur(...)`. Its default
remains Gaussian; Box uses a floored physical integer radius bounded to 128,
reuses the Gaussian pipelines, parameter buffers, bindings, and intermediate
texture, and performs no CPU readback. The headless WebGPU contract executes
Gaussian then Box through the same cached two-pipeline family and verifies that
the Box result is nonempty and distinct at transparent image edges.

A later intrinsic glyph experiment split each scanline's crossing positions
into positive- and negative-winding arrays. This halved each stored crossing
from `{float,int}` to `float` and specialized the NEON/SSE2 update direction at
compile time, but required two crossing loops and two offset streams. The
candidate remained byte-exact at `5B6EF4F70536C862` (1x) and
`706B261418EC5C3B` (2x), with zero channel difference. Its initial 120-frame
gate was decisively negative: submission/frame p50 regressed
1.0344/5.3215 -> 1.5310/5.9844 ms at 1x and
1.6587/5.0745 -> 2.5752/6.2036 ms at 2x, with p95 regressions as well. The
candidate was rejected without extending the run matrix; the qualified
interleaved `{x,direction}` traversal remains in source.

A subsequent row-local descriptor experiment precomputed the eight
`std::span<const cpu_crossing>` values after collecting crossings, intending
to remove repeated `subspan` construction from every two-pixel and odd-tail
iteration. The compiler-visible descriptors instead added hot stack traffic
and loads without reducing crossing comparisons. Initial Apple M3 Pro Metal
120-frame gates stayed byte-exact at `5B6EF4F70536C862` (1x) and
`706B261418EC5C3B` (2x), but submission/frame p50 changed from
`1.4922/5.5365` to `1.7465/5.2648` ms at 1x and from `1.9650/6.1749` to
`2.6905/6.3856` ms at 2x. The decisive submission regression rejected the
candidate before a longer alternating matrix; the qualified offset-array plus
inline `subspan` form remains authoritative.

## Extending the policy

Each additional compute-heavy workload must declare the semantics that make a
different shader stage eligible or ineligible, share typed resources and
constants across exact implementations, report the resolved path, and add a
final-output differential gate. CPU work with independent lanes must add an
intrinsic implementation plus a scalar oracle; a whole-buffer scalar loop
requires a documented data dependency and review.
