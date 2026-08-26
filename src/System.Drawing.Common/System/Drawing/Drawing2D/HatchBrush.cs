using System.Numerics;

namespace System.Drawing.Drawing2D;

public enum HatchStyle
{
    Horizontal = 0,
    Min = Horizontal,
    Vertical = 1,
    ForwardDiagonal = 2,
    BackwardDiagonal = 3,
    Cross = 4,
    LargeGrid = Cross,
    Max = LargeGrid,
    DiagonalCross = 5,
    Percent05 = 6,
    Percent10 = 7,
    Percent20 = 8,
    Percent25 = 9,
    Percent30 = 10,
    Percent40 = 11,
    Percent50 = 12,
    Percent60 = 13,
    Percent70 = 14,
    Percent75 = 15,
    Percent80 = 16,
    Percent90 = 17,
    LightDownwardDiagonal = 18,
    LightUpwardDiagonal = 19,
    DarkDownwardDiagonal = 20,
    DarkUpwardDiagonal = 21,
    WideDownwardDiagonal = 22,
    WideUpwardDiagonal = 23,
    LightVertical = 24,
    LightHorizontal = 25,
    NarrowVertical = 26,
    NarrowHorizontal = 27,
    DarkVertical = 28,
    DarkHorizontal = 29,
    DashedDownwardDiagonal = 30,
    DashedUpwardDiagonal = 31,
    DashedHorizontal = 32,
    DashedVertical = 33,
    SmallConfetti = 34,
    LargeConfetti = 35,
    ZigZag = 36,
    Wave = 37,
    DiagonalBrick = 38,
    HorizontalBrick = 39,
    Weave = 40,
    Plaid = 41,
    Divot = 42,
    DottedGrid = 43,
    DottedDiamond = 44,
    Shingle = 45,
    Trellis = 46,
    Sphere = 47,
    SmallGrid = 48,
    SmallCheckerBoard = 49,
    LargeCheckerBoard = 50,
    OutlinedDiamond = 51,
    SolidDiamond = 52
}

public sealed class HatchBrush : Brush, ICloneable
{
    private readonly HatchStyle _hatchStyle;
    private readonly Color _foregroundColor;
    private readonly Color _backgroundColor;
    private bool _disposed;

    public HatchBrush(HatchStyle hatchstyle, Color foreColor)
        : this(hatchstyle, foreColor, Color.Black)
    {
    }

    public HatchBrush(HatchStyle hatchstyle, Color foreColor, Color backColor)
    {
        if ((uint)hatchstyle > (uint)HatchStyle.SolidDiamond)
        {
            throw new ArgumentException("Parameter is not valid.", nameof(hatchstyle));
        }

        _hatchStyle = hatchstyle;
        _foregroundColor = foreColor;
        _backgroundColor = backColor;
    }

    public Color BackgroundColor
    {
        get
        {
            ThrowIfDisposed();
            return _backgroundColor;
        }
    }

    public Color ForegroundColor
    {
        get
        {
            ThrowIfDisposed();
            return _foregroundColor;
        }
    }

    public HatchStyle HatchStyle
    {
        get
        {
            ThrowIfDisposed();
            return _hatchStyle;
        }
    }

    public object Clone()
    {
        ThrowIfDisposed();
        return new HatchBrush(_hatchStyle, _foregroundColor, _backgroundColor);
    }

    public override ProGPU.Vector.Brush ToProGpuBrush()
        => ToProGpuBrush(Point.Empty);

    internal ProGPU.Vector.Brush ToProGpuBrush(Point renderingOrigin)
    {
        ThrowIfDisposed();
        return new ProGPU.Vector.TilePatternBrush(
            HatchPatternMasks.Get(_hatchStyle),
            ToVector(_foregroundColor),
            ToVector(_backgroundColor),
            new Vector2(renderingOrigin.X, renderingOrigin.Y));
    }

    public override void Dispose() => _disposed = true;

    private static Vector4 ToVector(Color color) =>
        new(color.R / 255f, color.G / 255f, color.B / 255f, color.A / 255f);

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }
}

internal static class HatchPatternMasks
{
    private static readonly ulong[] s_patterns = CreatePatterns();

    internal static ulong Get(HatchStyle style) => s_patterns[(int)style];

