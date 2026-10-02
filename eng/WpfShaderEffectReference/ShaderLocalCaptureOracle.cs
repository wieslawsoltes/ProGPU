// Independent original-Windows SOFTWARE capture expectations. Allocation is
// edge-based in scale space, never ceil(content extent) or translated bounds.
internal enum LocalCaptureHistory { Flat, Nested, SeparatelyNarrowed }

internal sealed record LocalCaptureCase(string Name, PaddingOutput Output, double Dpi,
    double Top, double Bottom, double Left, double Right, PaddingFrame Allocation,
    double DeviceX = 2, double DeviceY = 3, bool Clipped = false,
    LocalCaptureHistory History = LocalCaptureHistory.Flat, bool SquareUv = false)
{
    internal bool IntegralPlacement => DeviceX == Math.Floor(DeviceX) && DeviceY == Math.Floor(DeviceY);
    internal bool NativeCandidate => IntegralPlacement && History != LocalCaptureHistory.SeparatelyNarrowed;
    internal double FinalLeft => Allocation.Left + DeviceX;
    internal double FinalTop => Allocation.Top + DeviceY;
    internal PaddingFrame Clip => Clipped ? new(38, 37, 20, 10) : new(0, 0, 96, 64);
}

internal static class ShaderLocalCaptureOracle
{
    internal const int Width = 96, Height = 64;
    internal const double ContentX = 16.75, ContentY = 16.25, ContentWidth = 15.5, ContentHeight = 7.5;
    internal const double SeparateScale = 1 + 1.0 / (1 << 24);

    // Original aliased visual bounds are an OUTPUT clip, distinct from the
    // outward allocation. WPF first rounds to 28.4 (halves up), then applies
    // its top/left-inclusive half rule. Keep these two operations explicit.
    internal static int AliasedOutputEdge(float edge)
    {
        int fixedEdge = checked((int)Math.Floor((double)(edge * 16f) + .5));
        return checked(fixedEdge + 7) >> 4;
    }

    internal static PaddingFrame OutputClip(LocalCaptureCase input)
    {
        // Every authored history has unit original FLOAT local scale. The
        // separately narrowed double-history control also has float scale1.
        float l = ((float)ContentX - (float)input.Left) * (float)input.Dpi + (float)input.DeviceX;
        float t = ((float)ContentY - (float)input.Top) * (float)input.Dpi + (float)input.DeviceY;
        float r = ((float)(ContentX + ContentWidth) + (float)input.Right) * (float)input.Dpi + (float)input.DeviceX;
        float b = ((float)(ContentY + ContentHeight) + (float)input.Bottom) * (float)input.Dpi + (float)input.DeviceY;
        int left = Math.Max(input.Clip.Left,AliasedOutputEdge(l));
        int top = Math.Max(input.Clip.Top,AliasedOutputEdge(t));
        int right = Math.Min(input.Clip.Left + input.Clip.Width,AliasedOutputEdge(r));
        int bottom = Math.Min(input.Clip.Top + input.Clip.Height,AliasedOutputEdge(b));
        return new(left,top,Math.Max(0,right - left),Math.Max(0,bottom - top));
    }

    internal static LocalCaptureCase[] Cases() =>
    [
        new("local-input-zero-dpi1", PaddingOutput.Input, 1, 0, 0, 0, 0, new(16,16,17,8)),
        new("local-constant-zero-dpi1", PaddingOutput.Constant, 1, 0, 0, 0, 0, new(16,16,17,8)),
        new("local-input-asymmetric-dpi1", PaddingOutput.Input, 1, .25,1.25,.5,1.5, new(16,16,18,9)),
        new("local-constant-asymmetric-dpi1", PaddingOutput.Constant, 1, .25,1.25,.5,1.5, new(16,16,18,9)),
        new("local-uv-asymmetric-dpi1", PaddingOutput.Uv, 1, 2,6.25,4,11.5, new(12,14,32,16)),
        new("local-derivatives-asymmetric-dpi1", PaddingOutput.Derivatives, 1, .25,1.25,.5,1.5, new(16,16,18,9)),
        new("local-image-asymmetric-dpi1", PaddingOutput.Image, 1, .25,1.25,.5,1.5, new(16,16,18,9)),
        new("local-input-zero-dpi2", PaddingOutput.Input, 2, 0,0,0,0, new(33,32,32,16)),
        new("local-constant-asymmetric-dpi2", PaddingOutput.Constant, 2, .25,1.25,.5,1.5, new(32,32,36,18)),
        new("local-uv-zero-dpi2", PaddingOutput.Uv, 2, 0,0,0,0, new(33,32,32,16)),
        new("local-derivatives-asymmetric-dpi2", PaddingOutput.Derivatives, 2, .25,1.25,.5,1.5, new(32,32,36,18)),
        new("local-image-asymmetric-dpi2", PaddingOutput.Image, 2, .25,1.25,.5,1.5, new(32,32,36,18)),
        new("local-constant-final-clip", PaddingOutput.Constant, 2, .25,1.25,.5,1.5, new(32,32,36,18), Clipped:true),
        new("local-generation-zero", PaddingOutput.Constant, 1, 0,0,0,0, new(16,16,17,8)),
        new("local-generation-expand", PaddingOutput.Constant, 1, .25,1.25,.5,1.5, new(16,16,18,9)),
        new("local-generation-reset", PaddingOutput.Constant, 1, 0,0,0,0, new(16,16,17,8)),
        new("local-deferred-final-translation", PaddingOutput.Constant, 1, 0,0,0,0, new(16,16,17,8), 2.25,3.5),
        new("local-nested-order-dpi1", PaddingOutput.Constant, 1, 0,0,0,0, new(16,16,17,8), History:LocalCaptureHistory.Nested),
        new("local-nested-order-dpi2", PaddingOutput.Constant, 2, .25,1.25,.5,1.5, new(32,32,36,18), History:LocalCaptureHistory.Nested),
        new("local-deferred-float-history", PaddingOutput.Constant, 1, 0,0,0,0, new(16,16,17,8), History:LocalCaptureHistory.SeparatelyNarrowed),
        new("local-deferred-final-uv-squared", PaddingOutput.Uv, 1, 0,0,0,0, new(16,16,17,8), 2.25,3.5, SquareUv:true)
    ];

