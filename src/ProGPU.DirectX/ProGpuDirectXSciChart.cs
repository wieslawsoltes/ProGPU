using System.Buffers;
using System.Numerics;
using System.Runtime.InteropServices;
using ProGPU.Scene;
using ProGPU.Text;
using ProGPU.Vector;

namespace ProGPU.DirectX;

public enum ProGpuDirectXSciChartTextureFormat
{
    Bgra8,
    Float32,
    UInt32,
    Int32
}

public enum ProGpuDirectXSciChartTextureFiltering
{
    Point,
    Linear
}

public enum ProGpuDirectXSciChartSpriteAnchor
{
    TopLeft,
    Top,
    TopRight,
    Left,
    Center,
    Right,
    BottomLeft,
    Bottom,
    BottomRight
}

public enum ProGpuDirectXSciChartTextAlignment
{
    Left,
    Center,
    Right
}

public enum ProGpuDirectXSciChartVerticalTextAlignment
{
    Top,
    Center,
    Bottom,
    Baseline
}

public sealed record ProGpuDirectXSciChartPen2D
{
    public static ProGpuDirectXSciChartPen2D Default { get; } = new(0xFFFFFFFF, 1f);

    public ProGpuDirectXSciChartPen2D(
        uint colorArgb,
        float strokeThickness = 1f,
        bool isAntiAliased = true)
    {
        if (!float.IsFinite(strokeThickness) || strokeThickness <= 0f)
        {
            throw new ArgumentOutOfRangeException(nameof(strokeThickness), "SciChart pens require a finite positive stroke thickness.");
        }

        ColorArgb = colorArgb;
        StrokeThickness = strokeThickness;
        IsAntiAliased = isAntiAliased;
    }

    public uint ColorArgb { get; init; }

    public float StrokeThickness { get; init; }

    public bool IsAntiAliased { get; init; }
}

public sealed record ProGpuDirectXSciChartBrush2D
{
    public static ProGpuDirectXSciChartBrush2D Transparent { get; } = new(0);

    public ProGpuDirectXSciChartBrush2D(uint colorArgb)
    {
        ColorArgb = colorArgb;
        StartColorArgb = colorArgb;
        EndColorArgb = colorArgb;
    }

    public ProGpuDirectXSciChartBrush2D(
        uint startColorArgb,
        uint endColorArgb,
        double gradientRotationAngle = 0d)
    {
        if (!double.IsFinite(gradientRotationAngle))
        {
            throw new ArgumentOutOfRangeException(nameof(gradientRotationAngle), "SciChart gradient brush rotation angles must be finite.");
        }

        ColorArgb = startColorArgb;
        StartColorArgb = startColorArgb;
        EndColorArgb = endColorArgb;
        GradientRotationAngle = gradientRotationAngle;
    }

    public uint ColorArgb { get; init; }

    public uint StartColorArgb { get; init; }

    public uint EndColorArgb { get; init; }

    public double GradientRotationAngle { get; init; }

    public bool IsGradient => StartColorArgb != EndColorArgb;
}

public readonly record struct ProGpuDirectXSciChartPoint(float X, float Y);

public sealed class ProGpuDirectXSciChartFont
{
    internal ProGpuDirectXSciChartFont(
        TtfFont font,
        string familyName,
        string sourcePath,
        int faceIndex)
    {
        Font = font ?? throw new ArgumentNullException(nameof(font));
        FamilyName = familyName;
        SourcePath = sourcePath;
        FaceIndex = faceIndex;
    }

    internal TtfFont Font { get; }

    public string FamilyName { get; }

    public string SourcePath { get; }

    public int FaceIndex { get; }
}

public readonly record struct ProGpuDirectXSciChartAreaSegment(
    ProGpuDirectXSciChartPoint Start,
    ProGpuDirectXSciChartPoint End);

public sealed record ProGpuDirectXSciChartPalette(
    IReadOnlyList<uint>? LineAColors = null,
    IReadOnlyList<uint>? LineBColors = null,
    IReadOnlyList<uint>? FillPositiveColors = null,
    IReadOnlyList<uint>? FillNegativeColors = null);

public enum ProGpuDirectXSciChartFinancialBatchKind
{
    Candles,
    Ohlc
}

public sealed record ProGpuDirectXSciChartTextureDraw(
    ProGpuDirectXSciChartTexture2D Texture,
    DxRect ViewportRect,
    ProGpuDirectXSciChartTextureFiltering Filtering,
    bool IsUniform);

public sealed record ProGpuDirectXSciChartTextDraw(
    string Text,
    ProGpuDirectXSciChartFont Font,
    float FontSize,
    uint ColorArgb,
    ProGpuDirectXSciChartPoint Position,
    float RotationRadians,
    ProGpuDirectXSciChartTextAlignment HorizontalAlignment,
    ProGpuDirectXSciChartVerticalTextAlignment VerticalAlignment,
    bool IsBold,
    bool IsItalic,
    bool IsAntiAliased,
    DxRect? ClipRect);

public readonly record struct ProGpuDirectXSciChartTextureVertex(
    float X,
    float Y,
    float U,
    float V,
    uint ColorArgb);

public readonly record struct ProGpuDirectXSciChartColorVertex(
    float X,
    float Y,
    float Offset,
    uint ColorArgb);

public readonly record struct ProGpuDirectXSciChartBandVertex(
    float X,
    float Y0,
    float Y1);

public readonly record struct ProGpuDirectXSciChartColumnVertex(
    float X,
    float Y,
    float Width,
    float Height,
    uint FillColorArgb,
    uint StrokeColorArgb);

public readonly record struct ProGpuDirectXSciChartRectVertex(
    float X,
    float Y,
    float Width,
    float Height,
    uint ColorArgb);

public readonly record struct ProGpuDirectXSciChartSpriteVertex(
    float X,
    float Y,
    uint FillColorArgb,
    uint StrokeColorArgb);

public readonly record struct ProGpuDirectXSciChartColoredSpriteVertex(
    float X,
    float Y,
    uint ColorArgb);

public readonly record struct ProGpuDirectXSciChartColoredVertex(
    float X,
    float Y,
    float U,
    uint ColorArgb);

public readonly record struct ProGpuDirectXSciChartOhlcCandleVertex(
    float X,
    float O,
    float H,
    float L,
    float C,
    uint FillColorArgb,
    uint StrokeColorArgb);

public readonly record struct ProGpuDirectXSciChartVertex3D(
    float X,
    float Y,
    float Z,
    float NormalX,
    float NormalY,
    float NormalZ,
    uint ColorArgb);

public readonly record struct ProGpuDirectXSciChartXyzPoint3D(
    double X,
    double Y,
    double Z,
    uint ColorArgb = 0);

public readonly record struct ProGpuDirectXSciChartDoubleRange(double Minimum, double Maximum);

public sealed record ProGpuDirectXSciChartXyzSeries3DOptions
{
    public ProGpuDirectXSciChartDoubleRange? XRange { get; init; }

    public ProGpuDirectXSciChartDoubleRange? YRange { get; init; }

    public ProGpuDirectXSciChartDoubleRange? ZRange { get; init; }

    public bool NormalizeToUnitCube { get; init; } = true;

    public uint ColorArgb { get; init; } = 0xFF42C6FF;

    public Vector3 Normal { get; init; } = new(0f, 0f, 1f);
}

public sealed record ProGpuDirectXSciChartWaterfall3DOptions
{
    public ProGpuDirectXSciChartDoubleRange? XRange { get; init; }

    public ProGpuDirectXSciChartDoubleRange? YRange { get; init; }

    public ProGpuDirectXSciChartDoubleRange? ZRange { get; init; }

    public bool NormalizeToUnitCube { get; init; } = true;

    public float BaseY { get; init; } = -1f;

    public uint LowColorArgb { get; init; } = 0xFF1668C7;

    public uint HighColorArgb { get; init; } = 0xFFFFD166;

    public Vector3 Normal { get; init; } = new(0f, 0f, 1f);
}

public readonly record struct ProGpuDirectXSciChartVertexTransform(bool SwapAxis = false);

public enum ProGpuDirectXSciChartPrimitiveKind
{
    Line,
    Lines,
    Quad,
    RectangleFill,
    PolygonFill,
    AreaFill,
    Ellipse
}

public sealed record ProGpuDirectXSciChartPrimitiveDraw(
    ProGpuDirectXSciChartPrimitiveKind Kind,
    IReadOnlyList<ProGpuDirectXSciChartPoint> Points,
    ProGpuDirectXSciChartPen2D? Pen,
    ProGpuDirectXSciChartBrush2D? Brush,
    double Opacity,
    bool IsVerticalChart,
    double GradientRotationAngle,
    DxRect? ClipRect,
    double Width = 0d,
    double Height = 0d);

public sealed record ProGpuDirectXSciChartLineBatchDraw(
    IReadOnlyList<ProGpuDirectXSciChartColorVertex> Vertices,
    ProGpuDirectXSciChartPen2D Pen,
    bool IsStrips,
    bool IsDigital,
    bool? IsDrawNanAsGaps,
    ProGpuDirectXSciChartVertexTransform Transform,
    DxRect? ClipRect);

public sealed record ProGpuDirectXSciChartMountainBatchDraw(
    IReadOnlyList<ProGpuDirectXSciChartBandVertex> Vertices,
    ProGpuDirectXSciChartPen2D Pen,
    ProGpuDirectXSciChartBrush2D Brush,
    bool IsDigital,
    ProGpuDirectXSciChartVertexTransform Transform,
    ProGpuDirectXSciChartPalette? Palette,
    DxRect? ClipRect);

public sealed record ProGpuDirectXSciChartBandBatchDraw(
    IReadOnlyList<ProGpuDirectXSciChartBandVertex> Vertices,
    ProGpuDirectXSciChartPen2D PenA,
    ProGpuDirectXSciChartPen2D PenB,
    ProGpuDirectXSciChartBrush2D BrushPositive,
    ProGpuDirectXSciChartBrush2D BrushNegative,
    bool IsDigital,
    ProGpuDirectXSciChartVertexTransform Transform,
    ProGpuDirectXSciChartPalette? Palette,
    DxRect? ClipRect);

public sealed record ProGpuDirectXSciChartColumnBatchDraw(
    IReadOnlyList<ProGpuDirectXSciChartColumnVertex> Vertices,
    ProGpuDirectXSciChartVertexTransform Transform,
    DxRect? ClipRect);

public sealed record ProGpuDirectXSciChartRectBatchDraw(
    IReadOnlyList<ProGpuDirectXSciChartRectVertex> Vertices,
    ProGpuDirectXSciChartVertexTransform Transform,
    ProGpuDirectXSciChartSpriteAnchor Anchor,
    DxRect? ClipRect);

public sealed record ProGpuDirectXSciChartSpriteBatchDraw(
    IReadOnlyList<ProGpuDirectXSciChartSpriteVertex> Vertices,
    ProGpuDirectXSciChartSprite2D Sprite,
    ProGpuDirectXSciChartSprite2D? StrokeSprite,
    ProGpuDirectXSciChartVertexTransform Transform,
    float CenteredAmount,
    ProGpuDirectXSciChartTextureFiltering Filtering,
    DxRect? ClipRect);

public sealed record ProGpuDirectXSciChartColoredSpriteDraw(
    IReadOnlyList<ProGpuDirectXSciChartColoredSpriteVertex> Vertices,
    ProGpuDirectXSciChartSprite2D Sprite,
    int StartIndex,
    int Count,
    ProGpuDirectXSciChartVertexTransform Transform,
    float CenteredAmount,
    ProGpuDirectXSciChartTextureFiltering Filtering,
    DxRect? ClipRect);

public sealed record ProGpuDirectXSciChartFinancialBatchDraw(
    IReadOnlyList<ProGpuDirectXSciChartOhlcCandleVertex> Vertices,
    float Width,
    ProGpuDirectXSciChartFinancialBatchKind Kind,
    ProGpuDirectXSciChartVertexTransform Transform,
    bool IsDigital,
    bool IsVerticalChart,
    DxRect? ClipRect);

public sealed record ProGpuDirectXSciChartTextureVertexDraw(
    ProGpuDirectXSciChartTexture2D Texture,
    IReadOnlyList<ProGpuDirectXSciChartTextureVertex> Vertices,
    ProGpuDirectXSciChartVertexTransform Transform,
    ProGpuDirectXSciChartTextureFiltering Filtering,
    DxRect? ClipRect);

public sealed record ProGpuDirectXSciChartVerticalPixelsDraw(
    int XLeft,
    int XRight,
    IReadOnlyList<int>? YCoordinates,
    int YStartBottom,
    int YEndTop,
    IReadOnlyList<int> PixelColorsArgb,
    double Opacity,
    bool IsUniform,
    bool YAxisIsFlipped,
    DxRect? ClipRect);

public sealed record ProGpuDirectXSciChartShapedHeatmapDraw(
    ProGpuDirectXSciChartTexture2D HeightsTexture,
    ProGpuDirectXSciChartTexture2D GradientTexture,
    IReadOnlyList<ProGpuDirectXSciChartTextureVertex> Vertices,
    double ColorMapMin,
    double ColorMapMax,
    ProGpuDirectXSciChartTextureFiltering Filtering,
    DxRect? ClipRect);

public sealed record ProGpuDirectXSciChartHeightTextureContoursDraw(
    ProGpuDirectXSciChartTexture2D HeightsTexture,
    ProGpuDirectXSciChartTexture2D ContourTexture,
    DxRect ViewportRect,
    DxColor Color,
    float ZMin,
    float ZMax,
    float ZStep,
    float StrokeThickness,
    float Opacity,
    DxRect? ClipRect);

public sealed record ProGpuDirectXSciChartPointCloud3DDraw(
    IReadOnlyList<ProGpuDirectXSciChartVertex3D> Vertices,
    Matrix4x4 WorldViewProjection,
    Vector3 LightDirection,
    DxRect? ClipRect);

public sealed record ProGpuDirectXSciChartLine3DDraw(
    IReadOnlyList<ProGpuDirectXSciChartVertex3D> Vertices,
    bool IsStrip,
    Matrix4x4 WorldViewProjection,
    Vector3 LightDirection,
    DxRect? ClipRect);

public sealed record ProGpuDirectXSciChartMesh3DDraw(
    IReadOnlyList<ProGpuDirectXSciChartVertex3D> Vertices,
    IReadOnlyList<uint> Indices,
    Matrix4x4 WorldViewProjection,
    Vector3 LightDirection,
    DxCullMode CullMode,
    DxRect? ClipRect);

public sealed record ProGpuDirectXSciChartTriangleStrip3DDraw(
    IReadOnlyList<ProGpuDirectXSciChartVertex3D> Vertices,
    Matrix4x4 WorldViewProjection,
    Vector3 LightDirection,
    DxCullMode CullMode,
    DxRect? ClipRect);

public sealed record ProGpuDirectXSciChartSurfaceMesh3DDraw(
    IReadOnlyList<float> Heights,
    int Columns,
    int Rows,
    Vector2 XRange,
    Vector2 ZRange,
    uint LowColorArgb,
    uint HighColorArgb,
    IReadOnlyList<ProGpuDirectXSciChartVertex3D> Vertices,
    IReadOnlyList<uint> Indices,
    Matrix4x4 WorldViewProjection,
    Vector3 LightDirection,
    DxCullMode CullMode,
    DxRect? ClipRect);

public sealed record ProGpuDirectXSciChartWaterfall3DDraw(
    IReadOnlyList<float> Heights,
    int Columns,
    int Rows,
    ProGpuDirectXSciChartDoubleRange XRange,
    ProGpuDirectXSciChartDoubleRange YRange,
    ProGpuDirectXSciChartDoubleRange ZRange,
    float BaseY,
    uint LowColorArgb,
    uint HighColorArgb,
    IReadOnlyList<ProGpuDirectXSciChartVertex3D> Vertices,
    IReadOnlyList<uint> Indices,
    Matrix4x4 WorldViewProjection,
    Vector3 LightDirection,
    DxCullMode CullMode,
    DxRect? ClipRect);

public sealed class ProGpuDirectXSciChartTexture2D : IDisposable
{
    private ProGpuDirectXBuffer? _floatDataBuffer;
    private bool _isDisposed;

    internal ProGpuDirectXSciChartTexture2D(
        ProGpuDirectXTexture2D resource,
        ProGpuDirectXSciChartTextureFormat textureFormat)
    {
        Resource = resource;
        TextureFormat = textureFormat;
    }

    public ProGpuDirectXTexture2D Resource { get; }

    public uint Width => Resource.Width;

    public uint Height => Resource.Height;

    public ProGpuDirectXSciChartTextureFormat TextureFormat { get; }

    public ulong Generation => Resource.Generation;

    public void SetData(ReadOnlySpan<int> colorData)
    {
        ThrowIfDisposed();
        if (TextureFormat != ProGpuDirectXSciChartTextureFormat.Bgra8)
        {
            throw new InvalidOperationException("SciChart int texture data is supported only for Bgra8 textures.");
        }

        ValidateElementCount(colorData.Length);
        Resource.WritePixels(colorData);
    }

    public void SetFloatData(ReadOnlySpan<float> colorData)
    {
        ThrowIfDisposed();
        if (TextureFormat != ProGpuDirectXSciChartTextureFormat.Float32)
        {
            throw new InvalidOperationException("SciChart float texture data is supported only for Float32 textures.");
        }

        ValidateElementCount(colorData.Length);
        Resource.WritePixels(colorData);
        var buffer = GetOrCreateFloatDataBuffer();
        buffer.Write(colorData[..ExpectedElementCount]);
    }

    public void SetUIntData(ReadOnlySpan<uint> colorData)
    {
        ThrowIfDisposed();
        if (TextureFormat != ProGpuDirectXSciChartTextureFormat.UInt32)
        {
            throw new InvalidOperationException("SciChart uint texture data is supported only for UInt32 textures.");
        }

        ValidateElementCount(colorData.Length);
        Resource.WritePixels(colorData);
    }

    public void SetIntData(ReadOnlySpan<int> colorData)
    {
        ThrowIfDisposed();
        if (TextureFormat != ProGpuDirectXSciChartTextureFormat.Int32)
        {
            throw new InvalidOperationException("SciChart signed int texture data is supported only for Int32 textures.");
        }

        ValidateElementCount(colorData.Length);
        Resource.WritePixels(colorData);
    }

    public byte[] ReadPixels()
    {
        ThrowIfDisposed();
        return Resource.ReadPixels();
    }

    private void ValidateElementCount(int elementCount)
    {
        if (elementCount < ExpectedElementCount)
        {
            throw new ArgumentException($"Texture data contains {elementCount} element(s), expected at least {ExpectedElementCount}.");
        }
    }

    private int ExpectedElementCount => checked((int)(Width * Height));

    internal ProGpuDirectXBuffer GetFloatDataBuffer()
    {
        ThrowIfDisposed();
        if (TextureFormat != ProGpuDirectXSciChartTextureFormat.Float32)
        {
            throw new InvalidOperationException("Only SciChart Float32 textures have a height data buffer.");
        }

        return GetOrCreateFloatDataBuffer();
    }

    private ProGpuDirectXBuffer GetOrCreateFloatDataBuffer()
    {
        if (_floatDataBuffer is { IsDisposed: false } buffer)
        {
            return buffer;
        }

        buffer = Resource.Device.CreateBuffer(new DxBufferDescriptor
        {
            SizeInBytes = checked((uint)(ExpectedElementCount * sizeof(float))),
            Usage = DxBufferUsage.ShaderResource | DxBufferUsage.CopyDestination,
            StrideInBytes = sizeof(float),
            Label = $"SciChartFloatTextureData {Width}x{Height}"
        });
        buffer.Write(new float[ExpectedElementCount]);
        _floatDataBuffer = buffer;
        return buffer;
    }

    private void ThrowIfDisposed()
    {
        if (_isDisposed || Resource.IsDisposed)
        {
            throw new ObjectDisposedException(nameof(ProGpuDirectXSciChartTexture2D));
        }
    }

    public void Dispose()
    {
        if (_isDisposed)
        {
            return;
        }

        Resource.Dispose();
        _floatDataBuffer?.Dispose();
        _isDisposed = true;
    }
}

public sealed class ProGpuDirectXSciChartSprite2D : IDisposable
{
    private bool _isDisposed;

    internal ProGpuDirectXSciChartSprite2D(ProGpuDirectXSciChartTexture2D texture)
    {
        Texture = texture;
    }

    public ProGpuDirectXSciChartTexture2D Texture { get; }

    public uint Width => Texture.Width;

    public uint Height => Texture.Height;

    public void SetData(ReadOnlySpan<int> colorData)
    {
        ThrowIfDisposed();
        Texture.SetData(colorData);
    }

    public byte[] ReadPixels()
    {
        ThrowIfDisposed();
        return Texture.ReadPixels();
    }

    private void ThrowIfDisposed()
    {
        if (_isDisposed)
        {
            throw new ObjectDisposedException(nameof(ProGpuDirectXSciChartSprite2D));
        }
    }

    public void Dispose()
    {
        if (_isDisposed)
        {
            return;
        }

        Texture.Dispose();
        _isDisposed = true;
    }
}

public sealed class ProGpuDirectXSciChartRenderContext2D : IDisposable
{
    private const float LineAntialiasFeatherPixels = 1f;

    private readonly ProGpuDirectXDevice _device;
    private readonly ProGpuDirectXDeviceContext _context;
    private readonly List<IDisposable> _transientResources = new();
    private readonly List<ProGpuDirectXSciChartTextureDraw> _textureDraws = new();
    private readonly List<ProGpuDirectXSciChartTextDraw> _textDraws = new();
    private readonly List<ProGpuDirectXSciChartPrimitiveDraw> _primitiveDraws = new();
    private readonly List<ProGpuDirectXSciChartLineBatchDraw> _lineBatchDraws = new();
    private readonly List<ProGpuDirectXSciChartMountainBatchDraw> _mountainBatchDraws = new();
    private readonly List<ProGpuDirectXSciChartBandBatchDraw> _bandBatchDraws = new();
    private readonly List<ProGpuDirectXSciChartColumnBatchDraw> _columnBatchDraws = new();
    private readonly List<ProGpuDirectXSciChartRectBatchDraw> _rectBatchDraws = new();
    private readonly List<ProGpuDirectXSciChartSpriteBatchDraw> _spriteBatchDraws = new();
    private readonly List<ProGpuDirectXSciChartColoredSpriteDraw> _coloredSpriteDraws = new();
    private readonly List<ProGpuDirectXSciChartFinancialBatchDraw> _financialBatchDraws = new();
    private readonly List<ProGpuDirectXSciChartTextureVertexDraw> _textureVertexDraws = new();
    private readonly List<ProGpuDirectXSciChartVerticalPixelsDraw> _verticalPixelsDraws = new();
    private readonly List<ProGpuDirectXSciChartShapedHeatmapDraw> _shapedHeatmapDraws = new();
    private readonly List<ProGpuDirectXSciChartHeightTextureContoursDraw> _heightTextureContourDraws = new();
    private readonly Dictionary<(DxResourceFormat Format, ProGpuDirectXSciChartTextureFiltering Filtering), ProGpuDirectXGraphicsPipeline> _texturePipelines = new();
    private readonly Dictionary<DxResourceFormat, ProGpuDirectXGraphicsPipeline> _linePipelines = new();
    private readonly Dictionary<DxResourceFormat, ProGpuDirectXGraphicsPipeline> _columnFillPipelines = new();
    private readonly Dictionary<(DxResourceFormat Format, ProGpuDirectXSciChartTextureFiltering Filtering), ProGpuDirectXGraphicsPipeline> _textureVertexPipelines = new();
    private readonly Dictionary<(DxResourceFormat Format, ProGpuDirectXSciChartTextureFiltering Filtering), ProGpuDirectXGraphicsPipeline> _spritePipelines = new();
    private readonly Dictionary<(DxResourceFormat Format, ProGpuDirectXSciChartTextureFiltering Filtering), ProGpuDirectXGraphicsPipeline> _instancedSpritePipelines = new();
    private readonly Dictionary<(DxResourceFormat Format, ProGpuDirectXSciChartTextureFiltering Filtering), ProGpuDirectXGraphicsPipeline> _shapedHeatmapPipelines = new();
    private readonly Dictionary<DxResourceFormat, ProGpuDirectXGraphicsPipeline> _heightContourPipelines = new();
    private readonly Dictionary<ProGpuDirectXSciChartTextureFiltering, ProGpuDirectXSamplerState> _samplers = new();
    private ProGpuDirectXShader? _textureVertexShader;
    private ProGpuDirectXShader? _texturePixelShader;
    private ProGpuDirectXInputLayout? _textureInputLayout;
    private ProGpuDirectXShader? _lineVertexShader;
    private ProGpuDirectXShader? _linePixelShader;
    private ProGpuDirectXInputLayout? _lineInputLayout;
    private ProGpuDirectXShader? _batchedTextureVertexShader;
    private ProGpuDirectXShader? _batchedTexturePixelShader;
    private ProGpuDirectXInputLayout? _batchedTextureInputLayout;
    private ProGpuDirectXShader? _instancedSpriteVertexShader;
    private ProGpuDirectXInputLayout? _instancedSpriteInputLayout;
    private ProGpuDirectXBuffer? _spriteQuadVertexBuffer;
    private ProGpuDirectXShader? _shapedHeatmapPixelShader;
    private ProGpuDirectXShader? _heightContourPixelShader;
    private Compositor? _textCompositor;
    private DxRect? _clipRect;
    private bool _isDisposed;

    private readonly record struct PrimitiveBounds(float Left, float Top, float Right, float Bottom);

    public ProGpuDirectXSciChartRenderContext2D(
        ProGpuDirectXDevice device,
        uint width,
        uint height,
        DxResourceFormat renderTargetFormat = DxResourceFormat.R8G8B8A8Unorm)
    {
        _device = device ?? throw new ArgumentNullException(nameof(device));
        _context = device.CreateImmediateContext();
        RenderTarget = device.CreateTexture2D(new DxTexture2DDescriptor
        {
            Width = width,
            Height = height,
            Format = renderTargetFormat,
            Usage = DxTextureUsage.RenderTarget | DxTextureUsage.CopySource | DxTextureUsage.CopyDestination,
            CpuAccess = DxCpuAccessFlags.Read,
            Label = "SciChartRenderTarget"
        });
    }

    public ProGpuDirectXDevice Device => _device;

    public ProGpuDirectXDeviceContext ImmediateContext => _context;

    public ProGpuDirectXTexture2D RenderTarget { get; }

    public IReadOnlyList<ProGpuDirectXSciChartTextureDraw> TextureDraws => _textureDraws;

    public IReadOnlyList<ProGpuDirectXSciChartTextDraw> TextDraws => _textDraws;

    public IReadOnlyList<ProGpuDirectXSciChartPrimitiveDraw> PrimitiveDraws => _primitiveDraws;

    public IReadOnlyList<ProGpuDirectXSciChartLineBatchDraw> LineBatchDraws => _lineBatchDraws;

    public IReadOnlyList<ProGpuDirectXSciChartMountainBatchDraw> MountainBatchDraws => _mountainBatchDraws;

    public IReadOnlyList<ProGpuDirectXSciChartBandBatchDraw> BandBatchDraws => _bandBatchDraws;

    public IReadOnlyList<ProGpuDirectXSciChartColumnBatchDraw> ColumnBatchDraws => _columnBatchDraws;

    public IReadOnlyList<ProGpuDirectXSciChartRectBatchDraw> RectBatchDraws => _rectBatchDraws;

    public IReadOnlyList<ProGpuDirectXSciChartSpriteBatchDraw> SpriteBatchDraws => _spriteBatchDraws;

    public IReadOnlyList<ProGpuDirectXSciChartColoredSpriteDraw> ColoredSpriteDraws => _coloredSpriteDraws;

    public IReadOnlyList<ProGpuDirectXSciChartFinancialBatchDraw> FinancialBatchDraws => _financialBatchDraws;

    public IReadOnlyList<ProGpuDirectXSciChartTextureVertexDraw> TextureVertexDraws => _textureVertexDraws;

    public IReadOnlyList<ProGpuDirectXSciChartVerticalPixelsDraw> VerticalPixelsDraws => _verticalPixelsDraws;

    public IReadOnlyList<ProGpuDirectXSciChartShapedHeatmapDraw> ShapedHeatmapDraws => _shapedHeatmapDraws;

    public IReadOnlyList<ProGpuDirectXSciChartHeightTextureContoursDraw> HeightTextureContourDraws => _heightTextureContourDraws;

    public ProGpuDirectXSciChartTexture2D CreateTexture(
        uint width,
        uint height,
        ProGpuDirectXSciChartTextureFormat textureFormat = ProGpuDirectXSciChartTextureFormat.Bgra8)
    {
        ThrowIfDisposed();
        var resource = _device.CreateTexture2D(new DxTexture2DDescriptor
        {
            Width = width,
            Height = height,
            Format = ToDxFormat(textureFormat),
            Usage = DxTextureUsage.ShaderResource | DxTextureUsage.CopySource | DxTextureUsage.CopyDestination,
            CpuAccess = DxCpuAccessFlags.Read,
            Label = "SciChartTexture"
        });

        return new ProGpuDirectXSciChartTexture2D(resource, textureFormat);
    }

    public ProGpuDirectXSciChartSprite2D CreateSprite(uint width, uint height)
    {
        ThrowIfDisposed();
        return new ProGpuDirectXSciChartSprite2D(CreateTexture(width, height));
    }

    public ProGpuDirectXSciChartPen2D CreatePen(
        uint colorArgb,
        float strokeThickness = 1f,
        bool isAntiAliased = true)
    {
        ThrowIfDisposed();
        return new ProGpuDirectXSciChartPen2D(colorArgb, strokeThickness, isAntiAliased);
    }

    public ProGpuDirectXSciChartBrush2D CreateBrush(uint colorArgb)
    {
        ThrowIfDisposed();
        return new ProGpuDirectXSciChartBrush2D(colorArgb);
    }

    public ProGpuDirectXSciChartBrush2D CreateLinearGradientBrush(
        uint startColorArgb,
        uint endColorArgb,
        double gradientRotationAngle = 0d)
    {
        ThrowIfDisposed();
        return new ProGpuDirectXSciChartBrush2D(startColorArgb, endColorArgb, gradientRotationAngle);
    }

    public ProGpuDirectXSciChartFont CreateFont(string familyNameOrFilePath, int faceIndex = 0)
    {
        ThrowIfDisposed();
        if (string.IsNullOrWhiteSpace(familyNameOrFilePath))
        {
            throw new ArgumentException("SciChart fonts require a family name or font file path.", nameof(familyNameOrFilePath));
        }

        if (faceIndex < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(faceIndex), "SciChart font face indices must be non-negative.");
        }

        if (File.Exists(familyNameOrFilePath))
        {
            var fullPath = Path.GetFullPath(familyNameOrFilePath);
            var fontInfo = FontApi.ParseFontInfo(fullPath);
            var font = new TtfFont(fullPath, faceIndex);
            return new ProGpuDirectXSciChartFont(
                font,
                fontInfo?.FamilyName ?? Path.GetFileNameWithoutExtension(fullPath),
                fullPath,
                faceIndex);
        }

