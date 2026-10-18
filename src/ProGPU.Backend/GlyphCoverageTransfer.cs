using System.Runtime.InteropServices;

namespace ProGPU.Backend;

/// <summary>
/// Immutable paint-time coverage transfer. Default preserves the renderer's
/// existing coverage policy; shaping, glyph origins and font execution are
/// independent of this value.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public readonly struct GlyphCoverageTransfer : IEquatable<GlyphCoverageTransfer>
{
    private readonly uint _encoding;

    private GlyphCoverageTransfer(uint encoding) => _encoding = encoding;

    /// <summary>Zero is the legacy policy; 1 through 13 retain contrast 0 through 12.</summary>
    public uint Encoding => _encoding;

    /// <summary>
    /// Selects the original GDI+ grayscale contrast transfer. This does not
    /// select a spatial sampler, ClearType, GDI font quality or glyph hinting.
    /// </summary>
    public static GlyphCoverageTransfer GdiPlusGrayscale(int contrast)
    {
        if ((uint)contrast > 12u)
            throw new ArgumentOutOfRangeException(nameof(contrast));
        return new GlyphCoverageTransfer((uint)contrast + 1u);
    }

    public bool Equals(GlyphCoverageTransfer other) => _encoding == other._encoding;
    public override bool Equals(object? obj) => obj is GlyphCoverageTransfer other && Equals(other);
    public override int GetHashCode() => (int)_encoding;
    public static bool operator ==(GlyphCoverageTransfer left, GlyphCoverageTransfer right) => left.Equals(right);
    public static bool operator !=(GlyphCoverageTransfer left, GlyphCoverageTransfer right) => !left.Equals(right);
}
