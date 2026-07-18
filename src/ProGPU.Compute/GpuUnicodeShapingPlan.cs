using System.Runtime.InteropServices;
using ProGPU.Text;

namespace ProGPU.Compute;

/// <summary>A half-open range of identical Unicode shaping properties.</summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public readonly record struct GpuUnicodePropertyRange(
    uint Start,
    uint End,
    uint PropertiesA,
    uint PropertiesB);

/// <summary>A sparse directional code-point fallback record.</summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public readonly record struct GpuUnicodeDirectionalMapping(
    uint CodePoint,
    uint MirroredCodePoint,
    uint VerticalCodePoint,
    uint Reserved = 0);

/// <summary>A full canonical FormD decomposition sequence.</summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public readonly record struct GpuUnicodeDecomposition(uint CodePoint, uint Offset, uint Count);

/// <summary>One canonical FormC composition pair.</summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public readonly record struct GpuUnicodeComposition(uint First, uint Second, uint Composed);

/// <summary>
/// Process-wide Unicode 17 shaping properties compressed into ranges suitable
/// for binary search by WebGPU compute shaders.
/// </summary>
public static class GpuUnicodeShapingPlan
{
    private static readonly Lazy<ReadOnlyMemory<GpuUnicodePropertyRange>> s_ranges =
        new(CreateRanges, LazyThreadSafetyMode.ExecutionAndPublication);
    private static readonly Lazy<ReadOnlyMemory<GpuUnicodeDirectionalMapping>> s_directionalMappings =
        new(CreateDirectionalMappings, LazyThreadSafetyMode.ExecutionAndPublication);
    private static readonly Lazy<ReadOnlyMemory<GpuUnicodeDecomposition>> s_decompositions =
        new(CreateDecompositions, LazyThreadSafetyMode.ExecutionAndPublication);
    private static readonly Lazy<ReadOnlyMemory<GpuUnicodeComposition>> s_compositions =
        new(CreateCompositions, LazyThreadSafetyMode.ExecutionAndPublication);
    private static readonly Lazy<ReadOnlyMemory<uint>> s_packedData =
        new(CreatePackedData, LazyThreadSafetyMode.ExecutionAndPublication);

    public static ReadOnlyMemory<GpuUnicodePropertyRange> Ranges => s_ranges.Value;
    public static ReadOnlyMemory<GpuUnicodeDirectionalMapping> DirectionalMappings => s_directionalMappings.Value;
    public static ReadOnlyMemory<GpuUnicodeDecomposition> Decompositions => s_decompositions.Value;
    public static ReadOnlyMemory<uint> DecompositionScalars => UnicodeNormalizationPlan.DecompositionScalars;
    public static ReadOnlyMemory<GpuUnicodeComposition> Compositions => s_compositions.Value;
    public static ReadOnlyMemory<uint> PackedData => s_packedData.Value;

    private static ReadOnlyMemory<GpuUnicodePropertyRange> CreateRanges()
    {
        var ranges = new List<GpuUnicodePropertyRange>(4096);
        uint start = 0;
        (uint A, uint B) current = GetProperties(0);
        for (uint codePoint = 1; codePoint <= 0x10ffffu; codePoint++)
        {
            (uint A, uint B) next = GetProperties(codePoint);
            if (next == current) continue;
            ranges.Add(new GpuUnicodePropertyRange(start, codePoint, current.A, current.B));
            start = codePoint;
            current = next;
        }
        ranges.Add(new GpuUnicodePropertyRange(start, 0x110000u, current.A, current.B));
        return ranges.ToArray();
    }

    private static (uint A, uint B) GetProperties(uint codePoint)
    {
        uint propertiesA = UnicodeShapingProperties.GetArabicJoiningType(codePoint) |
            ((uint)UnicodeShapingProperties.GetCanonicalCombiningClass(codePoint) << 8) |
            ((uint)UnicodeShapingProperties.GetIndicProperties(codePoint) << 16);
        uint propertiesB = UnicodeShapingProperties.GetUseCategory(codePoint) |
            (UnicodeShapingProperties.IsMark(codePoint) ? 1u << 8 : 0u);
        return (propertiesA, propertiesB);
    }

    private static ReadOnlyMemory<GpuUnicodeDirectionalMapping> CreateDirectionalMappings()
    {
        var mappings = new List<GpuUnicodeDirectionalMapping>(512);
        for (uint codePoint = 0; codePoint <= 0x10ffffu; codePoint++)
        {
            uint mirrored = UnicodeShapingProperties.GetMirroredCodePoint(codePoint);
            uint vertical = UnicodeShapingProperties.GetVerticalCodePoint(codePoint);
            if (mirrored != codePoint || vertical != codePoint)
                mappings.Add(new GpuUnicodeDirectionalMapping(codePoint, mirrored, vertical));
        }
        return mappings.ToArray();
    }