        var systemFont = FontApi.FindSystemFont(familyNameOrFilePath)
            ?? throw new FileNotFoundException($"SciChart font '{familyNameOrFilePath}' was not found.");
        return new ProGpuDirectXSciChartFont(
            new TtfFont(systemFont.FilePath, systemFont.FaceIndex),
            systemFont.FamilyName,
            systemFont.FilePath,
            systemFont.FaceIndex);
    }

    public ProGpuDirectXSciChartFont CreateDefaultFont()
    {
        ThrowIfDisposed();
        var systemFont = FontApi.FindSystemFont(
                "Arial",
                "Helvetica",
                "Helvetica Neue",
                "Segoe UI",
                "DejaVu Sans",
                ".SF NS Text",
                "San Francisco")
            ?? FontApi.GetSystemFonts().FirstOrDefault(font => File.Exists(font.FilePath))
            ?? throw new FileNotFoundException("No system font was found for the SciChart DirectX shim.");

        return new ProGpuDirectXSciChartFont(
            new TtfFont(systemFont.FilePath, systemFont.FaceIndex),
            systemFont.FamilyName,
            systemFont.FilePath,
            systemFont.FaceIndex);
    }

    public void Clear(DxColor color)
    {
        ThrowIfDisposed();
        _context.SetRenderTargets(RenderTarget);
        _context.SetViewport(new DxViewport(0, 0, RenderTarget.Width, RenderTarget.Height));
        _context.ClearRenderTarget(RenderTarget, color);
    }

    public void BeginFrame()
    {
        ThrowIfDisposed();
        _textureDraws.Clear();
        _textDraws.Clear();
        _primitiveDraws.Clear();
        _lineBatchDraws.Clear();
        _mountainBatchDraws.Clear();
        _bandBatchDraws.Clear();
        _columnBatchDraws.Clear();
        _rectBatchDraws.Clear();
        _spriteBatchDraws.Clear();
        _coloredSpriteDraws.Clear();
        _financialBatchDraws.Clear();
        _textureVertexDraws.Clear();
        _verticalPixelsDraws.Clear();
        _shapedHeatmapDraws.Clear();
        _heightTextureContourDraws.Clear();
        _clipRect = null;
    }

    public void SetClipRect(DxRect? clipRect)
    {
        ThrowIfDisposed();
        if (clipRect is { } rect)
        {
            ValidateDrawRect(rect);
            _clipRect = ClampClipRect(rect);
        }
        else
        {
            _clipRect = null;
        }
    }

    public void DrawTexture(
        ProGpuDirectXSciChartTexture2D texture,
        DxRect viewportRect,
        ProGpuDirectXSciChartTextureFiltering filtering = ProGpuDirectXSciChartTextureFiltering.Linear,
        bool isUniform = false)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(texture);
        ValidateDrawRect(viewportRect);
        ValidateDrawableTexture(texture);

        var effectiveRect = isUniform
            ? FitUniform(texture, viewportRect)
            : viewportRect;
        if (HasEmptyClip)
        {
            return;
        }

        var vertexBuffer = CreateTextureQuadVertexBuffer(effectiveRect, texture);
        var shaderResourceView = _device.CreateShaderResourceView(texture.Resource);
        var sampler = GetSampler(filtering);
        var pipeline = GetTexturePipeline(RenderTarget.Descriptor.Format, filtering);

        _context.SetRenderTargets(RenderTarget);
        _context.SetViewport(new DxViewport(0, 0, RenderTarget.Width, RenderTarget.Height));
        _context.SetScissorRect(_clipRect ?? FullRenderTargetRect);
        _context.SetGraphicsPipeline(pipeline);
        _context.SetVertexBuffer(vertexBuffer);
        _context.SetShaderResource(DxShaderStage.Pixel, 0, shaderResourceView);
        _context.SetShaderResource(DxShaderStage.Pixel, 1, null);
        _context.SetConstantBuffer(DxShaderStage.Pixel, 0, null);
        _context.SetSampler(DxShaderStage.Pixel, 0, sampler);
        _context.Draw(6);

        _textureDraws.Add(new ProGpuDirectXSciChartTextureDraw(texture, effectiveRect, filtering, isUniform));
        _transientResources.Add(vertexBuffer);
        _transientResources.Add(shaderResourceView);
    }

    public void DrawPixelsVertically(
        int xLeft,
        int xRight,
        int yStartBottom,
        int yEndTop,
        ReadOnlySpan<int> pixelColorsArgb,
        double opacity,
        bool yAxisIsFlipped)
    {
        ThrowIfDisposed();
        ValidateVerticalPixels(xLeft, xRight, yStartBottom, yEndTop, pixelColorsArgb.Length, opacity);

        if (HasEmptyClip)
        {
            return;
        }

        DrawVerticalPixelsTexture(
            xLeft,
            xRight,
            yStartBottom,
            yEndTop,
            yCoordinates: ReadOnlySpan<int>.Empty,
            pixelColorsArgb,
            opacity,
            isUniform: true,
            yAxisIsFlipped);
    }

    public void DrawPixelsVertically(
        int xLeft,
        int xRight,
        ReadOnlySpan<int> yCoordinates,
        ReadOnlySpan<int> pixelColorsArgb,
        double opacity,
        bool isUniform,
        bool yAxisIsFlipped)
    {
        ThrowIfDisposed();
        ValidateVerticalPixelCoordinates(xLeft, xRight, yCoordinates.Length, pixelColorsArgb.Length, opacity, isUniform);

        if (HasEmptyClip)
        {
            return;
        }

        if (isUniform)
        {
            if (yCoordinates[0] == yCoordinates[^1])
            {
                throw new ArgumentOutOfRangeException(nameof(yCoordinates), "SciChart uniform vertical pixels require a non-empty y range.");
            }

            DrawVerticalPixelsTexture(
                xLeft,
                xRight,
                yCoordinates[0],
                yCoordinates[^1],
                yCoordinates,
                pixelColorsArgb,
                opacity,
                isUniform: true,
                yAxisIsFlipped);
            return;
        }

        var vertexBuffer = CreateVerticalPixelVertexBuffer(
            xLeft,
            xRight,
            yCoordinates,
            pixelColorsArgb,
            opacity,
            yAxisIsFlipped,
            out var submittedVertexCount);
        if (vertexBuffer is null)
        {
            return;
        }

        _context.SetRenderTargets(RenderTarget);
        _context.SetViewport(new DxViewport(0, 0, RenderTarget.Width, RenderTarget.Height));
        _context.SetScissorRect(_clipRect ?? FullRenderTargetRect);
        _context.SetGraphicsPipeline(GetColumnFillPipeline(RenderTarget.Descriptor.Format));
        _context.SetVertexBuffer(vertexBuffer);
        _context.SetShaderResource(DxShaderStage.Pixel, 0, null);
        _context.SetShaderResource(DxShaderStage.Pixel, 1, null);
        _context.SetConstantBuffer(DxShaderStage.Pixel, 0, null);
        _context.SetSampler(DxShaderStage.Pixel, 0, null);
        _context.Draw(submittedVertexCount);

        _verticalPixelsDraws.Add(new ProGpuDirectXSciChartVerticalPixelsDraw(
            xLeft,
            xRight,
            CopySpan(yCoordinates),
            yCoordinates[0],
            yCoordinates[^1],
            CopySpan(pixelColorsArgb),
            opacity,
            IsUniform: false,
            yAxisIsFlipped,
            _clipRect));
        _transientResources.Add(vertexBuffer);
    }

    public void DrawText(
        string text,
        ProGpuDirectXSciChartFont font,
        float fontSize,
        uint colorArgb,
        ProGpuDirectXSciChartPoint position,
        float rotationRadians = 0f,
        ProGpuDirectXSciChartTextAlignment horizontalAlignment = ProGpuDirectXSciChartTextAlignment.Left,
        ProGpuDirectXSciChartVerticalTextAlignment verticalAlignment = ProGpuDirectXSciChartVerticalTextAlignment.Top,
        bool isBold = false,
        bool isItalic = false,
        bool isAntiAliased = true)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(font);
        ValidateText(text, fontSize, position, rotationRadians);

        if (string.IsNullOrEmpty(text) || !HasVisibleColor(colorArgb) || HasEmptyClip)
        {
            return;
        }

        var draw = new ProGpuDirectXSciChartTextDraw(
            text,
            font,
            fontSize,
            colorArgb,
            position,
            rotationRadians,
            horizontalAlignment,
            verticalAlignment,
            isBold,
            isItalic,
            isAntiAliased,
            _clipRect);

        RenderTextDraw(draw);
        _textDraws.Add(draw);
    }

    public void DrawLine(
        ProGpuDirectXSciChartPen2D? pen,
        ProGpuDirectXSciChartPoint pt1,
        ProGpuDirectXSciChartPoint pt2)
    {
        Span<ProGpuDirectXSciChartPoint> points = stackalloc ProGpuDirectXSciChartPoint[2];
        points[0] = pt1;
        points[1] = pt2;
        DrawPrimitiveLineCore(ProGpuDirectXSciChartPrimitiveKind.Line, pen, points, points.Length);
    }

    public void DrawLines(
        ProGpuDirectXSciChartPen2D? pen,
        ReadOnlySpan<ProGpuDirectXSciChartPoint> points)
    {
        DrawLines(pen, points, points.Length);
    }

    public void DrawLines(
        ProGpuDirectXSciChartPen2D? pen,
        ReadOnlySpan<ProGpuDirectXSciChartPoint> points,
        int count)
    {
        DrawPrimitiveLineCore(ProGpuDirectXSciChartPrimitiveKind.Lines, pen, points, count);
    }

    public void DrawQuad(
        ProGpuDirectXSciChartPen2D? pen,
        ProGpuDirectXSciChartPoint pt1,
        ProGpuDirectXSciChartPoint pt2)
    {
        Span<ProGpuDirectXSciChartPoint> points = stackalloc ProGpuDirectXSciChartPoint[5];
        points[0] = pt1;
        points[1] = new ProGpuDirectXSciChartPoint(pt2.X, pt1.Y);
        points[2] = pt2;
        points[3] = new ProGpuDirectXSciChartPoint(pt1.X, pt2.Y);
        points[4] = pt1;
        DrawPrimitiveLineCore(ProGpuDirectXSciChartPrimitiveKind.Quad, pen, points, points.Length);
    }

    public void FillRectangle(
        uint colorArgb,
        ProGpuDirectXSciChartPoint pt1,
        ProGpuDirectXSciChartPoint pt2)
    {
        FillRectangle(new ProGpuDirectXSciChartBrush2D(colorArgb), pt1, pt2);
    }

    public void FillRectangle(
        ProGpuDirectXSciChartBrush2D? brush,
        ProGpuDirectXSciChartPoint pt1,
        ProGpuDirectXSciChartPoint pt2,
        double opacity = 1d)
    {
        ThrowIfDisposed();
        ValidateOpacity(opacity);
        brush ??= ProGpuDirectXSciChartBrush2D.Transparent;

        if (HasEmptyClip)
        {
            return;
        }

        var vertexBuffer = CreateRectangleFillVertexBuffer(pt1, pt2, brush, opacity, out var submittedVertexCount);
        if (vertexBuffer is null)
        {
            return;
        }

        SetSolidColorDrawState();
        DrawSolidColorBuffer(
            vertexBuffer,
            GetColumnFillPipeline(RenderTarget.Descriptor.Format),
            submittedVertexCount);

        _primitiveDraws.Add(new ProGpuDirectXSciChartPrimitiveDraw(
            ProGpuDirectXSciChartPrimitiveKind.RectangleFill,
            new[] { pt1, pt2 },
            null,
            brush,
            opacity,
            false,
            0d,
            _clipRect));
    }

    public void FillPolygon(
        ProGpuDirectXSciChartBrush2D? brush,
        ReadOnlySpan<ProGpuDirectXSciChartPoint> points)
    {
        FillPolygon(brush, points, points.Length);
    }

    public void FillPolygon(
        ProGpuDirectXSciChartBrush2D? brush,
        ReadOnlySpan<ProGpuDirectXSciChartPoint> points,
        int count)
    {
        ThrowIfDisposed();
        ValidatePointRange(points.Length, count, minCount: 3, shapeName: "polygon fills");
        brush ??= ProGpuDirectXSciChartBrush2D.Transparent;

        if (HasEmptyClip)
        {
            return;
        }

        var pointSpan = points[..count];
        var vertexBuffer = CreatePolygonFillVertexBuffer(pointSpan, brush, out var submittedVertexCount);
        if (vertexBuffer is null)
        {
            return;
        }

        SetSolidColorDrawState();
        DrawSolidColorBuffer(
            vertexBuffer,
            GetColumnFillPipeline(RenderTarget.Descriptor.Format),
            submittedVertexCount);

        _primitiveDraws.Add(new ProGpuDirectXSciChartPrimitiveDraw(
            ProGpuDirectXSciChartPrimitiveKind.PolygonFill,
            CopyPrimitivePoints(pointSpan),
            null,
            brush,
            1d,
            false,
            0d,
            _clipRect));
    }

    public void FillArea(
        ProGpuDirectXSciChartBrush2D? brush,
        ReadOnlySpan<ProGpuDirectXSciChartAreaSegment> lines,
        bool isVerticalChart,
        double gradientRotationAngle)
    {
        FillArea(brush, lines, lines.Length, isVerticalChart, gradientRotationAngle);
    }

    public void FillArea(
        ProGpuDirectXSciChartBrush2D? brush,
        ReadOnlySpan<ProGpuDirectXSciChartAreaSegment> lines,
        int count,
        bool isVerticalChart,
        double gradientRotationAngle)
    {
        ThrowIfDisposed();
        ValidatePointRange(lines.Length, count, minCount: 2, shapeName: "area fills");
        ValidateGradientRotationAngle(gradientRotationAngle);
        brush ??= ProGpuDirectXSciChartBrush2D.Transparent;

        if (HasEmptyClip)
        {
            return;
        }

        var lineSpan = lines[..count];
        var vertexBuffer = CreateAreaFillVertexBuffer(lineSpan, brush, gradientRotationAngle, out var submittedVertexCount);
        if (vertexBuffer is null)
        {
            return;
        }

        SetSolidColorDrawState();
        DrawSolidColorBuffer(
            vertexBuffer,
            GetColumnFillPipeline(RenderTarget.Descriptor.Format),
            submittedVertexCount);

        _primitiveDraws.Add(new ProGpuDirectXSciChartPrimitiveDraw(
            ProGpuDirectXSciChartPrimitiveKind.AreaFill,
            CopyAreaPoints(lineSpan),
            null,
            brush,
            1d,
            isVerticalChart,
            gradientRotationAngle,
            _clipRect));
    }

    public void DrawEllipse(
        ProGpuDirectXSciChartPen2D? strokePen,
        ProGpuDirectXSciChartBrush2D? fillBrush,
        ProGpuDirectXSciChartPoint center,
        double width,
        double height)
    {
        ThrowIfDisposed();
        ValidateEllipse(center, width, height);
        fillBrush ??= ProGpuDirectXSciChartBrush2D.Transparent;

        if (HasEmptyClip)
        {
            return;
        }

        var segmentCount = GetEllipseSegmentCount((float)width, (float)height);
        ProGpuDirectXBuffer? fillBuffer = null;
        uint fillVertexCount = 0;
        if (HasVisibleBrush(fillBrush))
        {
            fillBuffer = CreateEllipseFillVertexBuffer(
                center,
                (float)width,
                (float)height,
                segmentCount,
                fillBrush,
                out fillVertexCount);
        }

        ProGpuDirectXBuffer? strokeBuffer = null;
        uint strokeVertexCount = 0;
        var strokeUsesTriangleTopology = false;
        if (strokePen is not null && HasVisibleColor(strokePen.ColorArgb))
        {
            strokeBuffer = CreateEllipseStrokeVertexBuffer(
                center,
                (float)width,
                (float)height,
                segmentCount,
                strokePen,
                out strokeVertexCount,
                out strokeUsesTriangleTopology);
        }

        if (fillBuffer is null && strokeBuffer is null)
        {
            return;
        }

        SetSolidColorDrawState();
        if (fillBuffer is not null)
        {
            DrawSolidColorBuffer(
                fillBuffer,
                GetColumnFillPipeline(RenderTarget.Descriptor.Format),
                fillVertexCount);
        }

        if (strokeBuffer is not null)
        {
            DrawSolidColorBuffer(
                strokeBuffer,
                strokeUsesTriangleTopology
                    ? GetColumnFillPipeline(RenderTarget.Descriptor.Format)
                    : GetLinePipeline(RenderTarget.Descriptor.Format),
                strokeVertexCount);
        }

        _primitiveDraws.Add(new ProGpuDirectXSciChartPrimitiveDraw(
            ProGpuDirectXSciChartPrimitiveKind.Ellipse,
            new[] { center },
            strokePen,
            fillBrush,
            1d,
            false,
            0d,
            _clipRect,
            width,
            height));
    }

    public void DrawLinesBatch(
        ReadOnlySpan<ProGpuDirectXSciChartColorVertex> vertices,
        int count,
        ProGpuDirectXSciChartVertexTransform transform)
    {
        DrawLinesBatch(
            vertices,
            count,
            ProGpuDirectXSciChartPen2D.Default,
            isStrips: true,
            isDigital: false,
            isDrawNanAsGaps: true,
            transform);
    }

    public void DrawLinesBatch(
        ReadOnlySpan<ProGpuDirectXSciChartColorVertex> vertices,
        int count,
        ProGpuDirectXSciChartPen2D? pen,
        bool isStrips,
        bool isDigital,
        bool? isDrawNanAsGaps,
        ProGpuDirectXSciChartVertexTransform transform)
    {
        ThrowIfDisposed();
        ValidateLineVertexRange(vertices.Length, count);
        pen ??= ProGpuDirectXSciChartPen2D.Default;

        if (HasEmptyClip)
        {
            return;
        }

        var vertexSpan = vertices[..count];
        var vertexBuffer = CreateLineBatchVertexBuffer(
            vertexSpan,
            transform,
            pen,
            isStrips,
            isDigital,
            isDrawNanAsGaps,
            out var submittedVertexCount,
            out var usesTriangleTopology);
        if (vertexBuffer is null)
        {
            return;
        }

        var pipeline = usesTriangleTopology
            ? GetColumnFillPipeline(RenderTarget.Descriptor.Format)
            : GetLinePipeline(RenderTarget.Descriptor.Format);
        _context.SetRenderTargets(RenderTarget);
        _context.SetViewport(new DxViewport(0, 0, RenderTarget.Width, RenderTarget.Height));
        _context.SetScissorRect(_clipRect ?? FullRenderTargetRect);
        _context.SetGraphicsPipeline(pipeline);
        _context.SetVertexBuffer(vertexBuffer);
        _context.SetShaderResource(DxShaderStage.Pixel, 0, null);
        _context.SetShaderResource(DxShaderStage.Pixel, 1, null);
        _context.SetConstantBuffer(DxShaderStage.Pixel, 0, null);
        _context.SetSampler(DxShaderStage.Pixel, 0, null);
        _context.Draw(submittedVertexCount);

        _lineBatchDraws.Add(new ProGpuDirectXSciChartLineBatchDraw(
            CopySpan(vertexSpan),
            pen,
            isStrips,
            isDigital,
            isDrawNanAsGaps,
            transform,
            _clipRect));
        _transientResources.Add(vertexBuffer);
    }

    public void DrawLineStrip(
        ProGpuDirectXSciChartPen2D? pen,
        ReadOnlySpan<ProGpuDirectXSciChartColoredVertex> vertices,
        int startIndex,
        int count,
        ProGpuDirectXSciChartVertexTransform transform)
    {
        ThrowIfDisposed();
        ValidateVertexRange(vertices.Length, startIndex, count);
        ValidateLineVertexRange(count, count);
        if (HasEmptyClip)
        {
            return;
        }

        var lineVertices = new ProGpuDirectXSciChartColorVertex[count];
        for (var i = 0; i < count; i++)
        {
            var vertex = vertices[startIndex + i];
            lineVertices[i] = new ProGpuDirectXSciChartColorVertex(
                vertex.X,
                vertex.Y,
                vertex.U,
                vertex.ColorArgb);
        }

        DrawLinesBatch(
            lineVertices,
            count,
            pen,
            isStrips: true,
            isDigital: false,
            isDrawNanAsGaps: true,
            transform);
    }

    public void DrawMountainBatch(
        ReadOnlySpan<ProGpuDirectXSciChartBandVertex> vertices,
        int count,
        ProGpuDirectXSciChartPen2D? pen,
        ProGpuDirectXSciChartBrush2D? brush,
        bool isDigital,
        ProGpuDirectXSciChartVertexTransform transform,
        ProGpuDirectXSciChartPalette? palette = null)
    {
        ThrowIfDisposed();
        ValidateBandVertexRange(vertices.Length, count);
        pen ??= ProGpuDirectXSciChartPen2D.Default;
        brush ??= ProGpuDirectXSciChartBrush2D.Transparent;

        if (HasEmptyClip)
        {
            return;
        }

        var vertexSpan = vertices[..count];
        var fillBuffer = CreateMountainFillVertexBuffer(
            vertexSpan,
            brush,
            palette,
            transform,
            isDigital,
            out var fillVertexCount);
        var lineVertices = CreateBandLineVertices(vertexSpan, useY1: false, pen, palette);
        var lineBuffer = CreateLineBatchVertexBuffer(
            lineVertices,
            transform,
            pen,
            isStrips: true,
            isDigital,
            isDrawNanAsGaps: true,
            out var lineVertexCount,
            out var lineUsesTriangleTopology);

        if (fillBuffer is null && lineBuffer is null)
        {
            return;
        }

        SetSolidColorDrawState();
        if (fillBuffer is not null)
        {
            DrawSolidColorBuffer(
                fillBuffer,
                GetColumnFillPipeline(RenderTarget.Descriptor.Format),
                fillVertexCount);
        }

        if (lineBuffer is not null)
        {
            DrawSolidColorBuffer(
                lineBuffer,
                lineUsesTriangleTopology
                    ? GetColumnFillPipeline(RenderTarget.Descriptor.Format)
                    : GetLinePipeline(RenderTarget.Descriptor.Format),
                lineVertexCount);
        }

        _mountainBatchDraws.Add(new ProGpuDirectXSciChartMountainBatchDraw(
            CopySpan(vertexSpan),
            pen,
            brush,
            isDigital,
            transform,
            palette,
            _clipRect));
    }

    public void DrawBandsBatch(
        ReadOnlySpan<ProGpuDirectXSciChartBandVertex> vertices,
        int count,
        ProGpuDirectXSciChartPen2D? penA,
        ProGpuDirectXSciChartPen2D? penB,
        ProGpuDirectXSciChartBrush2D? brushPositive,
        ProGpuDirectXSciChartBrush2D? brushNegative,
        bool isDigital,
        ProGpuDirectXSciChartVertexTransform transform,
        ProGpuDirectXSciChartPalette? palette = null)
    {
        ThrowIfDisposed();
        ValidateBandVertexRange(vertices.Length, count);
        penA ??= ProGpuDirectXSciChartPen2D.Default;
        penB ??= ProGpuDirectXSciChartPen2D.Default;
        brushPositive ??= ProGpuDirectXSciChartBrush2D.Transparent;
        brushNegative ??= ProGpuDirectXSciChartBrush2D.Transparent;

        if (HasEmptyClip)
        {
            return;
        }

        var vertexSpan = vertices[..count];
        var fillBuffer = CreateBandFillVertexBuffer(
            vertexSpan,
            brushPositive,
            brushNegative,
            palette,
            transform,
            isDigital,
            out var fillVertexCount);
        var lineAVertices = CreateBandLineVertices(vertexSpan, useY1: false, penA, palette);
        var lineBVertices = CreateBandLineVertices(vertexSpan, useY1: true, penB, palette);
        var lineABuffer = CreateLineBatchVertexBuffer(
            lineAVertices,
            transform,
            penA,
            isStrips: true,
            isDigital,
            isDrawNanAsGaps: true,
            out var lineAVertexCount,
            out var lineAUsesTriangleTopology);
        var lineBBuffer = CreateLineBatchVertexBuffer(
            lineBVertices,
            transform,
            penB,
            isStrips: true,
            isDigital,
            isDrawNanAsGaps: true,
            out var lineBVertexCount,
            out var lineBUsesTriangleTopology);

        if (fillBuffer is null && lineABuffer is null && lineBBuffer is null)
        {
            return;
        }

        SetSolidColorDrawState();
        if (fillBuffer is not null)
        {
            DrawSolidColorBuffer(
                fillBuffer,
                GetColumnFillPipeline(RenderTarget.Descriptor.Format),
                fillVertexCount);
        }

        if (lineABuffer is not null)
        {
            DrawSolidColorBuffer(
                lineABuffer,
                lineAUsesTriangleTopology
                    ? GetColumnFillPipeline(RenderTarget.Descriptor.Format)
                    : GetLinePipeline(RenderTarget.Descriptor.Format),
                lineAVertexCount);
        }

        if (lineBBuffer is not null)
        {
            DrawSolidColorBuffer(
                lineBBuffer,
                lineBUsesTriangleTopology
                    ? GetColumnFillPipeline(RenderTarget.Descriptor.Format)
                    : GetLinePipeline(RenderTarget.Descriptor.Format),
                lineBVertexCount);
        }

        _bandBatchDraws.Add(new ProGpuDirectXSciChartBandBatchDraw(
            CopySpan(vertexSpan),
            penA,
            penB,
            brushPositive,
            brushNegative,
            isDigital,
            transform,
            palette,
            _clipRect));
    }

    public void DrawColumnsBatch(
        ReadOnlySpan<ProGpuDirectXSciChartColumnVertex> vertices,
        int count,
        ProGpuDirectXSciChartVertexTransform transform)
    {
        ThrowIfDisposed();
        ValidateColumnVertexRange(vertices.Length, count);

        if (HasEmptyClip)
        {
            return;
        }

        var vertexSpan = vertices[..count];
        var fillBuffer = CreateColumnFillVertexBuffer(vertexSpan, transform, out var fillVertexCount);
        var strokeBuffer = CreateColumnStrokeVertexBuffer(vertexSpan, transform, out var strokeVertexCount);
        if (fillBuffer is null && strokeBuffer is null)
        {
            return;
        }

        _context.SetRenderTargets(RenderTarget);
        _context.SetViewport(new DxViewport(0, 0, RenderTarget.Width, RenderTarget.Height));
        _context.SetScissorRect(_clipRect ?? FullRenderTargetRect);
        _context.SetShaderResource(DxShaderStage.Pixel, 0, null);
        _context.SetShaderResource(DxShaderStage.Pixel, 1, null);
        _context.SetConstantBuffer(DxShaderStage.Pixel, 0, null);
        _context.SetSampler(DxShaderStage.Pixel, 0, null);

        if (fillBuffer is not null)
        {
            _context.SetGraphicsPipeline(GetColumnFillPipeline(RenderTarget.Descriptor.Format));
            _context.SetVertexBuffer(fillBuffer);
            _context.Draw(fillVertexCount);
            _transientResources.Add(fillBuffer);
        }

        if (strokeBuffer is not null)
        {
            _context.SetGraphicsPipeline(GetLinePipeline(RenderTarget.Descriptor.Format));
            _context.SetVertexBuffer(strokeBuffer);
            _context.Draw(strokeVertexCount);
            _transientResources.Add(strokeBuffer);
        }

        _columnBatchDraws.Add(new ProGpuDirectXSciChartColumnBatchDraw(
            CopySpan(vertexSpan),
            transform,
            _clipRect));
    }

    public void DrawRectsBatch(
        ReadOnlySpan<ProGpuDirectXSciChartRectVertex> vertices,
        int count,
        ProGpuDirectXSciChartVertexTransform transform,
        ProGpuDirectXSciChartSpriteAnchor anchor = ProGpuDirectXSciChartSpriteAnchor.TopLeft)
    {
        ThrowIfDisposed();
        ValidateRectVertexRange(vertices.Length, count);

        if (HasEmptyClip)
        {
            return;
        }

        var vertexSpan = vertices[..count];
        var vertexBuffer = CreateRectBatchVertexBuffer(vertexSpan, transform, anchor, out var submittedVertexCount);
        if (vertexBuffer is null)
        {
            return;
        }

        _context.SetRenderTargets(RenderTarget);
        _context.SetViewport(new DxViewport(0, 0, RenderTarget.Width, RenderTarget.Height));
        _context.SetScissorRect(_clipRect ?? FullRenderTargetRect);
        _context.SetGraphicsPipeline(GetColumnFillPipeline(RenderTarget.Descriptor.Format));
        _context.SetVertexBuffer(vertexBuffer);
        _context.SetShaderResource(DxShaderStage.Pixel, 0, null);
        _context.SetShaderResource(DxShaderStage.Pixel, 1, null);
        _context.SetConstantBuffer(DxShaderStage.Pixel, 0, null);
        _context.SetSampler(DxShaderStage.Pixel, 0, null);
        _context.Draw(submittedVertexCount);

        _rectBatchDraws.Add(new ProGpuDirectXSciChartRectBatchDraw(
            CopySpan(vertexSpan),
            transform,
            anchor,
            _clipRect));
        _transientResources.Add(vertexBuffer);
    }

    public void DrawSpritesBatch(
        ReadOnlySpan<ProGpuDirectXSciChartSpriteVertex> vertices,
        int count,
        ProGpuDirectXSciChartSprite2D sprite,
        ProGpuDirectXSciChartSprite2D? strokeSprite,
        ProGpuDirectXSciChartVertexTransform transform,
        float centeredAmount,
        ProGpuDirectXSciChartTextureFiltering filtering = ProGpuDirectXSciChartTextureFiltering.Linear)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(sprite);
        ValidateSpriteVertexRange(vertices.Length, count);
        ValidateDrawableSprite(sprite);
        if (strokeSprite is not null)
        {
            ValidateDrawableSprite(strokeSprite);
        }

        if (!float.IsFinite(centeredAmount))
        {
            throw new ArgumentOutOfRangeException(nameof(centeredAmount), "SciChart sprite batches require a finite centered amount.");
        }

        if (HasEmptyClip)
        {
            return;
        }

        var vertexSpan = vertices[..count];
        var fillBuffer = CreateSpriteBatchVertexBuffer(
            vertexSpan,
            sprite,
            transform,
            centeredAmount,
            useStrokeColor: false,
            out var fillVertexCount);
        uint strokeVertexCount = 0;
        var strokeBuffer = strokeSprite is null
            ? null
            : CreateSpriteBatchVertexBuffer(
                vertexSpan,
                strokeSprite,
                transform,
                centeredAmount,
                useStrokeColor: true,
                out strokeVertexCount);

        if (fillBuffer is null && strokeBuffer is null)
        {
            return;
        }

        _context.SetRenderTargets(RenderTarget);
        _context.SetViewport(new DxViewport(0, 0, RenderTarget.Width, RenderTarget.Height));
        _context.SetScissorRect(_clipRect ?? FullRenderTargetRect);
        _context.SetGraphicsPipeline(GetSpritePipeline(RenderTarget.Descriptor.Format, filtering));
        _context.SetShaderResource(DxShaderStage.Pixel, 1, null);
        _context.SetConstantBuffer(DxShaderStage.Pixel, 0, null);
        _context.SetSampler(DxShaderStage.Pixel, 0, GetSampler(filtering));

        if (fillBuffer is not null)
        {
            var spriteView = _device.CreateShaderResourceView(sprite.Texture.Resource);
            _context.SetVertexBuffer(fillBuffer);
            _context.SetShaderResource(DxShaderStage.Pixel, 0, spriteView);
            _context.Draw(fillVertexCount);
            _transientResources.Add(fillBuffer);
            _transientResources.Add(spriteView);
        }

        if (strokeBuffer is not null && strokeSprite is not null)
        {
            var strokeSpriteView = _device.CreateShaderResourceView(strokeSprite.Texture.Resource);
            _context.SetVertexBuffer(strokeBuffer);
            _context.SetShaderResource(DxShaderStage.Pixel, 0, strokeSpriteView);
            _context.Draw(strokeVertexCount);
            _transientResources.Add(strokeBuffer);
            _transientResources.Add(strokeSpriteView);
        }

        _spriteBatchDraws.Add(new ProGpuDirectXSciChartSpriteBatchDraw(
            CopySpan(vertexSpan),
            sprite,
            strokeSprite,
            transform,
            centeredAmount,
            filtering,
            _clipRect));
    }

    public void DrawColoredSprites(
        ProGpuDirectXSciChartSprite2D sprite,
        ReadOnlySpan<ProGpuDirectXSciChartColoredSpriteVertex> vertices,
        int startIndex,
        int count,
        ProGpuDirectXSciChartVertexTransform transform,
        float centeredAmount,
        ProGpuDirectXSciChartTextureFiltering filtering = ProGpuDirectXSciChartTextureFiltering.Linear)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(sprite);
        ValidateVertexRange(vertices.Length, startIndex, count);
        ValidateDrawableSprite(sprite);
        if (!float.IsFinite(centeredAmount))
        {
            throw new ArgumentOutOfRangeException(nameof(centeredAmount), "SciChart colored sprites require a finite centered amount.");
        }

        if (HasEmptyClip)
        {
            return;
        }

        var vertexSpan = vertices.Slice(startIndex, count);
        DrawColoredSpritesCore(
            sprite,
            vertexSpan,
            startIndex,
            count,
            transform,
            centeredAmount,
            filtering);
    }

    public void DrawColoredSprites(
        ProGpuDirectXSciChartSprite2D sprite,
        ReadOnlySpan<ProGpuDirectXSciChartColoredVertex> vertices,
        int startIndex,
        int count,
        ProGpuDirectXSciChartVertexTransform transform,
        float centeredAmount,
        ProGpuDirectXSciChartTextureFiltering filtering = ProGpuDirectXSciChartTextureFiltering.Linear)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(sprite);
        ValidateVertexRange(vertices.Length, startIndex, count);
        ValidateDrawableSprite(sprite);
        if (!float.IsFinite(centeredAmount))
        {
            throw new ArgumentOutOfRangeException(nameof(centeredAmount), "SciChart colored sprites require a finite centered amount.");
        }

        if (HasEmptyClip)
        {
            return;
        }

        var copiedVertices = new ProGpuDirectXSciChartColoredSpriteVertex[count];
        for (var i = 0; i < count; i++)
        {
            var vertex = vertices[startIndex + i];
            copiedVertices[i] = new ProGpuDirectXSciChartColoredSpriteVertex(
                vertex.X,
                vertex.Y,
                vertex.ColorArgb);
        }

        DrawColoredSpritesCore(
            sprite,
            copiedVertices,
            startIndex,
            count,
            transform,
            centeredAmount,
            filtering);
    }

    private void DrawColoredSpritesCore(
        ProGpuDirectXSciChartSprite2D sprite,
        ReadOnlySpan<ProGpuDirectXSciChartColoredSpriteVertex> vertices,
        int startIndex,
        int count,
        ProGpuDirectXSciChartVertexTransform transform,
        float centeredAmount,
        ProGpuDirectXSciChartTextureFiltering filtering)
    {
        var instanceBuffer = CreateColoredSpriteInstanceBuffer(
            vertices,
            sprite,
            transform,
            centeredAmount,
            out var submittedInstanceCount);
        if (instanceBuffer is null)
        {
            return;
        }

        _context.SetRenderTargets(RenderTarget);
        _context.SetViewport(new DxViewport(0, 0, RenderTarget.Width, RenderTarget.Height));
        _context.SetScissorRect(_clipRect ?? FullRenderTargetRect);
        _context.SetGraphicsPipeline(GetInstancedSpritePipeline(RenderTarget.Descriptor.Format, filtering));
        _context.SetShaderResource(DxShaderStage.Pixel, 1, null);
        _context.SetConstantBuffer(DxShaderStage.Pixel, 0, null);
        _context.SetSampler(DxShaderStage.Pixel, 0, GetSampler(filtering));

        var spriteView = _device.CreateShaderResourceView(sprite.Texture.Resource);
        _context.SetVertexBuffer(0, GetSpriteQuadVertexBuffer(), 8);
        _context.SetVertexBuffer(1, instanceBuffer, 32);
        _context.SetShaderResource(DxShaderStage.Pixel, 0, spriteView);
        _context.DrawInstanced(6, submittedInstanceCount);

        _coloredSpriteDraws.Add(new ProGpuDirectXSciChartColoredSpriteDraw(
            CopySpan(vertices),
            sprite,
            startIndex,
            count,
            transform,
            centeredAmount,
            filtering,
            _clipRect));
        _transientResources.Add(instanceBuffer);
        _transientResources.Add(spriteView);
    }

    public void DrawCandlesBatch(
        ReadOnlySpan<ProGpuDirectXSciChartOhlcCandleVertex> vertices,
        int count,
        float width,
        ProGpuDirectXSciChartVertexTransform transform)
    {
        ThrowIfDisposed();
        ValidateFinancialVertexRange(vertices.Length, count);
        ValidateFinancialWidth(width);

        if (HasEmptyClip)
        {
            return;
        }

        var vertexSpan = vertices[..count];
        var fillBuffer = CreateCandleFillVertexBuffer(vertexSpan, width, transform, out var fillVertexCount);
        var strokeBuffer = CreateCandleStrokeVertexBuffer(vertexSpan, width, transform, out var strokeVertexCount);
        if (fillBuffer is null && strokeBuffer is null)
        {
            return;
        }

        _context.SetRenderTargets(RenderTarget);
        _context.SetViewport(new DxViewport(0, 0, RenderTarget.Width, RenderTarget.Height));
        _context.SetScissorRect(_clipRect ?? FullRenderTargetRect);
        _context.SetShaderResource(DxShaderStage.Pixel, 0, null);
        _context.SetShaderResource(DxShaderStage.Pixel, 1, null);
        _context.SetConstantBuffer(DxShaderStage.Pixel, 0, null);
        _context.SetSampler(DxShaderStage.Pixel, 0, null);

        if (fillBuffer is not null)
        {
            _context.SetGraphicsPipeline(GetColumnFillPipeline(RenderTarget.Descriptor.Format));
            _context.SetVertexBuffer(fillBuffer);
            _context.Draw(fillVertexCount);
            _transientResources.Add(fillBuffer);
        }

        if (strokeBuffer is not null)
        {
            _context.SetGraphicsPipeline(GetLinePipeline(RenderTarget.Descriptor.Format));
            _context.SetVertexBuffer(strokeBuffer);
            _context.Draw(strokeVertexCount);
            _transientResources.Add(strokeBuffer);
        }

        _financialBatchDraws.Add(new ProGpuDirectXSciChartFinancialBatchDraw(
            CopySpan(vertexSpan),
            width,
            ProGpuDirectXSciChartFinancialBatchKind.Candles,
            transform,
            false,
            false,
            _clipRect));
    }

    public void DrawOhlcBatch(
        ReadOnlySpan<ProGpuDirectXSciChartOhlcCandleVertex> vertices,
        int count,
        float width,
        ProGpuDirectXSciChartVertexTransform transform,
        bool isDigital = false,
        bool isVerticalChart = false)
    {
        ThrowIfDisposed();
        ValidateFinancialVertexRange(vertices.Length, count);
        ValidateFinancialWidth(width);

        if (HasEmptyClip)
        {
            return;
        }

        var vertexSpan = vertices[..count];
        var effectiveTransform = isVerticalChart
            ? new ProGpuDirectXSciChartVertexTransform(!transform.SwapAxis)
            : transform;
        var strokeBuffer = CreateOhlcStrokeVertexBuffer(vertexSpan, width, effectiveTransform, out var strokeVertexCount);
        if (strokeBuffer is null)
        {
            return;
        }

        _context.SetRenderTargets(RenderTarget);
        _context.SetViewport(new DxViewport(0, 0, RenderTarget.Width, RenderTarget.Height));
        _context.SetScissorRect(_clipRect ?? FullRenderTargetRect);
        _context.SetGraphicsPipeline(GetLinePipeline(RenderTarget.Descriptor.Format));
        _context.SetVertexBuffer(strokeBuffer);
        _context.SetShaderResource(DxShaderStage.Pixel, 0, null);
        _context.SetShaderResource(DxShaderStage.Pixel, 1, null);
        _context.SetConstantBuffer(DxShaderStage.Pixel, 0, null);
        _context.SetSampler(DxShaderStage.Pixel, 0, null);
        _context.Draw(strokeVertexCount);

        _financialBatchDraws.Add(new ProGpuDirectXSciChartFinancialBatchDraw(
            CopySpan(vertexSpan),
            width,
            ProGpuDirectXSciChartFinancialBatchKind.Ohlc,
            transform,
            isDigital,
            isVerticalChart,
            _clipRect));
        _transientResources.Add(strokeBuffer);
    }

    public void DrawTextureVertices(
        ReadOnlySpan<ProGpuDirectXSciChartTextureVertex> vertices,
        int startIndex,
        int count,
        ProGpuDirectXSciChartTexture2D texture,
        ProGpuDirectXSciChartVertexTransform transform,
        ProGpuDirectXSciChartTextureFiltering filtering = ProGpuDirectXSciChartTextureFiltering.Linear)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(texture);
        ValidateDrawableTexture(texture);
        ValidateVertexRange(vertices.Length, startIndex, count);

        if (HasEmptyClip)
        {
            return;
        }

        var vertexSpan = vertices.Slice(startIndex, count);
        var vertexBuffer = CreateBatchedTextureVertexBuffer(vertexSpan, transform);
        var shaderResourceView = _device.CreateShaderResourceView(texture.Resource);
        var sampler = GetSampler(filtering);
        var pipeline = GetBatchedTexturePipeline(RenderTarget.Descriptor.Format, filtering);

        _context.SetRenderTargets(RenderTarget);
        _context.SetViewport(new DxViewport(0, 0, RenderTarget.Width, RenderTarget.Height));
        _context.SetScissorRect(_clipRect ?? FullRenderTargetRect);
        _context.SetGraphicsPipeline(pipeline);
        _context.SetVertexBuffer(vertexBuffer);
        _context.SetShaderResource(DxShaderStage.Pixel, 0, shaderResourceView);
        _context.SetShaderResource(DxShaderStage.Pixel, 1, null);
        _context.SetConstantBuffer(DxShaderStage.Pixel, 0, null);
        _context.SetSampler(DxShaderStage.Pixel, 0, sampler);
        _context.Draw(checked((uint)count));

        _textureVertexDraws.Add(new ProGpuDirectXSciChartTextureVertexDraw(
            texture,
            CopySpan(vertexSpan),
            transform,
            filtering,
            _clipRect));
        _transientResources.Add(vertexBuffer);
        _transientResources.Add(shaderResourceView);
    }

    public void DrawShapedHeatmap(
        ReadOnlySpan<ProGpuDirectXSciChartTextureVertex> vertices,
        int startIndex,
        int count,
        double colorMapMin,
        double colorMapMax,
        ProGpuDirectXSciChartTexture2D heightsTexture,
        ProGpuDirectXSciChartTexture2D gradientTexture,
        ProGpuDirectXSciChartTextureFiltering filtering = ProGpuDirectXSciChartTextureFiltering.Linear)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(heightsTexture);
        ArgumentNullException.ThrowIfNull(gradientTexture);
        ValidateShapedHeatmapTextures(heightsTexture, gradientTexture);
        ValidateColorMapRange(colorMapMin, colorMapMax);
        ValidateVertexRange(vertices.Length, startIndex, count);

        if (HasEmptyClip)
        {
            return;
        }

        var vertexSpan = vertices.Slice(startIndex, count);
        var vertexBuffer = CreateBatchedTextureVertexBuffer(vertexSpan, default);
        var heightsView = _device.CreateShaderResourceView(
            heightsTexture.GetFloatDataBuffer(),
            new DxShaderResourceViewDescriptor
            {
                Dimension = DxResourceViewDimension.Buffer,
                Format = DxResourceFormat.R32Float,
                ElementCount = checked(heightsTexture.Width * heightsTexture.Height),
                ElementStrideInBytes = sizeof(float),
                Label = "SciChart Shaped Heatmap Heights"
            });
        var gradientView = _device.CreateShaderResourceView(gradientTexture.Resource);
        var constants = CreateShapedHeatmapConstants(colorMapMin, colorMapMax, heightsTexture.Width, heightsTexture.Height);
        var sampler = GetSampler(filtering);
        var pipeline = GetShapedHeatmapPipeline(RenderTarget.Descriptor.Format, filtering);

        _context.SetRenderTargets(RenderTarget);
        _context.SetViewport(new DxViewport(0, 0, RenderTarget.Width, RenderTarget.Height));
        _context.SetScissorRect(_clipRect ?? FullRenderTargetRect);
        _context.SetGraphicsPipeline(pipeline);
        _context.SetVertexBuffer(vertexBuffer);
        _context.SetShaderResource(DxShaderStage.Pixel, 0, heightsView);
        _context.SetShaderResource(DxShaderStage.Pixel, 1, gradientView);
        _context.SetConstantBuffer(DxShaderStage.Pixel, 0, constants);
        _context.SetSampler(DxShaderStage.Pixel, 0, sampler);
        _context.Draw(checked((uint)count));

        _shapedHeatmapDraws.Add(new ProGpuDirectXSciChartShapedHeatmapDraw(
            heightsTexture,
            gradientTexture,
            CopySpan(vertexSpan),
            colorMapMin,
            colorMapMax,
            filtering,
            _clipRect));
        _transientResources.Add(vertexBuffer);
        _transientResources.Add(heightsView);
        _transientResources.Add(gradientView);
        _transientResources.Add(constants);
    }

    public void DrawHeightTextureContours(
        ProGpuDirectXSciChartTexture2D heightsTexture,
        ProGpuDirectXSciChartTexture2D contourTexture,
        DxRect viewportRect,
        DxColor color,
        float zMin,
        float zMax,
        float zStep,
        float strokeThickness,
        float opacity)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(heightsTexture);
        ArgumentNullException.ThrowIfNull(contourTexture);
        ValidateDrawRect(viewportRect);
        ValidateHeightTextureContourInputs(heightsTexture, contourTexture, color, zMin, zMax, zStep, strokeThickness, opacity);

        if (HasEmptyClip)
        {
            return;
        }

        var vertexBuffer = CreateTextureQuadVertexBuffer(viewportRect, heightsTexture);
        var heightsView = _device.CreateShaderResourceView(
            heightsTexture.GetFloatDataBuffer(),
            new DxShaderResourceViewDescriptor
            {
                Dimension = DxResourceViewDimension.Buffer,
                Format = DxResourceFormat.R32Float,
                ElementCount = checked(heightsTexture.Width * heightsTexture.Height),
                ElementStrideInBytes = sizeof(float),
                Label = "SciChart Height Contour Heights"
            });
        var contourView = _device.CreateShaderResourceView(contourTexture.Resource);
        var constants = CreateHeightTextureContourConstants(
            zMin,
            zMax,
            zStep,
            strokeThickness,
            opacity,
            heightsTexture.Width,
            heightsTexture.Height,
            color);
        var sampler = GetSampler(ProGpuDirectXSciChartTextureFiltering.Linear);
        var pipeline = GetHeightTextureContourPipeline(RenderTarget.Descriptor.Format);

        _context.SetRenderTargets(RenderTarget);
        _context.SetViewport(new DxViewport(0, 0, RenderTarget.Width, RenderTarget.Height));
        _context.SetScissorRect(_clipRect ?? FullRenderTargetRect);
        _context.SetGraphicsPipeline(pipeline);
        _context.SetVertexBuffer(vertexBuffer);
        _context.SetShaderResource(DxShaderStage.Pixel, 0, heightsView);
        _context.SetShaderResource(DxShaderStage.Pixel, 1, contourView);
        _context.SetConstantBuffer(DxShaderStage.Pixel, 0, constants);
        _context.SetSampler(DxShaderStage.Pixel, 0, sampler);
        _context.Draw(6);

        _heightTextureContourDraws.Add(new ProGpuDirectXSciChartHeightTextureContoursDraw(
            heightsTexture,
            contourTexture,
            viewportRect,
            color,
            zMin,
            zMax,
            zStep,
            strokeThickness,
            opacity,
            _clipRect));
        _transientResources.Add(vertexBuffer);
        _transientResources.Add(heightsView);
        _transientResources.Add(contourView);
        _transientResources.Add(constants);
    }

    public void Flush(bool clearRecordedCommands = true)
    {
        ThrowIfDisposed();
        _context.Flush(clearRecordedCommands);
        DisposeTransientResources();
    }

    public byte[] ReadTargetPixels()
    {
        ThrowIfDisposed();
        return RenderTarget.ReadPixels();
    }

    private DxRect FullRenderTargetRect =>
        new(0, 0, checked((int)RenderTarget.Width), checked((int)RenderTarget.Height));

    private bool HasEmptyClip => _clipRect is { Width: <= 0 } or { Height: <= 0 };

    private void RenderTextDraw(ProGpuDirectXSciChartTextDraw draw)
    {
        if (!_device.IsGpuBacked ||
            _device.Context is not { IsDisposed: false } ||
            RenderTarget.BackendTexture is not { IsDisposed: false } targetTexture)
        {
            return;
        }

        Flush(clearRecordedCommands: true);

        var visual = new DrawingVisual
        {
            Size = new Vector2(RenderTarget.Width, RenderTarget.Height)
        };
        var context = visual.Context;
        if (draw.ClipRect is { } clipRect)
        {
            context.PushClip(new Rect(clipRect.X, clipRect.Y, clipRect.Width, clipRect.Height));
        }

        context.DrawText(
            draw.Text,
            draw.Font.Font,
            draw.FontSize,
            new SolidColorBrush(ToColorVector(draw.ColorArgb)),
            ResolveTextOrigin(draw),
            draw.IsBold,
            draw.IsItalic,
            draw.RotationRadians,
            draw.IsAntiAliased ? TextRenderingMode.Grayscale : TextRenderingMode.Aliased,
            TextHintingMode.Auto);

        if (draw.ClipRect is not null)
        {
            context.PopClip();
        }

        GetOrCreateTextCompositor().RenderOffscreen(
            visual,
            RenderTarget.Width,
            RenderTarget.Height,
            targetTexture,
            padding: 0f,
            dpiScale: 1f,
            clearColor: null,
            loadExistingContents: true);
    }

    private Compositor GetOrCreateTextCompositor()
    {
        if (_textCompositor is { } compositor)
        {
            return compositor;
        }

        var context = _device.Context
            ?? throw new InvalidOperationException("A GPU-backed WgpuContext is required for SciChart text rendering.");
        _textCompositor = new Compositor(
            context,
            ProGpuDirectXFormatConverter.ToTextureFormat(RenderTarget.Descriptor.Format));
        return _textCompositor;
    }

    private static Vector2 ResolveTextOrigin(ProGpuDirectXSciChartTextDraw draw)
    {
        var layout = new TextLayout(draw.Text, draw.Font.Font, draw.FontSize);
        var x = draw.Position.X;
        var y = draw.Position.Y;

        x -= draw.HorizontalAlignment switch
        {
            ProGpuDirectXSciChartTextAlignment.Center => layout.MeasuredSize.X / 2f,
            ProGpuDirectXSciChartTextAlignment.Right => layout.MeasuredSize.X,
            _ => 0f
        };

        y -= draw.VerticalAlignment switch
        {
            ProGpuDirectXSciChartVerticalTextAlignment.Center => layout.MeasuredSize.Y / 2f,
            ProGpuDirectXSciChartVerticalTextAlignment.Bottom => layout.MeasuredSize.Y,
            ProGpuDirectXSciChartVerticalTextAlignment.Baseline => draw.Font.Font.Ascender * draw.FontSize / draw.Font.Font.UnitsPerEm,
            _ => 0f
        };

        return new Vector2(x, y);
    }

    private ProGpuDirectXBuffer CreateTextureQuadVertexBuffer(
        DxRect viewportRect,
        ProGpuDirectXSciChartTexture2D texture)
    {
        var left = PixelXToNdc(viewportRect.X);
        var right = PixelXToNdc(viewportRect.X + viewportRect.Width);
        var top = PixelYToNdc(viewportRect.Y);
        var bottom = PixelYToNdc(viewportRect.Y + viewportRect.Height);
        ReadOnlySpan<float> vertices =
        [
            left,  top,    0f, 0f,
            right, top,    1f, 0f,
            right, bottom, 1f, 1f,
            left,  top,    0f, 0f,
            right, bottom, 1f, 1f,
            left,  bottom, 0f, 1f
        ];

        var vertexBuffer = _device.CreateBuffer(new DxBufferDescriptor
        {
            SizeInBytes = checked((uint)(vertices.Length * sizeof(float))),
            Usage = DxBufferUsage.Vertex | DxBufferUsage.CopyDestination,
            StrideInBytes = 16,
            Label = $"SciChartTextureQuad {texture.Width}x{texture.Height}"
        });
        vertexBuffer.Write(vertices);
        return vertexBuffer;
    }

    private void DrawVerticalPixelsTexture(
        int xLeft,
        int xRight,
        int yStartBottom,
        int yEndTop,
        ReadOnlySpan<int> yCoordinates,
        ReadOnlySpan<int> pixelColorsArgb,
        double opacity,
        bool isUniform,
        bool yAxisIsFlipped)
    {
        var uploadColors = ArrayPool<int>.Shared.Rent(pixelColorsArgb.Length);
        try
        {
            var uploadColorSpan = uploadColors.AsSpan(0, pixelColorsArgb.Length);
            CopyVerticalPixelColors(pixelColorsArgb, opacity, yAxisIsFlipped, uploadColorSpan);
            if (!ContainsVisibleColor(uploadColorSpan))
            {
                return;
            }

            var viewportRect = CreateVerticalPixelRect(xLeft, xRight, yStartBottom, yEndTop);
            var pixelsTexture = CreateTexture(1, checked((uint)uploadColorSpan.Length));
            pixelsTexture.SetData(uploadColorSpan);
            DrawTexture(
                pixelsTexture,
                viewportRect,
                ProGpuDirectXSciChartTextureFiltering.Point,
                isUniform: false);

            _verticalPixelsDraws.Add(new ProGpuDirectXSciChartVerticalPixelsDraw(
                xLeft,
                xRight,
                yCoordinates.IsEmpty ? null : CopySpan(yCoordinates),
                yStartBottom,
                yEndTop,
                CopySpan(pixelColorsArgb),
                opacity,
                isUniform,
                yAxisIsFlipped,
                _clipRect));
            _transientResources.Add(pixelsTexture);
        }
        finally
        {
            ArrayPool<int>.Shared.Return(uploadColors);
        }
    }

    private ProGpuDirectXBuffer CreateBatchedTextureVertexBuffer(
        ReadOnlySpan<ProGpuDirectXSciChartTextureVertex> vertices,
        ProGpuDirectXSciChartVertexTransform transform)
    {
        var vertexData = new float[checked(vertices.Length * 8)];
        for (var i = 0; i < vertices.Length; i++)
        {
            var source = vertices[i];
            var x = transform.SwapAxis ? source.Y : source.X;
            var y = transform.SwapAxis ? source.X : source.Y;
            var offset = i * 8;
            vertexData[offset] = PixelXToNdc(x);
            vertexData[offset + 1] = PixelYToNdc(y);
            vertexData[offset + 2] = source.U;
            vertexData[offset + 3] = source.V;
            WriteColorArgb(vertexData, offset + 4, source.ColorArgb);
        }

        var vertexBuffer = _device.CreateBuffer(new DxBufferDescriptor
        {
            SizeInBytes = checked((uint)(vertexData.Length * sizeof(float))),
            Usage = DxBufferUsage.Vertex | DxBufferUsage.CopyDestination,
            StrideInBytes = 32,
            Label = $"SciChartTextureVertices {vertices.Length}"
        });
        vertexBuffer.Write(vertexData);
        return vertexBuffer;
    }

    private void SetSolidColorDrawState()
    {
        _context.SetRenderTargets(RenderTarget);
        _context.SetViewport(new DxViewport(0, 0, RenderTarget.Width, RenderTarget.Height));
        _context.SetScissorRect(_clipRect ?? FullRenderTargetRect);
        _context.SetShaderResource(DxShaderStage.Pixel, 0, null);
        _context.SetShaderResource(DxShaderStage.Pixel, 1, null);
        _context.SetConstantBuffer(DxShaderStage.Pixel, 0, null);
        _context.SetSampler(DxShaderStage.Pixel, 0, null);
    }

    private void DrawSolidColorBuffer(
        ProGpuDirectXBuffer vertexBuffer,
        ProGpuDirectXGraphicsPipeline pipeline,
        uint vertexCount)
    {
        _context.SetGraphicsPipeline(pipeline);
        _context.SetVertexBuffer(vertexBuffer);
        _context.Draw(vertexCount);
        _transientResources.Add(vertexBuffer);
    }

    private void DrawPrimitiveLineCore(
        ProGpuDirectXSciChartPrimitiveKind kind,
        ProGpuDirectXSciChartPen2D? pen,
        ReadOnlySpan<ProGpuDirectXSciChartPoint> points,
        int count)
    {
        ThrowIfDisposed();
        ValidatePointRange(points.Length, count, minCount: 2, shapeName: "line primitives");
        pen ??= ProGpuDirectXSciChartPen2D.Default;

        if (HasEmptyClip)
        {
            return;
        }

        var pointSpan = points[..count];
        var lineVertices = CreateLineVertices(pointSpan, pen.ColorArgb);
        var vertexBuffer = CreateLineBatchVertexBuffer(
            lineVertices,
            default,
            pen,
            isStrips: true,
            isDigital: false,
            isDrawNanAsGaps: true,
            out var submittedVertexCount,
            out var usesTriangleTopology);
        if (vertexBuffer is null)
        {
            return;
        }

        SetSolidColorDrawState();
        DrawSolidColorBuffer(
            vertexBuffer,
            usesTriangleTopology
                ? GetColumnFillPipeline(RenderTarget.Descriptor.Format)
                : GetLinePipeline(RenderTarget.Descriptor.Format),
            submittedVertexCount);

        _primitiveDraws.Add(new ProGpuDirectXSciChartPrimitiveDraw(
            kind,
            CopyPrimitivePoints(pointSpan),
            pen,
            null,
            1d,
            false,
            0d,
            _clipRect));
    }

    private static ProGpuDirectXSciChartColorVertex[] CreateLineVertices(
        ReadOnlySpan<ProGpuDirectXSciChartPoint> points,
        uint colorArgb)
    {
        var vertices = new ProGpuDirectXSciChartColorVertex[points.Length];
        for (var i = 0; i < points.Length; i++)
        {
            var point = points[i];
            vertices[i] = new ProGpuDirectXSciChartColorVertex(point.X, point.Y, 0f, colorArgb);
        }

        return vertices;
    }

    private ProGpuDirectXBuffer? CreateRectangleFillVertexBuffer(
        ProGpuDirectXSciChartPoint pt1,
        ProGpuDirectXSciChartPoint pt2,
        ProGpuDirectXSciChartBrush2D brush,
        double opacity,
        out uint submittedVertexCount)
    {
        var vertexData = new List<float>(36);
        if (TryGetPrimitiveRect(pt1, pt2, out var left, out var top, out var right, out var bottom)
            && HasVisibleBrush(brush, opacity))
        {
            var bounds = new PrimitiveBounds(left, top, right, bottom);
            AppendBrushQuad(vertexData, left, top, right, bottom, brush, bounds, opacity);
        }

        return CreateSolidColorVertexBuffer(vertexData, "SciChartPrimitiveRectangleFillVertices", out submittedVertexCount);
    }

    private ProGpuDirectXBuffer? CreatePolygonFillVertexBuffer(
        ReadOnlySpan<ProGpuDirectXSciChartPoint> points,
        ProGpuDirectXSciChartBrush2D brush,
        out uint submittedVertexCount)
    {
        var vertexData = new List<float>(checked(Math.Max(points.Length - 2, 0) * 18));
        if (HasVisibleBrush(brush)
            && TryGetPointBounds(points, out var bounds))
        {
            AppendTriangulatedPolygon(vertexData, points, brush, bounds);
        }

        return CreateSolidColorVertexBuffer(vertexData, "SciChartPrimitivePolygonFillVertices", out submittedVertexCount);
    }

    private void AppendTriangulatedPolygon(
        List<float> vertexData,
        ReadOnlySpan<ProGpuDirectXSciChartPoint> points,
        ProGpuDirectXSciChartBrush2D brush,
        PrimitiveBounds bounds)
    {
        var polygon = CopyFinitePolygonPoints(points);
        if (polygon.Count < 3)
        {
            return;
        }

        var signedArea = GetSignedArea(polygon);
        if (Math.Abs(signedArea) <= float.Epsilon)
        {
            return;
        }

        var isCounterClockwise = signedArea > 0f;
        var indices = new List<int>(polygon.Count);
        for (var i = 0; i < polygon.Count; i++)
        {
            indices.Add(i);
        }

        var guard = polygon.Count * polygon.Count;
        var cursor = 0;
        while (indices.Count > 3 && guard-- > 0)
        {
            cursor %= indices.Count;
            var previousIndex = indices[(cursor + indices.Count - 1) % indices.Count];
            var currentIndex = indices[cursor];
            var nextIndex = indices[(cursor + 1) % indices.Count];
            var previous = polygon[previousIndex];
            var current = polygon[currentIndex];
            var next = polygon[nextIndex];
            var cross = Cross(previous, current, next);
            if (Math.Abs(cross) <= 0.0001f)
            {
                indices.RemoveAt(cursor);
                continue;
            }

            if (IsPolygonEar(polygon, indices, previousIndex, currentIndex, nextIndex, isCounterClockwise))
            {
                AppendBrushTriangle(vertexData, previous, current, next, brush, bounds);
                indices.RemoveAt(cursor);
                continue;
            }

            cursor++;
        }

        if (indices.Count == 3)
        {
            AppendBrushTriangle(
                vertexData,
                polygon[indices[0]],
                polygon[indices[1]],
                polygon[indices[2]],
                brush,
                bounds);
        }
    }

    private static List<ProGpuDirectXSciChartPoint> CopyFinitePolygonPoints(
        ReadOnlySpan<ProGpuDirectXSciChartPoint> points)
    {
        var polygon = new List<ProGpuDirectXSciChartPoint>(points.Length);
        foreach (var point in points)
        {
            if (!HasFinitePoint(point))
            {
                continue;
            }

            if (polygon.Count == 0
                || !AreSamePoint(polygon[^1], point))
            {
                polygon.Add(point);
            }
        }

        if (polygon.Count > 1
            && AreSamePoint(polygon[0], polygon[^1]))
        {
            polygon.RemoveAt(polygon.Count - 1);
        }

        return polygon;
    }

    private static bool IsPolygonEar(
        IReadOnlyList<ProGpuDirectXSciChartPoint> polygon,
        IReadOnlyList<int> indices,
        int previousIndex,
        int currentIndex,
        int nextIndex,
        bool isCounterClockwise)
    {
        var previous = polygon[previousIndex];
        var current = polygon[currentIndex];
        var next = polygon[nextIndex];
        var cross = Cross(previous, current, next);
        if (isCounterClockwise ? cross <= 0.0001f : cross >= -0.0001f)
        {
            return false;
        }

        foreach (var index in indices)
        {
            if (index == previousIndex
                || index == currentIndex
                || index == nextIndex)
            {
                continue;
            }

            if (IsPointInTriangle(polygon[index], previous, current, next))
            {
                return false;
            }
        }

        return true;
    }

    private void AppendSolidColorTriangle(
        List<float> vertexData,
        ProGpuDirectXSciChartPoint pt0,
        ProGpuDirectXSciChartPoint pt1,
        ProGpuDirectXSciChartPoint pt2,
        uint colorArgb)
    {
        AppendSolidColorVertex(vertexData, pt0.X, pt0.Y, colorArgb);
        AppendSolidColorVertex(vertexData, pt1.X, pt1.Y, colorArgb);
        AppendSolidColorVertex(vertexData, pt2.X, pt2.Y, colorArgb);
    }

    private void AppendBrushTriangle(
        List<float> vertexData,
        ProGpuDirectXSciChartPoint pt0,
        ProGpuDirectXSciChartPoint pt1,
        ProGpuDirectXSciChartPoint pt2,
        ProGpuDirectXSciChartBrush2D brush,
        PrimitiveBounds bounds,
        double opacity = 1d,
        double gradientRotationAngle = 0d)
    {
        AppendBrushVertex(vertexData, pt0.X, pt0.Y, brush, bounds, opacity, gradientRotationAngle);
        AppendBrushVertex(vertexData, pt1.X, pt1.Y, brush, bounds, opacity, gradientRotationAngle);
        AppendBrushVertex(vertexData, pt2.X, pt2.Y, brush, bounds, opacity, gradientRotationAngle);
    }

    private void AppendBrushQuad(
        List<float> vertexData,
        float left,
        float top,
        float right,
        float bottom,
        ProGpuDirectXSciChartBrush2D brush,
        PrimitiveBounds bounds,
        double opacity = 1d,
        double gradientRotationAngle = 0d)
    {
        AppendBrushVertex(vertexData, left, top, brush, bounds, opacity, gradientRotationAngle);
        AppendBrushVertex(vertexData, right, top, brush, bounds, opacity, gradientRotationAngle);
        AppendBrushVertex(vertexData, right, bottom, brush, bounds, opacity, gradientRotationAngle);
        AppendBrushVertex(vertexData, left, top, brush, bounds, opacity, gradientRotationAngle);
        AppendBrushVertex(vertexData, right, bottom, brush, bounds, opacity, gradientRotationAngle);
        AppendBrushVertex(vertexData, left, bottom, brush, bounds, opacity, gradientRotationAngle);
    }

    private void AppendBrushVertex(
        List<float> vertexData,
        float x,
        float y,
        ProGpuDirectXSciChartBrush2D brush,
        PrimitiveBounds bounds,
        double opacity,
        double gradientRotationAngle)
    {
        AppendSolidColorVertex(
            vertexData,
            x,
            y,
            GetBrushColorArgb(brush, x, y, bounds, opacity, gradientRotationAngle));
    }

    private static float GetSignedArea(IReadOnlyList<ProGpuDirectXSciChartPoint> points)
    {
        var area = 0f;
        for (var i = 0; i < points.Count; i++)
        {
            var current = points[i];
            var next = points[(i + 1) % points.Count];
            area += (current.X * next.Y) - (next.X * current.Y);
        }

        return area * 0.5f;
    }

    private static float Cross(
        ProGpuDirectXSciChartPoint pt0,
        ProGpuDirectXSciChartPoint pt1,
        ProGpuDirectXSciChartPoint pt2)
    {
        return ((pt1.X - pt0.X) * (pt2.Y - pt0.Y))
            - ((pt1.Y - pt0.Y) * (pt2.X - pt0.X));
    }

    private static bool IsPointInTriangle(
        ProGpuDirectXSciChartPoint point,
        ProGpuDirectXSciChartPoint pt0,
        ProGpuDirectXSciChartPoint pt1,
        ProGpuDirectXSciChartPoint pt2)
    {
        const float tolerance = 0.0001f;
        var cross0 = Cross(point, pt0, pt1);
        var cross1 = Cross(point, pt1, pt2);
        var cross2 = Cross(point, pt2, pt0);
        var hasNegative = cross0 < -tolerance || cross1 < -tolerance || cross2 < -tolerance;
        var hasPositive = cross0 > tolerance || cross1 > tolerance || cross2 > tolerance;
        return !(hasNegative && hasPositive);
    }

    private static bool AreSamePoint(
        ProGpuDirectXSciChartPoint pt0,
        ProGpuDirectXSciChartPoint pt1)
    {
        return Math.Abs(pt0.X - pt1.X) <= 0.0001f
            && Math.Abs(pt0.Y - pt1.Y) <= 0.0001f;
    }

    private ProGpuDirectXBuffer? CreateEllipseFillVertexBuffer(
        ProGpuDirectXSciChartPoint center,
        float width,
        float height,
        int segmentCount,
        ProGpuDirectXSciChartBrush2D brush,
        out uint submittedVertexCount)
    {
        var radiusX = width / 2f;
        var radiusY = height / 2f;
        var bounds = new PrimitiveBounds(
            center.X - radiusX,
            center.Y - radiusY,
            center.X + radiusX,
            center.Y + radiusY);
        var vertexData = new List<float>(checked(segmentCount * 18));
        for (var i = 0; i < segmentCount; i++)
        {
            AppendBrushTriangle(
                vertexData,
                center,
                GetEllipsePoint(center, radiusX, radiusY, i, segmentCount),
                GetEllipsePoint(center, radiusX, radiusY, i + 1, segmentCount),
                brush,
                bounds);
        }

        return CreateSolidColorVertexBuffer(vertexData, "SciChartPrimitiveEllipseFillVertices", out submittedVertexCount);
    }

    private ProGpuDirectXBuffer? CreateEllipseStrokeVertexBuffer(
        ProGpuDirectXSciChartPoint center,
        float width,
        float height,
        int segmentCount,
        ProGpuDirectXSciChartPen2D pen,
        out uint submittedVertexCount,
        out bool usesTriangleTopology)
    {
        var radiusX = width / 2f;
        var radiusY = height / 2f;
        var lineVertices = new ProGpuDirectXSciChartColorVertex[segmentCount + 1];
        for (var i = 0; i <= segmentCount; i++)
        {
            var point = GetEllipsePoint(center, radiusX, radiusY, i, segmentCount);
            lineVertices[i] = new ProGpuDirectXSciChartColorVertex(point.X, point.Y, 0f, pen.ColorArgb);
        }

        return CreateLineBatchVertexBuffer(
            lineVertices,
            default,
            pen,
            isStrips: true,
            isDigital: false,
            isDrawNanAsGaps: true,
            out submittedVertexCount,
            out usesTriangleTopology);
    }

    private static int GetEllipseSegmentCount(float width, float height)
    {
        var maxRadius = MathF.Max(width, height) / 2f;
        return Math.Clamp(checked((int)MathF.Ceiling(maxRadius * MathF.PI / 4f) * 4), 24, 128);
    }

    private static ProGpuDirectXSciChartPoint GetEllipsePoint(
        ProGpuDirectXSciChartPoint center,
        float radiusX,
        float radiusY,
        int index,
        int segmentCount)
    {
        var angle = ((MathF.PI * 2f) * index) / segmentCount;
        return new ProGpuDirectXSciChartPoint(
            center.X + (MathF.Cos(angle) * radiusX),
            center.Y + (MathF.Sin(angle) * radiusY));
    }

    private ProGpuDirectXBuffer? CreateAreaFillVertexBuffer(
        ReadOnlySpan<ProGpuDirectXSciChartAreaSegment> lines,
        ProGpuDirectXSciChartBrush2D brush,
        double gradientRotationAngle,
        out uint submittedVertexCount)
    {
        var vertexData = new List<float>(checked(Math.Max(lines.Length - 1, 0) * 36));
        if (HasVisibleBrush(brush)
            && TryGetAreaBounds(lines, out var bounds))
        {
            for (var i = 0; i < lines.Length - 1; i++)
            {
                var start = lines[i];
                var end = lines[i + 1];
                if (!HasFiniteAreaSegment(start)
                    || !HasFiniteAreaSegment(end))
                {
                    continue;
                }

                AppendBrushVertex(vertexData, start.Start.X, start.Start.Y, brush, bounds, 1d, gradientRotationAngle);
                AppendBrushVertex(vertexData, end.Start.X, end.Start.Y, brush, bounds, 1d, gradientRotationAngle);
                AppendBrushVertex(vertexData, end.End.X, end.End.Y, brush, bounds, 1d, gradientRotationAngle);
                AppendBrushVertex(vertexData, start.Start.X, start.Start.Y, brush, bounds, 1d, gradientRotationAngle);
                AppendBrushVertex(vertexData, end.End.X, end.End.Y, brush, bounds, 1d, gradientRotationAngle);
                AppendBrushVertex(vertexData, start.End.X, start.End.Y, brush, bounds, 1d, gradientRotationAngle);
            }
        }

        return CreateSolidColorVertexBuffer(vertexData, "SciChartPrimitiveAreaFillVertices", out submittedVertexCount);
    }

    private void AppendSolidColorQuad(
        List<float> vertexData,
        float left,
        float top,
        float right,
        float bottom,
        uint colorArgb)
    {
        AppendSolidColorVertex(vertexData, left, top, colorArgb);
        AppendSolidColorVertex(vertexData, right, top, colorArgb);
        AppendSolidColorVertex(vertexData, right, bottom, colorArgb);
        AppendSolidColorVertex(vertexData, left, top, colorArgb);
        AppendSolidColorVertex(vertexData, right, bottom, colorArgb);
        AppendSolidColorVertex(vertexData, left, bottom, colorArgb);
    }

    private static ProGpuDirectXSciChartPoint[] CopyAreaPoints(
        ReadOnlySpan<ProGpuDirectXSciChartAreaSegment> lines)
    {
        var points = new ProGpuDirectXSciChartPoint[checked(lines.Length * 2)];
        for (var i = 0; i < lines.Length; i++)
        {
            points[i * 2] = lines[i].Start;
            points[(i * 2) + 1] = lines[i].End;
        }

        return points;
    }

    private static ProGpuDirectXSciChartPoint[] CopyPrimitivePoints(
        ReadOnlySpan<ProGpuDirectXSciChartPoint> points)
    {
        var copiedPoints = new ProGpuDirectXSciChartPoint[points.Length];
        points.CopyTo(copiedPoints);
        return copiedPoints;
    }

    private static T[] CopySpan<T>(ReadOnlySpan<T> values)
    {
        var copiedValues = new T[values.Length];
        values.CopyTo(copiedValues);
        return copiedValues;
    }

    private ProGpuDirectXBuffer? CreateMountainFillVertexBuffer(
        ReadOnlySpan<ProGpuDirectXSciChartBandVertex> vertices,
        ProGpuDirectXSciChartBrush2D brush,
        ProGpuDirectXSciChartPalette? palette,
        ProGpuDirectXSciChartVertexTransform transform,
        bool isDigital,
        out uint submittedVertexCount)
    {
        var vertexData = new List<float>(checked(Math.Max(vertices.Length - 1, 0) * 36));
        for (var i = 0; i < vertices.Length - 1; i++)
        {
            var colorArgb = GetPaletteColor(palette?.FillPositiveColors, i, brush.ColorArgb);
            AppendBandFillSegment(
                vertexData,
                vertices[i],
                vertices[i + 1],
                transform,
                colorArgb,
                colorArgb,
                isDigital,
                splitAtCrossing: false);
        }

        return CreateSolidColorVertexBuffer(vertexData, "SciChartMountainFillVertices", out submittedVertexCount);
    }

    private ProGpuDirectXBuffer? CreateBandFillVertexBuffer(
        ReadOnlySpan<ProGpuDirectXSciChartBandVertex> vertices,
        ProGpuDirectXSciChartBrush2D brushPositive,
        ProGpuDirectXSciChartBrush2D brushNegative,
        ProGpuDirectXSciChartPalette? palette,
        ProGpuDirectXSciChartVertexTransform transform,
        bool isDigital,
        out uint submittedVertexCount)
    {
        var vertexData = new List<float>(checked(Math.Max(vertices.Length - 1, 0) * 72));
        for (var i = 0; i < vertices.Length - 1; i++)
        {
            var positiveColor = GetPaletteColor(palette?.FillPositiveColors, i, brushPositive.ColorArgb);
            var negativeColor = GetPaletteColor(palette?.FillNegativeColors, i, brushNegative.ColorArgb);
            AppendBandFillSegment(
                vertexData,
                vertices[i],
                vertices[i + 1],
                transform,
                positiveColor,
                negativeColor,
                isDigital,
                splitAtCrossing: true);
        }

        return CreateSolidColorVertexBuffer(vertexData, "SciChartBandFillVertices", out submittedVertexCount);
    }

    private static ProGpuDirectXSciChartColorVertex[] CreateBandLineVertices(
        ReadOnlySpan<ProGpuDirectXSciChartBandVertex> vertices,
        bool useY1,
        ProGpuDirectXSciChartPen2D pen,
        ProGpuDirectXSciChartPalette? palette)
    {
        var lineVertices = new ProGpuDirectXSciChartColorVertex[vertices.Length];
        var colors = useY1
            ? palette?.LineBColors
            : palette?.LineAColors;
        for (var i = 0; i < vertices.Length; i++)
        {
            var source = vertices[i];
            lineVertices[i] = new ProGpuDirectXSciChartColorVertex(
                source.X,
                useY1 ? source.Y1 : source.Y0,
                0f,
                GetPaletteColor(colors, i, pen.ColorArgb));
        }

        return lineVertices;
    }

    private void AppendBandFillSegment(
        List<float> vertexData,
        ProGpuDirectXSciChartBandVertex start,
        ProGpuDirectXSciChartBandVertex end,
        ProGpuDirectXSciChartVertexTransform transform,
        uint positiveColorArgb,
        uint negativeColorArgb,
        bool isDigital,
        bool splitAtCrossing)
    {
        if (!HasFiniteBandVertex(start) || !HasFiniteBandVertex(end))
        {
            return;
        }

        if (!isDigital
            && splitAtCrossing
            && TryGetBandCrossing(start, end, out var crossing))
        {
            AppendBandFillQuad(
                vertexData,
                start,
                crossing,
                transform,
                SelectBandFillColor(start, positiveColorArgb, negativeColorArgb),
                isDigital: false);
            AppendBandFillQuad(
                vertexData,
                crossing,
                end,
                transform,
                SelectBandFillColor(end, positiveColorArgb, negativeColorArgb),
                isDigital: false);
            return;
        }

        AppendBandFillQuad(
            vertexData,
            start,
            end,
            transform,
            SelectBandFillColor(start, positiveColorArgb, negativeColorArgb),
            isDigital);
    }

    private void AppendBandFillQuad(
        List<float> vertexData,
        ProGpuDirectXSciChartBandVertex start,
        ProGpuDirectXSciChartBandVertex end,
        ProGpuDirectXSciChartVertexTransform transform,
        uint colorArgb,
        bool isDigital)
    {
        if (!HasVisibleColor(colorArgb))
        {
            return;
        }

        var endY0 = isDigital ? start.Y0 : end.Y0;
        var endY1 = isDigital ? start.Y1 : end.Y1;
        AppendTransformedSolidColorVertex(vertexData, start.X, start.Y0, transform, colorArgb);
        AppendTransformedSolidColorVertex(vertexData, end.X, endY0, transform, colorArgb);
        AppendTransformedSolidColorVertex(vertexData, end.X, endY1, transform, colorArgb);
        AppendTransformedSolidColorVertex(vertexData, start.X, start.Y0, transform, colorArgb);
        AppendTransformedSolidColorVertex(vertexData, end.X, endY1, transform, colorArgb);
        AppendTransformedSolidColorVertex(vertexData, start.X, start.Y1, transform, colorArgb);
    }

    private ProGpuDirectXBuffer? CreateColumnFillVertexBuffer(
        ReadOnlySpan<ProGpuDirectXSciChartColumnVertex> vertices,
        ProGpuDirectXSciChartVertexTransform transform,
        out uint submittedVertexCount)
    {
        var vertexData = new List<float>(checked(vertices.Length * 36));
        foreach (var source in vertices)
        {
            if (!TryGetColumnRect(source, transform, out var left, out var top, out var right, out var bottom)
                || !HasVisibleColor(source.FillColorArgb))
            {
                continue;
            }

            AppendSolidColorVertex(vertexData, left, top, source.FillColorArgb);
            AppendSolidColorVertex(vertexData, right, top, source.FillColorArgb);
            AppendSolidColorVertex(vertexData, right, bottom, source.FillColorArgb);
            AppendSolidColorVertex(vertexData, left, top, source.FillColorArgb);
            AppendSolidColorVertex(vertexData, right, bottom, source.FillColorArgb);
            AppendSolidColorVertex(vertexData, left, bottom, source.FillColorArgb);
        }

        return CreateSolidColorVertexBuffer(vertexData, "SciChartColumnFillVertices", out submittedVertexCount);
    }

    private ProGpuDirectXBuffer? CreateColumnStrokeVertexBuffer(
        ReadOnlySpan<ProGpuDirectXSciChartColumnVertex> vertices,
        ProGpuDirectXSciChartVertexTransform transform,
        out uint submittedVertexCount)
    {
        var vertexData = new List<float>(checked(vertices.Length * 48));
        foreach (var source in vertices)
        {
            if (!TryGetColumnRect(source, transform, out var left, out var top, out var right, out var bottom)
                || !HasVisibleColor(source.StrokeColorArgb))
            {
                continue;
            }

            AppendSolidColorVertex(vertexData, left, top, source.StrokeColorArgb);
            AppendSolidColorVertex(vertexData, right, top, source.StrokeColorArgb);
            AppendSolidColorVertex(vertexData, right, top, source.StrokeColorArgb);
            AppendSolidColorVertex(vertexData, right, bottom, source.StrokeColorArgb);
            AppendSolidColorVertex(vertexData, right, bottom, source.StrokeColorArgb);
            AppendSolidColorVertex(vertexData, left, bottom, source.StrokeColorArgb);
            AppendSolidColorVertex(vertexData, left, bottom, source.StrokeColorArgb);
            AppendSolidColorVertex(vertexData, left, top, source.StrokeColorArgb);
        }

        return CreateSolidColorVertexBuffer(vertexData, "SciChartColumnStrokeVertices", out submittedVertexCount);
    }

    private ProGpuDirectXBuffer? CreateRectBatchVertexBuffer(
        ReadOnlySpan<ProGpuDirectXSciChartRectVertex> vertices,
        ProGpuDirectXSciChartVertexTransform transform,
        ProGpuDirectXSciChartSpriteAnchor anchor,
        out uint submittedVertexCount)
    {
        var vertexData = new List<float>(checked(vertices.Length * 36));
        foreach (var source in vertices)
        {
            if (!TryGetRect(source, transform, anchor, out var left, out var top, out var right, out var bottom)
                || !HasVisibleColor(source.ColorArgb))
            {
                continue;
            }

            AppendSolidColorVertex(vertexData, left, top, source.ColorArgb);
            AppendSolidColorVertex(vertexData, right, top, source.ColorArgb);
            AppendSolidColorVertex(vertexData, right, bottom, source.ColorArgb);
            AppendSolidColorVertex(vertexData, left, top, source.ColorArgb);
            AppendSolidColorVertex(vertexData, right, bottom, source.ColorArgb);
            AppendSolidColorVertex(vertexData, left, bottom, source.ColorArgb);
        }

        return CreateSolidColorVertexBuffer(vertexData, "SciChartRectBatchVertices", out submittedVertexCount);
    }

    private ProGpuDirectXBuffer? CreateVerticalPixelVertexBuffer(
        int xLeft,
        int xRight,
        ReadOnlySpan<int> yCoordinates,
        ReadOnlySpan<int> pixelColorsArgb,
        double opacity,
        bool yAxisIsFlipped,
        out uint submittedVertexCount)
    {
        var left = Math.Min(xLeft, xRight);
        var right = Math.Max(xLeft, xRight);
        var intervalCount = yCoordinates.Length - 1;
        var vertexData = new List<float>(checked(intervalCount * 36));

        for (var i = 0; i < intervalCount; i++)
        {
            var colorIndex = yAxisIsFlipped ? intervalCount - 1 - i : i;
            var colorArgb = ApplyOpacity(unchecked((uint)pixelColorsArgb[colorIndex]), opacity);
            if (!HasVisibleColor(colorArgb))
            {
                continue;
            }

            var y0 = yCoordinates[i];
            var y1 = yCoordinates[i + 1];
            if (y0 == y1)
            {
                continue;
            }

            var top = Math.Min(y0, y1);
            var bottom = Math.Max(y0, y1);
            AppendSolidColorVertex(vertexData, left, top, colorArgb);
            AppendSolidColorVertex(vertexData, right, top, colorArgb);
            AppendSolidColorVertex(vertexData, right, bottom, colorArgb);
            AppendSolidColorVertex(vertexData, left, top, colorArgb);
            AppendSolidColorVertex(vertexData, right, bottom, colorArgb);
            AppendSolidColorVertex(vertexData, left, bottom, colorArgb);
        }

        return CreateSolidColorVertexBuffer(vertexData, "SciChartVerticalPixels", out submittedVertexCount);
    }

    private ProGpuDirectXBuffer? CreateSpriteBatchVertexBuffer(
        ReadOnlySpan<ProGpuDirectXSciChartSpriteVertex> vertices,
        ProGpuDirectXSciChartSprite2D sprite,
        ProGpuDirectXSciChartVertexTransform transform,
        float centeredAmount,
        bool useStrokeColor,
        out uint submittedVertexCount)
    {
        var vertexData = new List<float>(checked(vertices.Length * 48));
        foreach (var source in vertices)
        {
            var colorArgb = useStrokeColor
                ? source.StrokeColorArgb
                : source.FillColorArgb;
            if (!HasVisibleColor(colorArgb)
                || !TryGetSpriteRect(source, sprite, transform, centeredAmount, out var left, out var top, out var right, out var bottom))
            {
                continue;
            }

            AppendTexturedColorVertex(vertexData, left, top, 0f, 0f, colorArgb);
            AppendTexturedColorVertex(vertexData, right, top, 1f, 0f, colorArgb);
            AppendTexturedColorVertex(vertexData, right, bottom, 1f, 1f, colorArgb);
            AppendTexturedColorVertex(vertexData, left, top, 0f, 0f, colorArgb);
            AppendTexturedColorVertex(vertexData, right, bottom, 1f, 1f, colorArgb);
            AppendTexturedColorVertex(vertexData, left, bottom, 0f, 1f, colorArgb);
        }

        return CreateTexturedColorVertexBuffer(vertexData, "SciChartSpriteBatchVertices", out submittedVertexCount);
    }

    private ProGpuDirectXBuffer? CreateColoredSpriteInstanceBuffer(
        ReadOnlySpan<ProGpuDirectXSciChartColoredSpriteVertex> vertices,
        ProGpuDirectXSciChartSprite2D sprite,
        ProGpuDirectXSciChartVertexTransform transform,
        float centeredAmount,
        out uint submittedInstanceCount)
    {
        var instanceData = new List<float>(checked(vertices.Length * 8));
        foreach (var source in vertices)
        {
            if (!HasVisibleColor(source.ColorArgb)
                || !TryGetSpriteRect(source, sprite, transform, centeredAmount, out var left, out var top, out var right, out var bottom))
            {
                continue;
            }

            instanceData.Add(PixelXToNdc(left));
            instanceData.Add(PixelYToNdc(top));
            instanceData.Add(PixelXToNdc(right));
            instanceData.Add(PixelYToNdc(bottom));
            AppendColorArgb(instanceData, source.ColorArgb);
        }

        submittedInstanceCount = checked((uint)(instanceData.Count / 8));
        if (submittedInstanceCount == 0)
        {
            return null;
        }

        var instanceSpan = CollectionsMarshal.AsSpan(instanceData);
        var instanceBuffer = _device.CreateBuffer(new DxBufferDescriptor
        {
            SizeInBytes = checked((uint)(instanceSpan.Length * sizeof(float))),
            Usage = DxBufferUsage.Vertex | DxBufferUsage.CopyDestination,
            StrideInBytes = 32,
            Label = $"SciChartColoredSpriteInstances {submittedInstanceCount}"
        });
        instanceBuffer.Write(instanceSpan);
        return instanceBuffer;
    }

    private ProGpuDirectXBuffer? CreateCandleFillVertexBuffer(
        ReadOnlySpan<ProGpuDirectXSciChartOhlcCandleVertex> vertices,
        float width,
        ProGpuDirectXSciChartVertexTransform transform,
        out uint submittedVertexCount)
    {
        var vertexData = new List<float>(checked(vertices.Length * 36));
        foreach (var source in vertices)
        {
            if (!HasVisibleColor(source.FillColorArgb)
                || !TryGetFinancialBodyRect(source, width, transform, out var left, out var top, out var right, out var bottom))
            {
                continue;
            }

            AppendSolidColorVertex(vertexData, left, top, source.FillColorArgb);
            AppendSolidColorVertex(vertexData, right, top, source.FillColorArgb);
            AppendSolidColorVertex(vertexData, right, bottom, source.FillColorArgb);
            AppendSolidColorVertex(vertexData, left, top, source.FillColorArgb);
            AppendSolidColorVertex(vertexData, right, bottom, source.FillColorArgb);
            AppendSolidColorVertex(vertexData, left, bottom, source.FillColorArgb);
        }

        return CreateSolidColorVertexBuffer(vertexData, "SciChartCandleFillVertices", out submittedVertexCount);
    }

    private ProGpuDirectXBuffer? CreateCandleStrokeVertexBuffer(
        ReadOnlySpan<ProGpuDirectXSciChartOhlcCandleVertex> vertices,
        float width,
        ProGpuDirectXSciChartVertexTransform transform,
        out uint submittedVertexCount)
    {
        var vertexData = new List<float>(checked(vertices.Length * 60));
        foreach (var source in vertices)
        {
            if (!HasVisibleColor(source.StrokeColorArgb)
                || !HasFiniteFinancialVertex(source)
                || !TryGetFinancialBodyRect(source, width, transform, out var left, out var top, out var right, out var bottom))
            {
                continue;
            }

            AppendFinancialLine(vertexData, source.X, source.H, source.X, source.L, transform, source.StrokeColorArgb);
            AppendSolidColorVertex(vertexData, left, top, source.StrokeColorArgb);
            AppendSolidColorVertex(vertexData, right, top, source.StrokeColorArgb);
            AppendSolidColorVertex(vertexData, right, top, source.StrokeColorArgb);
            AppendSolidColorVertex(vertexData, right, bottom, source.StrokeColorArgb);
            AppendSolidColorVertex(vertexData, right, bottom, source.StrokeColorArgb);
            AppendSolidColorVertex(vertexData, left, bottom, source.StrokeColorArgb);
            AppendSolidColorVertex(vertexData, left, bottom, source.StrokeColorArgb);
            AppendSolidColorVertex(vertexData, left, top, source.StrokeColorArgb);
        }

        return CreateSolidColorVertexBuffer(vertexData, "SciChartCandleStrokeVertices", out submittedVertexCount);
    }

    private ProGpuDirectXBuffer? CreateOhlcStrokeVertexBuffer(
        ReadOnlySpan<ProGpuDirectXSciChartOhlcCandleVertex> vertices,
        float width,
        ProGpuDirectXSciChartVertexTransform transform,
        out uint submittedVertexCount)
    {
        var halfWidth = width / 2f;
        var vertexData = new List<float>(checked(vertices.Length * 36));
        foreach (var source in vertices)
        {
            if (!HasVisibleColor(source.StrokeColorArgb)
                || !HasFiniteFinancialVertex(source))
            {
                continue;
            }

            AppendFinancialLine(vertexData, source.X, source.H, source.X, source.L, transform, source.StrokeColorArgb);
            AppendFinancialLine(vertexData, source.X - halfWidth, source.O, source.X, source.O, transform, source.StrokeColorArgb);
            AppendFinancialLine(vertexData, source.X, source.C, source.X + halfWidth, source.C, transform, source.StrokeColorArgb);
        }

        return CreateSolidColorVertexBuffer(vertexData, "SciChartOhlcStrokeVertices", out submittedVertexCount);
    }

    private ProGpuDirectXBuffer? CreateTexturedColorVertexBuffer(
        List<float> vertexData,
        string label,
        out uint submittedVertexCount)
    {
        submittedVertexCount = checked((uint)(vertexData.Count / 8));
        if (submittedVertexCount == 0)
        {
            return null;
        }

        var vertexSpan = CollectionsMarshal.AsSpan(vertexData);
        var vertexBuffer = _device.CreateBuffer(new DxBufferDescriptor
        {
            SizeInBytes = checked((uint)(vertexSpan.Length * sizeof(float))),
            Usage = DxBufferUsage.Vertex | DxBufferUsage.CopyDestination,
            StrideInBytes = 32,
            Label = $"{label} {submittedVertexCount}"
        });
        vertexBuffer.Write(vertexSpan);
        return vertexBuffer;
    }

    private ProGpuDirectXBuffer GetSpriteQuadVertexBuffer()
    {
        if (_spriteQuadVertexBuffer is not null)
        {
            return _spriteQuadVertexBuffer;
        }

        float[] corners =
        [
            0f, 0f,
            1f, 0f,
            1f, 1f,
            0f, 0f,
            1f, 1f,
            0f, 1f
        ];
        _spriteQuadVertexBuffer = _device.CreateBuffer(new DxBufferDescriptor
        {
            SizeInBytes = checked((uint)(corners.Length * sizeof(float))),
            Usage = DxBufferUsage.Vertex | DxBufferUsage.CopyDestination,
            StrideInBytes = 8,
            Label = "SciChartSpriteQuadCorners"
        });
        _spriteQuadVertexBuffer.Write(corners);
        return _spriteQuadVertexBuffer;
    }

    private ProGpuDirectXBuffer? CreateSolidColorVertexBuffer(
        List<float> vertexData,
        string label,
        out uint submittedVertexCount)
    {
        submittedVertexCount = checked((uint)(vertexData.Count / 6));
        if (submittedVertexCount == 0)
        {
            return null;
        }

        var vertexSpan = CollectionsMarshal.AsSpan(vertexData);
        var vertexBuffer = _device.CreateBuffer(new DxBufferDescriptor
        {
            SizeInBytes = checked((uint)(vertexSpan.Length * sizeof(float))),
            Usage = DxBufferUsage.Vertex | DxBufferUsage.CopyDestination,
            StrideInBytes = 24,
            Label = $"{label} {submittedVertexCount}"
        });
        vertexBuffer.Write(vertexSpan);
        return vertexBuffer;
    }

    private ProGpuDirectXBuffer? CreateLineBatchVertexBuffer(
        ReadOnlySpan<ProGpuDirectXSciChartColorVertex> vertices,
        ProGpuDirectXSciChartVertexTransform transform,
        ProGpuDirectXSciChartPen2D pen,
        bool isStrips,
        bool isDigital,
        bool? isDrawNanAsGaps,
        out uint submittedVertexCount,
        out bool usesTriangleTopology)
    {
        usesTriangleTopology = pen.StrokeThickness > 1f;
        var floatsPerVertex = usesTriangleTopology && pen.IsAntiAliased ? 108 : usesTriangleTopology ? 36 : 12;
        var vertexData = new List<float>(checked(vertices.Length * floatsPerVertex));
        if (isStrips)
        {
            AppendStripLineSegments(
                vertexData,
                vertices,
                transform,
                pen,
                isDigital,
                isDrawNanAsGaps,
                usesTriangleTopology);
        }
        else
        {
            AppendPairLineSegments(
                vertexData,
                vertices,
                transform,
                pen,
                isDigital,
                usesTriangleTopology);
        }

        return CreateSolidColorVertexBuffer(vertexData, "SciChartLineBatchVertices", out submittedVertexCount);
    }

    private void AppendStripLineSegments(
        List<float> vertexData,
        ReadOnlySpan<ProGpuDirectXSciChartColorVertex> vertices,
        ProGpuDirectXSciChartVertexTransform transform,
        ProGpuDirectXSciChartPen2D pen,
        bool isDigital,
        bool? isDrawNanAsGaps,
        bool usesTriangleTopology)
    {
        var resetOnInvalid = isDrawNanAsGaps != false;
        var hasPrevious = false;
        float previousX = 0f;
        float previousY = 0f;
        float previousOffset = 0f;
        uint previousColor = 0;

        foreach (var vertex in vertices)
        {
            if (!TryGetLinePoint(vertex, transform, pen, out var x, out var y, out var offset, out var color))
            {
                if (resetOnInvalid)
                {
                    hasPrevious = false;
                }

                continue;
            }

            if (hasPrevious)
            {
                AppendLineSegment(
                    vertexData,
                    previousX,
                    previousY,
                    previousOffset,
                    previousColor,
                    x,
                    y,
                    offset,
                    color,
                    pen.StrokeThickness,
                    pen.IsAntiAliased,
                    isDigital,
                    usesTriangleTopology);
            }

            previousX = x;
            previousY = y;
            previousOffset = offset;
            previousColor = color;
            hasPrevious = true;
        }
    }

    private void AppendPairLineSegments(
        List<float> vertexData,
        ReadOnlySpan<ProGpuDirectXSciChartColorVertex> vertices,
        ProGpuDirectXSciChartVertexTransform transform,
        ProGpuDirectXSciChartPen2D pen,
        bool isDigital,
        bool usesTriangleTopology)
    {
        for (var i = 0; i < vertices.Length - 1; i += 2)
        {
            if (!TryGetLinePoint(vertices[i], transform, pen, out var x0, out var y0, out var offset0, out var color0) ||
                !TryGetLinePoint(vertices[i + 1], transform, pen, out var x1, out var y1, out var offset1, out var color1))
            {
                continue;
            }

            AppendLineSegment(
                vertexData,
                x0,
                y0,
                offset0,
                color0,
                x1,
                y1,
                offset1,
                color1,
                pen.StrokeThickness,
                pen.IsAntiAliased,
                isDigital,
                usesTriangleTopology);
        }
    }

    private void AppendLineSegment(
        List<float> vertexData,
        float x0,
        float y0,
        float offset0,
        uint color0,
        float x1,
        float y1,
        float offset1,
        uint color1,
        float strokeThickness,
        bool isAntiAliased,
        bool isDigital,
        bool usesTriangleTopology)
    {
        if (isDigital &&
            x0 != x1 &&
            y0 != y1)
        {
            AppendStraightLineSegment(
                vertexData,
                x0,
                y0,
                offset0,
                color0,
                x1,
                y0,
                offset0,
                color0,
                strokeThickness,
                isAntiAliased,
                usesTriangleTopology);
            AppendStraightLineSegment(
                vertexData,
                x1,
                y0,
                offset0,
                color0,
                x1,
                y1,
                offset1,
                color1,
                strokeThickness,
                isAntiAliased,
                usesTriangleTopology);
            return;
        }

        AppendStraightLineSegment(
            vertexData,
            x0,
            y0,
            offset0,
            color0,
            x1,
            y1,
            offset1,
            color1,
            strokeThickness,
            isAntiAliased,
            usesTriangleTopology);
    }

    private void AppendStraightLineSegment(
        List<float> vertexData,
        float x0,
        float y0,
        float offset0,
        uint color0,
        float x1,
        float y1,
        float offset1,
        uint color1,
        float strokeThickness,
        bool isAntiAliased,
        bool usesTriangleTopology)
    {
        if (!TryOffsetLineEndpoints(x0, y0, offset0, x1, y1, offset1, out x0, out y0, out x1, out y1))
        {
            return;
        }

        if (usesTriangleTopology)
        {
            AppendThickLineQuad(vertexData, x0, y0, color0, x1, y1, color1, strokeThickness, isAntiAliased);
            return;
        }

        AppendSolidColorVertex(vertexData, x0, y0, color0);
        AppendSolidColorVertex(vertexData, x1, y1, color1);
    }

    private static bool TryOffsetLineEndpoints(
        float x0,
        float y0,
        float offset0,
        float x1,
        float y1,
        float offset1,
        out float offsetX0,
        out float offsetY0,
        out float offsetX1,
        out float offsetY1)
    {
        offsetX0 = x0;
        offsetY0 = y0;
        offsetX1 = x1;
        offsetY1 = y1;
        if (offset0 == 0f && offset1 == 0f)
        {
            return true;
        }

        var dx = x1 - x0;
        var dy = y1 - y0;
        var length = MathF.Sqrt((dx * dx) + (dy * dy));
        if (length <= float.Epsilon)
        {
            return false;
        }

        var unitNx = -dy / length;
        var unitNy = dx / length;
        offsetX0 += unitNx * offset0;
        offsetY0 += unitNy * offset0;
        offsetX1 += unitNx * offset1;
        offsetY1 += unitNy * offset1;
        return true;
    }

    private void AppendThickLineQuad(
        List<float> vertexData,
        float x0,
        float y0,
        uint color0,
        float x1,
        float y1,
        uint color1,
        float strokeThickness,
        bool isAntiAliased)
    {
        var dx = x1 - x0;
        var dy = y1 - y0;
        var length = MathF.Sqrt(dx * dx + dy * dy);
        if (length <= float.Epsilon)
        {
            return;
        }

        var halfThickness = strokeThickness / 2f;
        var unitNx = -dy / length;
        var unitNy = dx / length;
        if (!isAntiAliased)
        {
            AppendLineQuad(
                vertexData,
                x0 + (unitNx * halfThickness),
                y0 + (unitNy * halfThickness),
                color0,
                x1 + (unitNx * halfThickness),
                y1 + (unitNy * halfThickness),
                color1,
                x1 - (unitNx * halfThickness),
                y1 - (unitNy * halfThickness),
                color1,
                x0 - (unitNx * halfThickness),
                y0 - (unitNy * halfThickness),
                color0);
            return;
        }

        var feather = MathF.Min(LineAntialiasFeatherPixels, halfThickness);
        var coreHalfThickness = MathF.Max(halfThickness - feather, 0f);
        var transparentColor0 = WithAlpha(color0, 0);
        var transparentColor1 = WithAlpha(color1, 0);

        var coreNx = unitNx * coreHalfThickness;
        var coreNy = unitNy * coreHalfThickness;
        var outerNx = unitNx * halfThickness;
        var outerNy = unitNy * halfThickness;

        if (coreHalfThickness > 0.0001f)
        {
            AppendLineQuad(
                vertexData,
                x0 + coreNx,
                y0 + coreNy,
                color0,
                x1 + coreNx,
                y1 + coreNy,
                color1,
                x1 - coreNx,
                y1 - coreNy,
                color1,
                x0 - coreNx,
                y0 - coreNy,
                color0);
        }

        AppendLineQuad(
            vertexData,
            x0 + outerNx,
            y0 + outerNy,
            transparentColor0,
            x1 + outerNx,
            y1 + outerNy,
            transparentColor1,
            x1 + coreNx,
            y1 + coreNy,
            color1,
            x0 + coreNx,
            y0 + coreNy,
            color0);
        AppendLineQuad(
            vertexData,
            x0 - coreNx,
            y0 - coreNy,
            color0,
            x1 - coreNx,
            y1 - coreNy,
            color1,
            x1 - outerNx,
            y1 - outerNy,
            transparentColor1,
            x0 - outerNx,
            y0 - outerNy,
            transparentColor0);
    }

    private void AppendLineQuad(
        List<float> vertexData,
        float x0a,
        float y0a,
        uint color0a,
        float x1a,
        float y1a,
        uint color1a,
        float x1b,
        float y1b,
        uint color1b,
        float x0b,
        float y0b,
        uint color0b)
    {
        AppendSolidColorVertex(vertexData, x0a, y0a, color0a);
        AppendSolidColorVertex(vertexData, x1a, y1a, color1a);
        AppendSolidColorVertex(vertexData, x1b, y1b, color1b);
        AppendSolidColorVertex(vertexData, x0a, y0a, color0a);
        AppendSolidColorVertex(vertexData, x1b, y1b, color1b);
        AppendSolidColorVertex(vertexData, x0b, y0b, color0b);
    }

    private void AppendFinancialLine(
        List<float> vertexData,
        float x0,
        float y0,
        float x1,
        float y1,
        ProGpuDirectXSciChartVertexTransform transform,
        uint colorArgb)
    {
        AppendSolidColorVertex(vertexData, transform.SwapAxis ? y0 : x0, transform.SwapAxis ? x0 : y0, colorArgb);
        AppendSolidColorVertex(vertexData, transform.SwapAxis ? y1 : x1, transform.SwapAxis ? x1 : y1, colorArgb);
    }

    private void AppendTransformedSolidColorVertex(
        List<float> vertexData,
        float x,
        float y,
        ProGpuDirectXSciChartVertexTransform transform,
        uint colorArgb)
    {
        AppendSolidColorVertex(vertexData, transform.SwapAxis ? y : x, transform.SwapAxis ? x : y, colorArgb);
    }

    private ProGpuDirectXBuffer CreateShapedHeatmapConstants(
        double colorMapMin,
        double colorMapMax,
        uint heightTextureWidth,
        uint heightTextureHeight)
    {
        var min = (float)colorMapMin;
        var invRange = 1f / (float)(colorMapMax - colorMapMin);
        ReadOnlySpan<float> constants =
        [
            min,
            invRange,
            heightTextureWidth,
            heightTextureHeight
        ];

        var buffer = _device.CreateBuffer(new DxBufferDescriptor
        {
            SizeInBytes = checked((uint)(constants.Length * sizeof(float))),
            Usage = DxBufferUsage.Constant | DxBufferUsage.CopyDestination,
            StrideInBytes = 16,
            Label = "SciChart Shaped Heatmap Constants"
        });
        buffer.Write(constants);
        return buffer;
    }

    private ProGpuDirectXBuffer CreateHeightTextureContourConstants(
        float zMin,
        float zMax,
        float zStep,
        float strokeThickness,
        float opacity,
        uint heightTextureWidth,
        uint heightTextureHeight,
        DxColor color)
    {
        ReadOnlySpan<float> constants =
        [
            zMin,
            zMax,
            zStep,
            strokeThickness,
            opacity,
            heightTextureWidth,
            heightTextureHeight,
            0f,
            color.R,
            color.G,
            color.B,
            color.A
        ];

        var buffer = _device.CreateBuffer(new DxBufferDescriptor
        {
            SizeInBytes = checked((uint)(constants.Length * sizeof(float))),
            Usage = DxBufferUsage.Constant | DxBufferUsage.CopyDestination,
            StrideInBytes = 16,
            Label = "SciChart Height Contour Constants"
        });
        buffer.Write(constants);
        return buffer;
    }

    private float PixelXToNdc(int x)
    {
        return (x / (float)RenderTarget.Width * 2f) - 1f;
    }

    private float PixelYToNdc(int y)
    {
        return 1f - (y / (float)RenderTarget.Height * 2f);
    }

    private float PixelXToNdc(float x)
    {
        return (x / RenderTarget.Width * 2f) - 1f;
    }

    private float PixelYToNdc(float y)
    {
        return 1f - (y / RenderTarget.Height * 2f);
    }

    private ProGpuDirectXGraphicsPipeline GetTexturePipeline(
        DxResourceFormat renderTargetFormat,
        ProGpuDirectXSciChartTextureFiltering filtering)
    {
        var key = (renderTargetFormat, filtering);
        if (_texturePipelines.TryGetValue(key, out var pipeline))
        {
            return pipeline;
        }

        _textureVertexShader ??= _device.CreateShader(new DxShaderDescriptor
        {
            Stage = DxShaderStage.Vertex,
            SourceKind = DxShaderSourceKind.Wgsl,
            Source = TextureVertexShader,
            EntryPoint = "vs_main",
            Label = "SciChart Texture Vertex"
        });
        _texturePixelShader ??= _device.CreateShader(new DxShaderDescriptor
        {
            Stage = DxShaderStage.Pixel,
            SourceKind = DxShaderSourceKind.Wgsl,
            Source = CreateTexturePixelShader(),
            EntryPoint = "fs_main",
            Label = "SciChart Texture Pixel"
        });
        _textureInputLayout ??= _device.CreateInputLayout(new DxInputLayoutDescriptor
        {
            Label = "SciChart Texture Quad Layout",
            Elements =
            [
                new DxInputElementDescriptor
                {
                    SemanticName = "POSITION",
                    Format = DxResourceFormat.R32G32Float,
                    AlignedByteOffset = 0,
                    ShaderLocation = 0
                },
                new DxInputElementDescriptor
                {
                    SemanticName = "TEXCOORD",
                    Format = DxResourceFormat.R32G32Float,
                    AlignedByteOffset = 8,
                    ShaderLocation = 1
                }
            ]
        });

        pipeline = _device.CreateGraphicsPipeline(new DxGraphicsPipelineDescriptor
        {
            VertexShader = _textureVertexShader,
            PixelShader = _texturePixelShader,
            InputLayout = _textureInputLayout,
            RenderTargetFormat = renderTargetFormat,
            Topology = DxPrimitiveTopology.TriangleList,
            BlendState = new DxBlendStateDescriptor { EnableBlend = false },
            RasterizerState = new DxRasterizerStateDescriptor { CullMode = DxCullMode.None, ScissorEnable = true },
            Label = $"SciChart Texture Pipeline {renderTargetFormat} {filtering}"
        });
        _texturePipelines[key] = pipeline;
        return pipeline;
    }

    private ProGpuDirectXGraphicsPipeline GetColumnFillPipeline(DxResourceFormat renderTargetFormat)
    {
        if (_columnFillPipelines.TryGetValue(renderTargetFormat, out var pipeline))
        {
            return pipeline;
        }

        EnsureSolidColorResources();
        var vertexShader = _lineVertexShader ?? throw new InvalidOperationException("SciChart column vertex shader was not initialized.");
        var pixelShader = _linePixelShader ?? throw new InvalidOperationException("SciChart column pixel shader was not initialized.");
        var inputLayout = _lineInputLayout ?? throw new InvalidOperationException("SciChart column input layout was not initialized.");
        pipeline = _device.CreateGraphicsPipeline(new DxGraphicsPipelineDescriptor
        {
            VertexShader = vertexShader,
            PixelShader = pixelShader,
            InputLayout = inputLayout,
            RenderTargetFormat = renderTargetFormat,
            Topology = DxPrimitiveTopology.TriangleList,
            BlendState = new DxBlendStateDescriptor { EnableBlend = true },
            RasterizerState = new DxRasterizerStateDescriptor { CullMode = DxCullMode.None, ScissorEnable = true },
            Label = $"SciChart Column Fill Pipeline {renderTargetFormat}"
        });
        _columnFillPipelines[renderTargetFormat] = pipeline;
        return pipeline;
    }

    private ProGpuDirectXGraphicsPipeline GetLinePipeline(DxResourceFormat renderTargetFormat)
    {
        if (_linePipelines.TryGetValue(renderTargetFormat, out var pipeline))
        {
            return pipeline;
        }

        EnsureSolidColorResources();
        var vertexShader = _lineVertexShader ?? throw new InvalidOperationException("SciChart line vertex shader was not initialized.");
        var pixelShader = _linePixelShader ?? throw new InvalidOperationException("SciChart line pixel shader was not initialized.");
        var inputLayout = _lineInputLayout ?? throw new InvalidOperationException("SciChart line input layout was not initialized.");
        pipeline = _device.CreateGraphicsPipeline(new DxGraphicsPipelineDescriptor
        {
            VertexShader = vertexShader,
            PixelShader = pixelShader,
            InputLayout = inputLayout,
            RenderTargetFormat = renderTargetFormat,
            Topology = DxPrimitiveTopology.LineList,
            BlendState = new DxBlendStateDescriptor { EnableBlend = true },
            RasterizerState = new DxRasterizerStateDescriptor { CullMode = DxCullMode.None, ScissorEnable = true },
            Label = $"SciChart Line Batch Pipeline {renderTargetFormat}"
        });
        _linePipelines[renderTargetFormat] = pipeline;
        return pipeline;
    }

    private void EnsureSolidColorResources()
    {
        _lineVertexShader ??= _device.CreateShader(new DxShaderDescriptor
        {
            Stage = DxShaderStage.Vertex,
            SourceKind = DxShaderSourceKind.Wgsl,
            Source = LineVertexShader,
            EntryPoint = "vs_main",
            Label = "SciChart Line Batch Vertex"
        });
        _linePixelShader ??= _device.CreateShader(new DxShaderDescriptor
        {
            Stage = DxShaderStage.Pixel,
            SourceKind = DxShaderSourceKind.Wgsl,
            Source = LinePixelShader,
            EntryPoint = "fs_main",
            Label = "SciChart Line Batch Pixel"
        });
        _lineInputLayout ??= _device.CreateInputLayout(new DxInputLayoutDescriptor
        {
            Label = "SciChart Line Batch Layout",
            Elements =
            [
                new DxInputElementDescriptor
                {
                    SemanticName = "POSITION",
                    Format = DxResourceFormat.R32G32Float,
                    AlignedByteOffset = 0,
                    ShaderLocation = 0
                },
                new DxInputElementDescriptor
                {
                    SemanticName = "COLOR",
                    Format = DxResourceFormat.R32G32B32A32Float,
                    AlignedByteOffset = 8,
                    ShaderLocation = 1
                }
            ]
        });
    }

    private ProGpuDirectXGraphicsPipeline GetBatchedTexturePipeline(
        DxResourceFormat renderTargetFormat,
        ProGpuDirectXSciChartTextureFiltering filtering)
    {
        var key = (renderTargetFormat, filtering);
        if (_textureVertexPipelines.TryGetValue(key, out var pipeline))
        {
            return pipeline;
        }

        _batchedTextureVertexShader ??= _device.CreateShader(new DxShaderDescriptor
        {
            Stage = DxShaderStage.Vertex,
            SourceKind = DxShaderSourceKind.Wgsl,
            Source = BatchedTextureVertexShader,
            EntryPoint = "vs_main",
            Label = "SciChart Batched Texture Vertex"
        });
        _batchedTexturePixelShader ??= _device.CreateShader(new DxShaderDescriptor
        {
            Stage = DxShaderStage.Pixel,
            SourceKind = DxShaderSourceKind.Wgsl,
            Source = CreateBatchedTexturePixelShader(),
            EntryPoint = "fs_main",
            Label = "SciChart Batched Texture Pixel"
        });
        _batchedTextureInputLayout ??= _device.CreateInputLayout(new DxInputLayoutDescriptor
        {
            Label = "SciChart Batched Texture Layout",
            Elements =
            [
                new DxInputElementDescriptor
                {
                    SemanticName = "POSITION",
                    Format = DxResourceFormat.R32G32Float,
                    AlignedByteOffset = 0,
                    ShaderLocation = 0
                },
                new DxInputElementDescriptor
                {
                    SemanticName = "TEXCOORD",
                    Format = DxResourceFormat.R32G32Float,
                    AlignedByteOffset = 8,
                    ShaderLocation = 1
                },
                new DxInputElementDescriptor
                {
                    SemanticName = "COLOR",
                    Format = DxResourceFormat.R32G32B32A32Float,
                    AlignedByteOffset = 16,
                    ShaderLocation = 2
                }
            ]
        });

        pipeline = _device.CreateGraphicsPipeline(new DxGraphicsPipelineDescriptor
        {
            VertexShader = _batchedTextureVertexShader,
            PixelShader = _batchedTexturePixelShader,
            InputLayout = _batchedTextureInputLayout,
            RenderTargetFormat = renderTargetFormat,
            Topology = DxPrimitiveTopology.TriangleList,
            BlendState = new DxBlendStateDescriptor { EnableBlend = false },
            RasterizerState = new DxRasterizerStateDescriptor { CullMode = DxCullMode.None, ScissorEnable = true },
            Label = $"SciChart Batched Texture Pipeline {renderTargetFormat} {filtering}"
        });
        _textureVertexPipelines[key] = pipeline;
        return pipeline;
    }

    private ProGpuDirectXGraphicsPipeline GetSpritePipeline(
        DxResourceFormat renderTargetFormat,
        ProGpuDirectXSciChartTextureFiltering filtering)
    {
        var key = (renderTargetFormat, filtering);
        if (_spritePipelines.TryGetValue(key, out var pipeline))
        {
            return pipeline;
        }

        EnsureBatchedTextureVertexResources();
        _batchedTexturePixelShader ??= _device.CreateShader(new DxShaderDescriptor
        {
            Stage = DxShaderStage.Pixel,
            SourceKind = DxShaderSourceKind.Wgsl,
            Source = CreateBatchedTexturePixelShader(),
            EntryPoint = "fs_main",
            Label = "SciChart Batched Texture Pixel"
        });
        var vertexShader = _batchedTextureVertexShader ?? throw new InvalidOperationException("SciChart sprite vertex shader was not initialized.");
        var pixelShader = _batchedTexturePixelShader ?? throw new InvalidOperationException("SciChart sprite pixel shader was not initialized.");
        var inputLayout = _batchedTextureInputLayout ?? throw new InvalidOperationException("SciChart sprite input layout was not initialized.");

        pipeline = _device.CreateGraphicsPipeline(new DxGraphicsPipelineDescriptor
        {
            VertexShader = vertexShader,
            PixelShader = pixelShader,
            InputLayout = inputLayout,
            RenderTargetFormat = renderTargetFormat,
            Topology = DxPrimitiveTopology.TriangleList,
            BlendState = new DxBlendStateDescriptor { EnableBlend = true },
            RasterizerState = new DxRasterizerStateDescriptor { CullMode = DxCullMode.None, ScissorEnable = true },
            Label = $"SciChart Sprite Pipeline {renderTargetFormat} {filtering}"
        });
        _spritePipelines[key] = pipeline;
        return pipeline;
    }

    private ProGpuDirectXGraphicsPipeline GetInstancedSpritePipeline(
        DxResourceFormat renderTargetFormat,
        ProGpuDirectXSciChartTextureFiltering filtering)
    {
        var key = (renderTargetFormat, filtering);
        if (_instancedSpritePipelines.TryGetValue(key, out var pipeline))
        {
            return pipeline;
        }

        EnsureInstancedSpriteResources();
        _batchedTexturePixelShader ??= _device.CreateShader(new DxShaderDescriptor
        {
            Stage = DxShaderStage.Pixel,
            SourceKind = DxShaderSourceKind.Wgsl,
            Source = CreateBatchedTexturePixelShader(),
            EntryPoint = "fs_main",
            Label = "SciChart Batched Texture Pixel"
        });
        var vertexShader = _instancedSpriteVertexShader ?? throw new InvalidOperationException("SciChart instanced sprite vertex shader was not initialized.");
        var pixelShader = _batchedTexturePixelShader ?? throw new InvalidOperationException("SciChart instanced sprite pixel shader was not initialized.");
        var inputLayout = _instancedSpriteInputLayout ?? throw new InvalidOperationException("SciChart instanced sprite input layout was not initialized.");

        pipeline = _device.CreateGraphicsPipeline(new DxGraphicsPipelineDescriptor
        {
            VertexShader = vertexShader,
            PixelShader = pixelShader,
            InputLayout = inputLayout,
            RenderTargetFormat = renderTargetFormat,
            Topology = DxPrimitiveTopology.TriangleList,
            BlendState = new DxBlendStateDescriptor { EnableBlend = true },
            RasterizerState = new DxRasterizerStateDescriptor { CullMode = DxCullMode.None, ScissorEnable = true },
            Label = $"SciChart Instanced Sprite Pipeline {renderTargetFormat} {filtering}"
        });
        _instancedSpritePipelines[key] = pipeline;
        return pipeline;
    }

    private ProGpuDirectXGraphicsPipeline GetShapedHeatmapPipeline(
        DxResourceFormat renderTargetFormat,
        ProGpuDirectXSciChartTextureFiltering filtering)
    {
        var key = (renderTargetFormat, filtering);
        if (_shapedHeatmapPipelines.TryGetValue(key, out var pipeline))
        {
            return pipeline;
        }

        EnsureBatchedTextureVertexResources();
        _shapedHeatmapPixelShader ??= _device.CreateShader(new DxShaderDescriptor
        {
            Stage = DxShaderStage.Pixel,
            SourceKind = DxShaderSourceKind.Wgsl,
            Source = CreateShapedHeatmapPixelShader(),
            EntryPoint = "fs_main",
            Label = "SciChart Shaped Heatmap Pixel"
        });
        var vertexShader = _batchedTextureVertexShader ?? throw new InvalidOperationException("SciChart heatmap vertex shader was not initialized.");
        var pixelShader = _shapedHeatmapPixelShader ?? throw new InvalidOperationException("SciChart heatmap pixel shader was not initialized.");
        var inputLayout = _batchedTextureInputLayout ?? throw new InvalidOperationException("SciChart heatmap input layout was not initialized.");

        pipeline = _device.CreateGraphicsPipeline(new DxGraphicsPipelineDescriptor
        {
            VertexShader = vertexShader,
            PixelShader = pixelShader,
            InputLayout = inputLayout,
            RenderTargetFormat = renderTargetFormat,
            Topology = DxPrimitiveTopology.TriangleList,
            BlendState = new DxBlendStateDescriptor { EnableBlend = false },
            RasterizerState = new DxRasterizerStateDescriptor { CullMode = DxCullMode.None, ScissorEnable = true },
            Label = $"SciChart Shaped Heatmap Pipeline {renderTargetFormat} {filtering}"
        });
        _shapedHeatmapPipelines[key] = pipeline;
        return pipeline;
    }

    private ProGpuDirectXGraphicsPipeline GetHeightTextureContourPipeline(DxResourceFormat renderTargetFormat)
    {
        if (_heightContourPipelines.TryGetValue(renderTargetFormat, out var pipeline))
        {
            return pipeline;
        }

        _textureVertexShader ??= _device.CreateShader(new DxShaderDescriptor
        {
            Stage = DxShaderStage.Vertex,
            SourceKind = DxShaderSourceKind.Wgsl,
            Source = TextureVertexShader,
            EntryPoint = "vs_main",
            Label = "SciChart Texture Vertex"
        });
        _heightContourPixelShader ??= _device.CreateShader(new DxShaderDescriptor
        {
            Stage = DxShaderStage.Pixel,
            SourceKind = DxShaderSourceKind.Wgsl,
            Source = CreateHeightTextureContourPixelShader(),
            EntryPoint = "fs_main",
            Label = "SciChart Height Contour Pixel"
        });
        _textureInputLayout ??= _device.CreateInputLayout(new DxInputLayoutDescriptor
        {
            Label = "SciChart Texture Quad Layout",
            Elements =
            [
                new DxInputElementDescriptor
                {
                    SemanticName = "POSITION",
                    Format = DxResourceFormat.R32G32Float,
                    AlignedByteOffset = 0,
                    ShaderLocation = 0
                },
                new DxInputElementDescriptor
                {
                    SemanticName = "TEXCOORD",
                    Format = DxResourceFormat.R32G32Float,
                    AlignedByteOffset = 8,
                    ShaderLocation = 1
                }
            ]
        });
        var vertexShader = _textureVertexShader ?? throw new InvalidOperationException("SciChart contour vertex shader was not initialized.");
        var pixelShader = _heightContourPixelShader ?? throw new InvalidOperationException("SciChart contour pixel shader was not initialized.");
        var inputLayout = _textureInputLayout ?? throw new InvalidOperationException("SciChart contour input layout was not initialized.");

        pipeline = _device.CreateGraphicsPipeline(new DxGraphicsPipelineDescriptor
        {
            VertexShader = vertexShader,
            PixelShader = pixelShader,
            InputLayout = inputLayout,
            RenderTargetFormat = renderTargetFormat,
            Topology = DxPrimitiveTopology.TriangleList,
            BlendState = new DxBlendStateDescriptor { EnableBlend = true },
            RasterizerState = new DxRasterizerStateDescriptor { CullMode = DxCullMode.None, ScissorEnable = true },
            Label = $"SciChart Height Contour Pipeline {renderTargetFormat}"
        });
        _heightContourPipelines[renderTargetFormat] = pipeline;
        return pipeline;
    }

    private void EnsureBatchedTextureVertexResources()
    {
        _batchedTextureVertexShader ??= _device.CreateShader(new DxShaderDescriptor
        {
            Stage = DxShaderStage.Vertex,
            SourceKind = DxShaderSourceKind.Wgsl,
            Source = BatchedTextureVertexShader,
            EntryPoint = "vs_main",
            Label = "SciChart Batched Texture Vertex"
        });
        _batchedTextureInputLayout ??= _device.CreateInputLayout(new DxInputLayoutDescriptor
        {
            Label = "SciChart Batched Texture Layout",
            Elements =
            [
                new DxInputElementDescriptor
                {
                    SemanticName = "POSITION",
                    Format = DxResourceFormat.R32G32Float,
                    AlignedByteOffset = 0,
                    ShaderLocation = 0
                },
                new DxInputElementDescriptor
                {
                    SemanticName = "TEXCOORD",
                    Format = DxResourceFormat.R32G32Float,
                    AlignedByteOffset = 8,
                    ShaderLocation = 1
                },
                new DxInputElementDescriptor
                {
                    SemanticName = "COLOR",
                    Format = DxResourceFormat.R32G32B32A32Float,
                    AlignedByteOffset = 16,
                    ShaderLocation = 2
                }
            ]
        });
    }

    private void EnsureInstancedSpriteResources()
    {
        _instancedSpriteVertexShader ??= _device.CreateShader(new DxShaderDescriptor
        {
            Stage = DxShaderStage.Vertex,
            SourceKind = DxShaderSourceKind.Wgsl,
            Source = InstancedSpriteVertexShader,
            EntryPoint = "vs_main",
            Label = "SciChart Instanced Sprite Vertex"
        });
        _instancedSpriteInputLayout ??= _device.CreateInputLayout(new DxInputLayoutDescriptor
        {
            Label = "SciChart Instanced Sprite Layout",
            Elements =
            [
                new DxInputElementDescriptor
                {
                    SemanticName = "POSITION",
                    Format = DxResourceFormat.R32G32Float,
                    InputSlot = 0,
                    AlignedByteOffset = 0,
                    ShaderLocation = 0
                },
                new DxInputElementDescriptor
                {
                    SemanticName = "RECT",
                    Format = DxResourceFormat.R32G32B32A32Float,
                    InputSlot = 1,
                    AlignedByteOffset = 0,
                    InputSlotClass = DxInputClassification.PerInstanceData,
                    InstanceDataStepRate = 1,
                    ShaderLocation = 1
                },
                new DxInputElementDescriptor
                {
                    SemanticName = "COLOR",
                    Format = DxResourceFormat.R32G32B32A32Float,
                    InputSlot = 1,
                    AlignedByteOffset = 16,
                    InputSlotClass = DxInputClassification.PerInstanceData,
                    InstanceDataStepRate = 1,
                    ShaderLocation = 2
                }
            ]
        });
    }

    private ProGpuDirectXSamplerState GetSampler(ProGpuDirectXSciChartTextureFiltering filtering)
    {
        if (_samplers.TryGetValue(filtering, out var sampler))
        {
            return sampler;
        }

        sampler = _device.CreateSamplerState(new DxSamplerDescriptor
        {
            Filter = filtering == ProGpuDirectXSciChartTextureFiltering.Point
                ? DxFilter.MinMagMipPoint
                : DxFilter.MinMagMipLinear,
            AddressU = DxTextureAddressMode.Clamp,
            AddressV = DxTextureAddressMode.Clamp,
            Label = $"SciChart Texture Sampler {filtering}"
        });
        _samplers[filtering] = sampler;
        return sampler;
    }

    private static DxResourceFormat ToDxFormat(ProGpuDirectXSciChartTextureFormat textureFormat)
    {
        return textureFormat switch
        {
            ProGpuDirectXSciChartTextureFormat.Bgra8 => DxResourceFormat.B8G8R8A8Unorm,
            ProGpuDirectXSciChartTextureFormat.Float32 => DxResourceFormat.R32Float,
            ProGpuDirectXSciChartTextureFormat.UInt32 => DxResourceFormat.R32UInt,
            ProGpuDirectXSciChartTextureFormat.Int32 => DxResourceFormat.R32SInt,
            _ => throw new ArgumentOutOfRangeException(nameof(textureFormat), textureFormat, null)
        };
    }

    private DxRect FitUniform(ProGpuDirectXSciChartTexture2D texture, DxRect destination)
    {
        var sourceAspect = texture.Width / (float)texture.Height;
        var destinationAspect = destination.Width / (float)destination.Height;
        if (Math.Abs(sourceAspect - destinationAspect) < 0.0001f)
        {
            return destination;
        }

        if (destinationAspect > sourceAspect)
        {
            var width = checked((int)MathF.Round(destination.Height * sourceAspect));
            var x = destination.X + ((destination.Width - width) / 2);
            return destination with { X = x, Width = width };
        }
        else
        {
            var height = checked((int)MathF.Round(destination.Width / sourceAspect));
            var y = destination.Y + ((destination.Height - height) / 2);
            return destination with { Y = y, Height = height };
        }
    }

    private DxRect ClampClipRect(DxRect rect)
    {
        var left = Math.Clamp(rect.X, 0, checked((int)RenderTarget.Width));
        var top = Math.Clamp(rect.Y, 0, checked((int)RenderTarget.Height));
        var right = Math.Clamp(rect.X + rect.Width, left, checked((int)RenderTarget.Width));
        var bottom = Math.Clamp(rect.Y + rect.Height, top, checked((int)RenderTarget.Height));
        return new DxRect(left, top, right - left, bottom - top);
    }

    private static DxRect CreateVerticalPixelRect(int xLeft, int xRight, int yStartBottom, int yEndTop)
    {
        var left = Math.Min(xLeft, xRight);
        var right = Math.Max(xLeft, xRight);
        var top = Math.Min(yStartBottom, yEndTop);
        var bottom = Math.Max(yStartBottom, yEndTop);
        return new DxRect(left, top, right - left, bottom - top);
    }

    private static void ValidateDrawRect(DxRect rect)
    {
        if (rect.Width <= 0 || rect.Height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(rect), "SciChart texture draw rectangles must be non-empty.");
        }
    }

    private static void ValidateText(
        string text,
        float fontSize,
        ProGpuDirectXSciChartPoint position,
        float rotationRadians)
    {
        ArgumentNullException.ThrowIfNull(text);

        if (!float.IsFinite(fontSize) || fontSize <= 0f)
        {
            throw new ArgumentOutOfRangeException(nameof(fontSize), "SciChart text requires a finite positive font size.");
        }

        if (!HasFinitePoint(position))
        {
            throw new ArgumentOutOfRangeException(nameof(position), "SciChart text positions must be finite.");
        }

        if (!float.IsFinite(rotationRadians))
        {
            throw new ArgumentOutOfRangeException(nameof(rotationRadians), "SciChart text rotation must be finite.");
        }
    }

    private static void ValidateDrawableTexture(ProGpuDirectXSciChartTexture2D texture)
    {
        if (texture.TextureFormat is not (ProGpuDirectXSciChartTextureFormat.Bgra8 or ProGpuDirectXSciChartTextureFormat.Float32))
        {
            throw new NotSupportedException("SciChart texture drawing currently supports Bgra8 and Float32 sampled textures; integer textures are reserved for compute/height-map paths.");
        }
    }

    private static void ValidateDrawableSprite(ProGpuDirectXSciChartSprite2D sprite)
    {
        if (sprite.Texture.TextureFormat != ProGpuDirectXSciChartTextureFormat.Bgra8)
        {
            throw new NotSupportedException("SciChart sprite batches require Bgra8 sprite textures.");
        }
    }

    private static void ValidateShapedHeatmapTextures(
        ProGpuDirectXSciChartTexture2D heightsTexture,
        ProGpuDirectXSciChartTexture2D gradientTexture)
    {
        if (heightsTexture.TextureFormat != ProGpuDirectXSciChartTextureFormat.Float32)
        {
            throw new NotSupportedException("SciChart shaped heatmap requires a Float32 heights texture.");
        }

        if (gradientTexture.TextureFormat != ProGpuDirectXSciChartTextureFormat.Bgra8)
        {
            throw new NotSupportedException("SciChart shaped heatmap requires a Bgra8 gradient texture.");
        }
    }

    private static void ValidateColorMapRange(double colorMapMin, double colorMapMax)
    {
        if (!double.IsFinite(colorMapMin) || !double.IsFinite(colorMapMax) || colorMapMax <= colorMapMin)
        {
            throw new ArgumentOutOfRangeException(nameof(colorMapMax), "SciChart shaped heatmap color map range must be finite and increasing.");
        }
    }

    private static void ValidateHeightTextureContourInputs(
        ProGpuDirectXSciChartTexture2D heightsTexture,
        ProGpuDirectXSciChartTexture2D contourTexture,
        DxColor color,
        float zMin,
        float zMax,
        float zStep,
        float strokeThickness,
        float opacity)
    {
        if (heightsTexture.TextureFormat != ProGpuDirectXSciChartTextureFormat.Float32)
        {
            throw new NotSupportedException("SciChart height contours require a Float32 heights texture.");
        }

        if (contourTexture.TextureFormat != ProGpuDirectXSciChartTextureFormat.Bgra8)
        {
            throw new NotSupportedException("SciChart height contours require a Bgra8 contour texture.");
        }

        if (!float.IsFinite(zMin) || !float.IsFinite(zMax) || !float.IsFinite(zStep) || zMax <= zMin || zStep <= 0f)
        {
            throw new ArgumentOutOfRangeException(nameof(zStep), "SciChart height contours require finite zMin/zMax and a positive zStep.");
        }

        if (!float.IsFinite(strokeThickness) || strokeThickness <= 0f)
        {
            throw new ArgumentOutOfRangeException(nameof(strokeThickness), "SciChart height contour stroke thickness must be finite and positive.");
        }

        if (!float.IsFinite(opacity) || opacity < 0f)
        {
            throw new ArgumentOutOfRangeException(nameof(opacity), "SciChart height contour opacity must be finite and non-negative.");
        }

        if (!float.IsFinite(color.R) || !float.IsFinite(color.G) || !float.IsFinite(color.B) || !float.IsFinite(color.A))
        {
            throw new ArgumentOutOfRangeException(nameof(color), "SciChart height contour color must contain finite channels.");
        }
    }

    private static void ValidatePointRange(
        int pointLength,
        int count,
        int minCount,
        string shapeName)
    {
        if (count < minCount)
        {
            throw new ArgumentOutOfRangeException(nameof(count), $"SciChart {shapeName} require at least {minCount} point(s).");
        }

        if (count > pointLength)
        {
            throw new ArgumentOutOfRangeException(nameof(count), $"SciChart {shapeName} point count exceeds the supplied point span.");
        }
    }

    private static void ValidateOpacity(double opacity)
    {
        if (!double.IsFinite(opacity) || opacity < 0d || opacity > 1d)
        {
            throw new ArgumentOutOfRangeException(nameof(opacity), "SciChart primitive opacity must be finite and between 0 and 1.");
        }
    }

    private static void ValidateVerticalPixels(
        int xLeft,
        int xRight,
        int yStartBottom,
        int yEndTop,
        int colorCount,
        double opacity)
    {
        ValidateOpacity(opacity);
        if (xLeft == xRight)
        {
            throw new ArgumentOutOfRangeException(nameof(xRight), "SciChart vertical pixels require a non-empty x range.");
        }

        if (yStartBottom == yEndTop)
        {
            throw new ArgumentOutOfRangeException(nameof(yEndTop), "SciChart vertical pixels require a non-empty y range.");
        }

        if (colorCount <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(colorCount), "SciChart vertical pixels require at least one color.");
        }
    }

    private static void ValidateVerticalPixelCoordinates(
        int xLeft,
        int xRight,
        int coordinateCount,
        int colorCount,
        double opacity,
        bool isUniform)
    {
        ValidateOpacity(opacity);
        if (xLeft == xRight)
        {
            throw new ArgumentOutOfRangeException(nameof(xRight), "SciChart vertical pixels require a non-empty x range.");
        }

        if (coordinateCount < 2)
        {
            throw new ArgumentOutOfRangeException(nameof(coordinateCount), "SciChart vertical pixel coordinates require at least two y coordinates.");
        }

        if (colorCount <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(colorCount), "SciChart vertical pixels require at least one color.");
        }

        if (!isUniform && colorCount < coordinateCount - 1)
        {
            throw new ArgumentOutOfRangeException(nameof(colorCount), "SciChart non-uniform vertical pixels require one color per y interval.");
        }
    }

    private static void ValidateGradientRotationAngle(double gradientRotationAngle)
    {
        if (!double.IsFinite(gradientRotationAngle))
        {
            throw new ArgumentOutOfRangeException(nameof(gradientRotationAngle), "SciChart area-fill gradient rotation angle must be finite.");
        }
    }

    private static void ValidateEllipse(ProGpuDirectXSciChartPoint center, double width, double height)
    {
        if (!HasFinitePoint(center))
        {
            throw new ArgumentOutOfRangeException(nameof(center), "SciChart ellipse centers must be finite.");
        }

        if (!double.IsFinite(width) || width <= 0d)
        {
            throw new ArgumentOutOfRangeException(nameof(width), "SciChart ellipses require a finite positive width.");
        }

        if (!double.IsFinite(height) || height <= 0d)
        {
            throw new ArgumentOutOfRangeException(nameof(height), "SciChart ellipses require a finite positive height.");
        }
    }

    private static uint ApplyOpacity(uint colorArgb, double opacity)
    {
        if (opacity >= 1d)
        {
            return colorArgb;
        }

        var alpha = (int)((colorArgb >> 24) & 0xFF);
        var adjustedAlpha = (uint)Math.Clamp((int)Math.Round(alpha * opacity, MidpointRounding.AwayFromZero), 0, 255);
        return (colorArgb & 0x00FFFFFFu) | (adjustedAlpha << 24);
    }

    private static void CopyVerticalPixelColors(
        ReadOnlySpan<int> pixelColorsArgb,
        double opacity,
        bool reverse,
        Span<int> colors)
    {
        for (var i = 0; i < colors.Length; i++)
        {
            var sourceIndex = reverse ? colors.Length - 1 - i : i;
            colors[i] = unchecked((int)ApplyOpacity(unchecked((uint)pixelColorsArgb[sourceIndex]), opacity));
        }
    }

    private static bool ContainsVisibleColor(ReadOnlySpan<int> pixelColorsArgb)
    {
        foreach (var colorArgb in pixelColorsArgb)
        {
            if (HasVisibleColor(unchecked((uint)colorArgb)))
            {
                return true;
            }
        }

        return false;
    }

    private static uint WithAlpha(uint colorArgb, byte alpha)
    {
        return (colorArgb & 0x00FFFFFFu) | ((uint)alpha << 24);
    }

    private static void ValidateColumnVertexRange(int vertexLength, int count)
    {
        if (count <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(count), "SciChart column batches require at least one vertex.");
        }

        if (count > vertexLength)
        {
            throw new ArgumentOutOfRangeException(nameof(count), "SciChart column vertex count exceeds the supplied vertex span.");
        }
    }

    private static void ValidateRectVertexRange(int vertexLength, int count)
    {
        if (count <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(count), "SciChart rect batches require at least one vertex.");
        }

        if (count > vertexLength)
        {
            throw new ArgumentOutOfRangeException(nameof(count), "SciChart rect vertex count exceeds the supplied vertex span.");
        }
    }

    private static void ValidateSpriteVertexRange(int vertexLength, int count)
    {
        if (count <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(count), "SciChart sprite batches require at least one vertex.");
        }

        if (count > vertexLength)
        {
            throw new ArgumentOutOfRangeException(nameof(count), "SciChart sprite vertex count exceeds the supplied vertex span.");
        }
    }

    private static void ValidateFinancialVertexRange(int vertexLength, int count)
    {
        if (count <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(count), "SciChart financial batches require at least one vertex.");
        }

        if (count > vertexLength)
        {
            throw new ArgumentOutOfRangeException(nameof(count), "SciChart financial vertex count exceeds the supplied vertex span.");
        }
    }

    private static void ValidateFinancialWidth(float width)
    {
        if (!float.IsFinite(width) || width <= 0f)
        {
            throw new ArgumentOutOfRangeException(nameof(width), "SciChart financial batches require a finite positive width.");
        }
    }

    private static void ValidateLineVertexRange(int vertexLength, int count)
    {
        if (count < 2)
        {
            throw new ArgumentOutOfRangeException(nameof(count), "SciChart line batches require at least two vertices.");
        }

        if (count > vertexLength)
        {
            throw new ArgumentOutOfRangeException(nameof(count), "SciChart line vertex count exceeds the supplied vertex span.");
        }
    }

    private static void ValidateBandVertexRange(int vertexLength, int count)
    {
        if (count < 2)
        {
            throw new ArgumentOutOfRangeException(nameof(count), "SciChart band batches require at least two vertices.");
        }

        if (count > vertexLength)
        {
            throw new ArgumentOutOfRangeException(nameof(count), "SciChart band vertex count exceeds the supplied vertex span.");
        }
    }

    private static void ValidateVertexRange(int vertexLength, int startIndex, int count)
    {
        if (startIndex < 0 || startIndex > vertexLength)
        {
            throw new ArgumentOutOfRangeException(nameof(startIndex), "SciChart vertex start index is outside the supplied vertex span.");
        }

        if (count <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(count), "SciChart vertex draws require at least one vertex.");
        }

        if (count > vertexLength - startIndex)
        {
            throw new ArgumentOutOfRangeException(nameof(count), "SciChart vertex count exceeds the supplied vertex span.");
        }
    }

    private static bool HasFiniteLinePosition(ProGpuDirectXSciChartColorVertex vertex)
    {
        return float.IsFinite(vertex.X)
            && float.IsFinite(vertex.Y)
            && float.IsFinite(vertex.Offset);
    }

    private static bool HasFiniteBandVertex(ProGpuDirectXSciChartBandVertex vertex)
    {
        return float.IsFinite(vertex.X)
            && float.IsFinite(vertex.Y0)
            && float.IsFinite(vertex.Y1);
    }

    private static bool HasFinitePoint(ProGpuDirectXSciChartPoint point)
    {
        return float.IsFinite(point.X)
            && float.IsFinite(point.Y);
    }

    private static bool HasFiniteAreaSegment(ProGpuDirectXSciChartAreaSegment segment)
    {
        return HasFinitePoint(segment.Start)
            && HasFinitePoint(segment.End);
    }

    private static uint GetPaletteColor(IReadOnlyList<uint>? colors, int index, uint fallbackColorArgb)
    {
        return colors is not null && (uint)index < (uint)colors.Count
            ? colors[index]
            : fallbackColorArgb;
    }

    private static uint SelectBandFillColor(
        ProGpuDirectXSciChartBandVertex vertex,
        uint positiveColorArgb,
        uint negativeColorArgb)
    {
        return vertex.Y0 >= vertex.Y1
            ? positiveColorArgb
            : negativeColorArgb;
    }

    private static bool TryGetBandCrossing(
        ProGpuDirectXSciChartBandVertex start,
        ProGpuDirectXSciChartBandVertex end,
        out ProGpuDirectXSciChartBandVertex crossing)
    {
        var startDelta = start.Y0 - start.Y1;
        var endDelta = end.Y0 - end.Y1;
        if (startDelta == 0f
            || endDelta == 0f
            || MathF.Sign(startDelta) == MathF.Sign(endDelta))
        {
            crossing = default;
            return false;
        }

        var t = startDelta / (startDelta - endDelta);
        if (t <= 0f || t >= 1f)
        {
            crossing = default;
            return false;
        }

        var x = Lerp(start.X, end.X, t);
        var y = Lerp(start.Y0, end.Y0, t);
        crossing = new ProGpuDirectXSciChartBandVertex(x, y, y);
        return true;
    }

    private static float Lerp(float start, float end, float t)
    {
        return start + ((end - start) * t);
    }

    private static bool TryGetLinePoint(
        ProGpuDirectXSciChartColorVertex vertex,
        ProGpuDirectXSciChartVertexTransform transform,
        ProGpuDirectXSciChartPen2D pen,
        out float x,
        out float y,
        out float offset,
        out uint colorArgb)
    {
        if (!HasFiniteLinePosition(vertex))
        {
            x = y = 0f;
            offset = 0f;
            colorArgb = 0;
            return false;
        }

        x = transform.SwapAxis ? vertex.Y : vertex.X;
        y = transform.SwapAxis ? vertex.X : vertex.Y;
        offset = vertex.Offset;
        colorArgb = vertex.ColorArgb == 0
            ? pen.ColorArgb
            : vertex.ColorArgb;
        return HasVisibleColor(colorArgb);
    }

    private static bool TryGetPrimitiveRect(
        ProGpuDirectXSciChartPoint pt1,
        ProGpuDirectXSciChartPoint pt2,
        out float left,
        out float top,
        out float right,
        out float bottom)
    {
        if (!HasFinitePoint(pt1)
            || !HasFinitePoint(pt2))
        {
            left = top = right = bottom = 0f;
            return false;
        }

        left = MathF.Min(pt1.X, pt2.X);
        right = MathF.Max(pt1.X, pt2.X);
        top = MathF.Min(pt1.Y, pt2.Y);
        bottom = MathF.Max(pt1.Y, pt2.Y);
        return left != right && top != bottom;
    }

    private static bool TryGetPointBounds(
        ReadOnlySpan<ProGpuDirectXSciChartPoint> points,
        out PrimitiveBounds bounds)
    {
        var left = float.PositiveInfinity;
        var top = float.PositiveInfinity;
        var right = float.NegativeInfinity;
        var bottom = float.NegativeInfinity;
        var hasPoint = false;
        foreach (var point in points)
        {
            if (!HasFinitePoint(point))
            {
                continue;
            }

            hasPoint = true;
            left = MathF.Min(left, point.X);
            top = MathF.Min(top, point.Y);
            right = MathF.Max(right, point.X);
            bottom = MathF.Max(bottom, point.Y);
        }

        bounds = hasPoint
            ? new PrimitiveBounds(left, top, right, bottom)
            : default;
        return hasPoint;
    }

    private static bool TryGetAreaBounds(
        ReadOnlySpan<ProGpuDirectXSciChartAreaSegment> lines,
        out PrimitiveBounds bounds)
    {
        var left = float.PositiveInfinity;
        var top = float.PositiveInfinity;
        var right = float.NegativeInfinity;
        var bottom = float.NegativeInfinity;
        var hasPoint = false;
        foreach (var line in lines)
        {
            if (!HasFiniteAreaSegment(line))
            {
                continue;
            }

            hasPoint = true;
            left = MathF.Min(left, MathF.Min(line.Start.X, line.End.X));
            top = MathF.Min(top, MathF.Min(line.Start.Y, line.End.Y));
            right = MathF.Max(right, MathF.Max(line.Start.X, line.End.X));
            bottom = MathF.Max(bottom, MathF.Max(line.Start.Y, line.End.Y));
        }

        bounds = hasPoint
            ? new PrimitiveBounds(left, top, right, bottom)
            : default;
        return hasPoint;
    }

    private static bool HasFiniteFinancialVertex(ProGpuDirectXSciChartOhlcCandleVertex vertex)
    {
        return float.IsFinite(vertex.X)
            && float.IsFinite(vertex.O)
            && float.IsFinite(vertex.H)
            && float.IsFinite(vertex.L)
            && float.IsFinite(vertex.C);
    }

    private static bool TryGetColumnRect(
        ProGpuDirectXSciChartColumnVertex vertex,
        ProGpuDirectXSciChartVertexTransform transform,
        out float left,
        out float top,
        out float right,
        out float bottom)
    {
        var x = transform.SwapAxis ? vertex.Y : vertex.X;
        var y = transform.SwapAxis ? vertex.X : vertex.Y;
        var width = transform.SwapAxis ? vertex.Height : vertex.Width;
        var height = transform.SwapAxis ? vertex.Width : vertex.Height;
        if (!float.IsFinite(x)
            || !float.IsFinite(y)
            || !float.IsFinite(width)
            || !float.IsFinite(height)
            || width == 0f
            || height == 0f)
        {
            left = top = right = bottom = 0f;
            return false;
        }

        left = MathF.Min(x, x + width);
        right = MathF.Max(x, x + width);
        top = MathF.Min(y, y + height);
        bottom = MathF.Max(y, y + height);
        return true;
    }

    private static bool TryGetRect(
        ProGpuDirectXSciChartRectVertex vertex,
        ProGpuDirectXSciChartVertexTransform transform,
        ProGpuDirectXSciChartSpriteAnchor anchor,
        out float left,
        out float top,
        out float right,
        out float bottom)
    {
        if (!TryGetAnchoredRect(
            vertex.X,
            vertex.Y,
            vertex.Width,
            vertex.Height,
            anchor,
            out var x,
            out var y,
            out var width,
            out var height))
        {
            left = top = right = bottom = 0f;
            return false;
        }

        if (transform.SwapAxis)
        {
            (x, y) = (y, x);
            (width, height) = (height, width);
        }

        left = MathF.Min(x, x + width);
        right = MathF.Max(x, x + width);
        top = MathF.Min(y, y + height);
        bottom = MathF.Max(y, y + height);
        return true;
    }

    private static bool TryGetSpriteRect(
        ProGpuDirectXSciChartSpriteVertex vertex,
        ProGpuDirectXSciChartSprite2D sprite,
        ProGpuDirectXSciChartVertexTransform transform,
        float centeredAmount,
        out float left,
        out float top,
        out float right,
        out float bottom)
    {
        var x = transform.SwapAxis ? vertex.Y : vertex.X;
        var y = transform.SwapAxis ? vertex.X : vertex.Y;
        var width = transform.SwapAxis ? sprite.Height : sprite.Width;
        var height = transform.SwapAxis ? sprite.Width : sprite.Height;
        if (!float.IsFinite(x)
            || !float.IsFinite(y)
            || width == 0
            || height == 0)
        {
            left = top = right = bottom = 0f;
            return false;
        }

        left = x - (width * centeredAmount);
        top = y - (height * centeredAmount);
        right = left + width;
        bottom = top + height;
        return true;
    }

    private static bool TryGetSpriteRect(
        ProGpuDirectXSciChartColoredSpriteVertex vertex,
        ProGpuDirectXSciChartSprite2D sprite,
        ProGpuDirectXSciChartVertexTransform transform,
        float centeredAmount,
        out float left,
        out float top,
        out float right,
        out float bottom)
    {
        var x = transform.SwapAxis ? vertex.Y : vertex.X;
        var y = transform.SwapAxis ? vertex.X : vertex.Y;
        var width = transform.SwapAxis ? sprite.Height : sprite.Width;
        var height = transform.SwapAxis ? sprite.Width : sprite.Height;
        if (!float.IsFinite(x)
            || !float.IsFinite(y)
            || width == 0
            || height == 0)
        {
            left = top = right = bottom = 0f;
            return false;
        }

        left = x - (width * centeredAmount);
        top = y - (height * centeredAmount);
        right = left + width;
        bottom = top + height;
        return true;
    }

    private static bool TryGetFinancialBodyRect(
        ProGpuDirectXSciChartOhlcCandleVertex vertex,
        float width,
        ProGpuDirectXSciChartVertexTransform transform,
        out float left,
        out float top,
        out float right,
        out float bottom)
    {
        if (!HasFiniteFinancialVertex(vertex))
        {
            left = top = right = bottom = 0f;
            return false;
        }

        var halfWidth = width / 2f;
        var x0 = vertex.X - halfWidth;
        var x1 = vertex.X + halfWidth;
        var y0 = vertex.O;
        var y1 = vertex.C;
        if (y0 == y1)
        {
            y1 += 1f;
        }

        if (transform.SwapAxis)
        {
            left = MathF.Min(y0, y1);
            right = MathF.Max(y0, y1);
            top = MathF.Min(x0, x1);
            bottom = MathF.Max(x0, x1);
        }
        else
        {
            left = MathF.Min(x0, x1);
            right = MathF.Max(x0, x1);
            top = MathF.Min(y0, y1);
            bottom = MathF.Max(y0, y1);
        }

        return left != right && top != bottom;
    }

    private static bool TryGetAnchoredRect(
        float x,
        float y,
        float width,
        float height,
        ProGpuDirectXSciChartSpriteAnchor anchor,
        out float left,
        out float top,
        out float anchoredWidth,
        out float anchoredHeight)
    {
        if (!float.IsFinite(x)
            || !float.IsFinite(y)
            || !float.IsFinite(width)
            || !float.IsFinite(height)
            || width == 0f
            || height == 0f)
        {
            left = top = anchoredWidth = anchoredHeight = 0f;
            return false;
        }

        left = x;
        top = y;
        anchoredWidth = width;
        anchoredHeight = height;

        switch (anchor)
        {
            case ProGpuDirectXSciChartSpriteAnchor.TopLeft:
                break;
            case ProGpuDirectXSciChartSpriteAnchor.Top:
                left -= width / 2f;
                break;
            case ProGpuDirectXSciChartSpriteAnchor.TopRight:
                left -= width;
                break;
            case ProGpuDirectXSciChartSpriteAnchor.Left:
                top -= height / 2f;
                break;
            case ProGpuDirectXSciChartSpriteAnchor.Center:
                left -= width / 2f;
                top -= height / 2f;
                break;
            case ProGpuDirectXSciChartSpriteAnchor.Right:
                left -= width;
                top -= height / 2f;
                break;
            case ProGpuDirectXSciChartSpriteAnchor.BottomLeft:
                top -= height;
                break;
            case ProGpuDirectXSciChartSpriteAnchor.Bottom:
                left -= width / 2f;
                top -= height;
                break;
            case ProGpuDirectXSciChartSpriteAnchor.BottomRight:
                left -= width;
                top -= height;
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(anchor), anchor, null);
        }

        return true;
    }

    private static bool HasVisibleColor(uint colorArgb)
    {
        return (colorArgb >> 24) != 0;
    }

    private static bool HasVisibleBrush(ProGpuDirectXSciChartBrush2D brush, double opacity = 1d)
    {
        if (opacity <= 0d)
        {
            return false;
        }

        if (!brush.IsGradient)
        {
            return HasVisibleColor(ApplyOpacity(brush.ColorArgb, opacity));
        }

        return HasVisibleColor(ApplyOpacity(brush.StartColorArgb, opacity))
            || HasVisibleColor(ApplyOpacity(brush.EndColorArgb, opacity));
    }

    private static uint GetBrushColorArgb(
        ProGpuDirectXSciChartBrush2D brush,
        float x,
        float y,
        PrimitiveBounds bounds,
        double opacity,
        double gradientRotationAngle)
    {
        if (!brush.IsGradient)
        {
            return ApplyOpacity(brush.ColorArgb, opacity);
        }

        var position = GetLinearGradientPosition(x, y, bounds, brush.GradientRotationAngle + gradientRotationAngle);
        return InterpolateColorArgb(
            ApplyOpacity(brush.StartColorArgb, opacity),
            ApplyOpacity(brush.EndColorArgb, opacity),
            position);
    }

    private static float GetLinearGradientPosition(
        float x,
        float y,
        PrimitiveBounds bounds,
        double gradientRotationAngle)
    {
        var radians = gradientRotationAngle * (Math.PI / 180d);
        var axisX = (float)Math.Cos(radians);
        var axisY = (float)Math.Sin(radians);
        var p0 = Project(bounds.Left, bounds.Top, axisX, axisY);
        var p1 = Project(bounds.Right, bounds.Top, axisX, axisY);
        var p2 = Project(bounds.Right, bounds.Bottom, axisX, axisY);
        var p3 = Project(bounds.Left, bounds.Bottom, axisX, axisY);
        var min = MathF.Min(MathF.Min(p0, p1), MathF.Min(p2, p3));
        var max = MathF.Max(MathF.Max(p0, p1), MathF.Max(p2, p3));
        if (MathF.Abs(max - min) <= 0.0001f)
        {
            return 0f;
        }

        return Math.Clamp((Project(x, y, axisX, axisY) - min) / (max - min), 0f, 1f);
    }

    private static float Project(float x, float y, float axisX, float axisY)
    {
        return (x * axisX) + (y * axisY);
    }

    private static uint InterpolateColorArgb(uint startColorArgb, uint endColorArgb, float amount)
    {
        var startA = (startColorArgb >> 24) & 0xFF;
        var startR = (startColorArgb >> 16) & 0xFF;
        var startG = (startColorArgb >> 8) & 0xFF;
        var startB = startColorArgb & 0xFF;
        var endA = (endColorArgb >> 24) & 0xFF;
        var endR = (endColorArgb >> 16) & 0xFF;
        var endG = (endColorArgb >> 8) & 0xFF;
        var endB = endColorArgb & 0xFF;

        return (LerpByte(startA, endA, amount) << 24)
            | (LerpByte(startR, endR, amount) << 16)
            | (LerpByte(startG, endG, amount) << 8)
            | LerpByte(startB, endB, amount);
    }

    private static uint LerpByte(uint start, uint end, float amount)
    {
        return (uint)Math.Clamp((int)MathF.Round(start + (((float)end - start) * amount)), 0, 255);
    }

    private void AppendSolidColorVertex(List<float> vertexData, float x, float y, uint colorArgb)
    {
        vertexData.Add(PixelXToNdc(x));
        vertexData.Add(PixelYToNdc(y));
        AppendColorArgb(vertexData, colorArgb);
    }

    private void AppendTexturedColorVertex(List<float> vertexData, float x, float y, float u, float v, uint colorArgb)
    {
        vertexData.Add(PixelXToNdc(x));
        vertexData.Add(PixelYToNdc(y));
        vertexData.Add(u);
        vertexData.Add(v);
        AppendColorArgb(vertexData, colorArgb);
    }

    private static void WriteColorArgb(float[] vertexData, int offset, uint colorArgb)
    {
        vertexData[offset] = ((colorArgb >> 16) & 0xFF) / 255f;
        vertexData[offset + 1] = ((colorArgb >> 8) & 0xFF) / 255f;
        vertexData[offset + 2] = (colorArgb & 0xFF) / 255f;
        vertexData[offset + 3] = ((colorArgb >> 24) & 0xFF) / 255f;
    }

    private static Vector4 ToColorVector(uint colorArgb)
    {
        return new Vector4(
            ((colorArgb >> 16) & 0xFF) / 255f,
            ((colorArgb >> 8) & 0xFF) / 255f,
            (colorArgb & 0xFF) / 255f,
            ((colorArgb >> 24) & 0xFF) / 255f);
    }

    private static void AppendColorArgb(List<float> vertexData, uint colorArgb)
    {
        vertexData.Add(((colorArgb >> 16) & 0xFF) / 255f);
        vertexData.Add(((colorArgb >> 8) & 0xFF) / 255f);
        vertexData.Add((colorArgb & 0xFF) / 255f);
        vertexData.Add(((colorArgb >> 24) & 0xFF) / 255f);
    }

    private static string CreateTexturePixelShader()
    {
        return $$"""
@group(0) @binding({{ProGpuDirectXNativeBindingMap.GetShaderResourceBinding(DxShaderStage.Pixel, 0)}}) var SourceTexture: texture_2d<f32>;
@group(0) @binding({{ProGpuDirectXNativeBindingMap.GetSamplerBinding(DxShaderStage.Pixel, 0)}}) var SourceSampler: sampler;

struct VertexOut {
    @builtin(position) position: vec4<f32>,
    @location(0) uv: vec2<f32>,
};

@fragment
fn fs_main(input: VertexOut) -> @location(0) vec4<f32> {
    return textureSample(SourceTexture, SourceSampler, input.uv);
}
""";
    }

    private static string CreateBatchedTexturePixelShader()
    {
        return $$"""
@group(0) @binding({{ProGpuDirectXNativeBindingMap.GetShaderResourceBinding(DxShaderStage.Pixel, 0)}}) var SourceTexture: texture_2d<f32>;
@group(0) @binding({{ProGpuDirectXNativeBindingMap.GetSamplerBinding(DxShaderStage.Pixel, 0)}}) var SourceSampler: sampler;

struct VertexOut {
    @builtin(position) position: vec4<f32>,
    @location(0) uv: vec2<f32>,
    @location(1) color: vec4<f32>,
};

@fragment
fn fs_main(input: VertexOut) -> @location(0) vec4<f32> {
    return textureSample(SourceTexture, SourceSampler, input.uv) * input.color;
}
""";
    }

    private static string InstancedSpriteVertexShader => """
struct VertexIn {
    @location(0) corner: vec2<f32>,
    @location(1) rect: vec4<f32>,
    @location(2) color: vec4<f32>,
};

struct VertexOut {
    @builtin(position) position: vec4<f32>,
    @location(0) uv: vec2<f32>,
    @location(1) color: vec4<f32>,
};

@vertex
fn vs_main(input: VertexIn) -> VertexOut {
    var output: VertexOut;
    let position = input.rect.xy + ((input.rect.zw - input.rect.xy) * input.corner);
    output.position = vec4<f32>(position, 0.0, 1.0);
    output.uv = input.corner;
    output.color = input.color;
    return output;
}
""";

    private static string CreateShapedHeatmapPixelShader()
    {
        return $$"""
struct HeatmapParams {
    colorMapMin: f32,
    colorMapInvRange: f32,
    heightTextureWidth: f32,
    heightTextureHeight: f32,
};

@group(0) @binding({{ProGpuDirectXNativeBindingMap.GetConstantBufferBinding(DxShaderStage.Pixel, 0)}}) var<uniform> Heatmap: HeatmapParams;
@group(0) @binding({{ProGpuDirectXNativeBindingMap.GetShaderResourceBinding(DxShaderStage.Pixel, 0)}}) var<storage, read> Heights: array<f32>;
@group(0) @binding({{ProGpuDirectXNativeBindingMap.GetShaderResourceBinding(DxShaderStage.Pixel, 1)}}) var GradientTexture: texture_2d<f32>;
@group(0) @binding({{ProGpuDirectXNativeBindingMap.GetSamplerBinding(DxShaderStage.Pixel, 0)}}) var SourceSampler: sampler;

struct VertexOut {
    @builtin(position) position: vec4<f32>,
    @location(0) uv: vec2<f32>,
    @location(1) color: vec4<f32>,
};

@fragment
fn fs_main(input: VertexOut) -> @location(0) vec4<f32> {
    let heightSize = vec2<i32>(i32(Heatmap.heightTextureWidth), i32(Heatmap.heightTextureHeight));
    let heightCoord = clamp(
        vec2<i32>(input.uv * vec2<f32>(heightSize)),
        vec2<i32>(0, 0),
        heightSize - vec2<i32>(1, 1));
    let heightIndex = u32(heightCoord.y * heightSize.x + heightCoord.x);
    let height = Heights[heightIndex];
    let gradientU = clamp((height - Heatmap.colorMapMin) * Heatmap.colorMapInvRange, 0.0, 1.0);
    return textureSample(GradientTexture, SourceSampler, vec2<f32>(gradientU, 0.5)) * input.color;
}
""";
    }

    private static string CreateHeightTextureContourPixelShader()
    {
        return $$"""
struct ContourParams {
    zMin: f32,
    zMax: f32,
    zStep: f32,
    strokeThickness: f32,
    opacity: f32,
    heightTextureWidth: f32,
    heightTextureHeight: f32,
    _pad0: f32,
    color: vec4<f32>,
};

@group(0) @binding({{ProGpuDirectXNativeBindingMap.GetConstantBufferBinding(DxShaderStage.Pixel, 0)}}) var<uniform> Contour: ContourParams;
@group(0) @binding({{ProGpuDirectXNativeBindingMap.GetShaderResourceBinding(DxShaderStage.Pixel, 0)}}) var<storage, read> Heights: array<f32>;
@group(0) @binding({{ProGpuDirectXNativeBindingMap.GetShaderResourceBinding(DxShaderStage.Pixel, 1)}}) var ContourTexture: texture_2d<f32>;
@group(0) @binding({{ProGpuDirectXNativeBindingMap.GetSamplerBinding(DxShaderStage.Pixel, 0)}}) var SourceSampler: sampler;

struct VertexOut {
    @builtin(position) position: vec4<f32>,
    @location(0) uv: vec2<f32>,
};

fn load_height(coord: vec2<i32>, heightSize: vec2<i32>) -> f32 {
    let clampedCoord = clamp(coord, vec2<i32>(0, 0), heightSize - vec2<i32>(1, 1));
    let heightIndex = u32(clampedCoord.y * heightSize.x + clampedCoord.x);
    return Heights[heightIndex];
}

@fragment
fn fs_main(input: VertexOut) -> @location(0) vec4<f32> {
    let heightSize = vec2<i32>(i32(Contour.heightTextureWidth), i32(Contour.heightTextureHeight));
    let centerCoord = clamp(
        vec2<i32>(input.uv * vec2<f32>(heightSize)),
        vec2<i32>(0, 0),
        heightSize - vec2<i32>(1, 1));
    let height = load_height(centerCoord, heightSize);
    if (height < Contour.zMin || height > Contour.zMax) {
        discard;
    }

    let contourIndex = round((height - Contour.zMin) / Contour.zStep);
    let contourHeight = Contour.zMin + contourIndex * Contour.zStep;
    if (contourHeight < Contour.zMin || contourHeight > Contour.zMax) {
        discard;
    }

    let heightDx = abs(load_height(centerCoord + vec2<i32>(1, 0), heightSize) - height);
    let heightDy = abs(load_height(centerCoord + vec2<i32>(0, 1), heightSize) - height);
    let heightPerPixel = max(max(heightDx, heightDy), Contour.zStep * 0.001);
    let threshold = heightPerPixel * max(Contour.strokeThickness, 1.0);
    let distanceToContour = abs(height - contourHeight);
    var lineAlpha = 1.0 - smoothstep(threshold, threshold * 2.0, distanceToContour);
    let contourMask = textureSample(ContourTexture, SourceSampler, input.uv);
    lineAlpha = lineAlpha * clamp(Contour.opacity, 0.0, 1.0) * Contour.color.a * contourMask.a;
    if (lineAlpha <= 0.001) {
        discard;
    }

    return vec4<f32>(Contour.color.rgb, lineAlpha);
}
""";
    }

    private static string TextureVertexShader => """
struct VertexIn {
    @location(0) position: vec2<f32>,
    @location(1) uv: vec2<f32>,
};

struct VertexOut {
    @builtin(position) position: vec4<f32>,
    @location(0) uv: vec2<f32>,
};

@vertex
fn vs_main(input: VertexIn) -> VertexOut {
    var output: VertexOut;
    output.position = vec4<f32>(input.position, 0.0, 1.0);
    output.uv = input.uv;
    return output;
}
""";

    private static string LineVertexShader => """
struct VertexIn {
    @location(0) position: vec2<f32>,
    @location(1) color: vec4<f32>,
};

struct VertexOut {
    @builtin(position) position: vec4<f32>,
    @location(0) color: vec4<f32>,
};

@vertex
fn vs_main(input: VertexIn) -> VertexOut {
    var output: VertexOut;
    output.position = vec4<f32>(input.position, 0.0, 1.0);
    output.color = input.color;
    return output;
}
""";

    private static string LinePixelShader => """
struct VertexOut {
    @builtin(position) position: vec4<f32>,
    @location(0) color: vec4<f32>,
};

@fragment
fn fs_main(input: VertexOut) -> @location(0) vec4<f32> {
    return input.color;
}
""";

    private static string BatchedTextureVertexShader => """
struct VertexIn {
    @location(0) position: vec2<f32>,
    @location(1) uv: vec2<f32>,
    @location(2) color: vec4<f32>,
};

struct VertexOut {
    @builtin(position) position: vec4<f32>,
    @location(0) uv: vec2<f32>,
    @location(1) color: vec4<f32>,
};

@vertex
fn vs_main(input: VertexIn) -> VertexOut {
    var output: VertexOut;
    output.position = vec4<f32>(input.position, 0.0, 1.0);
    output.uv = input.uv;
    output.color = input.color;
    return output;
}
""";

    private void DisposeTransientResources()
    {
        foreach (var resource in _transientResources)
        {
            resource.Dispose();
        }

        _transientResources.Clear();
    }

    private void ThrowIfDisposed()
    {
        if (_isDisposed)
        {
            throw new ObjectDisposedException(nameof(ProGpuDirectXSciChartRenderContext2D));
        }
    }

    public void Dispose()
    {
        if (_isDisposed)
        {
            return;
        }

        DisposeTransientResources();
        foreach (var pipeline in _texturePipelines.Values)
        {
            pipeline.Dispose();
        }

        foreach (var pipeline in _linePipelines.Values)
        {
            pipeline.Dispose();
        }

        foreach (var pipeline in _columnFillPipelines.Values)
        {
            pipeline.Dispose();
        }

        foreach (var pipeline in _textureVertexPipelines.Values)
        {
            pipeline.Dispose();
        }

        foreach (var pipeline in _spritePipelines.Values)
        {
            pipeline.Dispose();
        }

        foreach (var pipeline in _instancedSpritePipelines.Values)
        {
            pipeline.Dispose();
        }

        foreach (var pipeline in _shapedHeatmapPipelines.Values)
        {
            pipeline.Dispose();
        }

        foreach (var pipeline in _heightContourPipelines.Values)
        {
            pipeline.Dispose();
        }

        foreach (var sampler in _samplers.Values)
        {
            sampler.Dispose();
        }

        _textureVertexShader?.Dispose();
        _texturePixelShader?.Dispose();
        _lineVertexShader?.Dispose();
        _linePixelShader?.Dispose();
        _batchedTextureVertexShader?.Dispose();
        _batchedTexturePixelShader?.Dispose();
        _instancedSpriteVertexShader?.Dispose();
        _spriteQuadVertexBuffer?.Dispose();
        _shapedHeatmapPixelShader?.Dispose();
        _heightContourPixelShader?.Dispose();
        _textCompositor?.Dispose();
        _context.Dispose();
        RenderTarget.Dispose();
        _isDisposed = true;
    }
}

