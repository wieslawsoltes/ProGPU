using System.Buffers.Binary;

namespace ProGPU.Text;

/// <summary>Original OpenType gasp recommendations, not a selected rendering mode.</summary>
[Flags]
public enum FontRasterizationRecommendations : ushort
{
    None = 0,
    GridFit = 1,
    Grayscale = 2,
    SymmetricGridFit = 4,
    SymmetricSmoothing = 8
}

/// <summary>An original inclusive size range and its independent recommendation flags.</summary>
public readonly record struct FontRasterizationRange(
    ushort Version, ushort PixelsPerEm, ushort MaximumPixelsPerEm,
    FontRasterizationRecommendations Recommendations);

/// <summary>Validates original gasp bytes without selecting a rasterizer or executing a font.</summary>
public static class SfntRasterizationRecommendations
{
    /// <summary>
    /// Malformed tables and zero-size queries return false with empty outputs.
    /// A valid empty inventory or a size beyond its last range returns true with
    /// available=false. Version-one symmetric flags remain independent of the
    /// original grayscale flags. Variable-font threshold adjustments are not applied.
    /// </summary>
    public static bool TryRead(ReadOnlySpan<byte> table, ushort pixelsPerEm,
        out FontRasterizationRange range, out bool available)
    {
        range = default;
        available = false;
        if (pixelsPerEm == 0 || table.Length < 4) return false;
        ushort version = U16(table, 0);
        int count = U16(table, 2);
        if (version > 1 || table.Length != 4 + count * 4) return false;
        int allowedFlags = version == 0 ? 3 : 15;
        int previousMaximum = -1;
        FontRasterizationRange candidate = default;
        bool selected = false;
        // O(R) time and O(1) storage. Validate every original range, including
        // records after the selected range, before publishing either output.
        for (int index = 0; index < count; index++)
        {
            int offset = 4 + index * 4;
            ushort maximum = U16(table, offset), flags = U16(table, offset + 2);
            if (maximum <= previousMaximum || (flags & ~allowedFlags) != 0) return false;
            if (!selected && pixelsPerEm <= maximum)
            {
                candidate = new(version, pixelsPerEm, maximum, (FontRasterizationRecommendations)flags);
                selected = true;
            }
            previousMaximum = maximum;
        }
        range = candidate;
        available = selected;
        return true;
    }

    private static ushort U16(ReadOnlySpan<byte> bytes, int offset) =>
        BinaryPrimitives.ReadUInt16BigEndian(bytes[offset..]);
}
