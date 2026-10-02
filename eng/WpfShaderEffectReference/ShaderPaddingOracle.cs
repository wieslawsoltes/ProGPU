// Independent, device-free expected frames and colors for the original Windows
// SOFTWARE reference. This is not a renderer or a source-admission policy.
internal enum PaddingOutput { Input, Constant, Uv, Derivatives, Image }

internal readonly record struct PaddingFrame(int Left, int Top, int Width, int Height)
{
    internal bool Contains(int x, int y) => x >= Left && y >= Top && x < Left + Width && y < Top + Height;
}

internal sealed record PaddingCase(string Name, int NativeVariant, PaddingOutput Output,
    double Dpi, double Top, double Bottom, double Left, double Right, PaddingFrame Frame);

internal static class ShaderPaddingOracle
{
    internal static readonly PaddingFrame Content = new(16, 16, 16, 8);
    internal static readonly PaddingFrame Clip = new(14, 12, 28, 20);

    internal static PaddingCase[] Cases() =>
    [
        new("padding-input-zero", 0, PaddingOutput.Input, 1, 0, 0, 0, 0, Content),
        new("padding-input-asymmetric", 1, PaddingOutput.Input, 1, 2, 6, 4, 12, new(12, 14, 32, 16)),
        new("padding-constant-asymmetric", 2, PaddingOutput.Constant, 1, 2, 6, 4, 12, new(12, 14, 32, 16)),
        new("padding-uv-asymmetric", 3, PaddingOutput.Uv, 1, 2, 6, 4, 12, new(12, 14, 32, 16)),
        new("padding-derivatives-dpi1", 4, PaddingOutput.Derivatives, 1, 2, 6, 4, 12, new(12, 14, 32, 16)),
        new("padding-derivatives-dpi2", 5, PaddingOutput.Derivatives, 2, 1, 3, 2, 6, new(12, 14, 32, 16)),
        new("padding-image-expanded-frame", 6, PaddingOutput.Image, 1, 2, 6, 4, 12, new(12, 14, 32, 16)),
        new("padding-image-shifted-frame", 7, PaddingOutput.Image, 1, 4, 4, 2, 14, new(14, 12, 32, 16)),
        new("padding-input-reset", 8, PaddingOutput.Input, 1, 0, 0, 0, 0, Content),
        new("padding-original-double", 11, PaddingOutput.Input, 1, 2 + Math.ScaleB(1, -25), 6, 4, 12, new(12, 14, 32, 16)),
        // These consecutive captures mutate only padding on the SAME effect and
        // receiving visual. Constant ink makes both expansion and reset visible.
        new("padding-constant-generation-zero", -1, PaddingOutput.Constant, 1, 0, 0, 0, 0, Content),
        new("padding-constant-generation-expand", -1, PaddingOutput.Constant, 1, 2, 6, 4, 12, new(12, 14, 32, 16)),
        new("padding-constant-generation-reset", -1, PaddingOutput.Constant, 1, 0, 0, 0, 0, Content)
    ];

    internal static byte Expected(PaddingCase input, int x, int y, int bgraChannel, bool unavailable)
    {
        if ((uint)bgraChannel > 3 || (uint)x >= 64 || (uint)y >= 64)
            throw new ArgumentOutOfRangeException(nameof(bgraChannel));
        if (bgraChannel == 3) return 255;
        if (!Clip.Contains(x, y)) return 0;
        // Preserve the existing architecture-specific negative distinction:
        // implicit input remains white; unavailable secondary input contributes
        // no shader color. Neither outcome qualifies original ARM64 shaders.
        if (unavailable)
            return input.Output != PaddingOutput.Image && Content.Contains(x, y) ? (byte)255 : (byte)0;
        if (!input.Frame.Contains(x, y)) return 0;
        return input.Output switch
        {
            PaddingOutput.Input => Content.Contains(x, y) ? (byte)255 : (byte)0,
            PaddingOutput.Constant => bgraChannel switch { 0 => 191, 1 => 128, _ => 64 },
            PaddingOutput.Uv => bgraChannel switch
            {
                2 => Quantize((x - input.Frame.Left + .5) / input.Frame.Width),
                1 => Quantize((y - input.Frame.Top + .5) / input.Frame.Height),
                _ => 0
            },
            PaddingOutput.Derivatives => bgraChannel switch
            {
                2 => Quantize(1.0 / input.Frame.Width),
                1 => Quantize(1.0 / input.Frame.Height),
                _ => 0
            },
            PaddingOutput.Image => bgraChannel == (x - input.Frame.Left < input.Frame.Width / 2 ? 2 : 1) ? (byte)255 : (byte)0,
            _ => throw new InvalidOperationException("Unknown padding reference output.")
        };
    }