public sealed class ProGpuDirectXSciChartRenderContext3D : IDisposable
{
    private readonly ProGpuDirectXDevice _device;
    private readonly ProGpuDirectXDeviceContext _context;
    private readonly List<IDisposable> _transientResources = new();
    private readonly List<ProGpuDirectXSciChartPointCloud3DDraw> _pointCloudDraws = new();
    private readonly List<ProGpuDirectXSciChartLine3DDraw> _lineDraws = new();
    private readonly List<ProGpuDirectXSciChartMesh3DDraw> _meshDraws = new();
    private readonly List<ProGpuDirectXSciChartTriangleStrip3DDraw> _triangleStripDraws = new();
    private readonly List<ProGpuDirectXSciChartSurfaceMesh3DDraw> _surfaceMeshDraws = new();
    private readonly List<ProGpuDirectXSciChartWaterfall3DDraw> _waterfallDraws = new();
    private readonly Dictionary<(DxPrimitiveTopology Topology, DxCullMode CullMode), ProGpuDirectXGraphicsPipeline> _pipelines = new();
    private ProGpuDirectXShader? _vertexShader;
    private ProGpuDirectXShader? _pixelShader;
    private ProGpuDirectXInputLayout? _inputLayout;
    private DxRect? _clipRect;
    private bool _isDisposed;

