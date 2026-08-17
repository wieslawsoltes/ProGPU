using System.Runtime.CompilerServices;

namespace ProGPU.Backend.Native;

/// <summary>
/// Allocation-free Unicode 17 UAX #14 default line-break resolution over
/// caller-owned decoded scalar records.
/// </summary>
public static unsafe class NativeTextLineBreakInterop
{
    public static NativeRendererStatus GetRequirements(
        ReadOnlySpan<NativeTextScalar> input,
        out NativeTextLineBreakRequirements requirements)
    {
        requirements = new NativeTextLineBreakRequirements
        {
            StructSize = (uint)Unsafe.SizeOf<NativeTextLineBreakRequirements>()
        };
        fixed (NativeTextScalar* inputData = input)
        {
            return NativeMethods.GetTextLineBreakRequirements(
                inputData,
                checked((uint)input.Length),
                (NativeTextLineBreakRequirements*)Unsafe.AsPointer(ref requirements));
        }
    }

    public static NativeRendererStatus Resolve(
        ReadOnlySpan<NativeTextScalar> input,
        Span<NativeTextLineBreakKind> breaksAfter,
        Span<byte> scratch,
        out NativeTextLineBreakResult result)
    {
        result = new NativeTextLineBreakResult
        {
            StructSize = (uint)Unsafe.SizeOf<NativeTextLineBreakResult>()
        };
        fixed (NativeTextScalar* inputData = input)
        fixed (NativeTextLineBreakKind* breakData = breaksAfter)
        fixed (byte* scratchData = scratch)
        {
            return NativeMethods.ResolveTextLineBreaks(
                inputData,
                checked((uint)input.Length),
                breakData,
                checked((uint)breaksAfter.Length),
                scratchData,
                checked((nuint)scratch.Length),
                (NativeTextLineBreakResult*)Unsafe.AsPointer(ref result));
        }
    }
}
