using System;
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace System.Drawing.Imaging;

public enum PixelFormat
{
    Indexed = 0x0001_0000,
    Gdi = 0x0002_0000,
    Alpha = 0x0004_0000,
    PAlpha = 0x0008_0000,
    Extended = 0x0010_0000,
    Canonical = 0x0020_0000,
    Undefined = 0,
    DontCare = 0,
    Format1bppIndexed = (1 << 8) | (1) | Indexed | Gdi,
    Format4bppIndexed = (4 << 8) | (2) | Indexed | Gdi,
    Format8bppIndexed = (8 << 8) | (3) | Indexed | Gdi,
    Format16bppGrayScale = (16 << 8) | (4) | Extended,
    Format16bppRgb555 = (16 << 8) | (5) | Gdi,
    Format16bppRgb565 = (16 << 8) | (6) | Gdi,
    Format16bppArgb1555 = (16 << 8) | (7) | Alpha | Gdi,
    Format24bppRgb = (24 << 8) | (8) | Gdi,
    Format32bppRgb = (32 << 8) | (9) | Gdi,
    Format32bppArgb = 2498570,
    Format32bppPArgb = (32 << 8) | (11) | Alpha | PAlpha | Gdi,
    Format48bppRgb = (48 << 8) | (12) | Extended,
    Format64bppArgb = (64 << 8) | (13) | Alpha | Canonical | Extended,
    Format64bppPArgb = (64 << 8) | (14) | Alpha | PAlpha | Extended,
    Max = 15
}

public enum DitherType
{
    None = 0,
    Solid = 1,
    Ordered4x4 = 2,
    Ordered8x8 = 3,
    Ordered16x16 = 4,
    Spiral4x4 = 5,
    Spiral8x8 = 6,
    DualSpiral4x4 = 7,
    DualSpiral8x8 = 8,
    ErrorDiffusion = 9
}

public enum EncoderParameterValueType
{
    ValueTypeByte = 1,
    ValueTypeAscii = 2,
    ValueTypeShort = 3,
    ValueTypeLong = 4,
    ValueTypeRational = 5,
    ValueTypeLongRange = 6,
    ValueTypeUndefined = 7,
    ValueTypeRationalRange = 8,
    ValueTypePointer = 9
}

public enum EncoderValue
{
    ColorTypeCMYK,
    ColorTypeYCCK,
    CompressionLZW,
    CompressionCCITT3,
    CompressionCCITT4,
    CompressionRle,
    CompressionNone,
    ScanMethodInterlaced,
    ScanMethodNonInterlaced,
    VersionGif87,
    VersionGif89,
    RenderProgressive,
    RenderNonProgressive,
    TransformRotate90,
    TransformRotate180,
    TransformRotate270,
    TransformFlipHorizontal,
    TransformFlipVertical,
    MultiFrame,
    LastFrame,
    Flush,
    FrameDimensionTime,
    FrameDimensionResolution,
    FrameDimensionPage
}

[Flags]
public enum ImageCodecFlags
{
    Encoder = 0x00000001,
    Decoder = 0x00000002,
    SupportBitmap = 0x00000004,
    SupportVector = 0x00000008,
    SeekableEncode = 0x00000010,
    BlockingDecode = 0x00000020,
    Builtin = 0x00010000,
    System = 0x00020000,
    User = 0x00040000
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

[Flags]
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

public enum ColorChannelFlag
{
    ColorChannelC = 0,
    ColorChannelM = 1,
    ColorChannelY = 2,
    ColorChannelK = 3,
    ColorChannelLast = 4
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
    private sealed class AdjustmentState
    {
        internal bool IsConfigured;
        internal ColorMatrix? ColorMatrix;
        internal ColorMatrix? GrayMatrix;
        internal ColorMatrixFlag MatrixFlag;
        internal (Color OldColor, Color NewColor)[] RemapTable = [];
        internal Color? ColorKeyLow;
        internal Color? ColorKeyHigh;
        internal float? Gamma;
        internal float? Threshold;
        internal bool NoOp;
        internal ColorChannelFlag? OutputChannel;