    private static byte Quantize(double normalized) => checked((byte)Math.Round(normalized * 255, MidpointRounding.ToEven));

    internal static int VerifyArithmeticControls()
    {
        int count = 0;
        void Require(bool condition)
        {
            if (!condition) throw new InvalidOperationException("Original padding expected-value control failed.");
            ++count;
        }
        PaddingCase[] inputs = Cases();
        Require(inputs.Length == 13 && inputs.Select(input => input.Name).Distinct().Count() == 13);
        Require(inputs.Count(input => input.NativeVariant >= 0) == 10);
        Require(!inputs.Any(input => input.NativeVariant is 9 or 10));
        foreach (PaddingCase input in inputs)
        {
            // Check authored frames independently against original observable
            // local-float inflation before positive-axis device mapping. The
            // capture itself uses only the original public double properties.
            float left = (float)(Content.Left / input.Dpi) - (float)input.Left;
            float top = (float)(Content.Top / input.Dpi) - (float)input.Top;
            float right = (float)((Content.Left + Content.Width) / input.Dpi) + (float)input.Right;
            float bottom = (float)((Content.Top + Content.Height) / input.Dpi) + (float)input.Bottom;
            Require(left * input.Dpi == input.Frame.Left && top * input.Dpi == input.Frame.Top &&
                right * input.Dpi == input.Frame.Left + input.Frame.Width &&
                bottom * input.Dpi == input.Frame.Top + input.Frame.Height);
        }
        Require(inputs[9].Top != 2 && (float)inputs[9].Top == 2);
        Require(Expected(inputs[1], 14, 14, 2, false) == 0);
        Require(Expected(inputs[2], 14, 14, 2, false) == 64);
        Require(Expected(inputs[2], 13, 14, 2, false) == 0);
        Require(Expected(inputs[2], 41, 29, 0, false) == 191);
        Require(Expected(inputs[2], 42, 29, 0, false) == 0);
        Require(Expected(inputs[3], 14, 14, 2, false) == 20);
        Require(Expected(inputs[3], 14, 14, 1, false) == 8);
        Require(Expected(inputs[4], 16, 16, 2, false) == 8 && Expected(inputs[4], 16, 16, 1, false) == 16);
        Require(Expected(inputs[5], 16, 16, 2, false) == 8 && Expected(inputs[5], 16, 16, 1, false) == 16);
        Require(Expected(inputs[6], 27, 16, 2, false) == 255 && Expected(inputs[6], 28, 16, 1, false) == 255);
        Require(Expected(inputs[7], 29, 16, 2, false) == 255 && Expected(inputs[7], 30, 16, 1, false) == 255);
        Require(Expected(inputs[10], 14, 14, 2, false) == 0 && Expected(inputs[11], 14, 14, 2, false) == 64 &&
            Expected(inputs[12], 14, 14, 2, false) == 0);
        Require(Expected(inputs[2], 14, 14, 2, true) == 0 && Expected(inputs[2], 16, 16, 2, true) == 255);
        Require(Expected(inputs[6], 16, 16, 2, true) == 0 && Expected(inputs[6], 16, 16, 3, true) == 255);
        return count;
    }
}
