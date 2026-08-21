using System;

namespace System.Drawing.Imaging;

[Flags]
public enum PixelFormat
{
    Format32bppArgb = 2498570,
    Format24bppRgb = 137224,
    Format8bppIndexed = 198658,
    Format32bppRgb = 139273,
    Format32bppPArgb = 925707,
    Format16bppRgb565 = 135173
}

[Flags]
public enum ImageFlags
{
    None = 0,
    Scalable = 0x0001,
    HasAlpha = 0x0002,
    HasTranslucent = 0x0004,
    PartiallyScalable = 0x0008,
    ColorSpaceRgb = 0x0010,
    ColorSpaceCmyk = 0x0020,
    ColorSpaceGray = 0x0040,
    ColorSpaceYcbcr = 0x0080,
    ColorSpaceYcck = 0x0100,
    HasRealDpi = 0x1000,
    HasRealPixelSize = 0x2000,
    ReadOnly = 0x10000,
    Caching = 0x20000
}

public enum ImageLockMode
{
    ReadOnly = 1,
    WriteOnly = 2,
    ReadWrite = 3,
    UserInputBuffer = 4
}

public enum ColorMatrixFlag
{
    Default = 0,
    SkipGrays = 1,
    AltGrays = 2
}

public enum ColorAdjustType
{
    Default = 0,
    Bitmap = 1,
    Brush = 2,
    Pen = 3,
    Text = 4,
    Count = 5,
    Any = 6
}

public enum ColorMapType
{
    Default = 0,
    Brush = 1
}

public sealed class ColorMap
{
    public Color OldColor { get; set; }

    public Color NewColor { get; set; }
}

public sealed class ColorMatrix
{
    private readonly float[][] _matrix;

    public ColorMatrix()
    {
        _matrix = CreateMatrix();
    }

    public ColorMatrix(float[][] newColorMatrix)
    {
        ArgumentNullException.ThrowIfNull(newColorMatrix);

        _matrix = CreateMatrix();
        for (int row = 0; row < Math.Min(5, newColorMatrix.Length); row++)
        {
            var sourceRow = newColorMatrix[row];
            if (sourceRow == null)
            {
                continue;
            }

            for (int column = 0; column < Math.Min(5, sourceRow.Length); column++)
            {
                _matrix[row][column] = sourceRow[column];
            }
        }
    }

    public ColorMatrix(ReadOnlySpan<float> newColorMatrix)
    {
        if (newColorMatrix.Length < 25)
        {
            throw new ArgumentException("A color matrix requires 25 values.", nameof(newColorMatrix));
        }

        _matrix = CreateMatrix();
        for (int index = 0; index < 25; index++)
        {
            _matrix[index / 5][index % 5] = newColorMatrix[index];
        }
    }

    public float[][] Matrix => _matrix;

    public float this[int row, int column]
    {
        get => _matrix[row][column];
        set => _matrix[row][column] = value;
    }

    public float Matrix00 { get => this[0, 0]; set => this[0, 0] = value; }
    public float Matrix01 { get => this[0, 1]; set => this[0, 1] = value; }
    public float Matrix02 { get => this[0, 2]; set => this[0, 2] = value; }
    public float Matrix03 { get => this[0, 3]; set => this[0, 3] = value; }
    public float Matrix04 { get => this[0, 4]; set => this[0, 4] = value; }
    public float Matrix10 { get => this[1, 0]; set => this[1, 0] = value; }
    public float Matrix11 { get => this[1, 1]; set => this[1, 1] = value; }
    public float Matrix12 { get => this[1, 2]; set => this[1, 2] = value; }
    public float Matrix13 { get => this[1, 3]; set => this[1, 3] = value; }
    public float Matrix14 { get => this[1, 4]; set => this[1, 4] = value; }
    public float Matrix20 { get => this[2, 0]; set => this[2, 0] = value; }
    public float Matrix21 { get => this[2, 1]; set => this[2, 1] = value; }
    public float Matrix22 { get => this[2, 2]; set => this[2, 2] = value; }
    public float Matrix23 { get => this[2, 3]; set => this[2, 3] = value; }
    public float Matrix24 { get => this[2, 4]; set => this[2, 4] = value; }
    public float Matrix30 { get => this[3, 0]; set => this[3, 0] = value; }
    public float Matrix31 { get => this[3, 1]; set => this[3, 1] = value; }
    public float Matrix32 { get => this[3, 2]; set => this[3, 2] = value; }
    public float Matrix33 { get => this[3, 3]; set => this[3, 3] = value; }
    public float Matrix34 { get => this[3, 4]; set => this[3, 4] = value; }
    public float Matrix40 { get => this[4, 0]; set => this[4, 0] = value; }
    public float Matrix41 { get => this[4, 1]; set => this[4, 1] = value; }
    public float Matrix42 { get => this[4, 2]; set => this[4, 2] = value; }
    public float Matrix43 { get => this[4, 3]; set => this[4, 3] = value; }
    public float Matrix44 { get => this[4, 4]; set => this[4, 4] = value; }