    private static ReadOnlyMemory<GpuUnicodeDecomposition> CreateDecompositions()
    {
        ReadOnlySpan<uint> source = UnicodeNormalizationPlan.DecompositionRecords.Span;
        var result = new GpuUnicodeDecomposition[source.Length / 3];
        for (var index = 0; index < result.Length; index++)
            result[index] = new(source[index * 3], source[index * 3 + 1], source[index * 3 + 2]);
        return result;
    }

    private static ReadOnlyMemory<GpuUnicodeComposition> CreateCompositions()
    {
        ReadOnlySpan<uint> source = UnicodeNormalizationPlan.CompositionRecords.Span;
        var result = new GpuUnicodeComposition[source.Length / 3];
        for (var index = 0; index < result.Length; index++)
            result[index] = new(source[index * 3], source[index * 3 + 1], source[index * 3 + 2]);
        return result;
    }

    private static ReadOnlyMemory<uint> CreatePackedData()
    {
        ReadOnlySpan<GpuUnicodePropertyRange> ranges = Ranges.Span;
        ReadOnlySpan<GpuUnicodeDirectionalMapping> directional = DirectionalMappings.Span;
        ReadOnlySpan<GpuUnicodeDecomposition> decompositions = Decompositions.Span;
        ReadOnlySpan<uint> scalars = DecompositionScalars.Span;
        ReadOnlySpan<GpuUnicodeComposition> compositions = Compositions.Span;
        var words = new List<uint>(checked(16 + ranges.Length * 4 + directional.Length * 4 +
            decompositions.Length * 3 + scalars.Length + compositions.Length * 3));
        for (var index = 0; index < 16; index++) words.Add(0);
        words[0] = 0x554e4950u;
        words[1] = checked((uint)ranges.Length);
        words[2] = checked((uint)words.Count);
        foreach (GpuUnicodePropertyRange value in ranges)
        {
            words.Add(value.Start); words.Add(value.End); words.Add(value.PropertiesA); words.Add(value.PropertiesB);
        }
        words[3] = checked((uint)directional.Length);
        words[4] = checked((uint)words.Count);
        foreach (GpuUnicodeDirectionalMapping value in directional)
        {
            words.Add(value.CodePoint); words.Add(value.MirroredCodePoint);
            words.Add(value.VerticalCodePoint); words.Add(0);
        }
        words[5] = checked((uint)decompositions.Length);
        words[6] = checked((uint)words.Count);
        foreach (GpuUnicodeDecomposition value in decompositions)
        {
            words.Add(value.CodePoint); words.Add(value.Offset); words.Add(value.Count);
        }
        words[7] = checked((uint)scalars.Length);
        words[8] = checked((uint)words.Count);
        foreach (uint value in scalars) words.Add(value);
        words[9] = checked((uint)compositions.Length);
        words[10] = checked((uint)words.Count);
        foreach (GpuUnicodeComposition value in compositions)
        {
            words.Add(value.First); words.Add(value.Second); words.Add(value.Composed);
        }
        words[11] = 4u;
        words[12] = checked((uint)words.Count);
        int machineDirectory = words.Count;
        for (var index = 0; index < 4 * 7; index++) words.Add(0);
        for (var machineIndex = 0; machineIndex < 4; machineIndex++)
        {
            var machine = (UnicodeShapingProperties.SyllableMachine)machineIndex;
            int stateCount = UnicodeShapingProperties.GetSyllableMachineStateCount(machine);
            int descriptor = machineDirectory + machineIndex * 7;
            words[descriptor] = checked((uint)machineIndex);
            words[descriptor + 1] = checked((uint)UnicodeShapingProperties.GetSyllableMachineStartState(machine));
            words[descriptor + 2] = checked((uint)stateCount);
            words[descriptor + 3] = checked((uint)words.Count);
            for (var state = 0; state < stateCount; state++)
            {
                for (var category = 0; category < 256; category++)
                {
                    (int target, int action) = UnicodeShapingProperties.GetSyllableTransition(
                        machine, state, checked((byte)category));
                    words.Add(Pack(target, action));
                }
            }
            words[descriptor + 4] = checked((uint)words.Count);
            for (var state = 0; state < stateCount; state++)
                words.Add(checked((uint)UnicodeShapingProperties.GetSyllableToStateAction(machine, state)));
            words[descriptor + 5] = checked((uint)words.Count);
            for (var state = 0; state < stateCount; state++)
                words.Add(checked((uint)UnicodeShapingProperties.GetSyllableFromStateAction(machine, state)));
            words[descriptor + 6] = checked((uint)words.Count);
            for (var state = 0; state < stateCount; state++)
            {
                (int Target, int Action)? eof = UnicodeShapingProperties.GetSyllableEofTransition(machine, state);
                words.Add(eof is { } value ? Pack(value.Target, value.Action) : uint.MaxValue);
            }
        }
        return words.ToArray();

        static uint Pack(int target, int action)
        {
            if ((uint)target > ushort.MaxValue || (uint)action > ushort.MaxValue)
                throw new InvalidOperationException("A Unicode syllable-machine transition exceeds packed limits.");
            return (uint)target | (uint)action << 16;
        }
    }
}
