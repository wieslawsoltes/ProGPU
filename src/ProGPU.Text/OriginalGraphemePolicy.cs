using System.Globalization;

namespace ProGPU.Text;

// This is the original managed shaper's segmentation policy, not a second
// Unicode table or an EDIT word classifier. Capture before fallback partitions
// the source, and never segment again during retained interaction queries.
internal static class OriginalGraphemePolicy
{
    internal static int GetLength(ReadOnlySpan<char> source)
        => StringInfo.GetNextTextElementLength(source);

    internal static int[]? Capture(string source)
    {
        for (int index = 0; index < source.Length; index++)
        {
            if (char.IsHighSurrogate(source[index]))
            {
                if (++index == source.Length || !char.IsLowSurrogate(source[index])) return null;
            }
            else if (char.IsLowSurrogate(source[index])) return null;
        }
        var boundaries = new List<int> { 0 };
        for (int index = 0; index < source.Length;)
        {
            index = checked(index + GetLength(source.AsSpan(index)));
            boundaries.Add(index);
        }
        return boundaries.ToArray();
    }
}