    public ProGpuDirectXSciChartRenderContext3D(
        ProGpuDirectXDevice device,
        uint width,
        uint height,
        DxResourceFormat renderTargetFormat = DxResourceFormat.R8G8B8A8Unorm)
    {
        _device = device ?? throw new ArgumentNullException(nameof(device));
        _context = device.CreateImmediateContext();
        RenderTarget = device.CreateTexture2D(new DxTexture2DDescriptor
        {
            Width = width,
            Height = height,
            Format = renderTargetFormat,
            Usage = DxTextureUsage.RenderTarget | DxTextureUsage.CopySource | DxTextureUsage.CopyDestination,
            CpuAccess = DxCpuAccessFlags.Read,
            Label = "SciChart3DRenderTarget"
        });
        DepthStencil = device.CreateTexture2D(new DxTexture2DDescriptor
        {
            Width = width,
            Height = height,
            Format = DxResourceFormat.D32Float,
            Usage = DxTextureUsage.DepthStencil,
            Label = "SciChart3DDepth"
        });
    }

    public ProGpuDirectXDevice Device => _device;

    public ProGpuDirectXDeviceContext ImmediateContext => _context;

    public ProGpuDirectXTexture2D RenderTarget { get; }

    public ProGpuDirectXTexture2D DepthStencil { get; }

