using System.Runtime.InteropServices;
using System.Security.Cryptography;

// Original Windows observations only. No bitmap rasterization or input.
internal static class GdiOutlineCapture
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
        foreach (bool unhinted in new[] { false, true })
        {
            for (int i = 0; i < glyphs.Length; i++)
            {
                uint format = 2u | 0x80u | (unhinted ? 0x100u : 0u);
                Matrix identity = new() { M11 = 65536, M22 = 65536 };
                uint size = GetGlyphOutlineW(dc, glyphs[i], format, out Metrics first, 0, null, in identity);
                if (size == uint.MaxValue || size > 1024 * 1024 || (total += size) > 4 * 1024 * 1024)
                    throw new InvalidOperationException("Original outline query failed or exceeded its byte bound.");
                byte[] bytes = new byte[checked((int)size)];
                if (size != 0)
                {
                    uint copied = GetGlyphOutlineW(dc, glyphs[i], format, out Metrics second, size, bytes, in identity);
                    if (copied != size || !first.Equals(second))
                        throw new InvalidOperationException("Original glyph changed during capture.");
                }
                observations.Add(new { sourceIndex = i, character = (int)text[i], glyphIndex = glyphs[i],
                    unhinted, format, bytes = size, blackBoxX = first.BlackBoxX, blackBoxY = first.BlackBoxY,
                    originX = first.OriginX, originY = first.OriginY, cellIncX = first.CellIncX, cellIncY = first.CellIncY,
                    sha256 = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(),
                    nativeOutlineBase64 = Convert.ToBase64String(bytes) });
            }
        }
        return new { schema = "original-gdi-outline-v1", qualified = false,
            text, selectedLogFontBase64 = Convert.ToBase64String(logFont),
            glyphIndices = glyphs, observations, totalBytes = total,
            limitation = "Original GDI outlines and metrics; no inference of GDI+ policy, dropout or ClearType pixels." };
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
