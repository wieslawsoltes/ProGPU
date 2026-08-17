using System.Runtime.CompilerServices;

namespace ProGPU.Backend.Native;

/// <summary>
/// Allocation-free Unicode 17 UAX #9 paragraph resolution over caller-owned
/// decoded scalars. A requested paragraph level of -1 selects first-strong.
/// </summary>
public static unsafe class NativeTextBidiInterop
{
    public static NativeRendererStatus GetRequirements(
        ReadOnlySpan<NativeTextScalar> input,
        out NativeTextBidiRequirements requirements)
    {
        requirements = new NativeTextBidiRequirements
        {
            StructSize = (uint)Unsafe.SizeOf<NativeTextBidiRequirements>()
        };
        fixed (NativeTextScalar* inputData = input)
        {
            return NativeMethods.GetTextBidiRequirements(
                inputData,
                checked((uint)input.Length),
                (NativeTextBidiRequirements*)Unsafe.AsPointer(ref requirements));
        }
    }

    public static NativeRendererStatus Resolve(
        ReadOnlySpan<NativeTextScalar> input,
        int requestedParagraphLevel,
        Span<NativeTextBidiLevel> levels,
        Span<byte> scratch,
        out NativeTextBidiResult result)
    {
        result = new NativeTextBidiResult
        {
            StructSize = (uint)Unsafe.SizeOf<NativeTextBidiResult>()
        };
        fixed (NativeTextScalar* inputData = input)
        fixed (NativeTextBidiLevel* levelData = levels)
        fixed (byte* scratchData = scratch)
        {
            return NativeMethods.ResolveTextBidi(
                inputData,
                checked((uint)input.Length),
                requestedParagraphLevel,
                levelData,
                checked((uint)levels.Length),
                scratchData,
                checked((nuint)scratch.Length),
                (NativeTextBidiResult*)Unsafe.AsPointer(ref result));
        }
    }
}
