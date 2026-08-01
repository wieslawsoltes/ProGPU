namespace SkiaSharp;

using ProGPU.Vector;

/// <summary>
/// Retains a coverage-only paint effect. Rendering backends consume this
/// immutable description without reading pixels back to the CPU.
/// </summary>
public class SKMaskFilter : SKObject
{
    public const int TableMaxLength = 256;

    private const float RadiusToSigmaScale = 0.57735f;
    private static readonly byte[] s_identityTable = CreateIdentityTable();
    private readonly byte[]? _table;

    private SKMaskFilter(
        MaskFilterKind kind,
        SKBlurStyle blurStyle = SKBlurStyle.Normal,
        float sigma = 0f,
        bool respectCtm = true,
        byte[]? table = null,
        SKShader? shader = null)
        : base(SKObjectHandle.Create(), owns: true)
    {
        Kind = kind;
        BlurStyle = blurStyle;
        Sigma = sigma;
        RespectCtm = respectCtm;
        _table = table;
        Shader = shader;
    }

    internal MaskFilterKind Kind { get; }

    internal SKBlurStyle BlurStyle { get; }

    internal float Sigma { get; }

    internal bool RespectCtm { get; }

    internal SKShader? Shader { get; }

    internal ReadOnlyMemory<byte> Table => _table;

    internal SKImageFilter CreateImageFilter(SKMatrix transform)
    {
        switch (Kind)
        {
            case MaskFilterKind.Blur:
            {
                var sigma = Sigma;
                if (RespectCtm)
                {
                    var scaleX = MathF.Sqrt(
                        transform.ScaleX * transform.ScaleX +
                        transform.SkewY * transform.SkewY);
                    var scaleY = MathF.Sqrt(
                        transform.SkewX * transform.SkewX +
                        transform.ScaleY * transform.ScaleY);
                    var scale = MathF.Max(scaleX, scaleY);
                    if (float.IsFinite(scale) && scale > 0f)
                    {
                        sigma *= scale;
                    }
                }

                var blur = SKImageFilter.CreateBlur(
                    sigma,
                    sigma,
                    SKShaderTileMode.Decal);
                return BlurStyle switch
                {
                    SKBlurStyle.Solid => SKImageFilter.CreateBlendMode(
                        SKBlendMode.SrcOver,
                        blur),
                    SKBlurStyle.Outer => SKImageFilter.CreateArithmetic(
                        -1f,
                        1f,
                        0f,
                        0f,
                        enforcePremultipliedColor: true,
                        background: blur),
                    SKBlurStyle.Inner => SKImageFilter.CreateArithmetic(
                        1f,
                        0f,
                        0f,
                        0f,
                        enforcePremultipliedColor: true,
                        background: blur),
                    _ => blur,
                };
            }
            case MaskFilterKind.Table:
            {
                var colorFilter = SKColorFilter.CreateTable(
                    _table!,
                    s_identityTable,
                    s_identityTable,
                    s_identityTable);
                return SKImageFilter.CreateColorFilter(colorFilter);
            }
            case MaskFilterKind.Shader:
            {
                var shader = SKImageFilter.CreateShader(Shader, dither: false);
                return SKImageFilter.CreateBlendMode(
                    SKBlendMode.DstIn,
                    background: null,
                    foreground: shader);
            }
            default:
                throw new InvalidOperationException("Unknown mask-filter kind.");
        }
    }

    public static SKMaskFilter CreateBlur(SKBlurStyle blurStyle, float sigma) =>
        CreateBlur(blurStyle, sigma, respectCTM: true);

    public static SKMaskFilter CreateBlur(
        SKBlurStyle blurStyle,
        float sigma,
        bool respectCTM)
    {
        if (!Enum.IsDefined(blurStyle) || !float.IsFinite(sigma) || sigma <= 0f)
        {
            return null!;
        }

        return new SKMaskFilter(
            MaskFilterKind.Blur,
            blurStyle,
            sigma,
            respectCTM);
    }

    public static SKMaskFilter CreateClip(byte min, byte max)
    {
        var table = new byte[TableMaxLength];
        if (min >= max)
        {
            for (var index = (int)max; index < TableMaxLength; index++)
            {
                table[index] = byte.MaxValue;
            }
        }
        else
        {
            var scale = byte.MaxValue / (float)(max - min);
            for (var index = (int)min + 1; index < max; index++)
            {
                table[index] = (byte)Math.Clamp(
                    MathF.Round((index - min) * scale),
                    byte.MinValue,
                    byte.MaxValue);
            }

            for (var index = (int)max; index < TableMaxLength; index++)
            {
                table[index] = byte.MaxValue;
            }
        }

        return new SKMaskFilter(MaskFilterKind.Table, table: table);
    }

    public static SKMaskFilter CreateGamma(float gamma)
    {
        if (!float.IsFinite(gamma) || gamma <= 0f)
        {
            return null!;
        }

        var table = new byte[TableMaxLength];
        for (var index = 0; index < TableMaxLength; index++)
        {
            var normalized = index / 255f;
            table[index] = (byte)Math.Clamp(
                MathF.Round(MathF.Pow(normalized, gamma) * byte.MaxValue),
                byte.MinValue,
                byte.MaxValue);
        }

        return new SKMaskFilter(MaskFilterKind.Table, table: table);
    }

    public static SKMaskFilter CreateShader(SKShader shader)
    {
        ArgumentNullException.ThrowIfNull(shader);
        return new SKMaskFilter(MaskFilterKind.Shader, shader: shader);
    }

    public static SKMaskFilter CreateTable(byte[] table)
    {
        ArgumentNullException.ThrowIfNull(table);
        if (table.Length != TableMaxLength)
        {
            throw new ArgumentException(
                $"Mask tables must contain exactly {TableMaxLength} entries.",
                nameof(table));
        }

        return new SKMaskFilter(MaskFilterKind.Table, table: (byte[])table.Clone());
    }

    public static float ConvertRadiusToSigma(float radius) =>
        radius > 0f && float.IsFinite(radius)
            ? RadiusToSigmaScale * radius + 0.5f
            : 0f;

    public static float ConvertSigmaToRadius(float sigma) =>
        sigma > 0.5f && float.IsFinite(sigma)
            ? (sigma - 0.5f) / RadiusToSigmaScale
            : 0f;

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
    }

    private static byte[] CreateIdentityTable()
    {
        var table = new byte[TableMaxLength];
        for (var index = 0; index < table.Length; index++)
        {
            table[index] = (byte)index;
        }

        return table;
    }

    internal enum MaskFilterKind
    {
        Blur,
        Table,
        Shader,
    }
}

internal sealed class SKMaskFilterBrush : Brush, IRetainedCommandInterceptBrush
{
    public SKMaskFilterBrush(Brush source, SKMaskFilter filter)
    {
        Source = source;
        Filter = filter;
        Opacity = source.Opacity;
    }

    public Brush Source { get; }

    public SKMaskFilter Filter { get; }
}