    private static float[][] CreateMatrix()
    {
        var matrix = new[]
        {
            new float[5],
            new float[5],
            new float[5],
            new float[5],
            new float[5]
        };
        matrix[0][0] = 1f;
        matrix[1][1] = 1f;
        matrix[2][2] = 1f;
        matrix[3][3] = 1f;
        matrix[4][4] = 1f;
        return matrix;
    }
}

public sealed class ImageAttributes : IDisposable, ICloneable
{
    private bool _isDisposed;

    public ColorMatrix? ColorMatrix { get; private set; }
    internal (Color OldColor, Color NewColor)[] RemapTable { get; private set; } = [];
    internal Drawing2D.WrapMode WrapMode { get; private set; } = Drawing2D.WrapMode.Clamp;
    internal Color WrapColor { get; private set; } = Color.Black;
    internal bool ClampWrap { get; private set; }

    public void SetColorMatrix(ColorMatrix newColorMatrix)
    {
        SetColorMatrix(newColorMatrix, ColorMatrixFlag.Default, ColorAdjustType.Default);
    }

    public void SetColorMatrix(ColorMatrix newColorMatrix, ColorMatrixFlag flags)
    {
        SetColorMatrix(newColorMatrix, flags, ColorAdjustType.Default);
    }

    public void SetColorMatrix(ColorMatrix newColorMatrix, ColorMatrixFlag mode, ColorAdjustType type)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(newColorMatrix);
        ValidateColorMatrixFlag(mode);
        ValidateColorAdjustType(type);
        ColorMatrix = new ColorMatrix(newColorMatrix.Matrix);
    }

    public void ClearColorMatrix() => ClearColorMatrix(ColorAdjustType.Default);

    public void ClearColorMatrix(ColorAdjustType type)
    {
        ThrowIfDisposed();
        ValidateColorAdjustType(type);
        ColorMatrix = null;
    }

    public void ClearColorKey() => ClearColorKey(ColorAdjustType.Default);

    public void ClearColorKey(ColorAdjustType type)
    {
        ThrowIfDisposed();
        ValidateColorAdjustType(type);
        // Color-key state is intentionally empty until SetColorKey is used.
    }

    public void SetRemapTable(ColorAdjustType type, ReadOnlySpan<(Color OldColor, Color NewColor)> map)
    {
        ThrowIfDisposed();
        ValidateColorAdjustType(type);
        RemapTable = map.ToArray();
    }

    public void SetRemapTable(params (Color OldColor, Color NewColor)[] map)
    {
        ArgumentNullException.ThrowIfNull(map);
        SetRemapTable(ColorAdjustType.Default, map);
    }

    public void SetRemapTable(params ColorMap[] map)
    {
        ArgumentNullException.ThrowIfNull(map);
        SetRemapTable(ColorAdjustType.Default, map.AsSpan());
    }

    public void SetRemapTable(ColorMap[] map, ColorAdjustType type)
    {
        ArgumentNullException.ThrowIfNull(map);
        SetRemapTable(type, map.AsSpan());
    }

    public void SetRemapTable(ReadOnlySpan<ColorMap> map) =>
        SetRemapTable(ColorAdjustType.Default, map);

    public void SetRemapTable(ColorAdjustType type, ReadOnlySpan<ColorMap> map)
    {
        ThrowIfDisposed();
        ValidateColorAdjustType(type);

        var snapshot = new (Color OldColor, Color NewColor)[map.Length];
        for (int index = 0; index < map.Length; index++)
        {
            ColorMap entry = map[index]
                ?? throw new ArgumentException("A remap table cannot contain null entries.", nameof(map));
            snapshot[index] = (entry.OldColor, entry.NewColor);
        }

        RemapTable = snapshot;
    }

    public void SetRemapTable(ReadOnlySpan<(Color OldColor, Color NewColor)> map) =>
        SetRemapTable(ColorAdjustType.Default, map);

    public void ClearRemapTable() => ClearRemapTable(ColorAdjustType.Default);

    public void ClearRemapTable(ColorAdjustType type)
    {
        ThrowIfDisposed();
        ValidateColorAdjustType(type);
        RemapTable = [];
    }

    public void SetWrapMode(Drawing2D.WrapMode mode) => SetWrapMode(mode, Color.Black, clamp: false);

    public void SetWrapMode(Drawing2D.WrapMode mode, Color color) => SetWrapMode(mode, color, clamp: false);

    public void SetWrapMode(Drawing2D.WrapMode mode, Color color, bool clamp)
    {
        ThrowIfDisposed();
        if (mode < Drawing2D.WrapMode.Tile || mode > Drawing2D.WrapMode.Clamp)
        {
            throw new ArgumentException("Invalid wrap mode.", nameof(mode));
        }

        WrapMode = mode;
        WrapColor = color;
        ClampWrap = clamp;
    }

    public void GetAdjustedPalette(ColorPalette palette, ColorAdjustType type)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(palette);
        ValidateColorAdjustType(type);

        Dictionary<int, int>? replacements = null;
        if (RemapTable.Length != 0)
        {
            replacements = new Dictionary<int, int>(RemapTable.Length);
            foreach ((Color oldColor, Color newColor) in RemapTable)
            {
                replacements[oldColor.ToArgb()] = newColor.ToArgb();
            }
        }

        Color[] entries = palette.Entries;
        for (int index = 0; index < entries.Length; index++)
        {
            Color color = entries[index];
            if (replacements is not null && replacements.TryGetValue(color.ToArgb(), out int remappedArgb))
            {
                color = Color.FromArgb(remappedArgb);
            }

            if (ColorMatrix is not null)
            {
                color = ApplyColorMatrix(color, ColorMatrix);
            }

            entries[index] = color;
        }
    }

    public object Clone()
    {
        ThrowIfDisposed();
        return new ImageAttributes
        {
            ColorMatrix = ColorMatrix is null ? null : new ColorMatrix(ColorMatrix.Matrix),
            RemapTable = (ValueTuple<Color, Color>[])RemapTable.Clone(),
            WrapMode = WrapMode,
            WrapColor = WrapColor,
            ClampWrap = ClampWrap
        };
    }

    public void Dispose()
    {
        _isDisposed = true;
        ColorMatrix = null;
        RemapTable = [];
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);
    }

    private static void ValidateColorAdjustType(ColorAdjustType type)
    {
        if (type < ColorAdjustType.Default || type > ColorAdjustType.Any)
        {
            throw new ArgumentException("Invalid color-adjust type.", nameof(type));
        }
    }

    private static void ValidateColorMatrixFlag(ColorMatrixFlag flag)
    {
        if (flag < ColorMatrixFlag.Default || flag > ColorMatrixFlag.AltGrays)
        {
            throw new ArgumentException("Invalid color-matrix flag.", nameof(flag));
        }
    }

    private static Color ApplyColorMatrix(Color color, ColorMatrix matrix)
    {
        float red = color.R / 255f;
        float green = color.G / 255f;
        float blue = color.B / 255f;
        float alpha = color.A / 255f;
        float[][] values = matrix.Matrix;

        return Color.FromArgb(
            ToByte(red * values[0][3] + green * values[1][3] + blue * values[2][3] + alpha * values[3][3] + values[4][3]),
            ToByte(red * values[0][0] + green * values[1][0] + blue * values[2][0] + alpha * values[3][0] + values[4][0]),
            ToByte(red * values[0][1] + green * values[1][1] + blue * values[2][1] + alpha * values[3][1] + values[4][1]),
            ToByte(red * values[0][2] + green * values[1][2] + blue * values[2][2] + alpha * values[3][2] + values[4][2]));
    }

    private static int ToByte(float value) =>
        (int)MathF.Round(Math.Clamp(value, 0f, 1f) * 255f);
}

public sealed class BitmapData
{
    public int Width { get; set; }
    public int Height { get; set; }
    public int Stride { get; set; }
    public PixelFormat PixelFormat { get; set; }
    public IntPtr Scan0 { get; set; }
    public int Reserved { get; set; }
}

public sealed class ImageFormat
{
    public Guid Guid { get; }

    public ImageFormat(Guid guid)
    {
        Guid = guid;
    }

    public static ImageFormat Bmp { get; } = new ImageFormat(new Guid("b96b3cab-0728-11d3-9d7b-0000f81ef32e"));
    public static ImageFormat Jpeg { get; } = new ImageFormat(new Guid("b96b3cae-0728-11d3-9d7b-0000f81ef32e"));
    public static ImageFormat Png { get; } = new ImageFormat(new Guid("b96b3caf-0728-11d3-9d7b-0000f81ef32e"));
    public static ImageFormat Gif { get; } = new ImageFormat(new Guid("b96b3cb0-0728-11d3-9d7b-0000f81ef32e"));
    public static ImageFormat Icon { get; } = new ImageFormat(new Guid("b96b3cb5-0728-11d3-9d7b-0000f81ef32e"));
}