    internal static byte Expected(LocalCaptureCase input, int x, int y, int channel)
    {
        if ((uint)x >= Width || (uint)y >= Height || (uint)channel > 3)
            throw new ArgumentOutOfRangeException(nameof(channel));
        if (input.Output == PaddingOutput.Input || !input.IntegralPlacement)
            throw new ArgumentException("This case requires its separately captured original drawing baseline.");
        if (channel == 3) return 255;
        if (!OutputClip(input).Contains(x,y) || x < input.FinalLeft || y < input.FinalTop ||
            x >= input.FinalLeft + input.Allocation.Width || y >= input.FinalTop + input.Allocation.Height) return 0;
        return input.Output switch
        {
            PaddingOutput.Constant => channel switch { 0 => 191, 1 => 128, _ => 64 },
            // SOFTWARE t0 starts at integer destination samples. These two
            // authored UV frames are powers of two, so inverse/offset and scan
            // increments are exact and do not conflate phase with FP packing.
            PaddingOutput.Uv => channel switch
            {
                2 => Quantize((x - input.FinalLeft) / input.Allocation.Width),
                1 => Quantize((y - input.FinalTop) / input.Allocation.Height),
                _ => 0
            },
            PaddingOutput.Derivatives => channel switch
            {
                2 => Quantize(1.0 / input.Allocation.Width),
                1 => Quantize(1.0 / input.Allocation.Height),
                _ => 0
            },
            PaddingOutput.Image => channel == (x - input.FinalLeft < input.Allocation.Width / 2 ? 2 : 1) ? (byte)255 : (byte)0,
            _ => throw new InvalidOperationException("Unknown original capture output.")
        };
    }

    private static byte Quantize(double value) => checked((byte)Math.Round(value * 255, MidpointRounding.ToEven));

    internal static byte SquaredUv(LocalCaptureCase input,int x,int y,int channel) => channel switch
    {
        2 => Quantize(Math.Pow((x - input.FinalLeft) / input.Allocation.Width,2)),
        1 => Quantize(Math.Pow((y - input.FinalTop) / input.Allocation.Height,2)),
        0 => 0,
        3 => 255,
        _ => throw new ArgumentOutOfRangeException(nameof(channel))
    };

