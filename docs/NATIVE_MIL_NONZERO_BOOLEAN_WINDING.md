# Native MIL Nonzero Boolean Winding

## Outcome

ProGPU's native MIL compiler and shared WebGPU path rasterizer support exact
WPF `GeometryGroup.FillRule=Nonzero` semantics when the group contains
`CombinedGeometry`, ordinary contours, nested groups, or recursively combined
groups. The implementation is GPU-only on the rendering hot path and is shared
by direct path fills and retained vector clips.

The previous implementation could represent only inside/outside masks for a
boolean subtree. That was sufficient for CombinedGeometry and an outer EvenOdd
group, but not for Nonzero aggregation: WPF appends the boolean result's
oriented contours to the other group contours before applying the outer fill
rule. Replacing that operation with predicate union would lose winding
cancellation.

## WPF semantic oracle

The source analysis follows these WPF paths in the tracked WPF tree:

- `CMilCombinedGeometryDuce::GetShapeDataCore` evaluates `CShapeBase::Combine`;
- the boolean engine emits well-oriented result figures;
- `CMilGeometryGroupDuce::GetShapeDataCore` appends every child shape with
  `CShape::AddShapeData`, then assigns the one outer fill mode.

The standalone Windows-only oracle is
`eng/oracles/WpfNonzeroGeometryOracle`. It references only the system WPF
framework, not LibreWPF or ProGPU, and must run on an STA thread:

```powershell
dotnet run --configuration Release `
  --project eng/oracles/WpfNonzeroGeometryOracle/WpfNonzeroGeometryOracle.csproj