        internal AdjustmentState Clone() => new()
        {
            IsConfigured = IsConfigured,
            ColorMatrix = CloneMatrix(ColorMatrix),
            GrayMatrix = CloneMatrix(GrayMatrix),
            MatrixFlag = MatrixFlag,
            RemapTable = (ValueTuple<Color, Color>[])RemapTable.Clone(),
            ColorKeyLow = ColorKeyLow,
            ColorKeyHigh = ColorKeyHigh,
            Gamma = Gamma,
            Threshold = Threshold,
            NoOp = NoOp,
            OutputChannel = OutputChannel
        };
    }

    private bool _isDisposed;
    private AdjustmentState[] _adjustments = CreateAdjustmentStates();

    public ColorMatrix? ColorMatrix => _adjustments[(int)ColorAdjustType.Default].ColorMatrix;
    internal (Color OldColor, Color NewColor)[] RemapTable =>
        _adjustments[(int)ColorAdjustType.Default].RemapTable;
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
        AdjustmentState state = Configure(type);
        state.ColorMatrix = CloneMatrix(newColorMatrix);
        state.GrayMatrix = null;
        state.MatrixFlag = mode;
    }

    public void SetColorMatrices(ColorMatrix newColorMatrix, ColorMatrix? grayMatrix) =>
        SetColorMatrices(newColorMatrix, grayMatrix, ColorMatrixFlag.Default, ColorAdjustType.Default);

    public void SetColorMatrices(
        ColorMatrix newColorMatrix,
        ColorMatrix? grayMatrix,
        ColorMatrixFlag flags) =>
        SetColorMatrices(newColorMatrix, grayMatrix, flags, ColorAdjustType.Default);

