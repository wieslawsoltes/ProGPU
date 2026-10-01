using System.Runtime.InteropServices;

namespace HintedTextureSamplingProbe;

// Original caller values from the verified run36882608788 failure receipt.
// Only the frame is derived: no original raster pixels were captured. The native
// producer is 8adb's progpu_native_glyph_execution.cpp:1045-1058,1235-1260;
// the96byte layout is progpu_native_gpu_records.hpp:245-256. No font is loaded.
internal static class NativeFrameControl
{
    internal const float MinimumX = 1.125f, MinimumY = .171875f;
    internal const float MaximumX = 12.09375f, MaximumY = 13.171875f;
    internal const int Padding = 4;
    internal static float BearingX => MathF.Floor(MinimumX) - Padding;
    internal static float BearingY => MathF.Floor(-MaximumY) - Padding;
    internal static int Width => checked((int)(MathF.Ceiling(MaximumX) + Padding - BearingX));
    internal static int Height => checked((int)(MathF.Ceiling(-MinimumY) + Padding - BearingY));

    internal static byte[] Instances()
    {
        // Basis, flags, scale, brush/paint indices and reference texel colors
        // already match the original producer. Replace only captured placement
        // and the actual padded native frame, without another device snap.
        byte[] bytes = Program.Instances();
        for (int occurrence = 0; occurrence < 2; occurrence++)
        {
            Span<float> value = MemoryMarshal.Cast<byte, float>(bytes.AsSpan(occurrence * 96, 96));
            value[0] = occurrence == 0 ? 4.0625f : 4.25f;
            value[1] = occurrence == 0 ? 13.1875f : 13.25f;
            value[6] = BearingX; value[7] = BearingY; value[8] = Width; value[9] = Height;
            value[10] = 2; value[11] = 2; value[12] = 2 + Width; value[13] = 2 + Height;
        }
        return bytes;
    }

    internal static byte[] Atlas() => Program.Atlas(2, 2, Width, Height, Padding);

    internal static object Provenance() => new
    {
        SourceCommit = "d51e9ed6d9ea149c0bf3da255809d35054ec6247",
        RunId = "36882608788", ArtifactId = "11172665868",
        ReceiptSha256 = "4A52006544EE42D54459D677B5F9BC58741A37E41EE105D410650E76AE856345",
        OutlineSha256 = "B1702EF2FC01190C4CD2D849E770DD77B1AC8E5DE0CCB1ACCB1E8C08E1FD0C6B",
        ReferenceGlyphSha256 = "22D50E78EE840F7AE08663196682C6A40BF056BD634D94887A1965CFBBC58B65",
        FontSha256 = "40D692FCE188E4471E2B3CBA937BE967878F631AD3EBBBDCD587687C7EBE0C82",
        OriginalOutlineMinimum = new[] { MinimumX, MinimumY },
        OriginalOutlineMaximum = new[] { MaximumX, MaximumY },
        OriginalPositions = new[] { new[] { 4.0625f, 13.1875f }, new[] { 4.25f, 13.25f } },
        OriginalRasterScale = 1, OriginalSubpixelX = 0,
        NativePadding = Padding, DerivedBearing = new[] { BearingX, BearingY },
        DerivedTileExtent = new[] { Width, Height },
        Derivation = "Original y-up bounds at rasterScale1; floor(minX)-4, floor(-maxY)-4; ceil(maxX)+4-startX, ceil(-minY)+4-startY; original native first tile(2,2).",
        InstanceColor = "Original reference-glyphs.bin texel-normalized RGB and alpha; texture paint ignores the differing captured semantic glyph RGB.",
        Coverage = "Controlled synthetic nonuniform R8 interior, same relative formula as default, four clear padding texels; NOT original hinted O coverage or captured GPU atlas."
    };
}