    private static ulong[] CreatePatterns()
    {
        var patterns = new ulong[53];
        patterns[0] = Horizontal(spacing: 4, thickness: 1);
        patterns[1] = Vertical(spacing: 4, thickness: 1);
        patterns[2] = Diagonal(downward: true, spacing: 8, thickness: 1);
        patterns[3] = Diagonal(downward: false, spacing: 8, thickness: 1);
        patterns[4] = patterns[0] | patterns[1];
        patterns[5] = patterns[2] | patterns[3];

        int[] densityCounts = [3, 6, 13, 16, 19, 26, 32, 38, 45, 48, 51, 58];
        for (int index = 0; index < densityCounts.Length; index++)
        {
            patterns[6 + index] = Density(densityCounts[index]);
        }

        patterns[18] = Diagonal(true, 4, 1);
        patterns[19] = Diagonal(false, 4, 1);
        patterns[20] = Diagonal(true, 4, 2);
        patterns[21] = Diagonal(false, 4, 2);
        patterns[22] = Diagonal(true, 8, 3);
        patterns[23] = Diagonal(false, 8, 3);
        patterns[24] = Vertical(2, 1);
        patterns[25] = Horizontal(2, 1);
        patterns[26] = Vertical(3, 1);
        patterns[27] = Horizontal(3, 1);
        patterns[28] = Vertical(4, 2);
        patterns[29] = Horizontal(4, 2);
        patterns[30] = DashedDiagonal(true);
        patterns[31] = DashedDiagonal(false);
        patterns[32] = DashedHorizontal();
        patterns[33] = DashedVertical();
        patterns[34] = Rows(0x22, 0x00, 0x08, 0x00, 0x80, 0x04, 0x00, 0x11);
        patterns[35] = Rows(0x33, 0x33, 0x00, 0x0C, 0x0C, 0x00, 0xC0, 0xC0);
        patterns[36] = Rows(0x81, 0x42, 0x24, 0x18, 0x00, 0x00, 0x00, 0x00);
        patterns[37] = Rows(0x00, 0x42, 0xA5, 0x18, 0x00, 0x00, 0x00, 0x00);
        patterns[38] = Rows(0x11, 0x22, 0x44, 0x88, 0xFF, 0x22, 0x44, 0x88);
        patterns[39] = Rows(0xFF, 0x01, 0x01, 0xFF, 0x10, 0x10, 0xFF, 0x00);
        patterns[40] = Rows(0x99, 0x5A, 0x3C, 0xA5, 0x99, 0xA5, 0x3C, 0x5A);
        patterns[41] = Rows(0x99, 0x99, 0xFF, 0x18, 0x99, 0x99, 0x18, 0xFF);
        patterns[42] = Rows(0x00, 0x24, 0x18, 0x00, 0x00, 0x42, 0x18, 0x00);
        patterns[43] = Rows(0x11, 0x00, 0x44, 0x00, 0x11, 0x00, 0x44, 0x00);
        patterns[44] = Rows(0x11, 0x00, 0x44, 0x00, 0x11, 0x00, 0x44, 0x00) ^ 0x0008000008000000UL;
        patterns[45] = Rows(0x11, 0x22, 0x44, 0x88, 0x55, 0x22, 0x44, 0x88);
        patterns[46] = Rows(0x81, 0x42, 0x24, 0x18, 0x18, 0x24, 0x42, 0x81);
        patterns[47] = Rows(0x3C, 0x42, 0xA5, 0x81, 0x81, 0xA5, 0x42, 0x3C);
        patterns[48] = Horizontal(2, 1) | Vertical(2, 1);
        patterns[49] = Checker(1);
        patterns[50] = Checker(2);
        patterns[51] = Rows(0x18, 0x24, 0x42, 0x81, 0x81, 0x42, 0x24, 0x18);
        patterns[52] = Rows(0x18, 0x3C, 0x7E, 0xFF, 0xFF, 0x7E, 0x3C, 0x18);
        return patterns;
    }

    private static ulong Horizontal(int spacing, int thickness) =>
        Generate((_, y) => (y % spacing) < thickness);

    private static ulong Vertical(int spacing, int thickness) =>
        Generate((x, _) => (x % spacing) < thickness);

    private static ulong Diagonal(bool downward, int spacing, int thickness) =>
        Generate((x, y) => Mod(downward ? x - y : x + y, spacing) < thickness);

    private static ulong DashedDiagonal(bool downward) =>
        Generate((x, y) => Mod(downward ? x - y : x + y, 4) == 0 && ((x + y) & 3) < 2);

    private static ulong DashedHorizontal() =>
        Generate((x, y) => (y & 3) == 0 && (x & 3) < 2);

    private static ulong DashedVertical() =>
        Generate((x, y) => (x & 3) == 0 && (y & 3) < 2);

    private static ulong Checker(int size) =>
        Generate((x, y) => (((x / size) + (y / size)) & 1) == 0);

    private static ulong Density(int foregroundPixelCount)
    {
        ReadOnlySpan<byte> bayer =
        [
             0, 48, 12, 60,  3, 51, 15, 63,
            32, 16, 44, 28, 35, 19, 47, 31,
             8, 56,  4, 52, 11, 59,  7, 55,
            40, 24, 36, 20, 43, 27, 39, 23,
             2, 50, 14, 62,  1, 49, 13, 61,
            34, 18, 46, 30, 33, 17, 45, 29,
            10, 58,  6, 54,  9, 57,  5, 53,
            42, 26, 38, 22, 41, 25, 37, 21
        ];
        ulong mask = 0UL;
        for (int bit = 0; bit < 64; bit++)
        {
            if (bayer[bit] < foregroundPixelCount)
            {
                mask |= 1UL << bit;
            }
        }
        return mask;
    }

    private static ulong Generate(Func<int, int, bool> predicate)
    {
        ulong mask = 0UL;
        for (int y = 0; y < 8; y++)
        {
            for (int x = 0; x < 8; x++)
            {
                if (predicate(x, y))
                {
                    mask |= 1UL << ((y * 8) + x);
                }
            }
        }
        return mask;
    }

    private static ulong Rows(
        byte row0,
        byte row1,
        byte row2,
        byte row3,
        byte row4,
        byte row5,
        byte row6,
        byte row7) =>
        row0 |
        ((ulong)row1 << 8) |
        ((ulong)row2 << 16) |
        ((ulong)row3 << 24) |
        ((ulong)row4 << 32) |
        ((ulong)row5 << 40) |
        ((ulong)row6 << 48) |
        ((ulong)row7 << 56);

    private static int Mod(int value, int divisor)
    {
        int remainder = value % divisor;
        return remainder < 0 ? remainder + divisor : remainder;
    }
}
