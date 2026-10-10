namespace ProGPU.Wpf.Interop;

public interface IPortablePixelShaderSource
{
    bool TryGetPortablePixelShader(out PortablePixelShader pixelShader);
}

public interface IPortableShaderEffectSource
{
    bool TryGetPortableShaderEffect(out PortableShaderEffect effect);
}

public enum PortableShaderSamplingMode
{
    NearestNeighbor = 0,
    Bilinear = 1,
    Auto = 2
}

public enum PortableShaderRenderMode
{
    Auto = 0,
    SoftwareOnly = 1,
    HardwareOnly = 2
}

public enum PortableShaderSamplerKind
{
    Brush = 0,
    ImplicitInput = 1,
    ImageSource = 2
}

public sealed class PortablePixelShader
{
    public PortablePixelShader(
        string? uriSource,
        string? absoluteUri,
        byte[]? bytecode,
        short majorVersion,
        short minorVersion)
    {
        UriSource = string.IsNullOrWhiteSpace(uriSource) ? null : uriSource;
        AbsoluteUri = string.IsNullOrWhiteSpace(absoluteUri) ? null : absoluteUri;
        Bytecode = bytecode is { Length: > 0 } ? (byte[])bytecode.Clone() : Array.Empty<byte>();
        MajorVersion = majorVersion;
        MinorVersion = minorVersion;
    }

    public string? UriSource { get; }

    public string? AbsoluteUri { get; }

    public byte[] Bytecode { get; }

    public short MajorVersion { get; }

    public short MinorVersion { get; }

    /// <summary>Null means the source did not publish its execution intent.</summary>
    public PortableShaderRenderMode? RenderMode { get; init; }

    /// <summary>The original source identity, never a substitute for captured bytecode.</summary>
    public IPortablePixelShaderSource? Source { get; init; }
}

public sealed class PortableShaderSampler
{
    public PortableShaderSampler(
        int registerIndex,
        object brush,
        PortableShaderSamplingMode samplingMode)
        : this(
            registerIndex,
            PortableShaderSamplerKind.Brush,
            brush ?? throw new ArgumentNullException(nameof(brush)),
            imageSource: null,
            samplingMode)
    {
    }

    private PortableShaderSampler(
        int registerIndex,
        PortableShaderSamplerKind kind,
        object? brush,
        object? imageSource,
        PortableShaderSamplingMode samplingMode)
    {
        RegisterIndex = registerIndex;
        Kind = kind;
        Brush = brush;
        ImageSource = imageSource;
        SamplingMode = samplingMode;
    }

    public static PortableShaderSampler ImplicitInput(
        int registerIndex,
        PortableShaderSamplingMode samplingMode)
    {
        return new PortableShaderSampler(
            registerIndex,
            PortableShaderSamplerKind.ImplicitInput,
            brush: null,
            imageSource: null,
            samplingMode);
    }

    public static PortableShaderSampler Image(
        int registerIndex,
        object? imageSource,
        PortableShaderSamplingMode samplingMode)
    {
        return new PortableShaderSampler(
            registerIndex,
            PortableShaderSamplerKind.ImageSource,
            brush: null,
            imageSource,
            samplingMode);
    }

    /// <summary>
    /// Retains the actual source ImageBrush as well as its image. Native MIL
    /// requires its original opacity, mapping and transform state; an image
    /// alone cannot describe that source sampler.
    /// </summary>
    public static PortableShaderSampler Image(
        int registerIndex,
        object? imageSource,
        PortableShaderSamplingMode samplingMode,
        object brush)
    {
        return new PortableShaderSampler(
            registerIndex,
            PortableShaderSamplerKind.ImageSource,
            brush ?? throw new ArgumentNullException(nameof(brush)),
            imageSource,
            samplingMode);
    }

    public int RegisterIndex { get; }

    public PortableShaderSamplerKind Kind { get; }

    public object? Brush { get; }

    public object? ImageSource { get; }

    public PortableShaderSamplingMode SamplingMode { get; }
}

public sealed class PortableShaderEffect
{
    public PortableShaderEffect(
        string? effectTypeFullName,
        string? effectTypeName,
        PortablePixelShader? pixelShader,
        float[]? floatConstants,
        PortableShaderSampler[]? samplers,
        uint intConstantCount,
        uint boolConstantCount,
        double paddingTop,
        double paddingBottom,
        double paddingLeft,
        double paddingRight,
        int ddxUvDdyUvRegisterIndex)
    {
        EffectTypeFullName = string.IsNullOrWhiteSpace(effectTypeFullName) ? null : effectTypeFullName;
        EffectTypeName = string.IsNullOrWhiteSpace(effectTypeName) ? null : effectTypeName;
        PixelShader = pixelShader;
        FloatConstants = floatConstants is { Length: > 0 } ? (float[])floatConstants.Clone() : Array.Empty<float>();
        Samplers = samplers is { Length: > 0 } ? (PortableShaderSampler[])samplers.Clone() : Array.Empty<PortableShaderSampler>();
        IntConstantCount = intConstantCount;
        BoolConstantCount = boolConstantCount;
        // This is source transport, not rendering admission. Retain invalid
        // metadata and signed zero so consumers can reject the original state
        // instead of silently publishing a different, zero-padding effect.
        PaddingTop = paddingTop;
        PaddingBottom = paddingBottom;
        PaddingLeft = paddingLeft;
        PaddingRight = paddingRight;
        DdxUvDdyUvRegisterIndex = ddxUvDdyUvRegisterIndex;
    }

    public string? EffectTypeFullName { get; }

    public string? EffectTypeName { get; }

    public PortablePixelShader? PixelShader { get; }

    public float[] FloatConstants { get; }

    public PortableShaderSampler[] Samplers { get; }

    public uint IntConstantCount { get; }

    public uint BoolConstantCount { get; }

    public double PaddingTop { get; }

    public double PaddingBottom { get; }

    public double PaddingLeft { get; }

    public double PaddingRight { get; }

    public int DdxUvDdyUvRegisterIndex { get; }

    public double MaxPadding
    {
        get
        {
            return Math.Max(
                Math.Max(PaddingTop, PaddingBottom),
                Math.Max(PaddingLeft, PaddingRight));
        }
    }
}
