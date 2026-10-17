using System.Buffers.Binary;
using System.IO;

namespace ProGPU.Text;

public partial class TtfFont
{
    /// <summary>
    /// Reads exact square-pixel VDMX metrics from this static TrueType face.
    /// Missing tables/sizes and unsupported variable/symbol faces return false;
    /// malformed or duplicate VDMX metadata throws before publishing output.
    /// This does not change ordinary line metrics, shaping or glyph placement.
    /// </summary>
    public bool TryGetVerticalDeviceMetrics(ushort pixelsPerEm, out FontVerticalDeviceMetrics metrics)
    {
        metrics = default;
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
            variable |= tag == 0x66766172; // fvar, including an invalid record ignored by legacy metadata.
            if (tag != 0x56444D58) continue;
            uint offset = BinaryPrimitives.ReadUInt32BigEndian(entry[8..]);
            uint length = BinaryPrimitives.ReadUInt32BigEndian(entry[12..]);
            if (found || offset > font.Length || length > font.Length - offset)
                throw new InvalidDataException("Duplicate or out-of-range VDMX table.");
            found = true;
            table = font.Slice((int)offset, (int)length);
        }
        if (!found) return false;
        if (!SfntVerticalDeviceMetrics.TryRead(table, pixelsPerEm, 1, 1, out var candidate, out bool available))
            throw new InvalidDataException("Malformed VDMX table.");
        if (variable || !HasTrueTypeOutlines || UsesSymbolCharacterMap || !available) return false;
        metrics = candidate;
        return true;
    }
}
