using System.Buffers.Binary;
using System.IO;

namespace ProGPU.Text;

public partial class TtfFont
{
    /// <summary>
    /// Reads this static TrueType face's original gasp recommendation at an exact
    /// integral device size. Missing coverage and variable/non-TrueType faces
    /// return false. Malformed or duplicate gasp records throw before output.
    /// This query changes neither glyph placement nor rendering policy.
    /// </summary>
    public bool TryGetStaticRasterizationRecommendations(ushort pixelsPerEm, out FontRasterizationRange range)
    {
        range = default;
        ArgumentOutOfRangeException.ThrowIfZero(pixelsPerEm);
        ReadOnlySpan<byte> font = _data.Span;
        int directory = checked((int)_face.BaseOffset + 12);
        int count = BinaryPrimitives.ReadUInt16BigEndian(font.Slice(directory - 8, 2));
        ReadOnlySpan<byte> table = default;
        bool found = false, variable = false;
        for (int index = 0; index < count; index++)
        {
            ReadOnlySpan<byte> entry = font.Slice(directory + index * 16, 16);
            uint tag = BinaryPrimitives.ReadUInt32BigEndian(entry);
            // Raw fvar presence is authoritative even if legacy metadata omitted
            // a malformed entry. Never return unadjusted ranges as instance policy.
            variable |= tag == 0x66766172;
            if (tag != 0x67617370) continue;
            uint offset = BinaryPrimitives.ReadUInt32BigEndian(entry[8..]);
            uint length = BinaryPrimitives.ReadUInt32BigEndian(entry[12..]);
            if (found || offset > font.Length || length > font.Length - offset)
                throw new InvalidDataException("Duplicate or out-of-range gasp table.");
            found = true;
            table = font.Slice((int)offset, (int)length);
        }
        if (!found) return false;
        if (!SfntRasterizationRecommendations.TryRead(table, pixelsPerEm, out var candidate, out bool available))
            throw new InvalidDataException("Malformed gasp table.");
        if (variable || !HasTrueTypeOutlines || !available) return false;
        range = candidate;
        return true;
    }
}
