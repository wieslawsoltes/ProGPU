using System.Buffers;
using ProGPU.Backend.Native;

namespace ProGPU.SystemDrawing;

/// <summary>
/// Immutable original UTF-16 EDIT selection boundaries owned by one retained
/// drawing-text generation. Positions are not modern grapheme/caret stops.
/// </summary>
public sealed class DrawingEditWordBoundarySnapshot
{
    private DrawingEditWordBoundarySnapshot(int[] positions, int leadingContentStart,
        int textLength, int paragraphLevel)
    {
        Positions = Array.AsReadOnly(positions);
        LeadingContentStart = leadingContentStart;
        TextLength = textLength;
        ParagraphLevel = paragraphLevel;
    }

    public IReadOnlyList<int> Positions { get; }
    public int LeadingContentStart { get; }
    public int TextLength { get; }
    /// <summary>The retained source paragraph direction: zero LTR or one RTL.</summary>
    public int ParagraphLevel { get; }

    internal static DrawingEditWordBoundaryCapture Capture(string source, int paragraphLevel,
        EditWordBoundaryResolver resolver)
    {
        int capacity = checked(source.Length + 1);
        uint[] scratch = ArrayPool<uint>.Shared.Rent(capacity);
        try
        {
            NativeEditWordBoundaryResult result = resolver(source.AsSpan(), paragraphLevel,
                scratch.AsSpan(0, capacity));
            if (result.Status != NativeRendererStatus.Success)
            {
                if (result.ErrorCode == NativeEditWordBoundaryError.None ||
                    result.BoundaryCount != 0 || result.LeadingContentStart != 0)
                    throw new InvalidDataException("Rejected EDIT boundary capture published invalid metadata.");
                return new(result, null);
            }
            if (result.ErrorCode != NativeEditWordBoundaryError.None ||
                result.BoundaryCount == 0 || result.BoundaryCount > (uint)capacity ||
                result.LeadingContentStart > (uint)source.Length)
                throw new InvalidDataException("Native EDIT boundary capture returned invalid success metadata.");

            int count = checked((int)result.BoundaryCount);
            ReadOnlySpan<uint> positions = scratch.AsSpan(0, count);
            if (positions[0] != 0 || positions[^1] != (uint)source.Length)
                throw new InvalidDataException("Native EDIT boundary capture does not cover the original source.");
            bool containsLeading = result.LeadingContentStart == 0;
            for (int i = 1; i < positions.Length; i++)
            {
                if (positions[i] <= positions[i - 1] || positions[i] > (uint)source.Length)
                    throw new InvalidDataException("Native EDIT boundary inventory is not strictly ordered original UTF-16.");
                containsLeading |= positions[i] == result.LeadingContentStart;
            }
            if (!containsLeading)
                throw new InvalidDataException("Native EDIT leading-content seam is absent from its inventory.");

            // Only structural validation is performed above. Preserve every
            // original endpoint, including those inside a shaped cluster.
            var owned = new int[count];
            for (int i = 0; i < owned.Length; i++) owned[i] = checked((int)positions[i]);
            return new(result, new(owned, checked((int)result.LeadingContentStart),
                source.Length, paragraphLevel));
        }
        finally
        {
            ArrayPool<uint>.Shared.Return(scratch);
        }
    }
}

internal delegate NativeEditWordBoundaryResult EditWordBoundaryResolver(
    ReadOnlySpan<char> source, int paragraphLevel, Span<uint> positions);

internal readonly record struct DrawingEditWordBoundaryCapture(
    NativeEditWordBoundaryResult Result, DrawingEditWordBoundarySnapshot? Snapshot);
