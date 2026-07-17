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

    public bool TryGetTable(OpenTypeTag tag, out ReadOnlyMemory<byte> table) =>
        Font.TryGetTable(tag.ToString(), out table);

    public uint GetNominalGlyph(uint codePoint) => Font.GetGlyphIndex(codePoint);

    public bool TryGetVariationGlyph(uint codePoint, uint variationSelector, out uint glyphId)
    {
        // cmap format 14 support is implemented in the standalone font model.
        // Returning false is distinct from returning glyph zero and keeps the
        // existing adapter honest until that table parser is moved.
        glyphId = 0;
        return false;
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
}
