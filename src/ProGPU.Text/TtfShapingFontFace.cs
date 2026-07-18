using ProGPU.Text.Shaping;

namespace ProGPU.Text;

/// <summary>
/// Adapts the existing parsed <see cref="TtfFont"/> to the standalone shaping
/// package. It performs no graphics initialization and borrows table memory from
/// the immutable font for the adapter's lifetime.
/// </summary>
public sealed class TtfShapingFontFace : IShapingFontFace
{
    public TtfShapingFontFace(TtfFont font)
    {
        Font = font ?? throw new ArgumentNullException(nameof(font));
    }

    public TtfFont Font { get; }
    public int FaceIndex => Font.FaceIndex;
    public ushort UnitsPerEm => Font.UnitsPerEm;
    public uint GlyphCount => Font.NumGlyphs;
    public uint VariationAxisCount => checked((uint)Font.VariationAxes.Count);
    public bool HasActiveVariations => Font.HasActiveFontVariations;

    public bool TryGetTable(OpenTypeTag tag, out ReadOnlyMemory<byte> table) =>
        Font.TryGetTable(tag.ToString(), out table);

    public uint GetNominalGlyph(uint codePoint) => Font.GetGlyphIndex(codePoint);

    public bool TryGetVariationGlyph(uint codePoint, uint variationSelector, out uint glyphId)
    {
        bool found = Font.TryGetVariationGlyph(codePoint, variationSelector, out ushort glyph);
        glyphId = glyph;
        return found;
    }

    public int GetHorizontalAdvance(uint glyphId)
    {
        if (glyphId > ushort.MaxValue) return 0;
        return checked((int)MathF.Round(Font.GetAdvanceWidth((ushort)glyphId, Font.UnitsPerEm)));
    }

    public int GetVerticalAdvance(uint glyphId)
    {
        if (glyphId > ushort.MaxValue) return 0;
        return checked((int)MathF.Round(Font.GetAdvanceHeight((ushort)glyphId, Font.UnitsPerEm)));
    }

    public int GetHorizontalOrigin(uint glyphId) => GetHorizontalAdvance(glyphId) / 2;

    public int GetVerticalOrigin(uint glyphId)
    {
        if (glyphId > ushort.MaxValue) return 0;
        return checked((int)MathF.Round(Font.GetVerticalOriginY((ushort)glyphId, Font.UnitsPerEm)));
    }

    public bool TryGetNormalizedVariationCoordinate(uint axisIndex, out short coordinate)
    {
        coordinate = 0;
        return axisIndex <= ushort.MaxValue &&
            Font.TryGetNormalizedVariationCoordinate((ushort)axisIndex, out coordinate);
    }

    public float GetLayoutVariationDelta(ushort outerIndex, ushort innerIndex) =>
        Font.GetLayoutVariationDelta(outerIndex, innerIndex);
}