    public void SetColorMatrices(
        ColorMatrix newColorMatrix,
        ColorMatrix? grayMatrix,
        ColorMatrixFlag mode,
        ColorAdjustType type)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(newColorMatrix);
        ValidateColorMatrixFlag(mode);
        ValidateColorAdjustType(type);
        AdjustmentState state = Configure(type);
        state.ColorMatrix = CloneMatrix(newColorMatrix);
        state.GrayMatrix = CloneMatrix(grayMatrix);
        state.MatrixFlag = mode;
    }

    public void ClearColorMatrix() => ClearColorMatrix(ColorAdjustType.Default);

    public void ClearColorMatrix(ColorAdjustType type)
    {
        ThrowIfDisposed();
        ValidateColorAdjustType(type);
        AdjustmentState state = Configure(type);
        state.ColorMatrix = null;
        state.GrayMatrix = null;
        state.MatrixFlag = ColorMatrixFlag.Default;
    }

    public void ClearColorKey() => ClearColorKey(ColorAdjustType.Default);

    public void ClearColorKey(ColorAdjustType type)
    {
        ThrowIfDisposed();
        ValidateColorAdjustType(type);
        AdjustmentState state = Configure(type);
        state.ColorKeyLow = null;
        state.ColorKeyHigh = null;
    }

    public void SetColorKey(Color colorLow, Color colorHigh) =>
        SetColorKey(colorLow, colorHigh, ColorAdjustType.Default);

    public void SetColorKey(Color colorLow, Color colorHigh, ColorAdjustType type)
    {
        ThrowIfDisposed();
        ValidateColorAdjustType(type);
        AdjustmentState state = Configure(type);
        state.ColorKeyLow = colorLow;
        state.ColorKeyHigh = colorHigh;
    }

    public void SetRemapTable(ColorAdjustType type, ReadOnlySpan<(Color OldColor, Color NewColor)> map)
    {
        ThrowIfDisposed();
        ValidateColorAdjustType(type);
        Configure(type).RemapTable = map.ToArray();
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

        Configure(type).RemapTable = snapshot;
    }

    public void SetRemapTable(ReadOnlySpan<(Color OldColor, Color NewColor)> map) =>
        SetRemapTable(ColorAdjustType.Default, map);

    public void ClearRemapTable() => ClearRemapTable(ColorAdjustType.Default);

    public void ClearRemapTable(ColorAdjustType type)
    {
        ThrowIfDisposed();
        ValidateColorAdjustType(type);
        Configure(type).RemapTable = [];
    }

    public void SetBrushRemapTable(params ColorMap[] map)
    {
        ArgumentNullException.ThrowIfNull(map);
        SetRemapTable(ColorAdjustType.Brush, map.AsSpan());
    }

    public void SetBrushRemapTable(params ReadOnlySpan<ColorMap> map) =>
        SetRemapTable(ColorAdjustType.Brush, map);

    public void SetBrushRemapTable(params ReadOnlySpan<(Color OldColor, Color NewColor)> map) =>
        SetRemapTable(ColorAdjustType.Brush, map);

    public void ClearBrushRemapTable() => ClearRemapTable(ColorAdjustType.Brush);

    public void SetGamma(float gamma) => SetGamma(gamma, ColorAdjustType.Default);

    public void SetGamma(float gamma, ColorAdjustType type)
    {
        ThrowIfDisposed();
        ValidateColorAdjustType(type);
        if (!float.IsFinite(gamma) || gamma <= 0f)
        {
            throw new ArgumentException("Gamma must be a finite positive value.", nameof(gamma));
        }

        Configure(type).Gamma = gamma;
    }

    public void ClearGamma() => ClearGamma(ColorAdjustType.Default);

    public void ClearGamma(ColorAdjustType type)
    {
        ThrowIfDisposed();
        ValidateColorAdjustType(type);
        Configure(type).Gamma = null;
    }

    public void SetThreshold(float threshold) => SetThreshold(threshold, ColorAdjustType.Default);

    public void SetThreshold(float threshold, ColorAdjustType type)
    {
        ThrowIfDisposed();
        ValidateColorAdjustType(type);
        if (!float.IsFinite(threshold) || threshold < 0f || threshold > 1f)
        {
            throw new ArgumentException("Threshold must be between zero and one.", nameof(threshold));
        }

        Configure(type).Threshold = threshold;
    }

    public void ClearThreshold() => ClearThreshold(ColorAdjustType.Default);

    public void ClearThreshold(ColorAdjustType type)
    {
        ThrowIfDisposed();
        ValidateColorAdjustType(type);
        Configure(type).Threshold = null;
    }

    public void SetNoOp() => SetNoOp(ColorAdjustType.Default);

    public void SetNoOp(ColorAdjustType type)
    {
        ThrowIfDisposed();
        ValidateColorAdjustType(type);
        Configure(type).NoOp = true;
    }

    public void ClearNoOp() => ClearNoOp(ColorAdjustType.Default);

    public void ClearNoOp(ColorAdjustType type)
    {
        ThrowIfDisposed();
        ValidateColorAdjustType(type);
        Configure(type).NoOp = false;
    }

    public void SetOutputChannel(ColorChannelFlag flags) =>
        SetOutputChannel(flags, ColorAdjustType.Default);

    public void SetOutputChannel(ColorChannelFlag flags, ColorAdjustType type)
    {
        ThrowIfDisposed();
        ValidateColorAdjustType(type);
        if (flags < ColorChannelFlag.ColorChannelC || flags >= ColorChannelFlag.ColorChannelLast)
        {
            throw new ArgumentException("Invalid output color channel.", nameof(flags));
        }

        Configure(type).OutputChannel = flags;
    }

    public void ClearOutputChannel() => ClearOutputChannel(ColorAdjustType.Default);

    public void ClearOutputChannel(ColorAdjustType type)
    {
        ThrowIfDisposed();
        ValidateColorAdjustType(type);
        Configure(type).OutputChannel = null;
    }

    public void SetOutputChannelColorProfile(string colorProfileFilename) =>
        SetOutputChannelColorProfile(colorProfileFilename, ColorAdjustType.Default);

    public void SetOutputChannelColorProfile(string colorProfileFilename, ColorAdjustType type)
    {
        ThrowIfDisposed();
        ArgumentException.ThrowIfNullOrEmpty(colorProfileFilename);
        ValidateColorAdjustType(type);
        throw new PlatformNotSupportedException(
            "ICC output color profiles require a typed platform color-management adapter.");
    }

    public void ClearOutputChannelColorProfile() =>
        ClearOutputChannelColorProfile(ColorAdjustType.Default);

    public void ClearOutputChannelColorProfile(ColorAdjustType type)
    {
        ThrowIfDisposed();
        ValidateColorAdjustType(type);
        Configure(type);
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

        Dictionary<int, int>? replacements = CreateRemapLookup(type);

        Color[] entries = palette.Entries;
        for (int index = 0; index < entries.Length; index++)
        {
            entries[index] = ApplyAdjustments(entries[index], type, replacements);
        }
    }

    public object Clone()
    {
        ThrowIfDisposed();
        var clone = new ImageAttributes
        {
            WrapMode = WrapMode,
            WrapColor = WrapColor,
            ClampWrap = ClampWrap
        };
        clone._adjustments = _adjustments.Select(static state => state.Clone()).ToArray();
        return clone;
    }

    public void Dispose()
    {
        _isDisposed = true;
        _adjustments = CreateAdjustmentStates();
    }

    internal (Color OldColor, Color NewColor)[] GetRemapTable(ColorAdjustType type)
    {
        ThrowIfDisposed();
        AdjustmentState state = Resolve(type);
        return state.NoOp ? [] : state.RemapTable;
    }

    internal ColorMatrix? GetGpuColorMatrix(ColorAdjustType type)
    {
        ThrowIfDisposed();
        AdjustmentState state = Resolve(type);
        return state.NoOp || state.MatrixFlag != ColorMatrixFlag.Default || state.GrayMatrix is not null
            ? null
            : state.ColorMatrix;
    }

    internal bool RequiresCpuAdjustment(ColorAdjustType type)
    {
        ThrowIfDisposed();
        AdjustmentState state = Resolve(type);
        if (state.NoOp)
        {
            return false;
        }

        return state.ColorKeyLow.HasValue ||
            state.Gamma.HasValue ||
            state.Threshold.HasValue ||
            state.OutputChannel.HasValue ||
            state.MatrixFlag != ColorMatrixFlag.Default ||
            state.GrayMatrix is not null;
    }

    internal Dictionary<int, int>? CreateRemapLookup(ColorAdjustType type)
    {
        ThrowIfDisposed();
        (Color OldColor, Color NewColor)[] table = GetRemapTable(type);
        if (table.Length == 0)
        {
            return null;
        }

        var replacements = new Dictionary<int, int>(table.Length);
        foreach ((Color oldColor, Color newColor) in table)
        {
            replacements[oldColor.ToArgb()] = newColor.ToArgb();
        }

        return replacements;
    }

    internal Color ApplyAdjustments(
        Color color,
        ColorAdjustType type,
        IReadOnlyDictionary<int, int>? replacements = null)
    {
        ThrowIfDisposed();
        AdjustmentState state = Resolve(type);
        if (state.NoOp)
        {
            return color;
        }

        if (replacements is not null &&
            replacements.TryGetValue(color.ToArgb(), out int remappedArgb))
        {
            color = Color.FromArgb(remappedArgb);
        }

        if (state.ColorKeyLow is Color low && state.ColorKeyHigh is Color high &&
            IsWithinColorKey(color, low, high))
        {
            color = Color.FromArgb(0, color);
        }

        if (state.ColorMatrix is not null)
        {
            bool isGray = color.R == color.G && color.G == color.B;
            if (!isGray || state.MatrixFlag != ColorMatrixFlag.SkipGrays)
            {
                ColorMatrix matrix = isGray &&
                    state.MatrixFlag == ColorMatrixFlag.AltGrays &&
                    state.GrayMatrix is not null
                        ? state.GrayMatrix
                        : state.ColorMatrix;
                color = ApplyColorMatrix(color, matrix);
            }
        }

        if (state.Gamma is float gamma)
        {
            color = Color.FromArgb(
                color.A,
                ApplyGamma(color.R, gamma),
                ApplyGamma(color.G, gamma),
                ApplyGamma(color.B, gamma));
        }

        if (state.Threshold is float threshold)
        {
            color = Color.FromArgb(
                color.A,
                color.R / 255f >= threshold ? 255 : 0,
                color.G / 255f >= threshold ? 255 : 0,
                color.B / 255f >= threshold ? 255 : 0);
        }

        if (state.OutputChannel is ColorChannelFlag channel)
        {
            int ink = channel switch
            {
                ColorChannelFlag.ColorChannelC => 255 - color.R,
                ColorChannelFlag.ColorChannelM => 255 - color.G,
                ColorChannelFlag.ColorChannelY => 255 - color.B,
                ColorChannelFlag.ColorChannelK => Math.Min(255 - color.R, Math.Min(255 - color.G, 255 - color.B)),
                _ => 0
            };
            int separation = 255 - ink;
            color = Color.FromArgb(color.A, separation, separation, separation);
        }

        return color;
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);
    }

    internal void EnsureNotDisposed() => ThrowIfDisposed();

    private static void ValidateColorAdjustType(ColorAdjustType type)
    {
        if (type < ColorAdjustType.Default || type > ColorAdjustType.Text)
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

    internal static Color ApplyColorMatrix(Color color, ColorMatrix matrix)
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

    private static byte ApplyGamma(byte value, float gamma) =>
        (byte)ToByte(MathF.Pow(value / 255f, 1f / gamma));

    private static bool IsWithinColorKey(Color color, Color low, Color high) =>
        color.R >= low.R && color.R <= high.R &&
        color.G >= low.G && color.G <= high.G &&
        color.B >= low.B && color.B <= high.B;

    private static ColorMatrix? CloneMatrix(ColorMatrix? matrix) =>
        matrix is null ? null : new ColorMatrix(matrix.Matrix);

    private static AdjustmentState[] CreateAdjustmentStates() =>
        Enumerable.Range(0, (int)ColorAdjustType.Count)
            .Select(static _ => new AdjustmentState())
            .ToArray();

    private AdjustmentState Configure(ColorAdjustType type)
    {
        AdjustmentState state = _adjustments[(int)type];
        state.IsConfigured = true;
        return state;
    }

    private AdjustmentState Resolve(ColorAdjustType type)
    {
        ValidateColorAdjustType(type);
        AdjustmentState state = _adjustments[(int)type];
        return type != ColorAdjustType.Default && state.IsConfigured
            ? state
            : _adjustments[(int)ColorAdjustType.Default];
    }
}