```

Windows 11 ARM64 with .NET SDK 10.0.400 produced these decisive results:

| Shape | Flattened signed areas | Interior result |
|---|---:|---|
| Union of overlapping rectangles | `+150` | filled |
| Union plus matching counter-clockwise contour in a Nonzero group | `+150,-150` | cancelled |
| Union whose own transform reflects it | `+150` | filled |
| Union inside a reflected GeometryGroup | `-150` | filled |
| Reflected group union plus matching clockwise contour | `-150,+150` | cancelled |
| Reflected group union plus matching counter-clockwise contour | `-150,-150` | filled |
| Exclude outer/inner rectangles | `-36,+100` | outer filled, hole clear |

The transform distinction is important. CombinedGeometry owns its transform as
part of the boolean solve, so WPF re-orients the transformed result to positive
winding. A GeometryGroup transform is applied after its child boolean result
exists and therefore flips that result's winding when its determinant is
negative.

## Typed postfix representation

Existing node values `0..6` and the 48-byte node layout remain unchanged. Three
values are appended:

| Value | Node | Stack behavior |
|---:|---|---|
| `7` | `WINDING_LEAF` | push the leaf's raw eight-lane signed winding |
| `8` | `WINDING_ADD` | pop two winding values and push their sum |
| `9` | `WINDING_NEGATE` | negate the top winding value |

Ordinary `LEAF` nodes still push a fill-rule-reduced `0/1` predicate. Boolean
operators accept either form, interpret nonzero as inside, and publish a
well-oriented `0/1` result. This makes recursively nested combinations exact:
a Nonzero group can feed a CombinedGeometry predicate, and that normalized
predicate can feed another signed group.

The compiler walks nested GeometryGroup children under the outer group's fill
rule, deliberately ignoring nested group fill rules as WPF does. It preserves a
group's own rule when that group is reached as a CombinedGeometry operand.
Ordinary contours become raw winding leaves for Nonzero and EvenOdd predicate
leaves for EvenOdd. A boolean child contributes `+1`; the compiler emits
`WINDING_NEGATE` only when the determinant of the containing group transform is
negative. Empty and singular branches remain exact empty values.

Validation keeps the existing bounds of 32 group children, 63 postfix
instructions, and stack depth 16. Signed programs require a Nonzero root and
fail closed when a bound or typed invariant is not satisfied.

## GPU execution

Mask-only programs retain the existing compact `array<u32,16>` evaluator and
the phased two-word sample-mask route for translated-equivalent leaves. Signed
programs are marked in the GPU program descriptor. The fastest/default policy
executes their bounded vector evaluator inline in `PathRasterizer.wgsl`, with
no per-leaf intermediate buffer. This is the qualified path on Metal and
D3D12.

An explicit staged compatibility policy executes three typed GPU stages:

1. `PathSignedWindingLeaf.wgsl` writes each leaf's raw signed winding for all
   active supersamples. Its analytic segment walker evaluates eight horizontal
   samples at once through two `vec4<i32>` values, so roots are still solved
   once per supersample row rather than once per scalar sample.
2. `PathSignedWindingEvaluate.wgsl` evaluates the bounded postfix program once
   per supersample row, carrying all eight horizontal lanes in two
   `vec4<i32>` values, and writes two normalized predicate-mask words per
   texel.
3. `PathSignedWindingCoverage.wgsl` counts those masks and packs four
   adjacent R8 texels into the buffer layout consumed by the atlas copy.

`PathRasterizerCommon.wgsl` owns the shared analytic segment and winding
algorithms used by the ordinary and signed-leaf modules. CMake composes that
source fragment into each complete shader at build time; the runtime does not
concatenate or specialize shader text.

`PathRasterizer.wgsl` exposes two compute entry points. `cs_main_ordinary`
contains only ordinary paths, direct two-operand operations, and mask-only
postfix programs. `cs_main` additionally reaches the bounded inline signed
evaluator. Managed `PathAtlas` descriptors cannot carry signed-winding program
tokens and therefore always select the lean ordinary entry point. Native C++
batches select the ordinary pipeline unless at least one path explicitly uses
inline signed winding; mixed inline batches select the full pipeline. This
keeps the common path out of the larger D3D12 shader-control-flow graph without
changing pixels, uploading another arena, or adding a submission. The native
C++ API also exposes the staged implementation as a typed forced path rather
than silently selecting it.

Neither route performs CPU readback, CPU repacking, per-item GPU submission, or
managed fallback. For `S` visited segments, `N` instructions, sample-grid width
`G<=8`, and stack bound `D<=16`, the inline route uses `O(G*(S+N))` work per
pixel and `O(D)` bounded private evaluator storage. In the staged route, leaf
ordinal phases are batched across paths; the evaluator and pack stages are each
one dispatch with the path index in the third dispatch dimension. Leaf work is
`O(G*S)` per pixel, postfix work is `O(G*N)` eight-lane vector operations, and
pack work is `O(1)`. Staging uses 64 32-bit words per leaf texel plus one
two-word predicate mask per signed path, with fixed public program and stack
bounds. Compared with the scalar-per-supersample staging prototype, an 8x8
path uses one sixty-fourth as many launched evaluator invocations (an eightfold
reduction in each dispatch dimension) and one thirty-second as much result
storage.

### Portable buffer-to-texture placement

R8 atlas rows use WebGPU's 256-byte `bytesPerRow` alignment. Each path, clip,
and glyph tile's source offset is independently aligned to 512 bytes, which is
the stricter D3D12 placed-texture-footprint offset requirement. The row-pitch
and placement constraints are deliberately separate constants and have a
permanent layout invariant test.

This distinction was found through a bounded Parallels D3D12 reduction: the
same signed leaf/evaluate/pack dispatches and path draw remained valid, and the
device was lost only when an R8 copy began at byte offset 72,960 (256-aligned
but not 512-aligned). Moving that tile to byte offset 73,216 resolved the loss.
The implementation therefore fixes the shared path, retained-clip, and glyph
atlas allocators rather than adding a D3D-only special case.

The work also corrects the shared WGSL interpretation of the already-public
fill-rule ABI: `NonZero=0` and `EvenOdd=1`. Both `PathRasterizer.wgsl` and
`PathOpGeometry.wgsl` now apply parity only for value `1`. A permanent sample
uses two identical clockwise contours to require Nonzero coverage and EvenOdd
cancellation. `ProGPU.Vector.FillRule` retains its public managed ordering of
`EvenOdd=0` and `Nonzero=1`; the shared `GpuPathFillRuleEncoding` performs an
explicit, fail-closed conversion whenever `PathAtlas` or
`PathOpGeometrySolver` writes `GpuPathRecord`. Managed SVG/path rasterization,
GPU boolean geometry, and the native C ABI therefore consume the same shader
values without relying on managed enum ordinals.

## Coverage and gates

Native compiler tests require the exact postfix sequence for fill and clip,
including winding leaves/additions and reflection negation. Backend unit tests
cover validator rejection, signed GPU program tagging, portable row/offset
alignment, and exact signed staging non-overlap. Managed interop tests preserve
the appended enum values and validate public signed clip programs.
`NativeSignedWindingExecutionPreference` exposes fastest, forced
inline-vector-compute, and forced staged-vector-compute modes; the resolved
path is reported in frame metrics and clip-chain state. Invalid forced values
fail closed.

The permanent native sample checks these pixels on the real GPU:

- signed mask cancellation: dark background;
- signed mask positive island: cyan;
- signed direct-fill cancellation: dark background;
- signed direct-fill positive island: cyan;
- two identical contours with Nonzero: cyan;
- the same contours with EvenOdd: dark background.

Apple M3 Pro Metal passed the live readback with exact dark `5,6,10` and cyan
`51,209,242` values, and all 10 configured native CTests passed. Windows 11
ARM64 in Parallels selected `Parallels Display Adapter (WDDM)` through D3D12
and produced the same six decisive pixels: cancelled mask/path cases and the
EvenOdd double contour were `5,6,10`; both positive islands and the Nonzero
double contour were `51,209,242`. That run completed 22 draw calls from 35
retained commands with 16,096 uploaded vertex bytes and no device loss.

The Windows gate used source checkpoint
`51d63ed24640d279923496fc5216f6399f56494b` from archive SHA-256
`0a09a31491e115bf4794c0567e43e98013ecae91ed1d127a9717dd9365f9e9c2`.
Strict ARM64 MSVC `/W4 /WX` completed all 315 build steps and all 11 native/Dawn
CTests passed before the live sample. The provider-free macOS build also passed
10/10 CTests; managed shader-resource coverage passed 20/20 and the selected
managed PathAtlas GPU cases passed 5/5. The collected qualification bundle has
SHA-256 `2459e7141471ab2101b885fe51b95a6b041e82e60ac1aaf093ddd75ee0b78aef`.

## Inline-versus-staged performance qualification

The benchmark's `--signed-winding-paths --rerasterize-paths` scene builds four
matched high-precision Nonzero paths. Native replay evaluates
`Leaf(A), Leaf(B), Union, WindingLeaf(C), WindingAdd`; managed replay aggregates
the same three oriented contours. Every measured frame forces rerasterization,
uses an 8x8 sample grid, synchronizes completion, and compares all 518,400 RGBA
pixels. `--signed-winding-execution inline|staged` selects the exact native
path and the JSON report records the resolved mode.

On Apple M3 Pro/Metal, four alternating Release runs per mode used three
warm-ups and 30 measured frames. The median of run p50 values was `3.1407 ms`
inline versus `7.7894 ms` staged; the median p95 was `3.3726 ms` versus
`9.3647 ms`. Inline is therefore 2.48 times faster at p50 and 2.78 times faster
at p95 for this rerasterization workload. Coverage staging was `165,888` bytes
inline versus `119,844,576` bytes staged, a 722.44-fold reduction. Both modes
allocated `0 B/frame` on the managed heap and produced exact hash
`4026F1AF5062CEA5` with maximum channel difference zero. Matched Instruments
Time Profiler traces and a Metal System Trace accompany the JSON evidence under
`artifacts/performance/signed-winding-simd/`; these artifacts are deliberately
not versioned.

Checkpoint `cf0792aa` was also rebuilt from archive SHA-256
`4606478e5e70db32d312186171d7816a60842335e862272ebfb143c971636e01`
inside Windows 11 ARM64. Strict MSVC `/W4 /WX` completed all 315 build steps,
11/11 native/Dawn CTests passed, and the native renderer reproduced all six
exact winding pixels on `Parallels Display Adapter (WDDM)` through D3D12. The
forced inline and staged differential runs both compared all 518,400 pixels
exactly with hash `4026F1AF5062CEA5`, reported their requested execution path,
allocated `0 B/frame`, and completed without device loss. Their staging byte
counts matched Metal. The short VM timing samples (`10.699 ms` versus
`35.3319 ms` p50) are correctness evidence only, not a physical-D3D12
performance claim. The resulting `progpu_native.dll` and
`progpu_native_dawn.dll` SHA-256 values are respectively
`97D1D63AD750C4F7D2B44057119057ADBDADFF52CC96BB7C75E04B309706C0B4`
and `4FE268BFD290385922E954E1DE2E7C7D5754E5A6931C60E8202563307FED66CC`.
