using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace ProGPU.Backend.Native;

/// <summary>Explicit failures of the original-source EDIT selection profile.</summary>
public enum NativeEditWordBoundaryError : uint
{
    None = (uint)NativeMethods.EditWordBoundaryNone,
    InvalidEncoding = (uint)NativeMethods.EditWordBoundaryInvalidEncoding,
    InputTooLarge = (uint)NativeMethods.EditWordBoundaryInputTooLarge,
    AllocationFailure = (uint)NativeMethods.EditWordBoundaryAllocationFailure,
    DependencyUnavailable = (uint)NativeMethods.EditWordBoundaryDependencyUnavailable,
    DependencyFailure = (uint)NativeMethods.EditWordBoundaryDependencyFailure,
    UnqualifiedBmpSymbolPolicy = (uint)NativeMethods.EditWordBoundaryUnqualifiedBmpSymbolPolicy,
    UnqualifiedJoinerPolicy = (uint)NativeMethods.EditWordBoundaryUnqualifiedJoinerPolicy,
    UnqualifiedComplexScriptPolicy = (uint)NativeMethods.EditWordBoundaryUnqualifiedComplexScriptPolicy,
    UnqualifiedScriptItemTransitionPolicy = (uint)NativeMethods.EditWordBoundaryUnqualifiedScriptItemTransitionPolicy,
    InvalidParagraphLevel = (uint)NativeMethods.EditWordBoundaryInvalidParagraphLevel,
    InvalidBuffer = (uint)NativeMethods.EditWordBoundaryInvalidBuffer,
    OutputTooSmall = (uint)NativeMethods.EditWordBoundaryOutputTooSmall
}

/// <summary>One synchronous batch over complete unchanged original UTF-16 text.</summary>
/// <remarks>
/// This explicit profile is not a general Unicode word classifier or Forms
/// capability. Unsupported policy/dependency cases remain failures. Callers own
/// the resulting inventory and must bind it to the same source/layout generation.
/// No native pointer is retained and no font/device is created.
/// </remarks>
public static unsafe class NativeEditWordBoundaryInterop
{
    /// <summary>
    /// Resolves paragraph level zero (LTR) or one (RTL). A buffer of source.Length
    /// plus one is sufficient. On failure every output element is untouched;
    /// success writes only BoundaryCount elements, preserving the caller tail.
    /// Output positions and LeadingContentStart are original UTF-16 offsets,
    /// including legacy EDIT boundaries inside modern graphemes.
    /// </summary>
    public static NativeEditWordBoundaryResult Resolve(ReadOnlySpan<char> source,
        int paragraphLevel, Span<uint> positions)
    {
        if ((uint)paragraphLevel > 1U)
            return new() { Status = NativeRendererStatus.InvalidArgument,
                ErrorCode = NativeEditWordBoundaryError.InvalidParagraphLevel };
        fixed (char* text = source)
        fixed (uint* output = positions)
            return NativeMethods.ResolveEditWordBoundariesUtf16((ushort*)text,
                checked((uint)source.Length), paragraphLevel, output, checked((uint)positions.Length));
    }
}

internal static unsafe partial class NativeMethods
{
    [LibraryImport(LibraryName, EntryPoint = "progpu_native_text_resolve_edit_word_boundaries_utf16")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial NativeEditWordBoundaryResult ResolveEditWordBoundariesUtf16(
        ushort* source, uint sourceLength, int paragraphLevel, uint* positions, uint capacity);
}