[StructLayout(LayoutKind.Sequential)]
public sealed class BitmapData
{
    private int _width;
    private int _height;
    private int _stride;
    private PixelFormat _pixelFormat;
    private IntPtr _scan0;
    private int _reserved;

    public int Width { get => _width; set => _width = value; }
    public int Height { get => _height; set => _height = value; }
    public int Stride { get => _stride; set => _stride = value; }

    public PixelFormat PixelFormat
    {
        get => _pixelFormat;
        set
        {
            if (!PixelFormatInfo.IsDefined(value))
            {
                throw new InvalidEnumArgumentException(nameof(value), (int)value, typeof(PixelFormat));
            }

            _pixelFormat = value;
        }
    }

    public IntPtr Scan0 { get => _scan0; set => _scan0 = value; }
    public int Reserved { get => _reserved; set => _reserved = value; }
}

internal static class PixelFormatInfo
{
    internal static bool IsDefined(PixelFormat format) => format is
        PixelFormat.DontCare or
        PixelFormat.Max or
        PixelFormat.Indexed or
        PixelFormat.Gdi or
        PixelFormat.Format16bppRgb555 or
        PixelFormat.Format16bppRgb565 or
        PixelFormat.Format24bppRgb or
        PixelFormat.Format32bppRgb or
        PixelFormat.Format1bppIndexed or
        PixelFormat.Format4bppIndexed or
        PixelFormat.Format8bppIndexed or
        PixelFormat.Alpha or
        PixelFormat.Format16bppArgb1555 or
        PixelFormat.PAlpha or
        PixelFormat.Format32bppPArgb or
        PixelFormat.Extended or
        PixelFormat.Format16bppGrayScale or
        PixelFormat.Format48bppRgb or
        PixelFormat.Format64bppPArgb or
        PixelFormat.Canonical or
        PixelFormat.Format32bppArgb or
        PixelFormat.Format64bppArgb;

