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
programs are marked in the GPU program descriptor and execute as three typed
GPU stages:

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

The existing managed `PathAtlas` composes the same common fragment with
`PathRasterizer.wgsl` and retains its bounded inline signed evaluator. This
keeps the portable managed implementation behavior-compatible while the native
C++ backend selects the staged pipelines before dispatch.

The staged route never performs CPU readback, CPU repacking, per-item GPU
submission, or managed fallback. Leaf ordinal phases are batched across paths;
the evaluator and pack stages are each one dispatch with the path index in the
third dispatch dimension. For `S` visited segments, `N` instructions,
sample-grid width `G<=8`, and stack bound `D<=16`, leaf work is `O(G*S)` per
pixel, postfix work is `O(G*N)` eight-lane vector operations, and pack work is
`O(1)`. Staging uses 64 32-bit words per leaf texel plus one two-word predicate
mask per signed path, with fixed public program and stack bounds. Compared with
the scalar-per-supersample staging prototype, this uses one eighth as many
evaluator invocations and one thirty-second as much result storage.

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
cancellation.

## Coverage and gates

Native compiler tests require the exact postfix sequence for fill and clip,
including winding leaves/additions and reflection negation. Backend unit tests
cover validator rejection, signed GPU program tagging, portable row/offset
alignment, and exact signed staging non-overlap. Managed interop tests preserve
the appended enum values and validate public signed clip programs.

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