    internal static int VerifyArithmeticControls()
    {
        int count = 0;
        void Require(bool condition)
        {
            if (!condition) throw new InvalidOperationException("Original local capture arithmetic control failed.");
            ++count;
        }
        LocalCaptureCase[] cases = Cases();
        Require(cases.Length == 21 && cases.Select(x => x.Name).Distinct().Count() == 21);
        Require(cases.Count(x => x.NativeCandidate) == 18);
        Require(cases.Count(x => !x.IntegralPlacement) == 2);
        foreach (LocalCaptureCase input in cases)
        {
            float l = ((float)ContentX - (float)input.Left) * (float)input.Dpi;
            float t = ((float)ContentY - (float)input.Top) * (float)input.Dpi;
            float r = ((float)(ContentX + ContentWidth) + (float)input.Right) * (float)input.Dpi;
            float b = ((float)(ContentY + ContentHeight) + (float)input.Bottom) * (float)input.Dpi;
            Require(MathF.Floor(l) == input.Allocation.Left && MathF.Floor(t) == input.Allocation.Top &&
                MathF.Ceiling(r) == input.Allocation.Left + input.Allocation.Width &&
                MathF.Ceiling(b) == input.Allocation.Top + input.Allocation.Height);
            Require(input.FinalLeft >= 0 && input.FinalTop >= 0 &&
                input.FinalLeft + input.Allocation.Width <= Width && input.FinalTop + input.Allocation.Height <= Height);
        }
        Require(cases[0].Allocation.Width == 17 && Math.Ceiling(ContentWidth) == 16);
        Require(cases[7].Allocation.Width == 32 && Math.Ceiling(ContentWidth * 2) == 31);
        Require(cases[2].Allocation.Height == 9 && cases[8].Allocation.Height == 18);
        Require((float)SeparateScale == 1 && (float)(SeparateScale * SeparateScale) != 1);
        Require((float)SeparateScale * (float)SeparateScale == 1);
        Require(2 * .5 == 1 && 2 * 2 != 2); // scale cancellation does not permit translating in the wrong order
        Require(Expected(cases[1],19,19,2) == 64 && Expected(cases[1],33,26,2) == 64);
        Require(Expected(cases[1],17,19,2) == 0 && Expected(cases[1],35,19,2) == 0);
        Require(Expected(cases[3],35,27,0) == 191 && Expected(cases[1],35,27,0) == 0);
        Require(Expected(cases[4],14,17,2) == 0 && Expected(cases[4],15,17,2) == 8);
        Require(Expected(cases[4],15,18,1) == 16 && Expected(cases[4],30,25,2) == 128);
        Require(Expected(cases[9],35,35,2) == 0 && Expected(cases[9],36,36,1) == 16);
        Require(Expected(cases[4],14,17,2) != Quantize(.5 / 32));
        Require(Expected(cases[5],20,20,2) == 14 && Expected(cases[5],20,20,1) == 28);
        Require(Expected(cases[10],40,40,2) == 7 && Expected(cases[10],40,40,1) == 14);
        Require(Expected(cases[6],26,20,2) == 255 && Expected(cases[6],27,20,1) == 255);
        Require(Expected(cases[11],51,40,2) == 255 && Expected(cases[11],52,40,1) == 255);
        Require(Expected(cases[12],38,37,2) == 64 && Expected(cases[12],37,37,2) == 0);
        Require(Expected(cases[12],57,46,2) == 64 && Expected(cases[12],58,46,2) == 0);
        Require(cases[13].Allocation == cases[15].Allocation && cases[13].Allocation != cases[14].Allocation);
        Require(cases[16].FinalLeft == 18.25 && cases[16].FinalTop == 19.5 && !cases[16].NativeCandidate);
        Require(cases[17].Allocation == cases[1].Allocation && cases[18].Allocation == cases[8].Allocation);
        Require(!cases[19].NativeCandidate && cases[19].Allocation == cases[1].Allocation);
        Require(cases[20].SquareUv && !cases[20].NativeCandidate && SquaredUv(cases[20],20,20,2) == 3);
        Require(SquaredUv(cases[20],20,20,1) == 1 && SquaredUv(cases[20],30,24,2) == 122);
        Require(AliasedOutputEdge(.5f) == 0 && AliasedOutputEdge(-.5f) == -1);
        Require(AliasedOutputEdge(17f / 32) == 1 && AliasedOutputEdge(-15f / 32) == 0);
        Require(AliasedOutputEdge(MathF.BitDecrement(17f / 32)) == 0);
        Require(AliasedOutputEdge(MathF.BitDecrement(-15f / 32)) == -1);
        Require(AliasedOutputEdge(18.5f) == 18 && AliasedOutputEdge(18.53125f) == 19);
        Require(AliasedOutputEdge(-18.5f) == -19 && AliasedOutputEdge(-18.46875f) == -18);
        Require(OutputClip(cases[1]) == new PaddingFrame(19,19,15,8));
        Require(OutputClip(cases[3]) == new PaddingFrame(18,19,18,9));
        Require(OutputClip(cases[8]) == new PaddingFrame(34,35,35,18));
        Require(OutputClip(cases[9]) == new PaddingFrame(35,35,31,15));
        Require(OutputClip(cases[12]) == cases[12].Clip);
        Require(OutputClip(cases[16]) == new PaddingFrame(19,20,15,7));
        Require(OutputClip(cases[17]) == OutputClip(cases[1]) && OutputClip(cases[19]) == OutputClip(cases[1]));
        Require(Expected(cases[1],18,19,2) == 0 && Expected(cases[1],34,26,2) == 0);
        Require(Expected(cases[4],14,18,1) == 0 && Expected(cases[4],15,18,1) == 16);
        Require(Expected(cases[9],66,35,2) == 0 && Expected(cases[9],65,35,2) == 239);
        Require(Expected(cases[8],69,35,0) == 0 && Expected(cases[8],68,35,0) == 191);
        Require(Expected(cases[10],68,35,2) == 7 && Expected(cases[10],69,35,2) == 0);
        return count;
    }
}