    internal static bool IsConcrete(PixelFormat format) => ((int)format & 0xff) is >= 1 and <= 14;

    internal static bool IsIndexed(PixelFormat format) => ((int)format & (int)PixelFormat.Indexed) != 0;
}

[TypeConverter(typeof(ImageFormatConverter))]
public sealed class ImageFormat
{
    private static readonly ImageFormat s_memoryBmp = new(new Guid("b96b3caa-0728-11d3-9d7b-0000f81ef32e"));
    private static readonly ImageFormat s_bmp = new(new Guid("b96b3cab-0728-11d3-9d7b-0000f81ef32e"));
    private static readonly ImageFormat s_emf = new(new Guid("b96b3cac-0728-11d3-9d7b-0000f81ef32e"));
    private static readonly ImageFormat s_wmf = new(new Guid("b96b3cad-0728-11d3-9d7b-0000f81ef32e"));
    private static readonly ImageFormat s_jpeg = new(new Guid("b96b3cae-0728-11d3-9d7b-0000f81ef32e"));
    private static readonly ImageFormat s_png = new(new Guid("b96b3caf-0728-11d3-9d7b-0000f81ef32e"));
    private static readonly ImageFormat s_gif = new(new Guid("b96b3cb0-0728-11d3-9d7b-0000f81ef32e"));
    private static readonly ImageFormat s_tiff = new(new Guid("b96b3cb1-0728-11d3-9d7b-0000f81ef32e"));
    private static readonly ImageFormat s_exif = new(new Guid("b96b3cb2-0728-11d3-9d7b-0000f81ef32e"));
    private static readonly ImageFormat s_icon = new(new Guid("b96b3cb5-0728-11d3-9d7b-0000f81ef32e"));
    private static readonly ImageFormat s_heif = new(new Guid("b96b3cb6-0728-11d3-9d7b-0000f81ef32e"));
    private static readonly ImageFormat s_webp = new(new Guid("b96b3cb7-0728-11d3-9d7b-0000f81ef32e"));

