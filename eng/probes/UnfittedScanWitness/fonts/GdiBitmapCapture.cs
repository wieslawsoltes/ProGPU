using System.Runtime.InteropServices;
using System.Security.Cryptography;

// Independent original Windows bitmap observations; no desktop windows or input.
internal static class GdiBitmapCapture
{
    internal static object Capture(nint dc, nint font, string text)
    {
        if (text.Length is 0 or > 64 || text.Any(c => c > 127))
            throw new InvalidOperationException("The original bounded ASCII source is required.");
        if (Marshal.SizeOf<Metrics>() != 20 || Marshal.SizeOf<Matrix>() != 16)
            throw new InvalidOperationException("Unexpected Windows glyph ABI.");
        byte[] logFont = new byte[92];
        if (GetObjectW(font, logFont.Length, logFont) != logFont.Length)
            throw new InvalidOperationException("Original selected LOGFONT unavailable.");
        ushort[] glyphs = new ushort[text.Length];
        uint mapped = GetGlyphIndicesW(dc, text, text.Length, glyphs, 1);
        if (mapped != glyphs.Length || glyphs.Any(g => g == ushort.MaxValue))
            throw new InvalidOperationException("Original glyph mapping was incomplete.");
        List<object> observations = new();
        long total = 0;
        foreach (uint bitmapFormat in new uint[] { 1, 4, 5, 6 })
        {
            for (int i = 0; i < glyphs.Length; i++)
            {
                uint format = bitmapFormat | 0x80u;
                Matrix identity = new() { M11 = 65536, M22 = 65536 };
                uint size = GetGlyphOutlineW(dc, glyphs[i], format, out Metrics first, 0, null, in identity);
                if (size == uint.MaxValue || size > 1024 * 1024 || (total += size) > 4 * 1024 * 1024)
                    throw new InvalidOperationException("Original bitmap query failed or exceeded its byte bound.");
                if (first.BlackBoxX > 4096 || first.BlackBoxY > 4096)
                    throw new InvalidOperationException("Original glyph extent exceeds its observation bound.");
                uint stride = bitmapFormat == 1 ? ((first.BlackBoxX + 31) / 32) * 4 : ((first.BlackBoxX + 3) / 4) * 4;
                if (size != 0 && size != checked(stride * first.BlackBoxY))
                    throw new InvalidOperationException("Original bitmap row layout was unexpected.");
                byte[] bytes = new byte[checked((int)size)];
                if (size != 0)
                {
                    uint copied = GetGlyphOutlineW(dc, glyphs[i], format, out Metrics second, size, bytes, in identity);
                    if (copied != size || !first.Equals(second))
                        throw new InvalidOperationException("Original glyph changed during capture.");
                }
                observations.Add(new { sourceIndex = i, character = (int)text[i], glyphIndex = glyphs[i],
                    unhinted = false, format, bitmapFormat, stride, bytes = size, blackBoxX = first.BlackBoxX, blackBoxY = first.BlackBoxY,
                    originX = first.OriginX, originY = first.OriginY, cellIncX = first.CellIncX, cellIncY = first.CellIncY,
                    sha256 = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(),
                    nativeBitmapBase64 = Convert.ToBase64String(bytes) });
            }
        }
        if (observations.Count != text.Length * 4)
            throw new InvalidOperationException("Incomplete original bitmap policy inventory.");
        return new { schema = "original-gdi-bitmap-v1", qualified = false,
            text, selectedLogFontBase64 = Convert.ToBase64String(logFont),
            glyphIndices = glyphs, observations, totalBytes = total,
            limitation = "Original GDI bitmap formats and metrics; GDI+ equivalence, native geometry and dropout attribution remain independent." };
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Metrics
    {
        public uint BlackBoxX, BlackBoxY;
        public int OriginX, OriginY;
        public short CellIncX, CellIncY;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Matrix { public int M11, M12, M21, M22; }

    [DllImport("gdi32.dll", ExactSpelling = true)]
    private static extern int GetObjectW(nint value, int bytes, [Out] byte[] output);
    [DllImport("gdi32.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    private static extern uint GetGlyphIndicesW(nint dc, string text, int count, [Out] ushort[] glyphs, uint flags);
    [DllImport("gdi32.dll", ExactSpelling = true)]
    private static extern uint GetGlyphOutlineW(nint dc, uint glyph, uint format, out Metrics metrics,
        uint bytes, [Out] byte[]? output, in Matrix matrix);
}
