using System.Numerics;
using System.Runtime.InteropServices;

namespace HintedTextureSamplingProbe;

// Same authored italic-shear transform as the original unchanged package
// fixture. The input tile remains independently derived, with synthetic
// coverage, not a captured prepared GPU instance or original font atlas.
internal static class AffineFrameControl
{
    internal static byte[] Instances()
    {
        byte[] bytes = NativeFrameControl.Instances();
        ReadOnlySpan<float> first = MemoryMarshal.Cast<byte, float>(bytes.AsSpan(0, 96));
        var origin = new Vector2(first[0], first[1]);
        var basisX = new Vector2(1, .23f);
        var basisY = new Vector2(.61f, 1);
        for (int occurrence = 0; occurrence < 2; occurrence++)
        {
            Span<float> value = MemoryMarshal.Cast<byte, float>(bytes.AsSpan(occurrence * 96, 96));
            Vector2 relative = new Vector2(value[0], value[1]) - origin;
            Vector2 position = new Vector2(48.125f / 2, 48.375f / 2) +
                relative.X * basisX + relative.Y * basisY;
            value[0] = position.X; value[1] = position.Y;
            value[2] = basisX.X; value[3] = basisX.Y;
            value[4] = basisY.X; value[5] = basisY.Y;
            value[20] = .37f;
        }
        return bytes;
    }

    internal static object Provenance() => new
    {
        SourceCommit = "e4ab2a7b9091e6116ed3533b6e8e1dd0bb6f8c70",
        Source = "tests/ProGPU.Native.PackageConsumer/TextHintedGlyphPaintRenderingValidation.cs:VerifyAffinePaints",
        SourceLfSha256 = "B637462272BED13738D3B3E351D42A785B180E6436FD801F6CF85BD9C4F0582C",
        Transform = "italic-shear", BasisX = new[] { 1f, .23f }, BasisY = new[] { .61f, 1f }, Italic = .37f,
        Origin = new[] { 48.125f / 2, 48.375f / 2 }, Dpi = 2,
        Derivation = "Exact fixture position=origin+relative.X*basisX+relative.Y*basisY, unchanged96byte tile/style fields.",
        Coverage = "Original derived native-frame dimensions and controlled synthetic nonuniform R8 bytes, NOT captured font coverage.",
        Comparison = "Same-head gate0 vs certified original Text/material/bounded paths; old axis-only receipts do not qualify this affine control."
    };
}