    public IReadOnlyList<ProGpuDirectXSciChartPointCloud3DDraw> PointCloudDraws => _pointCloudDraws;

    public IReadOnlyList<ProGpuDirectXSciChartLine3DDraw> LineDraws => _lineDraws;

    public IReadOnlyList<ProGpuDirectXSciChartMesh3DDraw> MeshDraws => _meshDraws;

    public IReadOnlyList<ProGpuDirectXSciChartTriangleStrip3DDraw> TriangleStripDraws => _triangleStripDraws;

    public IReadOnlyList<ProGpuDirectXSciChartSurfaceMesh3DDraw> SurfaceMeshDraws => _surfaceMeshDraws;

    public IReadOnlyList<ProGpuDirectXSciChartWaterfall3DDraw> WaterfallDraws => _waterfallDraws;

    public void BeginFrame()
    {
        ThrowIfDisposed();
        _pointCloudDraws.Clear();
        _lineDraws.Clear();
        _meshDraws.Clear();
        _triangleStripDraws.Clear();
        _surfaceMeshDraws.Clear();
        _waterfallDraws.Clear();
        _clipRect = null;
    }

    public void SetClipRect(DxRect? clipRect)
    {
        ThrowIfDisposed();
        if (clipRect is { } rect)
        {
            if (rect.Width < 0 || rect.Height < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(clipRect), "SciChart 3D clip rectangles cannot have negative dimensions.");
            }

            _clipRect = ClampClipRect(rect);
        }
        else
        {
            _clipRect = null;
        }
    }

    public void Clear(DxColor color, float depth = 1f)
    {
        ThrowIfDisposed();
        if (!float.IsFinite(depth))
        {
            throw new ArgumentOutOfRangeException(nameof(depth), "SciChart 3D depth clear values must be finite.");
        }

        _context.SetRenderTargets(RenderTarget, DepthStencil);
        _context.SetViewport(new DxViewport(0, 0, RenderTarget.Width, RenderTarget.Height));
        _context.SetScissorRect(FullRenderTargetRect);
        _context.ClearRenderTarget(RenderTarget, color);
        _context.ClearDepthStencil(DepthStencil, DxDepthStencilClearFlags.Depth, depth, 0);
    }

    public void DrawPointCloud(
        ReadOnlySpan<ProGpuDirectXSciChartVertex3D> vertices,
        Matrix4x4 worldViewProjection,
        Vector3? lightDirection = null)
    {
        ThrowIfDisposed();
        ValidateVertices(vertices, minCount: 1);
        ValidateMatrix(worldViewProjection);
        if (HasEmptyClip)
        {
            return;
        }

        var vertexSpan = vertices;
        var light = ResolveLightDirection(lightDirection);
        var vertexBuffer = CreateVertexBuffer(vertexSpan);
        var cameraBuffer = CreateCameraBuffer(worldViewProjection, light);
        var pipeline = GetPipeline(DxPrimitiveTopology.PointList, DxCullMode.None);

        SetDrawState(pipeline, vertexBuffer, cameraBuffer);
        _context.Draw((uint)vertexSpan.Length);
        _pointCloudDraws.Add(new ProGpuDirectXSciChartPointCloud3DDraw(
            CopyVertices(vertexSpan),
            worldViewProjection,
            light,
            _clipRect));
        _transientResources.Add(vertexBuffer);
        _transientResources.Add(cameraBuffer);
    }

    public void DrawLineList(
        ReadOnlySpan<ProGpuDirectXSciChartVertex3D> vertices,
        Matrix4x4 worldViewProjection,
        Vector3? lightDirection = null)
    {
        DrawLines(vertices, isStrip: false, worldViewProjection, lightDirection);
    }

    public void DrawLineStrip(
        ReadOnlySpan<ProGpuDirectXSciChartVertex3D> vertices,
        Matrix4x4 worldViewProjection,
        Vector3? lightDirection = null)
    {
        DrawLines(vertices, isStrip: true, worldViewProjection, lightDirection);
    }

    public void DrawTriangleMesh(
        ReadOnlySpan<ProGpuDirectXSciChartVertex3D> vertices,
        ReadOnlySpan<uint> indices,
        Matrix4x4 worldViewProjection,
        Vector3? lightDirection = null,
        DxCullMode cullMode = DxCullMode.Back)
    {
        ThrowIfDisposed();
        ValidateVertices(vertices, minCount: 3);
        ValidateIndices(indices, vertices.Length);
        ValidateMatrix(worldViewProjection);
        if (!Enum.IsDefined(cullMode))
        {
            throw new ArgumentOutOfRangeException(nameof(cullMode), "Unknown SciChart 3D mesh cull mode.");
        }

        if (HasEmptyClip)
        {
            return;
        }

        var vertexSpan = vertices;
        var indexSpan = indices;
        var light = ResolveLightDirection(lightDirection);
        var vertexBuffer = CreateVertexBuffer(vertexSpan);
        var indexBuffer = CreateIndexBuffer(indexSpan);
        var cameraBuffer = CreateCameraBuffer(worldViewProjection, light);
        var pipeline = GetPipeline(DxPrimitiveTopology.TriangleList, cullMode);

        SetDrawState(pipeline, vertexBuffer, cameraBuffer);
        _context.SetIndexBuffer(indexBuffer, DxIndexFormat.UInt32);
        _context.DrawIndexed((uint)indexSpan.Length, indexFormat: DxIndexFormat.UInt32);
        _meshDraws.Add(new ProGpuDirectXSciChartMesh3DDraw(
            CopyVertices(vertexSpan),
            CopyIndices(indexSpan),
            worldViewProjection,
            light,
            cullMode,
            _clipRect));
        _transientResources.Add(vertexBuffer);
        _transientResources.Add(indexBuffer);
        _transientResources.Add(cameraBuffer);
    }

    public void DrawTriangleStrip(
        ReadOnlySpan<ProGpuDirectXSciChartVertex3D> vertices,
        Matrix4x4 worldViewProjection,
        Vector3? lightDirection = null,
        DxCullMode cullMode = DxCullMode.Back)
    {
        ThrowIfDisposed();
        ValidateVertices(vertices, minCount: 3);
        ValidateMatrix(worldViewProjection);
        if (!Enum.IsDefined(cullMode))
        {
            throw new ArgumentOutOfRangeException(nameof(cullMode), "Unknown SciChart 3D triangle-strip cull mode.");
        }

        if (HasEmptyClip)
        {
            return;
        }

        var vertexSpan = vertices;
        var light = ResolveLightDirection(lightDirection);
        var vertexBuffer = CreateVertexBuffer(vertexSpan);
        var cameraBuffer = CreateCameraBuffer(worldViewProjection, light);
        var pipeline = GetPipeline(DxPrimitiveTopology.TriangleStrip, cullMode);

        SetDrawState(pipeline, vertexBuffer, cameraBuffer);
        _context.Draw((uint)vertexSpan.Length);
        _triangleStripDraws.Add(new ProGpuDirectXSciChartTriangleStrip3DDraw(
            CopyVertices(vertexSpan),
            worldViewProjection,
            light,
            cullMode,
            _clipRect));
        _transientResources.Add(vertexBuffer);
        _transientResources.Add(cameraBuffer);
    }

    public void DrawSurfaceMesh(
        ReadOnlySpan<float> heights,
        int columns,
        int rows,
        Matrix4x4 worldViewProjection,
        Vector2? xRange = null,
        Vector2? zRange = null,
        uint lowColorArgb = 0xFF1455D9,
        uint highColorArgb = 0xFFFFD166,
        Vector3? lightDirection = null,
        DxCullMode cullMode = DxCullMode.Back)
    {
        ThrowIfDisposed();
        ValidateSurfaceMesh(heights, columns, rows, xRange, zRange);
        ValidateMatrix(worldViewProjection);
        if (!Enum.IsDefined(cullMode))
        {
            throw new ArgumentOutOfRangeException(nameof(cullMode), "Unknown SciChart 3D surface mesh cull mode.");
        }

        if (HasEmptyClip)
        {
            return;
        }

        var resolvedXRange = xRange ?? new Vector2(-1f, 1f);
        var resolvedZRange = zRange ?? new Vector2(0.25f, 0.75f);
        var heightSpan = heights;
        var vertices = CreateSurfaceMeshVertices(heightSpan, columns, rows, resolvedXRange, resolvedZRange, lowColorArgb, highColorArgb);
        var indices = CreateSurfaceMeshIndices(columns, rows);
        var light = ResolveLightDirection(lightDirection);
        var vertexBuffer = CreateVertexBuffer(vertices);
        var indexBuffer = CreateIndexBuffer(indices);
        var cameraBuffer = CreateCameraBuffer(worldViewProjection, light);
        var pipeline = GetPipeline(DxPrimitiveTopology.TriangleList, cullMode);

        SetDrawState(pipeline, vertexBuffer, cameraBuffer);
        _context.SetIndexBuffer(indexBuffer, DxIndexFormat.UInt32);
        _context.DrawIndexed((uint)indices.Length, indexFormat: DxIndexFormat.UInt32);
        _surfaceMeshDraws.Add(new ProGpuDirectXSciChartSurfaceMesh3DDraw(
            CopyHeights(heightSpan),
            columns,
            rows,
            resolvedXRange,
            resolvedZRange,
            lowColorArgb,
            highColorArgb,
            vertices,
            indices,
            worldViewProjection,
            light,
            cullMode,
            _clipRect));
        _transientResources.Add(vertexBuffer);
        _transientResources.Add(indexBuffer);
        _transientResources.Add(cameraBuffer);
    }

    public void DrawWaterfallDataSeries(
        ReadOnlySpan<float> heights,
        int columns,
        int rows,
        Matrix4x4 worldViewProjection,
        ProGpuDirectXSciChartWaterfall3DOptions? options = null,
        Vector3? lightDirection = null,
        DxCullMode cullMode = DxCullMode.None)
    {
        ThrowIfDisposed();
        options ??= new ProGpuDirectXSciChartWaterfall3DOptions();
        ValidateWaterfallDataSeries(heights, columns, rows, options);
        ValidateMatrix(worldViewProjection);
        if (!Enum.IsDefined(cullMode))
        {
            throw new ArgumentOutOfRangeException(nameof(cullMode), "Unknown SciChart 3D waterfall cull mode.");
        }

        if (HasEmptyClip)
        {
            return;
        }

        var heightSpan = heights;
        var xRange = ResolveWaterfallRange(options.XRange, columns);
        var yRange = ResolveWaterfallHeightRange(heightSpan, options.YRange);
        var zRange = ResolveWaterfallRange(options.ZRange, rows);
        var vertices = CreateWaterfallVertices(heightSpan, columns, rows, options, xRange, yRange, zRange);
        var indices = CreateWaterfallIndices(columns, rows);
        var light = ResolveLightDirection(lightDirection);
        var vertexBuffer = CreateVertexBuffer(vertices);
        var indexBuffer = CreateIndexBuffer(indices);
        var cameraBuffer = CreateCameraBuffer(worldViewProjection, light);
        var pipeline = GetPipeline(DxPrimitiveTopology.TriangleList, cullMode);

        SetDrawState(pipeline, vertexBuffer, cameraBuffer);
        _context.SetIndexBuffer(indexBuffer, DxIndexFormat.UInt32);
        _context.DrawIndexed((uint)indices.Length, indexFormat: DxIndexFormat.UInt32);
        _waterfallDraws.Add(new ProGpuDirectXSciChartWaterfall3DDraw(
            CopyHeights(heightSpan),
            columns,
            rows,
            xRange,
            yRange,
            zRange,
            options.BaseY,
            options.LowColorArgb,
            options.HighColorArgb,
            vertices,
            indices,
            worldViewProjection,
            light,
            cullMode,
            _clipRect));
        _transientResources.Add(vertexBuffer);
        _transientResources.Add(indexBuffer);
        _transientResources.Add(cameraBuffer);
    }

    public void DrawXyzDataSeriesPointCloud(
        ReadOnlySpan<ProGpuDirectXSciChartXyzPoint3D> points,
        Matrix4x4 worldViewProjection,
        ProGpuDirectXSciChartXyzSeries3DOptions? options = null,
        Vector3? lightDirection = null)
    {
        var vertices = CreateXyzSeriesVertices(points, options, minCount: 1);
        DrawPointCloud(vertices, worldViewProjection, lightDirection);
    }

    public void DrawXyzDataSeriesLineStrip(
        ReadOnlySpan<ProGpuDirectXSciChartXyzPoint3D> points,
        Matrix4x4 worldViewProjection,
        ProGpuDirectXSciChartXyzSeries3DOptions? options = null,
        Vector3? lightDirection = null)
    {
        var vertices = CreateXyzSeriesVertices(points, options, minCount: 2);
        DrawLineStrip(vertices, worldViewProjection, lightDirection);
    }

    public void DrawXyzDataSeriesRibbon(
        ReadOnlySpan<ProGpuDirectXSciChartXyzPoint3D> points,
        Matrix4x4 worldViewProjection,
        float halfThickness = 0.015f,
        ProGpuDirectXSciChartXyzSeries3DOptions? options = null,
        Vector3? lightDirection = null,
        DxCullMode cullMode = DxCullMode.None)
    {
        if (!float.IsFinite(halfThickness) || halfThickness <= 0f)
        {
            throw new ArgumentOutOfRangeException(nameof(halfThickness), "SciChart 3D XYZ ribbons require a finite positive half-thickness.");
        }

        var vertices = CreateXyzSeriesVertices(points, options, minCount: 2);
        var ribbon = new ProGpuDirectXSciChartVertex3D[checked(vertices.Length * 2)];
        for (var i = 0; i < vertices.Length; i++)
        {
            var vertex = vertices[i];
            var target = i * 2;
            ribbon[target] = vertex with { Y = vertex.Y - halfThickness };
            ribbon[target + 1] = vertex with { Y = vertex.Y + halfThickness };
        }

        DrawTriangleStrip(ribbon, worldViewProjection, lightDirection, cullMode);
    }

    private void DrawLines(
        ReadOnlySpan<ProGpuDirectXSciChartVertex3D> vertices,
        bool isStrip,
        Matrix4x4 worldViewProjection,
        Vector3? lightDirection)
    {
        ThrowIfDisposed();
        ValidateVertices(vertices, minCount: 2);
        if (!isStrip && vertices.Length % 2 != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(vertices), "SciChart 3D line lists require an even vertex count.");
        }

        ValidateMatrix(worldViewProjection);
        if (HasEmptyClip)
        {
            return;
        }

        var vertexSpan = vertices;
        var light = ResolveLightDirection(lightDirection);
        var vertexBuffer = CreateVertexBuffer(vertexSpan);
        var cameraBuffer = CreateCameraBuffer(worldViewProjection, light);
        var pipeline = GetPipeline(isStrip ? DxPrimitiveTopology.LineStrip : DxPrimitiveTopology.LineList, DxCullMode.None);

        SetDrawState(pipeline, vertexBuffer, cameraBuffer);
        _context.Draw((uint)vertexSpan.Length);
        _lineDraws.Add(new ProGpuDirectXSciChartLine3DDraw(
            CopyVertices(vertexSpan),
            isStrip,
            worldViewProjection,
            light,
            _clipRect));
        _transientResources.Add(vertexBuffer);
        _transientResources.Add(cameraBuffer);
    }

    public void Flush(bool clearRecordedCommands = true)
    {
        ThrowIfDisposed();
        _context.Flush(clearRecordedCommands);
        DisposeTransientResources();
    }

    public byte[] ReadTargetPixels()
    {
        ThrowIfDisposed();
        return RenderTarget.ReadPixels();
    }

    private void SetDrawState(
        ProGpuDirectXGraphicsPipeline pipeline,
        ProGpuDirectXBuffer vertexBuffer,
        ProGpuDirectXBuffer cameraBuffer)
    {
        _context.SetRenderTargets(RenderTarget, DepthStencil);
        _context.SetViewport(new DxViewport(0, 0, RenderTarget.Width, RenderTarget.Height));
        _context.SetScissorRect(_clipRect ?? FullRenderTargetRect);
        _context.SetGraphicsPipeline(pipeline);
        _context.SetVertexBuffer(vertexBuffer);
        _context.SetConstantBuffer(DxShaderStage.Vertex, 0, cameraBuffer);
    }

    private ProGpuDirectXGraphicsPipeline GetPipeline(DxPrimitiveTopology topology, DxCullMode cullMode)
    {
        var key = (topology, cullMode);
        if (_pipelines.TryGetValue(key, out var pipeline))
        {
            return pipeline;
        }

        EnsurePipelineResources();
        pipeline = _device.CreateGraphicsPipeline(new DxGraphicsPipelineDescriptor
        {
            VertexShader = _vertexShader ?? throw new InvalidOperationException("SciChart 3D vertex shader was not initialized."),
            PixelShader = _pixelShader ?? throw new InvalidOperationException("SciChart 3D pixel shader was not initialized."),
            InputLayout = _inputLayout,
            RenderTargetFormat = RenderTarget.Descriptor.Format,
            DepthStencilFormat = DxResourceFormat.D32Float,
            Topology = topology,
            BlendState = new DxBlendStateDescriptor { EnableBlend = true },
            RasterizerState = new DxRasterizerStateDescriptor { CullMode = cullMode, ScissorEnable = true },
            DepthStencilState = new DxDepthStencilStateDescriptor
            {
                DepthEnable = true,
                DepthWriteMask = DxDepthWriteMask.All,
                DepthFunction = DxComparisonFunction.LessEqual
            },
            Label = $"SciChart 3D {topology} {cullMode}"
        });
        _pipelines[key] = pipeline;
        return pipeline;
    }

    private void EnsurePipelineResources()
    {
        _vertexShader ??= _device.CreateShader(new DxShaderDescriptor
        {
            Stage = DxShaderStage.Vertex,
            SourceKind = DxShaderSourceKind.Wgsl,
            Source = SciChart3DVertexShader,
            EntryPoint = "vs_main",
            Label = "SciChart 3D Vertex"
        });
        _pixelShader ??= _device.CreateShader(new DxShaderDescriptor
        {
            Stage = DxShaderStage.Pixel,
            SourceKind = DxShaderSourceKind.Wgsl,
            Source = SciChart3DPixelShader,
            EntryPoint = "fs_main",
            Label = "SciChart 3D Pixel"
        });
        _inputLayout ??= _device.CreateInputLayout(new DxInputLayoutDescriptor
        {
            Label = "SciChart 3D Vertex Layout",
            Elements =
            [
                new DxInputElementDescriptor
                {
                    SemanticName = "POSITION",
                    Format = DxResourceFormat.R32G32B32Float,
                    AlignedByteOffset = 0,
                    ShaderLocation = 0
                },
                new DxInputElementDescriptor
                {
                    SemanticName = "NORMAL",
                    Format = DxResourceFormat.R32G32B32Float,
                    AlignedByteOffset = 12,
                    ShaderLocation = 1
                },
                new DxInputElementDescriptor
                {
                    SemanticName = "COLOR",
                    Format = DxResourceFormat.R32G32B32A32Float,
                    AlignedByteOffset = 24,
                    ShaderLocation = 2
                }
            ]
        });
    }

    private ProGpuDirectXBuffer CreateVertexBuffer(ReadOnlySpan<ProGpuDirectXSciChartVertex3D> vertices)
    {
        var data = new float[checked(vertices.Length * 10)];
        for (var i = 0; i < vertices.Length; i++)
        {
            var vertex = vertices[i];
            var offset = i * 10;
            data[offset] = vertex.X;
            data[offset + 1] = vertex.Y;
            data[offset + 2] = vertex.Z;
            var normal = ResolveNormal(vertex);
            data[offset + 3] = normal.X;
            data[offset + 4] = normal.Y;
            data[offset + 5] = normal.Z;
            WriteColorArgb(data, offset + 6, vertex.ColorArgb);
        }

        var buffer = _device.CreateBuffer(new DxBufferDescriptor
        {
            SizeInBytes = checked((uint)(data.Length * sizeof(float))),
            Usage = DxBufferUsage.Vertex | DxBufferUsage.CopyDestination,
            StrideInBytes = 40,
            Label = $"SciChart3DVertices {vertices.Length}"
        });
        buffer.Write(data);
        return buffer;
    }

    private static ProGpuDirectXSciChartVertex3D[] CopyVertices(ReadOnlySpan<ProGpuDirectXSciChartVertex3D> vertices)
    {
        var copiedVertices = new ProGpuDirectXSciChartVertex3D[vertices.Length];
        vertices.CopyTo(copiedVertices);
        return copiedVertices;
    }

    private static uint[] CopyIndices(ReadOnlySpan<uint> indices)
    {
        var copiedIndices = new uint[indices.Length];
        indices.CopyTo(copiedIndices);
        return copiedIndices;
    }

    private static float[] CopyHeights(ReadOnlySpan<float> heights)
    {
        var copiedHeights = new float[heights.Length];
        heights.CopyTo(copiedHeights);
        return copiedHeights;
    }

    private ProGpuDirectXBuffer CreateIndexBuffer(ReadOnlySpan<uint> indices)
    {
        var buffer = _device.CreateBuffer(new DxBufferDescriptor
        {
            SizeInBytes = checked((uint)(indices.Length * sizeof(uint))),
            Usage = DxBufferUsage.Index | DxBufferUsage.CopyDestination,
            StrideInBytes = sizeof(uint),
            Label = $"SciChart3DIndices {indices.Length}"
        });
        buffer.Write(indices);
        return buffer;
    }

    private ProGpuDirectXBuffer CreateCameraBuffer(Matrix4x4 worldViewProjection, Vector3 lightDirection)
    {
        ReadOnlySpan<float> data =
        [
            worldViewProjection.M11, worldViewProjection.M12, worldViewProjection.M13, worldViewProjection.M14,
            worldViewProjection.M21, worldViewProjection.M22, worldViewProjection.M23, worldViewProjection.M24,
            worldViewProjection.M31, worldViewProjection.M32, worldViewProjection.M33, worldViewProjection.M34,
            worldViewProjection.M41, worldViewProjection.M42, worldViewProjection.M43, worldViewProjection.M44,
            lightDirection.X, lightDirection.Y, lightDirection.Z, 0f
        ];

        var buffer = _device.CreateBuffer(new DxBufferDescriptor
        {
            SizeInBytes = checked((uint)(data.Length * sizeof(float))),
            Usage = DxBufferUsage.Constant | DxBufferUsage.CopyDestination,
            Label = "SciChart3DCamera"
        });
        buffer.Write(data);
        return buffer;
    }

    private static ProGpuDirectXSciChartVertex3D[] CreateSurfaceMeshVertices(
        ReadOnlySpan<float> heights,
        int columns,
        int rows,
        Vector2 xRange,
        Vector2 zRange,
        uint lowColorArgb,
        uint highColorArgb)
    {
        var vertices = new ProGpuDirectXSciChartVertex3D[checked(columns * rows)];
        var minHeight = float.PositiveInfinity;
        var maxHeight = float.NegativeInfinity;
        foreach (var height in heights)
        {
            minHeight = Math.Min(minHeight, height);
            maxHeight = Math.Max(maxHeight, height);
        }

        var heightRange = maxHeight - minHeight;
        for (var row = 0; row < rows; row++)
        {
            for (var column = 0; column < columns; column++)
            {
                var index = checked(row * columns + column);
                var height = heights[index];
                var normal = CreateSurfaceNormal(heights, columns, rows, column, row, xRange, zRange);
                var colorWeight = heightRange <= 0.000001f
                    ? 0.5f
                    : Math.Clamp((height - minHeight) / heightRange, 0f, 1f);
                vertices[index] = new ProGpuDirectXSciChartVertex3D(
                    Lerp(xRange.X, xRange.Y, columns == 1 ? 0f : column / (float)(columns - 1)),
                    height,
                    Lerp(zRange.X, zRange.Y, rows == 1 ? 0f : row / (float)(rows - 1)),
                    normal.X,
                    normal.Y,
                    normal.Z,
                    LerpColorArgb(lowColorArgb, highColorArgb, colorWeight));
            }
        }

        return vertices;
    }

    private static uint[] CreateSurfaceMeshIndices(int columns, int rows)
    {
        var indices = new uint[checked((columns - 1) * (rows - 1) * 6)];
        var target = 0;
        for (var row = 0; row < rows - 1; row++)
        {
            for (var column = 0; column < columns - 1; column++)
            {
                var topLeft = checked((uint)(row * columns + column));
                var topRight = topLeft + 1;
                var bottomLeft = checked((uint)((row + 1) * columns + column));
                var bottomRight = bottomLeft + 1;

                indices[target++] = topLeft;
                indices[target++] = bottomLeft;
                indices[target++] = topRight;
                indices[target++] = topRight;
                indices[target++] = bottomLeft;
                indices[target++] = bottomRight;
            }
        }

        return indices;
    }

    private static Vector3 CreateSurfaceNormal(
        ReadOnlySpan<float> heights,
        int columns,
        int rows,
        int column,
        int row,
        Vector2 xRange,
        Vector2 zRange)
    {
        var left = CreateSurfacePosition(heights, columns, rows, Math.Max(0, column - 1), row, xRange, zRange);
        var right = CreateSurfacePosition(heights, columns, rows, Math.Min(columns - 1, column + 1), row, xRange, zRange);
        var top = CreateSurfacePosition(heights, columns, rows, column, Math.Max(0, row - 1), xRange, zRange);
        var bottom = CreateSurfacePosition(heights, columns, rows, column, Math.Min(rows - 1, row + 1), xRange, zRange);
        var dx = right - left;
        var dz = bottom - top;
        var normal = Vector3.Cross(dz, dx);
        return normal.LengthSquared() <= 0.000001f
            ? new Vector3(0f, 1f, 0f)
            : Vector3.Normalize(normal);
    }

    private static Vector3 CreateSurfacePosition(
        ReadOnlySpan<float> heights,
        int columns,
        int rows,
        int column,
        int row,
        Vector2 xRange,
        Vector2 zRange)
    {
        return new Vector3(
            Lerp(xRange.X, xRange.Y, columns == 1 ? 0f : column / (float)(columns - 1)),
            heights[checked(row * columns + column)],
            Lerp(zRange.X, zRange.Y, rows == 1 ? 0f : row / (float)(rows - 1)));
    }

    private static ProGpuDirectXSciChartVertex3D[] CreateWaterfallVertices(
        ReadOnlySpan<float> heights,
        int columns,
        int rows,
        ProGpuDirectXSciChartWaterfall3DOptions options,
        ProGpuDirectXSciChartDoubleRange xRange,
        ProGpuDirectXSciChartDoubleRange yRange,
        ProGpuDirectXSciChartDoubleRange zRange)
    {
        var normal = Vector3.Normalize(options.Normal);
        var vertices = new ProGpuDirectXSciChartVertex3D[checked(rows * columns * 2)];
        for (var row = 0; row < rows; row++)
        {
            var z = ToSeriesCoordinate(row, zRange, options.NormalizeToUnitCube);
            for (var column = 0; column < columns; column++)
            {
                var sourceIndex = checked((row * columns) + column);
                var height = heights[sourceIndex];
                var colorWeight = GetRangeWeight(height, yRange);
                var color = LerpColorArgb(options.LowColorArgb, options.HighColorArgb, colorWeight);
                var x = ToSeriesCoordinate(column, xRange, options.NormalizeToUnitCube);
                var y = ToSeriesCoordinate(height, yRange, options.NormalizeToUnitCube);
                var target = checked(sourceIndex * 2);
                vertices[target] = new ProGpuDirectXSciChartVertex3D(
                    x,
                    y,
                    z,
                    normal.X,
                    normal.Y,
                    normal.Z,
                    color);
                vertices[target + 1] = new ProGpuDirectXSciChartVertex3D(
                    x,
                    options.BaseY,
                    z,
                    normal.X,
                    normal.Y,
                    normal.Z,
                    color);
            }
        }

        return vertices;
    }

    private static uint[] CreateWaterfallIndices(int columns, int rows)
    {
        var indices = new uint[checked(rows * (columns - 1) * 6)];
        var target = 0;
        for (var row = 0; row < rows; row++)
        {
            for (var column = 0; column < columns - 1; column++)
            {
                var topLeft = checked((uint)(((row * columns) + column) * 2));
                var bottomLeft = topLeft + 1;
                var topRight = topLeft + 2;
                var bottomRight = topRight + 1;

                indices[target++] = topLeft;
                indices[target++] = bottomLeft;
                indices[target++] = topRight;
                indices[target++] = topRight;
                indices[target++] = bottomLeft;
                indices[target++] = bottomRight;
            }
        }

        return indices;
    }

    private static ProGpuDirectXSciChartVertex3D[] CreateXyzSeriesVertices(
        ReadOnlySpan<ProGpuDirectXSciChartXyzPoint3D> points,
        ProGpuDirectXSciChartXyzSeries3DOptions? options,
        int minCount)
    {
        if (points.Length < minCount)
        {
            throw new ArgumentOutOfRangeException(nameof(points), $"SciChart 3D XYZ series draws require at least {minCount} point/points.");
        }

        options ??= new ProGpuDirectXSciChartXyzSeries3DOptions();
        ValidateXyzSeriesOptions(options);
        var xRange = ResolveXyzRange(points, options.XRange, Axis.X);
        var yRange = ResolveXyzRange(points, options.YRange, Axis.Y);
        var zRange = ResolveXyzRange(points, options.ZRange, Axis.Z);
        var normal = Vector3.Normalize(options.Normal);
        var vertices = new ProGpuDirectXSciChartVertex3D[points.Length];

        for (var i = 0; i < points.Length; i++)
        {
            var point = points[i];
            ValidateXyzPoint(point);
            vertices[i] = new ProGpuDirectXSciChartVertex3D(
                ToSeriesCoordinate(point.X, xRange, options.NormalizeToUnitCube),
                ToSeriesCoordinate(point.Y, yRange, options.NormalizeToUnitCube),
                ToSeriesCoordinate(point.Z, zRange, options.NormalizeToUnitCube),
                normal.X,
                normal.Y,
                normal.Z,
                point.ColorArgb == 0 ? options.ColorArgb : point.ColorArgb);
        }

        return vertices;
    }

    private enum Axis
    {
        X,
        Y,
        Z
    }

    private static ProGpuDirectXSciChartDoubleRange ResolveXyzRange(
        ReadOnlySpan<ProGpuDirectXSciChartXyzPoint3D> points,
        ProGpuDirectXSciChartDoubleRange? requestedRange,
        Axis axis)
    {
        if (requestedRange is { } range)
        {
            ValidateXyzRange(range);
            return range;
        }

        var minimum = double.PositiveInfinity;
        var maximum = double.NegativeInfinity;
        foreach (var point in points)
        {
            ValidateXyzPoint(point);
            var value = axis switch
            {
                Axis.X => point.X,
                Axis.Y => point.Y,
                _ => point.Z
            };
            minimum = Math.Min(minimum, value);
            maximum = Math.Max(maximum, value);
        }

        return new ProGpuDirectXSciChartDoubleRange(minimum, maximum);
    }

    private static ProGpuDirectXSciChartDoubleRange ResolveWaterfallRange(
        ProGpuDirectXSciChartDoubleRange? requestedRange,
        int count)
    {
        if (requestedRange is { } range)
        {
            ValidateXyzRange(range);
            return range;
        }

        return count <= 1
            ? new ProGpuDirectXSciChartDoubleRange(0d, 0d)
            : new ProGpuDirectXSciChartDoubleRange(0d, count - 1d);
    }

    private static ProGpuDirectXSciChartDoubleRange ResolveWaterfallHeightRange(
        ReadOnlySpan<float> heights,
        ProGpuDirectXSciChartDoubleRange? requestedRange)
    {
        if (requestedRange is { } range)
        {
            ValidateXyzRange(range);
            return range;
        }

        var minimum = double.PositiveInfinity;
        var maximum = double.NegativeInfinity;
        foreach (var height in heights)
        {
            minimum = Math.Min(minimum, height);
            maximum = Math.Max(maximum, height);
        }

        return new ProGpuDirectXSciChartDoubleRange(minimum, maximum);
    }

    private static float ToSeriesCoordinate(
        double value,
        ProGpuDirectXSciChartDoubleRange range,
        bool normalizeToUnitCube)
    {
        if (!normalizeToUnitCube)
        {
            return ToFiniteFloat(value);
        }

        var span = range.Maximum - range.Minimum;
        if (Math.Abs(span) <= double.Epsilon)
        {
            return 0f;
        }

        return ToFiniteFloat(((value - range.Minimum) / span * 2d) - 1d);
    }

    private static float ToFiniteFloat(double value)
    {
        if (!double.IsFinite(value) ||
            value < -float.MaxValue ||
            value > float.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(value), "SciChart 3D XYZ values must fit in finite single-precision coordinates.");
        }

        return (float)value;
    }

    private static float GetRangeWeight(double value, ProGpuDirectXSciChartDoubleRange range)
    {
        var span = range.Maximum - range.Minimum;
        return Math.Abs(span) <= double.Epsilon
            ? 0.5f
            : Math.Clamp((float)((value - range.Minimum) / span), 0f, 1f);
    }

    private DxRect FullRenderTargetRect =>
        new(0, 0, checked((int)RenderTarget.Width), checked((int)RenderTarget.Height));

    private bool HasEmptyClip => _clipRect is { Width: <= 0 } or { Height: <= 0 };

    private DxRect ClampClipRect(DxRect rect)
    {
        var left = Math.Clamp(rect.X, 0, checked((int)RenderTarget.Width));
        var top = Math.Clamp(rect.Y, 0, checked((int)RenderTarget.Height));
        var right = Math.Clamp(rect.X + rect.Width, 0, checked((int)RenderTarget.Width));
        var bottom = Math.Clamp(rect.Y + rect.Height, 0, checked((int)RenderTarget.Height));
        return new DxRect(left, top, Math.Max(0, right - left), Math.Max(0, bottom - top));
    }

    private static void ValidateVertices(ReadOnlySpan<ProGpuDirectXSciChartVertex3D> vertices, int minCount)
    {
        if (vertices.Length < minCount)
        {
            throw new ArgumentOutOfRangeException(nameof(vertices), $"SciChart 3D draws require at least {minCount} vertex/vertices.");
        }

        foreach (var vertex in vertices)
        {
            if (!float.IsFinite(vertex.X) ||
                !float.IsFinite(vertex.Y) ||
                !float.IsFinite(vertex.Z) ||
                !float.IsFinite(vertex.NormalX) ||
                !float.IsFinite(vertex.NormalY) ||
                !float.IsFinite(vertex.NormalZ))
            {
                throw new ArgumentOutOfRangeException(nameof(vertices), "SciChart 3D vertices must contain finite position and normal components.");
            }
        }
    }

    private static void ValidateIndices(ReadOnlySpan<uint> indices, int vertexCount)
    {
        if (indices.Length < 3 || indices.Length % 3 != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(indices), "SciChart 3D mesh indices must contain complete triangles.");
        }

        foreach (var index in indices)
        {
            if (index >= vertexCount)
            {
                throw new ArgumentOutOfRangeException(nameof(indices), "SciChart 3D mesh indices must reference supplied vertices.");
            }
        }
    }

    private static void ValidateSurfaceMesh(
        ReadOnlySpan<float> heights,
        int columns,
        int rows,
        Vector2? xRange,
        Vector2? zRange)
    {
        if (columns < 2)
        {
            throw new ArgumentOutOfRangeException(nameof(columns), "SciChart 3D surface meshes require at least two columns.");
        }

        if (rows < 2)
        {
            throw new ArgumentOutOfRangeException(nameof(rows), "SciChart 3D surface meshes require at least two rows.");
        }

        var expectedHeightCount = checked(columns * rows);
        if (heights.Length != expectedHeightCount)
        {
            throw new ArgumentOutOfRangeException(nameof(heights), "SciChart 3D surface mesh heights must exactly match columns times rows.");
        }

        foreach (var height in heights)
        {
            if (!float.IsFinite(height))
            {
                throw new ArgumentOutOfRangeException(nameof(heights), "SciChart 3D surface mesh heights must be finite.");
            }
        }

        if (xRange is { } resolvedXRange)
        {
            ValidateSurfaceRange(resolvedXRange, nameof(xRange));
        }

        if (zRange is { } resolvedZRange)
        {
            ValidateSurfaceRange(resolvedZRange, nameof(zRange));
        }
    }

    private static void ValidateSurfaceRange(Vector2 range, string parameterName)
    {
        if (!float.IsFinite(range.X) || !float.IsFinite(range.Y))
        {
            throw new ArgumentOutOfRangeException(parameterName, "SciChart 3D surface mesh ranges must contain finite values.");
        }
    }

    private static void ValidateWaterfallDataSeries(
        ReadOnlySpan<float> heights,
        int columns,
        int rows,
        ProGpuDirectXSciChartWaterfall3DOptions options)
    {
        if (columns < 2)
        {
            throw new ArgumentOutOfRangeException(nameof(columns), "SciChart 3D waterfall series require at least two columns.");
        }

        if (rows < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(rows), "SciChart 3D waterfall series require at least one row.");
        }

        var expectedHeightCount = checked(columns * rows);
        if (heights.Length != expectedHeightCount)
        {
            throw new ArgumentOutOfRangeException(nameof(heights), "SciChart 3D waterfall heights must exactly match columns times rows.");
        }

        foreach (var height in heights)
        {
            if (!float.IsFinite(height))
            {
                throw new ArgumentOutOfRangeException(nameof(heights), "SciChart 3D waterfall heights must be finite.");
            }
        }

        ValidateWaterfallOptions(options);
    }

    private static void ValidateWaterfallOptions(ProGpuDirectXSciChartWaterfall3DOptions options)
    {
        if (options.XRange is { } xRange)
        {
            ValidateXyzRange(xRange);
        }

        if (options.YRange is { } yRange)
        {
            ValidateXyzRange(yRange);
        }

        if (options.ZRange is { } zRange)
        {
            ValidateXyzRange(zRange);
        }

        if (!float.IsFinite(options.BaseY))
        {
            throw new ArgumentOutOfRangeException(nameof(options), "SciChart 3D waterfall base coordinates must be finite.");
        }

        if (!float.IsFinite(options.Normal.X) ||
            !float.IsFinite(options.Normal.Y) ||
            !float.IsFinite(options.Normal.Z) ||
            options.Normal.LengthSquared() <= 0.000001f)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "SciChart 3D waterfall normals must be finite and non-zero.");
        }
    }

    private static void ValidateXyzSeriesOptions(ProGpuDirectXSciChartXyzSeries3DOptions options)
    {
        if (options.XRange is { } xRange)
        {
            ValidateXyzRange(xRange);
        }

        if (options.YRange is { } yRange)
        {
            ValidateXyzRange(yRange);
        }

        if (options.ZRange is { } zRange)
        {
            ValidateXyzRange(zRange);
        }

        if (!float.IsFinite(options.Normal.X) ||
            !float.IsFinite(options.Normal.Y) ||
            !float.IsFinite(options.Normal.Z) ||
            options.Normal.LengthSquared() <= 0.000001f)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "SciChart 3D XYZ series normals must be finite and non-zero.");
        }
    }

    private static void ValidateXyzRange(ProGpuDirectXSciChartDoubleRange range)
    {
        if (!double.IsFinite(range.Minimum) || !double.IsFinite(range.Maximum))
        {
            throw new ArgumentOutOfRangeException(nameof(range), "SciChart 3D XYZ ranges must contain finite values.");
        }
    }

    private static void ValidateXyzPoint(ProGpuDirectXSciChartXyzPoint3D point)
    {
        if (!double.IsFinite(point.X) ||
            !double.IsFinite(point.Y) ||
            !double.IsFinite(point.Z))
        {
            throw new ArgumentOutOfRangeException(nameof(point), "SciChart 3D XYZ points must contain finite coordinates.");
        }
    }

    private static void ValidateMatrix(Matrix4x4 matrix)
    {
        if (!float.IsFinite(matrix.M11) || !float.IsFinite(matrix.M12) || !float.IsFinite(matrix.M13) || !float.IsFinite(matrix.M14) ||
            !float.IsFinite(matrix.M21) || !float.IsFinite(matrix.M22) || !float.IsFinite(matrix.M23) || !float.IsFinite(matrix.M24) ||
            !float.IsFinite(matrix.M31) || !float.IsFinite(matrix.M32) || !float.IsFinite(matrix.M33) || !float.IsFinite(matrix.M34) ||
            !float.IsFinite(matrix.M41) || !float.IsFinite(matrix.M42) || !float.IsFinite(matrix.M43) || !float.IsFinite(matrix.M44))
        {
            throw new ArgumentOutOfRangeException(nameof(matrix), "SciChart 3D camera matrices must contain finite values.");
        }
    }

    private static Vector3 ResolveLightDirection(Vector3? lightDirection)
    {
        var light = lightDirection ?? new Vector3(0f, 0f, 1f);
        if (!float.IsFinite(light.X) || !float.IsFinite(light.Y) || !float.IsFinite(light.Z) || light.LengthSquared() <= 0.000001f)
        {
            throw new ArgumentOutOfRangeException(nameof(lightDirection), "SciChart 3D light directions must be finite non-zero vectors.");
        }

        return Vector3.Normalize(light);
    }

    private static Vector3 ResolveNormal(ProGpuDirectXSciChartVertex3D vertex)
    {
        var normal = new Vector3(vertex.NormalX, vertex.NormalY, vertex.NormalZ);
        return normal.LengthSquared() <= 0.000001f
            ? new Vector3(0f, 0f, 1f)
            : Vector3.Normalize(normal);
    }

    private static void WriteColorArgb(float[] target, int offset, uint colorArgb)
    {
        target[offset] = ((colorArgb >> 16) & 0xFF) / 255f;
        target[offset + 1] = ((colorArgb >> 8) & 0xFF) / 255f;
        target[offset + 2] = (colorArgb & 0xFF) / 255f;
        target[offset + 3] = ((colorArgb >> 24) & 0xFF) / 255f;
    }

    private static float Lerp(float start, float end, float amount) => start + ((end - start) * amount);

    private static uint LerpColorArgb(uint startArgb, uint endArgb, float amount)
    {
        static byte LerpChannel(uint startArgb, uint endArgb, int shift, float amount)
        {
            var start = (int)((startArgb >> shift) & 0xFF);
            var end = (int)((endArgb >> shift) & 0xFF);
            return (byte)Math.Clamp((int)MathF.Round(start + ((end - start) * amount)), 0, 255);
        }

        return ((uint)LerpChannel(startArgb, endArgb, 24, amount) << 24) |
            ((uint)LerpChannel(startArgb, endArgb, 16, amount) << 16) |
            ((uint)LerpChannel(startArgb, endArgb, 8, amount) << 8) |
            LerpChannel(startArgb, endArgb, 0, amount);
    }

    private static string SciChart3DVertexShader => """
struct Camera {
    worldViewProjection: mat4x4<f32>,
    lightDirection: vec4<f32>,
};

@group(0) @binding(0) var<uniform> CameraData: Camera;

struct VertexIn {
    @location(0) position: vec3<f32>,
    @location(1) normal: vec3<f32>,
    @location(2) color: vec4<f32>,
};

struct VertexOut {
    @builtin(position) position: vec4<f32>,
    @location(0) color: vec4<f32>,
    @location(1) normal: vec3<f32>,
};

@vertex
fn vs_main(input: VertexIn) -> VertexOut {
    var output: VertexOut;
    output.position = CameraData.worldViewProjection * vec4<f32>(input.position, 1.0);
    output.color = input.color;
    output.normal = input.normal;
    return output;
}
""";

    private static string SciChart3DPixelShader => """
struct VertexOut {
    @builtin(position) position: vec4<f32>,
    @location(0) color: vec4<f32>,
    @location(1) normal: vec3<f32>,
};

struct Camera {
    worldViewProjection: mat4x4<f32>,
    lightDirection: vec4<f32>,
};

@group(0) @binding(0) var<uniform> CameraData: Camera;

@fragment
fn fs_main(input: VertexOut) -> @location(0) vec4<f32> {
    let normal = normalize(input.normal);
    let light = normalize(CameraData.lightDirection.xyz);
    let diffuse = max(dot(normal, light), 0.0);
    let shaded = input.color.rgb * (0.25 + diffuse * 0.75);
    return vec4<f32>(shaded, input.color.a);
}
""";

    private void DisposeTransientResources()
    {
        foreach (var resource in _transientResources)
        {
            resource.Dispose();
        }

        _transientResources.Clear();
    }

    private void ThrowIfDisposed()
    {
        if (_isDisposed)
        {
            throw new ObjectDisposedException(nameof(ProGpuDirectXSciChartRenderContext3D));
        }
    }

    public void Dispose()
    {
        if (_isDisposed)
        {
            return;
        }

        DisposeTransientResources();
        foreach (var pipeline in _pipelines.Values)
        {
            pipeline.Dispose();
        }

        _vertexShader?.Dispose();
        _pixelShader?.Dispose();
        _context.Dispose();
        DepthStencil.Dispose();
        RenderTarget.Dispose();
        _isDisposed = true;
    }
}
