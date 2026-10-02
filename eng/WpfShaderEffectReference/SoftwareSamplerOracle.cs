// Independent expected-value arithmetic for the original WPF SOFTWARE probe.
// This is not a ProGPU shader, renderer or portable sampling policy.
internal static class SoftwareSamplerOracle
{
    internal static int Coefficient(float value)
    {
        float scaled = value * 65536f;
        if (!float.IsFinite(scaled)) throw new InvalidOperationException("Nonfinite software coefficient.");
        return checked((int)Math.Floor((double)scaled + .5));
    }

    internal static long Coordinate(float scale, float translation, int devicePixel)
        => checked((long)Coefficient(scale) * devicePixel
            + Coefficient(scale * .5f - .5f - translation * scale));

    internal static int Address(long index, bool mirror)
    {
        int period = mirror ? 4 : 2;
        int wrapped = (int)((index % period + period) % period);
        return mirror ? (wrapped is 1 or 2 ? 1 : 0) : wrapped;
    }

    internal static int Fraction(long coordinate) => (int)((coordinate >> 8) & 255);

    internal static byte ApplyOpacity(int color, int alpha)
    {
        if ((uint)color > 255 || (uint)alpha > 65536)
            throw new InvalidOperationException("Invalid software color or opacity.");
        return checked((byte)((color * (long)alpha + 32768) >> 16));
    }

    internal static byte Checkerboard(long u, long v, bool mirrorX, bool mirrorY, bool green, int alpha)
    {
        long left = u >> 16, top = v >> 16;
        int horizontal = Fraction(u), vertical = Fraction(v);
        int weighted = 0;
        for (int dy = 0; dy < 2; ++dy)
        for (int dx = 0; dx < 2; ++dx)
        {
            bool sourceGreen = Address(left + dx, mirrorX) != Address(top + dy, mirrorY);
            if (sourceGreen != green) continue;
            int wx = dx == 0 ? 256 - horizontal : horizontal;
            int wy = dy == 0 ? 256 - vertical : vertical;
            weighted += 255 * wx * wy;
        }
        int brushByte = (weighted + 32768) >> 16;
        return ApplyOpacity(brushByte, alpha);
    }

    internal static int VerifyArithmeticControls()
    {
        int count = 0;
        void Require(bool condition)
        {
            if (!condition) throw new InvalidOperationException("Original software arithmetic control failed.");
            ++count;
        }
        Require(Coefficient(.5f / 65536f) == 1);
        Require(Coefficient(-.5f / 65536f) == 0);
        Require(Coefficient(1f / 8f) == 8192);
        Require(Coefficient(1f / 12f) == 5461);
        Require(Coordinate(1f / 8f, 0, 0) == -28672);
        Require(Coordinate(1f / 12f, 0, 0) == -30037);
        Require((-1L >> 16) == -1 && Fraction(-1) == 255);
        Require(Fraction(255) == 0 && Fraction(256) == 1);
        int[] mirror = [1, 0, 0, 1, 1, 0, 0, 1];
        for (int i = 0; i < mirror.Length; ++i) Require(Address(i - 2, true) == mirror[i]);
        Require(Checkerboard(32768, 0, false, false, true, 65536) == 128);
        Require(ApplyOpacity(121, 32768) == 61);
        Require(Coordinate(1f / 8f, 8, 16) == Coordinate(1f / 8f, 0, 8));
        return count;
    }
}