    public Guid Guid { get; }

    public ImageFormat(Guid guid)
    {
        Guid = guid;
    }

    public static ImageFormat MemoryBmp => s_memoryBmp;
    public static ImageFormat Bmp => s_bmp;
    public static ImageFormat Emf => s_emf;
    public static ImageFormat Wmf => s_wmf;
    public static ImageFormat Jpeg => s_jpeg;
    public static ImageFormat Png => s_png;
    public static ImageFormat Gif => s_gif;
    public static ImageFormat Tiff => s_tiff;
    public static ImageFormat Exif => s_exif;
    public static ImageFormat Icon => s_icon;
    public static ImageFormat Heif => s_heif;
    public static ImageFormat Webp => s_webp;

    public override bool Equals(object? obj) => obj is ImageFormat format && Guid == format.Guid;

    public override int GetHashCode() => Guid.GetHashCode();

    public override string ToString()
    {
        if (Equals(MemoryBmp)) return "MemoryBMP";
        if (Equals(Bmp)) return "Bmp";
        if (Equals(Emf)) return "Emf";
        if (Equals(Wmf)) return "Wmf";
        if (Equals(Gif)) return "Gif";
        if (Equals(Jpeg)) return "Jpeg";
        if (Equals(Png)) return "Png";
        if (Equals(Tiff)) return "Tiff";
        if (Equals(Exif)) return "Exif";
        if (Equals(Icon)) return "Icon";
        if (Equals(Heif)) return "Heif";
        if (Equals(Webp)) return "Webp";
        return $"[ImageFormat: {Guid}]";
    }
}
