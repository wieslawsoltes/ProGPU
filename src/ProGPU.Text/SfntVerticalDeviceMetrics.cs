using System.Buffers;
using System.Buffers.Binary;

namespace ProGPU.Text;

/// <summary>An exact font-provided VDMX record; no scaled or nearest-size substitution.</summary>
public readonly record struct FontVerticalDeviceMetrics(ushort PixelsPerEm, short MaximumY, short MinimumY)
{
    public int Height => MaximumY - MinimumY;
}

/// <summary>Validates and reads original OpenType VDMX table bytes.</summary>
public static class SfntVerticalDeviceMetrics
{
    /// <summary>
    /// Returns false for malformed data or invalid query arguments, with atomic empty outputs.
    /// A valid table without an applicable ANSI/square-or-requested-ratio size returns true
    /// and available=false. This table reader does not qualify variable fonts or source layout.
    /// </summary>
    public static bool TryRead(ReadOnlySpan<byte> table, ushort pixelsPerEm,
        ushort pixelRatioX, ushort pixelRatioY, out FontVerticalDeviceMetrics metrics, out bool available)
    {
        metrics = default;
        available = false;
        if (pixelsPerEm == 0 || pixelRatioX == 0 || pixelRatioY == 0 || table.Length < 6)
            return false;
        int version = U16(table, 0), groups = U16(table, 2), ratios = U16(table, 4);
        int headerEnd = 6 + ratios * 6;
        if (version > 1 || groups == 0 || ratios == 0 || groups > ratios || headerEnd > table.Length)
            return false;

        int[] storage = ArrayPool<int>.Shared.Rent(groups);
        try
        {
            Span<int> starts = storage.AsSpan(0, groups);
            int cursor = headerEnd;
            // Group and record counts are font metadata. Traverse their complete bounded
            // inventory before selecting a record; later corruption cannot be trimmed away.
            for (int group = 0; group < groups; group++)
            {
                if (cursor > table.Length - 4 || cursor > ushort.MaxValue) return false;
                starts[group] = cursor;
                int count = U16(table, cursor);
                int first = table[cursor + 2], last = table[cursor + 3];
                if (count == 0 || first > last || count > (table.Length - cursor - 4) / 6) return false;
                int previous = -1;
                for (int index = 0; index < count; index++)
                {
                    int record = cursor + 4 + index * 6;
                    int height = U16(table, record);
                    int maximum = I16(table, record + 2), minimum = I16(table, record + 4);
                    if (height == 0 || height <= previous || height < first ||
                        (height <= byte.MaxValue ? height > last : last != byte.MaxValue) || maximum <= minimum)
                        return false;
                    previous = height;
                }
                cursor += 4 + count * 6;
            }
            if (cursor != table.Length) return false;

            int selected = -1;
            int defaultCharsets = 0;
            for (int index = 0; index < ratios; index++)
            {
                int ratio = 6 + index * 4;
                int charset = table[ratio], x = table[ratio + 1], low = table[ratio + 2], high = table[ratio + 3];
                bool fallback = x == 0 && low == 0 && high == 0;
                if (charset > 1 || (!fallback && (x == 0 || low == 0 || low > high))) return false;
                // Original version-zero fonts can end each distinct charset inventory
                // with its own default (Microsoft Sans Serif). No subsequent entry may
                // follow that charset's default. Version one has one shared inventory.
                int charsetMask = version == 0 ? 1 << charset : 3;
                if ((defaultCharsets & charsetMask) != 0) return false;
                if (fallback) defaultCharsets |= charsetMask;
                int offset = U16(table, 6 + ratios * 4 + index * 2);
                if (starts.BinarySearch(offset) < 0) return false;
                // Version 0 Windows metrics use the ANSI subset; v1 groups 0/1
                // both describe all glyphs for the admitted ANSI source contract.
                bool matches = fallback || ((long)pixelRatioY * x >= (long)low * pixelRatioX &&
                                             (long)pixelRatioY * x <= (long)high * pixelRatioX);
                if (selected < 0 && (version == 1 || charset == 1) && matches) selected = offset;
            }
            if (selected < 0) return true;
            int records = U16(table, selected);
            for (int index = 0; index < records; index++)
            {
                int record = selected + 4 + index * 6;
                if (U16(table, record) != pixelsPerEm) continue;
                metrics = new(pixelsPerEm, I16(table, record + 2), I16(table, record + 4));
                available = true;
                break;
            }
            return true;
        }
        finally { ArrayPool<int>.Shared.Return(storage); }
    }

    private static ushort U16(ReadOnlySpan<byte> bytes, int offset) => BinaryPrimitives.ReadUInt16BigEndian(bytes[offset..]);
    private static short I16(ReadOnlySpan<byte> bytes, int offset) => BinaryPrimitives.ReadInt16BigEndian(bytes[offset..]);
}
