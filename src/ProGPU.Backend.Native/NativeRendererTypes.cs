using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace ProGPU.Backend.Native;

public enum NativeRendererStatus : uint
{
    Success = 0,
    InvalidArgument = 1,
    Unsupported = 2,
    OutOfMemory = 3,
    WrongThread = 4,
    DeviceLost = 5,
    InternalError = 6
}

public enum NativeRendererTextureFormat : uint
{
    Rgba8Unorm = 1,
    Bgra8Unorm = 2,
    Rgba8UnormSrgb = 3,
    Bgra8UnormSrgb = 4
}

public enum NativeAnalyticPrimitiveKind : uint
{
    Rectangle = 0,
    Ellipse = 1,
    RoundedRectangle = 2
}

public enum NativeGeometryPrimitiveKind : uint
{
    Line = 0,
    Triangle = 1,
    Quadrilateral = 2,
    QuadraticBezier = 3,
    CubicBezier = 4,
    DotGrid = 5
}

public enum NativeStrokeCap : uint
{
    Flat = 0,
    Square = 1,
    Round = 2,
    Triangle = 3
}

public enum NativeStrokeJoin : uint
{
    Miter = 0,
    Bevel = 1,
    Round = 2
}

public enum NativePathSegmentKind : uint
{
    Line = 0,
    Quadratic = 1,
    Cubic = 2,
    Arc = 3
}

public enum NativeFillRule : uint
{
    NonZero = 0,
    EvenOdd = 1
}

public enum NativeImageSampling : uint
{
    Nearest = 0,
    Linear = 1,
    Cubic = 2
}

[Flags]
public enum NativeSceneImageFlags : uint
{
    None = 0,
    ColorMatrix = 1U << 0
}

public enum NativeGroupMaskKind : uint
{
    None = 0,
    Texture = 1,
    RoundedRectangle = 2,
    VectorClipChain = 3
}

public enum NativeClipOperation : uint
{
    Intersect = 0,
    Difference = 1
}

public enum NativeGroupEffectKind : uint
{
    None = 0,
    GaussianBlur = 1,
    DropShadow = 2
}

internal enum NativeMaskTextureFormat : uint
{
    R8Unorm = 1,
    Rgba8Unorm = 2,
    Bgra8Unorm = 3
}

[Flags]
public enum NativeAnalyticPrimitiveFlags : uint
{
    None = 0,
    EdgeAliased = 1U << 0
}

[Flags]
public enum NativeGeometryPrimitiveFlags : uint
{
    None = 0,
    EdgeAliased = 1U << 0,
    Hairline = 1U << 1,
    FixedDeviceStroke = 1U << 2,
    StartCapMask = 3U << 3,
    EndCapMask = 3U << 5
}

[Flags]
public enum NativePolylineFlags : uint
{
    None = 0,
    EdgeAliased = 1U << 0,
    Hairline = 1U << 1,
    FixedDeviceStroke = 1U << 2,
    StartCapMask = 3U << 3,
    EndCapMask = 3U << 5,
    JoinMask = 3U << 7,
    Closed = 1U << 9
}

[Flags]
public enum NativeRendererCapabilities : ulong
{
    None = 0,
    SolidRectBatch = 1UL << 0,
    SharedVectorShader = 1UL << 1,
    ExternalTarget = 1UL << 2,
    IndexedAnalyticBatch = 1UL << 3,
    Affine2D = 1UL << 4,
    IndexedGeometryBatch = 1UL << 5,
    DeviceStrokes = 1UL << 6,
    BezierStrokes = 1UL << 7,
    StrokeCaps = 1UL << 8,
    ConnectedStrokes = 1UL << 9,
    SplineStrokes = 1UL << 10,
    DashedStrokes = 1UL << 11,
    RetainedGeometryReplay = 1UL << 12,
    PathFillAtlas = 1UL << 13,
    PositionedGlyphAtlas = 1UL << 14,
    ResizableAtlases = 1UL << 15,
    RetainedRgbaImage = 1UL << 16,
    ExternalRgbaView = 1UL << 17,
    ExternalImageMask = 1UL << 18,
    ExplicitQueueTimeline = 1UL << 19,
    FrameDrawState = 1UL << 20,
    GroupOpacity = 1UL << 21,
    CommonGroupMask = 1UL << 22,
    AnalyticRoundedGroupMask = 1UL << 23,
    RetainedVectorClipChain = 1UL << 24,
    GroupGaussianBlur = 1UL << 25,
    GroupDropShadow = 1UL << 26,
    BoundedGroupEffectChain = 1UL << 27,
    GroupBlendModes = 1UL << 28,
    SemanticSceneSnapshots = 1UL << 29,
    SemanticSceneRendering = 1UL << 30,
    SemanticRetainedBrushes = 1UL << 31,
    SemanticRetainedTextStyles = 1UL << 32,
    SemanticColorGlyphAtlas = 1UL << 33,
    DeviceLossRecreation = 1UL << 34,
    SemanticGeometryBatch = 1UL << 35
}

public enum NativeSceneResourceKind : uint
{
    AnalyticBatch = 1,
    PathBatch = 2,
    GlyphRun = 3,
    Image = 4,
    State = 5,
    LayerMask = 6,
    EffectChain = 7,
    BrushTable = 8,
    TextStyleTable = 9,
    GeometryBatch = 10
}

public enum NativeSceneTextRenderingMode : uint
{
    Grayscale = 0,
    Aliased = 1,
    ClearType = 2
}

/// <summary>
/// Selects the production WebGPU material program used by a native semantic
/// brush-table record.
/// </summary>
public enum NativeSceneBrushKind : uint
{
    Solid = 0,
    LinearGradient = 1,
    RadialGradient = 2,
    TwoPointConicalGradient = 5,
    SweepGradient = 6,
    PerlinNoise = 7
}

public enum NativeSceneGradientSpread : uint
{
    Pad = 0,
    Reflect = 1,
    Repeat = 2,
    Decal = 3
}

public enum NativeSceneGradientInterpolation : uint
{
    SRgb = 0,
    ScRgb = 1
}

public enum NativeSceneLayerMaskKind : uint
{
    RoundedRectangle = 1,
    CoverageBitmap = 2
}

public enum NativeSceneCommandKind : uint
{
    Save = 1,
    Restore = 2,
    PushLayer = 3,
    PopLayer = 4,
    DrawAnalytic = 16,
    DrawPath = 17,
    DrawGlyphRun = 18,
    DrawImage = 19,
    DrawGeometry = 20
}

[Flags]
public enum NativeSceneRecordFlags : uint
{
    None = 0,
    Required = 1U << 0,
    StyledGlyphs = 1U << 1,
    ColorGlyphBitmaps = 1U << 2
}

[Flags]
public enum NativeSceneStateFlags : uint
{
    None = 0,
    ClipRect = 1U << 0
}

[Flags]
public enum NativeSceneLayerFlags : uint
{
    /// <summary>
    /// Applies no optional layer behavior.
    /// </summary>
    None = 0,

    /// <summary>
    /// Uses the finite logical bounds carried by the layer descriptor.
    /// </summary>
    Bounds = 1U << 0,

    /// <summary>
    /// Initializes the isolated child from the already-rendered parent region.
    /// When the layer also has an effect, the effect filters that captured
    /// backdrop before child commands are rendered.
    /// </summary>
    Backdrop = 1U << 1,

    /// <summary>
    /// Materializes an isolated child even when the remaining layer state could
    /// otherwise be lowered directly into its parent.
    /// </summary>
    ForceIsolation = 1U << 2
}

public enum NativeSceneValidationError : uint
{
    None = 0,
    Header = 1,
    Range = 2,
    Record = 3,
    Id = 4,
    Stack = 5,
    Value = 6,
    Generation = 7,
    Unsupported = 8
}

/// <summary>
/// One exact 32-byte gradient stop consumed by the shared production vector
/// shader. Stops belong to the auxiliary span of a semantic brush table.
/// </summary>
[StructLayout(LayoutKind.Explicit, Size = 32)]
public readonly struct NativeSceneGradientStop
{
    public NativeSceneGradientStop(Vector4 color, float offset)
    {
        Color = color;
        Offset = offset;
        Reserved0 = 0U;
        Reserved1 = 0U;
        Reserved2 = 0U;
    }

    [FieldOffset(0)] public readonly Vector4 Color;
    [FieldOffset(16)] public readonly float Offset;
    [FieldOffset(20)] private readonly uint Reserved0;
    [FieldOffset(24)] private readonly uint Reserved1;
    [FieldOffset(28)] private readonly uint Reserved2;

    internal bool HasCanonicalReservedFields =>
        Reserved0 == 0U && Reserved1 == 0U && Reserved2 == 0U;
}

/// <summary>
/// Exact 256-byte retained material record consumed directly by
/// <c>Vector.wgsl</c> on the native renderer.
/// </summary>
/// <remarks>
/// Gradient stop offsets are local to the matching brush-table resource. The
/// native compiler remaps them into one scene-wide retained GPU page. Factory
/// methods initialize the coordinate transform and the first eight inline
/// stop values consistently with the production managed compositor.
/// </remarks>
[StructLayout(LayoutKind.Explicit, Size = 256)]
public struct NativeSceneBrush
{
    public const uint PerlinTableRecordCount = 512U;
    public const uint MaximumPerlinOctaves = 255U;

    [FieldOffset(0)] public NativeSceneBrushKind Kind;
    [FieldOffset(4)] public float Opacity;
    [FieldOffset(8)] public Vector2 StartPoint;
    [FieldOffset(16)] public Vector2 EndPoint;
    [FieldOffset(24)] public Vector2 Center;
    [FieldOffset(32)] public float Radius;
    [FieldOffset(36)] public uint StopCount;
    [FieldOffset(40)] public float RadiusY;
    [FieldOffset(44)] public NativeSceneGradientSpread Spread;
    [FieldOffset(48)] public NativeSceneGradientInterpolation Interpolation;
    [FieldOffset(52)] public uint StopOffset;
    [FieldOffset(56)] private uint Reserved0;
    [FieldOffset(60)] private uint Reserved1;
    [FieldOffset(64)] public Vector4 Color0;
    [FieldOffset(80)] public Vector4 Color1;
    [FieldOffset(96)] public Vector4 Color2;
    [FieldOffset(112)] public Vector4 Color3;
    [FieldOffset(128)] public Vector4 Color4;
    [FieldOffset(144)] public Vector4 Color5;
    [FieldOffset(160)] public Vector4 Color6;
    [FieldOffset(176)] public Vector4 Color7;
    [FieldOffset(192)] public Vector4 Offsets0;
    [FieldOffset(208)] public Vector4 Offsets1;
    [FieldOffset(224)] public Vector4 CoordinateTransform0;
    [FieldOffset(240)] public Vector4 CoordinateTransform1;

    public static NativeSceneBrush Solid(
        Vector4 color,
        float opacity = 1f)
    {
        var brush = CreateBase(
            NativeSceneBrushKind.Solid,
            opacity,
            Matrix3x2.Identity);
        brush.Color0 = color;
        return brush;
    }

    public static NativeSceneBrush LinearGradient(
        Vector2 startPoint,
        Vector2 endPoint,
        uint stopOffset,
        ReadOnlySpan<NativeSceneGradientStop> stops,
        float opacity = 1f,
        NativeSceneGradientSpread spread = NativeSceneGradientSpread.Pad,
        NativeSceneGradientInterpolation interpolation =
            NativeSceneGradientInterpolation.SRgb,
        Matrix3x2? coordinateTransform = null)
    {
        var brush = CreateGradient(
            NativeSceneBrushKind.LinearGradient,
            stopOffset,
            stops,
            opacity,
            spread,
            interpolation,
            coordinateTransform ?? Matrix3x2.Identity);
        brush.StartPoint = startPoint;
        brush.EndPoint = endPoint;
        return brush;
    }

    public static NativeSceneBrush RadialGradient(
        Vector2 center,
        Vector2 origin,
        float radiusX,
        float radiusY,
        uint stopOffset,
        ReadOnlySpan<NativeSceneGradientStop> stops,
        float opacity = 1f,
        NativeSceneGradientSpread spread = NativeSceneGradientSpread.Pad,
        NativeSceneGradientInterpolation interpolation =
            NativeSceneGradientInterpolation.SRgb,
        Matrix3x2? coordinateTransform = null)
    {
        var brush = CreateGradient(
            NativeSceneBrushKind.RadialGradient,
            stopOffset,
            stops,
            opacity,
            spread,
            interpolation,
            coordinateTransform ?? Matrix3x2.Identity);
        brush.Center = center;
        brush.StartPoint = origin;
        brush.Radius = radiusX;
        brush.RadiusY = radiusY;
        return brush;
    }

    public static NativeSceneBrush TwoPointConicalGradient(
        Vector2 startCenter,
        float startRadius,
        Vector2 endCenter,
        float endRadius,
        uint stopOffset,
        ReadOnlySpan<NativeSceneGradientStop> stops,
        Vector4? outsideColor = null,
        float opacity = 1f,
        NativeSceneGradientSpread spread = NativeSceneGradientSpread.Pad,
        NativeSceneGradientInterpolation interpolation =
            NativeSceneGradientInterpolation.SRgb,
        Matrix3x2? coordinateTransform = null)
    {
        var brush = CreateGradient(
            NativeSceneBrushKind.TwoPointConicalGradient,
            stopOffset,
            stops,
            opacity,
            spread,
            interpolation,
            coordinateTransform ?? Matrix3x2.Identity);
        brush.StartPoint = startCenter;
        brush.Center = endCenter;
        brush.Radius = startRadius;
        brush.RadiusY = endRadius;
        if (outsideColor is { } color)
        {
            brush.Spread = (NativeSceneGradientSpread)(
                (uint)brush.Spread | 0x80000000U);
            brush.Color0 = color;
        }
        return brush;
    }

    public static NativeSceneBrush SweepGradient(
        Vector2 center,
        float startAngle,
        float endAngle,
        uint stopOffset,
        ReadOnlySpan<NativeSceneGradientStop> stops,
        float opacity = 1f,
        NativeSceneGradientSpread spread = NativeSceneGradientSpread.Repeat,
        NativeSceneGradientInterpolation interpolation =
            NativeSceneGradientInterpolation.SRgb,
        Matrix3x2? coordinateTransform = null)
    {
        var brush = CreateGradient(
            NativeSceneBrushKind.SweepGradient,
            stopOffset,
            stops,
            opacity,
            spread,
            interpolation,
            coordinateTransform ?? Matrix3x2.Identity);
        brush.Center = center;
        brush.StartPoint = new Vector2(startAngle, endAngle);
        return brush;
    }

    /// <summary>
    /// Creates a retained procedural Perlin-noise brush. When
    /// <paramref name="useExactTable"/> is true, the owning brush resource
    /// must contain exactly <see cref="PerlinTableRecordCount"/> table records
    /// beginning at <paramref name="tableOffset"/>. The fallback path carries
    /// no table records and remains deterministic for the same parameters.
    /// </summary>
    public static NativeSceneBrush PerlinNoise(
        Vector2 baseFrequency,
        Vector2 stitchPeriod,
        Vector2 tileSize,
        float seed,
        uint octaveCount,
        bool turbulence,
        uint tableOffset = 0U,
        bool useExactTable = false,
        float opacity = 1f,
        Matrix3x2? coordinateTransform = null)
    {
        var brush = CreateBase(
            NativeSceneBrushKind.PerlinNoise,
            opacity,
            coordinateTransform ?? Matrix3x2.Identity);
        brush.StartPoint = baseFrequency;
        brush.EndPoint = stitchPeriod;
        brush.Center = tileSize;
        brush.Radius = seed;
        brush.StopCount = Math.Min(octaveCount, MaximumPerlinOctaves);
        brush.Spread = turbulence
            ? (NativeSceneGradientSpread)1U
            : NativeSceneGradientSpread.Pad;
        brush.Interpolation = useExactTable
            ? NativeSceneGradientInterpolation.ScRgb
            : NativeSceneGradientInterpolation.SRgb;
        brush.StopOffset = useExactTable && brush.StopCount != 0U
            ? tableOffset
            : 0U;
        return brush;
    }

    internal readonly bool HasCanonicalReservedFields =>
        Reserved0 == 0U && Reserved1 == 0U;

    private static NativeSceneBrush CreateBase(
        NativeSceneBrushKind kind,
        float opacity,
        Matrix3x2 coordinateTransform)
    {
        var brush = new NativeSceneBrush
        {
            Kind = kind,
            Opacity = opacity
        };
        brush.SetCoordinateTransform(coordinateTransform);
        return brush;
    }

    private static NativeSceneBrush CreateGradient(
        NativeSceneBrushKind kind,
        uint stopOffset,
        ReadOnlySpan<NativeSceneGradientStop> stops,
        float opacity,
        NativeSceneGradientSpread spread,
        NativeSceneGradientInterpolation interpolation,
        Matrix3x2 coordinateTransform)
    {
        var brush = CreateBase(kind, opacity, coordinateTransform);
        brush.StopOffset = stopOffset;
        brush.StopCount = checked((uint)stops.Length);
        brush.Spread = spread;
        brush.Interpolation = interpolation;
        brush.CopyInlineStops(stops);
        return brush;
    }

    private void CopyInlineStops(ReadOnlySpan<NativeSceneGradientStop> stops)
    {
        Span<Vector4> colors = MemoryMarshal.CreateSpan(ref Color0, 8);
        Span<float> offsets = stackalloc float[8]
        {
            0f, 1f, 1f, 1f, 1f, 1f, 1f, 1f
        };
        int count = Math.Min(stops.Length, 8);
        for (int index = 0; index < count; index++)
        {
            colors[index] = stops[index].Color;
            offsets[index] = stops[index].Offset;
        }
        Offsets0 = new Vector4(offsets[0], offsets[1], offsets[2], offsets[3]);
        Offsets1 = new Vector4(offsets[4], offsets[5], offsets[6], offsets[7]);
    }

    private void SetCoordinateTransform(Matrix3x2 transform)
    {
        CoordinateTransform0 = new Vector4(
            transform.M11,
            transform.M21,
            transform.M31,
            0f);
        CoordinateTransform1 = new Vector4(
            transform.M12,
            transform.M22,
            transform.M32,
            0f);
    }

}

[StructLayout(LayoutKind.Sequential)]
internal readonly struct NativeSceneDrawBrushes
{
    internal NativeSceneDrawBrushes(uint brushResourceIndex, uint brushCount)
    {
        StructSize = (uint)Unsafe.SizeOf<NativeSceneDrawBrushes>();
        BrushResourceIndex = brushResourceIndex;
        BrushCount = brushCount;
        Reserved = 0U;
    }

    internal readonly uint StructSize;
    internal readonly uint BrushResourceIndex;
    internal readonly uint BrushCount;
    private readonly uint Reserved;
}

/// <summary>
/// Exact 32-byte solid text presentation record consumed by
/// <c>Text.wgsl</c>. Shaping and positioned glyph ownership remain separate.
/// </summary>
[StructLayout(LayoutKind.Explicit, Size = 32)]
public readonly struct NativeSceneTextStyle
{
    public NativeSceneTextStyle(
        Vector4 color,
        NativeSceneTextRenderingMode textRenderingMode =
            NativeSceneTextRenderingMode.Grayscale)
    {
        Color = color;
        TextRenderingMode = textRenderingMode;
        Reserved0 = 0U;
        Reserved1 = 0U;
        Reserved2 = 0U;
    }

    [FieldOffset(0)] public readonly Vector4 Color;
    [FieldOffset(16)] public readonly NativeSceneTextRenderingMode TextRenderingMode;
    [FieldOffset(20)] private readonly uint Reserved0;
    [FieldOffset(24)] private readonly uint Reserved1;
    [FieldOffset(28)] private readonly uint Reserved2;

    internal bool HasCanonicalReservedFields =>
        Reserved0 == 0U && Reserved1 == 0U && Reserved2 == 0U;
}

/// <summary>
/// Exact pointer-free metadata for one already-decoded straight-alpha RGBA8
/// color glyph. Pixel offsets are relative to the owning resource's pixel
/// span; native code never parses fonts or compressed image formats.
/// </summary>
[StructLayout(LayoutKind.Explicit, Size = 48)]
public readonly struct NativeSceneColorGlyphBitmap
{
    public NativeSceneColorGlyphBitmap(
        ulong pixelOffset,
        uint width,
        uint height,
        uint rowBytes,
        float bearX,
        float bearY,
        float renderWidth = 0f,
        float renderHeight = 0f)
    {
        PixelOffset = pixelOffset;
        Width = width;
        Height = height;
        RowBytes = rowBytes;
        Reserved0 = 0U;
        BearX = bearX;
        BearY = bearY;
        RenderWidth = renderWidth;
        RenderHeight = renderHeight;
        Reserved1 = 0U;
        Reserved2 = 0U;
    }

    [FieldOffset(0)] public readonly ulong PixelOffset;
    [FieldOffset(8)] public readonly uint Width;
    [FieldOffset(12)] public readonly uint Height;
    [FieldOffset(16)] public readonly uint RowBytes;
    [FieldOffset(20)] private readonly uint Reserved0;
    [FieldOffset(24)] public readonly float BearX;
    [FieldOffset(28)] public readonly float BearY;
    [FieldOffset(32)] public readonly float RenderWidth;
    [FieldOffset(36)] public readonly float RenderHeight;
    [FieldOffset(40)] private readonly uint Reserved1;
    [FieldOffset(44)] private readonly uint Reserved2;

    internal bool HasCanonicalReservedFields =>
        Reserved0 == 0U && Reserved1 == 0U && Reserved2 == 0U;
}

[StructLayout(LayoutKind.Sequential)]
internal readonly struct NativeSceneGlyphDraw
{
    internal NativeSceneGlyphDraw(
        uint styleResourceIndex,
        uint styleIndex,
        uint glyphCount)
    {
        StructSize = (uint)Unsafe.SizeOf<NativeSceneGlyphDraw>();
        StyleResourceIndex = styleResourceIndex;
        StyleIndex = styleIndex;
        GlyphCount = glyphCount;
        Reserved0 = 0U;
        Reserved1 = 0U;
    }

    internal readonly uint StructSize;
    internal readonly uint StyleResourceIndex;
    internal readonly uint StyleIndex;
    internal readonly uint GlyphCount;
    private readonly uint Reserved0;
    private readonly uint Reserved1;
}

/// <summary>
/// Describes one upload-backed image draw stored inside a semantic scene.
/// </summary>
/// <remarks>
/// The matching image resource owns the RGBA8 byte payload. Source and
/// destination rectangles use logical image and target coordinates,
/// respectively. The record is pointer-free and can be written directly to
/// the semantic scene arena.
/// </remarks>
[StructLayout(LayoutKind.Sequential)]
public readonly struct NativeSceneImageDraw
{
    public NativeSceneImageDraw(
        uint imageWidth,
        uint imageHeight,
        uint rowBytes,
        NativeImageSampling sampling,
        NativeImageRect sourceRect,
        NativeImageRect destinationRect,
        Matrix3x2 transform,
        float opacity,
        NativeSceneImageFlags flags = NativeSceneImageFlags.None)
    {
        StructSize = (uint)Unsafe.SizeOf<NativeSceneImageDraw>();
        Flags = flags;
        ImageWidth = imageWidth;
        ImageHeight = imageHeight;
        RowBytes = rowBytes;
        Sampling = sampling;
        SourceRect = sourceRect;
        DestinationRect = destinationRect;
        Transform = transform;
        Opacity = opacity;
        Reserved = 0U;
    }

    public readonly uint StructSize;
    public readonly NativeSceneImageFlags Flags;
    public readonly uint ImageWidth;
    public readonly uint ImageHeight;
    public readonly uint RowBytes;
    public readonly NativeImageSampling Sampling;
    public readonly NativeImageRect SourceRect;
    public readonly NativeImageRect DestinationRect;
    public readonly Matrix3x2 Transform;
    public readonly float Opacity;
    private readonly uint Reserved;
}

/// <summary>
/// Optional pointer-free suffix for a semantic cubic image draw.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public readonly struct NativeSceneImageSamplingOptions
{
    public NativeSceneImageSamplingOptions(float cubicB, float cubicC)
    {
        StructSize = (uint)Unsafe.SizeOf<NativeSceneImageSamplingOptions>();
        Flags = 0U;
        CubicB = cubicB;
        CubicC = cubicC;
    }

    public static NativeSceneImageSamplingOptions Mitchell =>
        new(1f / 3f, 1f / 3f);

    public static NativeSceneImageSamplingOptions CatmullRom => new(0f, 0.5f);

    public readonly uint StructSize;
    private readonly uint Flags;
    public readonly float CubicB;
    public readonly float CubicC;

    internal bool HasCanonicalFields =>
        StructSize == Unsafe.SizeOf<NativeSceneImageSamplingOptions>() &&
        Flags == 0U && float.IsFinite(CubicB) && float.IsFinite(CubicC) &&
        MathF.Abs(CubicB) <= 16f && MathF.Abs(CubicC) <= 16f;
}

/// <summary>
/// Optional pointer-free straight-RGBA affine transform for a semantic image.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public readonly struct NativeSceneImageColorMatrix
{
    public NativeSceneImageColorMatrix(
        Vector4 red,
        Vector4 green,
        Vector4 blue,
        Vector4 alpha,
        Vector4 offset)
    {
        StructSize = (uint)Unsafe.SizeOf<NativeSceneImageColorMatrix>();
        Flags = 0U;
        Red = red;
        Green = green;
        Blue = blue;
        Alpha = alpha;
        Offset = offset;
        Reserved0 = 0U;
        Reserved1 = 0U;
    }

    public static NativeSceneImageColorMatrix Identity => new(
        Vector4.UnitX,
        Vector4.UnitY,
        Vector4.UnitZ,
        Vector4.UnitW,
        Vector4.Zero);

    public readonly uint StructSize;
    private readonly uint Flags;
    public readonly Vector4 Red;
    public readonly Vector4 Green;
    public readonly Vector4 Blue;
    public readonly Vector4 Alpha;
    public readonly Vector4 Offset;
    private readonly uint Reserved0;
    private readonly uint Reserved1;

    internal bool HasCanonicalFields =>
        StructSize == Unsafe.SizeOf<NativeSceneImageColorMatrix>() &&
        Flags == 0U && Reserved0 == 0U && Reserved1 == 0U &&
        IsFiniteAndBounded(Red) && IsFiniteAndBounded(Green) &&
        IsFiniteAndBounded(Blue) && IsFiniteAndBounded(Alpha) &&
        IsFiniteAndBounded(Offset);

    private static bool IsFiniteAndBounded(Vector4 value) =>
        float.IsFinite(value.X) && float.IsFinite(value.Y) &&
        float.IsFinite(value.Z) && float.IsFinite(value.W) &&
        Vector4.Abs(value).X <= 1024f &&
        Vector4.Abs(value).Y <= 1024f &&
        Vector4.Abs(value).Z <= 1024f &&
        Vector4.Abs(value).W <= 1024f;
}

/// <summary>
/// Pointer-free state referenced by semantic save and draw commands.
/// </summary>
/// <remarks>
/// The transform and opacity are absolute. A save command makes its referenced
/// state current until the matching restore; a draw command uses its state for
/// that draw only. Clip coordinates are logical target coordinates.
/// </remarks>
[StructLayout(LayoutKind.Sequential)]
public readonly struct NativeSceneState
{
    public NativeSceneState(
        Matrix3x2 transform,
        float opacity = 1f,
        NativeSceneStateFlags flags = NativeSceneStateFlags.None,
        NativeImageRect clipRect = default)
    {
        StructSize = (uint)Unsafe.SizeOf<NativeSceneState>();
        Flags = flags;
        Transform = transform;
        Opacity = opacity;
        Reserved = 0U;
        ClipRect = clipRect;
        Reserved0 = 0U;
        Reserved1 = 0U;
    }

    public static NativeSceneState Identity => new(Matrix3x2.Identity);

    public readonly uint StructSize;
    public readonly NativeSceneStateFlags Flags;
    public readonly Matrix3x2 Transform;
    public readonly float Opacity;
    private readonly uint Reserved;
    public readonly NativeImageRect ClipRect;
    private readonly uint Reserved0;
    private readonly uint Reserved1;
}

/// <summary>
/// Pointer-free state for one semantic isolated-layer scope.
/// </summary>
/// <remarks>
/// Bounds are logical target coordinates. Mask and effect indices reference
/// preceding typed resources or use <see cref="uint.MaxValue"/> to disable the
/// feature. Revisions are retained identity hints; zero disables the
/// corresponding hint.
/// </remarks>
[StructLayout(LayoutKind.Sequential)]
public readonly struct NativeSceneLayer
{
    public NativeSceneLayer()
        : this(opacity: 1f)
    {
    }

    public NativeSceneLayer(
        float opacity = 1f,
        GpuBlendMode blendMode = GpuBlendMode.SrcOver,
        NativeSceneLayerFlags flags = NativeSceneLayerFlags.None,
        NativeImageRect bounds = default,
        uint maskResourceIndex = uint.MaxValue,
        uint effectResourceIndex = uint.MaxValue,
        ulong contentRevision = 0U,
        ulong compositeRevision = 0U)
    {
        if ((uint)blendMode > (uint)GpuBlendMode.Modulate)
        {
            throw new ArgumentOutOfRangeException(nameof(blendMode));
        }

        StructSize = (uint)Unsafe.SizeOf<NativeSceneLayer>();
        Flags = flags;
        Bounds = bounds;
        Opacity = opacity;
        BlendMode = blendMode;
        MaskResourceIndex = maskResourceIndex;
        EffectResourceIndex = effectResourceIndex;
        ContentRevision = contentRevision;
        CompositeRevision = compositeRevision;
        Reserved0 = 0U;
        Reserved1 = 0U;
    }

    public static NativeSceneLayer Default => new(opacity: 1f);

    public readonly uint StructSize;
    public readonly NativeSceneLayerFlags Flags;
    public readonly NativeImageRect Bounds;
    public readonly float Opacity;
    public readonly GpuBlendMode BlendMode;
    public readonly uint MaskResourceIndex;
    public readonly uint EffectResourceIndex;
    public readonly ulong ContentRevision;
    public readonly ulong CompositeRevision;
    private readonly uint Reserved0;
    private readonly uint Reserved1;

    internal bool HasCanonicalReservedFields => Reserved0 == 0U && Reserved1 == 0U;
}

/// <summary>
/// Pointer-free analytic mask for one semantic layer resource.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public readonly struct NativeSceneLayerMask
{
    public NativeSceneLayerMask(
        NativeImageRect bounds,
        Matrix3x2 transform,
        Vector4 cornerRadiiX,
        Vector4 cornerRadiiY,
        float opacity = 1f)
    {
        StructSize = (uint)Unsafe.SizeOf<NativeSceneLayerMask>();
        Kind = NativeSceneLayerMaskKind.RoundedRectangle;
        Flags = 0U;
        Reserved = 0U;
        Bounds = bounds;
        Transform = transform;
        CornerRadiiX = cornerRadiiX;
        CornerRadiiY = cornerRadiiY;
        Opacity = opacity;
        Reserved0 = 0U;
        Reserved1 = 0U;
        Reserved2 = 0U;
    }

    public readonly uint StructSize;
    public readonly NativeSceneLayerMaskKind Kind;
    public readonly uint Flags;
    private readonly uint Reserved;
    public readonly NativeImageRect Bounds;
    public readonly Matrix3x2 Transform;
    public readonly Vector4 CornerRadiiX;
    public readonly Vector4 CornerRadiiY;
    public readonly float Opacity;
    private readonly uint Reserved0;
    private readonly uint Reserved1;
    private readonly uint Reserved2;

    internal bool HasCanonicalReservedFields =>
        Reserved == 0U && Reserved0 == 0U && Reserved1 == 0U &&
        Reserved2 == 0U;
}

/// <summary>
/// Pointer-free metadata for a retained row-strided R8 layer coverage mask.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public readonly struct NativeSceneLayerCoverageMask
{
    public NativeSceneLayerCoverageMask(
        uint width,
        uint height,
        uint rowBytes,
        NativeImageRect bounds,
        Matrix3x2 transform,
        NativeImageSampling sampling = NativeImageSampling.Linear,
        float opacity = 1f)
    {
        StructSize = (uint)Unsafe.SizeOf<NativeSceneLayerCoverageMask>();
        Kind = NativeSceneLayerMaskKind.CoverageBitmap;
        Flags = 0U;
        Width = width;
        Height = height;
        RowBytes = rowBytes;
        Sampling = sampling;
        Reserved0 = 0U;
        Bounds = bounds;
        Transform = transform;
        Opacity = opacity;
        Reserved1 = 0U;
    }

    public readonly uint StructSize;
    public readonly NativeSceneLayerMaskKind Kind;
    public readonly uint Flags;
    public readonly uint Width;
    public readonly uint Height;
    public readonly uint RowBytes;
    public readonly NativeImageSampling Sampling;
    private readonly uint Reserved0;
    public readonly NativeImageRect Bounds;
    public readonly Matrix3x2 Transform;
    public readonly float Opacity;
    private readonly uint Reserved1;

    internal bool HasCanonicalReservedFields =>
        Reserved0 == 0U && Reserved1 == 0U;
}

/// <summary>
/// Pointer-free descriptor for one semantic layer effect.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public readonly struct NativeSceneEffect
{
    private NativeSceneEffect(
        NativeGroupEffectKind kind,
        float sigmaX,
        float sigmaY,
        Vector2 offset,
        Vector4 color,
        uint revision)
    {
        StructSize = (uint)Unsafe.SizeOf<NativeSceneEffect>();
        Kind = kind;
        Flags = 0U;
        Revision = revision;
        SigmaX = sigmaX;
        SigmaY = sigmaY;
        Reserved = 0U;
        Reserved2 = 0U;
        OffsetX = offset.X;
        OffsetY = offset.Y;
        ColorR = color.X;
        ColorG = color.Y;
        ColorB = color.Z;
        ColorA = color.W;
    }

    public static NativeSceneEffect GaussianBlur(
        float sigmaX,
        float sigmaY,
        uint revision) => new(
            NativeGroupEffectKind.GaussianBlur,
            sigmaX,
            sigmaY,
            default,
            default,
            revision);

    public static NativeSceneEffect DropShadow(
        float sigma,
        Vector2 offset,
        Vector4 color,
        uint revision) => new(
            NativeGroupEffectKind.DropShadow,
            sigma,
            sigma,
            offset,
            color,
            revision);

    public readonly uint StructSize;
    public readonly NativeGroupEffectKind Kind;
    public readonly uint Flags;
    public readonly uint Revision;
    public readonly float SigmaX;
    public readonly float SigmaY;
    private readonly uint Reserved;
    private readonly uint Reserved2;
    public readonly float OffsetX;
    public readonly float OffsetY;
    public readonly float ColorR;
    public readonly float ColorG;
    public readonly float ColorB;
    public readonly float ColorA;

    internal bool HasCanonicalReservedFields =>
        Reserved == 0U && Reserved2 == 0U;
}

/// <summary>
/// Pointer-free header for a bounded semantic layer effect chain.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public readonly struct NativeSceneEffectChain
{
    public NativeSceneEffectChain(uint effectCount, uint revision)
    {
        StructSize = (uint)Unsafe.SizeOf<NativeSceneEffectChain>();
        EffectCount = effectCount;
        Revision = revision;
        Reserved = 0U;
    }

    public readonly uint StructSize;
    public readonly uint EffectCount;
    public readonly uint Revision;
    private readonly uint Reserved;

    internal bool HasCanonicalReservedFields => Reserved == 0U;
}

/// <summary>
/// Fixed-width path record stored in a semantic scene resource arena.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public readonly struct NativeScenePathFill
{
    public NativeScenePathFill(
        ulong segmentOffset,
        ulong segmentCount,
        Vector2 minimum,
        Vector2 maximum,
        Vector4 color,
        Matrix3x2 transform,
        NativeFillRule fillRule = NativeFillRule.NonZero,
        uint sampleGrid = 4)
    {
        SegmentOffset = segmentOffset;
        SegmentCount = segmentCount;
        Minimum = minimum;
        Maximum = maximum;
        Color = color;
        Transform = transform;
        FillRule = fillRule;
        SampleGrid = sampleGrid;
    }

    public readonly ulong SegmentOffset;
    public readonly ulong SegmentCount;
    public readonly Vector2 Minimum;
    public readonly Vector2 Maximum;
    public readonly Vector4 Color;
    public readonly Matrix3x2 Transform;
    public readonly NativeFillRule FillRule;
    public readonly uint SampleGrid;
}

/// <summary>
/// Fixed-width glyph-outline record stored in a semantic scene resource arena.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public readonly struct NativeSceneGlyphOutline
{
    public NativeSceneGlyphOutline(
        ulong segmentOffset,
        ulong segmentCount,
        Vector2 minimum,
        Vector2 maximum,
        float rasterScale,
        float subpixelX = 0f)
    {
        SegmentOffset = segmentOffset;
        SegmentCount = segmentCount;
        Minimum = minimum;
        Maximum = maximum;
        RasterScale = rasterScale;
        SubpixelX = subpixelX;
    }

    public readonly ulong SegmentOffset;
    public readonly ulong SegmentCount;
    public readonly Vector2 Minimum;
    public readonly Vector2 Maximum;
    public readonly float RasterScale;
    public readonly float SubpixelX;
}

[Flags]
public enum NativeDrawStateFlags : uint
{
    None = 0,
    ClipRect = 1U << 0
}

/// <summary>
/// Describes a typed mask applied once to a pooled native frame-family result.
/// </summary>
/// <remarks>
/// A texture mask remains zero-copy and samples its red channel. Keep its
/// texture alive until another mask view replaces it or the compositor is
/// disposed. Rounded-rectangle bounds and radii use local coordinates while
/// Transform maps that local space to logical target coordinates. Vector clip
/// chains remain immutable and pinned so their typed segment payload crosses
/// the C ABI without a per-frame copy or pin allocation.
/// </remarks>
public readonly struct NativeGroupMask
{
    private NativeGroupMask(
        NativeGroupMaskKind kind,
        GpuTexture? texture,
        NativeImageRect destinationRect,
        NativeImageSampling sampling,
        uint revision,
        NativeImageRect bounds,
        Matrix3x2 transform,
        Vector4 cornerRadiiX,
        Vector4 cornerRadiiY,
        float opacity,
        NativeClipChain? clipChain)
    {
        Kind = kind;
        Texture = texture;
        DestinationRect = destinationRect;
        Sampling = sampling;
        Revision = revision;
        Bounds = bounds;
        Transform = transform;
        CornerRadiiX = cornerRadiiX;
        CornerRadiiY = cornerRadiiY;
        Opacity = opacity;
        ClipChain = clipChain;
    }

    public static NativeGroupMask TextureMask(
        GpuTexture texture,
        NativeImageRect destinationRect,
        NativeImageSampling sampling,
        uint revision) => new(
            NativeGroupMaskKind.Texture,
            texture,
            destinationRect,
            sampling,
            revision,
            default,
            Matrix3x2.Identity,
            default,
            default,
            1f,
            null);

    public static NativeGroupMask RoundedRectangle(
        NativeImageRect bounds,
        Matrix3x2 transform,
        Vector4 cornerRadiiX,
        Vector4 cornerRadiiY,
        float opacity = 1f) => new(
            NativeGroupMaskKind.RoundedRectangle,
            null,
            default,
            NativeImageSampling.Linear,
            0U,
            bounds,
            transform,
            cornerRadiiX,
            cornerRadiiY,
            opacity,
            null);

    public static NativeGroupMask VectorClipChain(
        NativeClipChain clipChain,
        uint revision)
    {
        ArgumentNullException.ThrowIfNull(clipChain);
        if (revision == 0U)
            throw new ArgumentOutOfRangeException(nameof(revision));

        return new(
            NativeGroupMaskKind.VectorClipChain,
            null,
            default,
            NativeImageSampling.Linear,
            revision,
            default,
            Matrix3x2.Identity,
            default,
            default,
            1f,
            clipChain);
    }

    public NativeGroupMaskKind Kind { get; }
    public GpuTexture? Texture { get; }
    public NativeImageRect DestinationRect { get; }
    public NativeImageSampling Sampling { get; }
    public uint Revision { get; }
    public NativeImageRect Bounds { get; }
    public Matrix3x2 Transform { get; }
    public Vector4 CornerRadiiX { get; }
    public Vector4 CornerRadiiY { get; }
    public float Opacity { get; }
    public NativeClipChain? ClipChain { get; }
    public bool IsEnabled => Kind != NativeGroupMaskKind.None;
}

/// <summary>
/// Describes a retained GPU effect applied once to a pooled native frame.
/// </summary>
public readonly struct NativeGroupEffect
{
    private NativeGroupEffect(
        NativeGroupEffectKind kind,
        float sigmaX,
        float sigmaY,
        Vector2 offset,
        Vector4 color,
        uint revision)
    {
        Kind = kind;
        SigmaX = sigmaX;
        SigmaY = sigmaY;
        Offset = offset;
        Color = color;
        Revision = revision;
    }

    public static NativeGroupEffect GaussianBlur(
        float sigma,
        uint revision) => GaussianBlur(sigma, sigma, revision);

    public static NativeGroupEffect GaussianBlur(
        float sigmaX,
        float sigmaY,
        uint revision) => new(
            NativeGroupEffectKind.GaussianBlur,
            sigmaX,
            sigmaY,
            default,
            default,
            revision);

    public static NativeGroupEffect DropShadow(
        float blurSigma,
        Vector2 offset,
        Vector4 color,
        uint revision) => new(
            NativeGroupEffectKind.DropShadow,
            blurSigma,
            blurSigma,
            offset,
            color,
            revision);

    public NativeGroupEffectKind Kind { get; }
    public float SigmaX { get; }
    public float SigmaY { get; }
    public Vector2 Offset { get; }
    public Vector4 Color { get; }
    public uint Revision { get; }
    public bool IsEnabled => Kind != NativeGroupEffectKind.None;
}

/// <summary>
/// Owns an immutable bounded linear chain of retained native GPU effects.
/// </summary>
public sealed class NativeGroupEffectChain
{
    public const int MaximumEffectCount = 8;
    private readonly NativeGroupEffect[] _effects;

    public NativeGroupEffectChain(
        ReadOnlySpan<NativeGroupEffect> effects,
        uint revision)
    {
        if (effects.IsEmpty || effects.Length > MaximumEffectCount)
        {
            throw new ArgumentOutOfRangeException(
                nameof(effects),
                $"A native effect chain requires between 1 and {MaximumEffectCount} effects.");
        }
        if (revision == 0U)
        {
            throw new ArgumentOutOfRangeException(
                nameof(revision),
                "A native effect-chain revision must be nonzero.");
        }

        _effects = effects.ToArray();
        Revision = revision;
    }

    public uint Revision { get; }

    public int Count => _effects.Length;

    public ReadOnlySpan<NativeGroupEffect> Effects => _effects;
}

/// <summary>
/// Identifies one submitted native WebGPU command buffer on its owning queue.
/// </summary>
/// <remarks>
/// External-image producers keep their texture lease alive until this token
/// completes. The value is backend-local and must not cross compositor instances.
/// </remarks>
public readonly struct NativeSubmissionToken : IEquatable<NativeSubmissionToken>
{
    internal NativeSubmissionToken(ulong value, nint owner)
    {
        Value = value;
        Owner = owner;
    }

    public ulong Value { get; }

    internal nint Owner { get; }

    public bool IsValid => Value != 0 && Owner != 0;

    public bool Equals(NativeSubmissionToken other) =>
        Value == other.Value && Owner == other.Owner;

    public override bool Equals(object? obj) =>
        obj is NativeSubmissionToken other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(Value, Owner);

    public static bool operator ==(
        NativeSubmissionToken left,
        NativeSubmissionToken right) => left.Equals(right);

    public static bool operator !=(
        NativeSubmissionToken left,
        NativeSubmissionToken right) => !left.Equals(right);
}

[StructLayout(LayoutKind.Sequential)]
public readonly struct NativeImageRect
{
    public NativeImageRect(float x, float y, float width, float height)
    {
        X = x;
        Y = y;
        Width = width;
        Height = height;
    }

    public readonly float X;
    public readonly float Y;
    public readonly float Width;
    public readonly float Height;
}

/// <summary>
/// Describes allocation-free state applied to one native draw submission.
/// </summary>
/// <remarks>
/// Opacity multiplies each primitive independently. GroupOpacity composites
/// the whole family through a pooled transparent layer. A nonzero
/// GroupRevision permits the layer pixels to be reused until content changes.
/// </remarks>
public readonly struct NativeDrawState
{
    public NativeDrawState(float opacity)
        : this(opacity, default, NativeDrawStateFlags.None)
    {
    }

    public NativeDrawState(float opacity, NativeImageRect clipRect)
        : this(opacity, clipRect, NativeDrawStateFlags.ClipRect)
    {
    }

    public NativeDrawState(
        float opacity,
        NativeImageRect clipRect,
        NativeDrawStateFlags flags)
        : this(opacity, clipRect, flags, 1f, 0U)
    {
    }

    public NativeDrawState(
        float opacity,
        NativeImageRect clipRect,
        NativeDrawStateFlags flags,
        float groupOpacity,
        uint groupRevision)
        : this(
            opacity,
            clipRect,
            flags,
            groupOpacity,
            groupRevision,
            default,
            default(NativeGroupEffect),
            null,
            GpuBlendMode.SrcOver)
    {
    }

    public NativeDrawState(
        float opacity,
        NativeImageRect clipRect,
        NativeDrawStateFlags flags,
        float groupOpacity,
        uint groupRevision,
        GpuBlendMode groupBlendMode)
        : this(
            opacity,
            clipRect,
            flags,
            groupOpacity,
            groupRevision,
            default,
            default(NativeGroupEffect),
            null,
            groupBlendMode)
    {
    }

    public NativeDrawState(
        float opacity,
        NativeImageRect clipRect,
        NativeDrawStateFlags flags,
        float groupOpacity,
        uint groupRevision,
        NativeGroupMask groupMask)
        : this(
            opacity,
            clipRect,
            flags,
            groupOpacity,
            groupRevision,
            groupMask,
            default(NativeGroupEffect),
            null,
            GpuBlendMode.SrcOver)
    {
    }

    public NativeDrawState(
        float opacity,
        NativeImageRect clipRect,
        NativeDrawStateFlags flags,
        float groupOpacity,
        uint groupRevision,
        NativeGroupMask groupMask,
        NativeGroupEffect groupEffect)
        : this(
            opacity,
            clipRect,
            flags,
            groupOpacity,
            groupRevision,
            groupMask,
            groupEffect,
            null,
            GpuBlendMode.SrcOver)
    {
    }

    public NativeDrawState(
        float opacity,
        NativeImageRect clipRect,
        NativeDrawStateFlags flags,
        float groupOpacity,
        uint groupRevision,
        NativeGroupMask groupMask,
        NativeGroupEffectChain groupEffectChain)
        : this(
            opacity,
            clipRect,
            flags,
            groupOpacity,
            groupRevision,
            groupMask,
            default,
            groupEffectChain,
            GpuBlendMode.SrcOver)
    {
    }

    private NativeDrawState(
        float opacity,
        NativeImageRect clipRect,
        NativeDrawStateFlags flags,
        float groupOpacity,
        uint groupRevision,
        NativeGroupMask groupMask,
        NativeGroupEffect groupEffect,
        NativeGroupEffectChain? groupEffectChain,
        GpuBlendMode groupBlendMode)
    {
        if ((uint)groupBlendMode > (uint)GpuBlendMode.Modulate)
        {
            throw new ArgumentOutOfRangeException(nameof(groupBlendMode));
        }
        Opacity = opacity;
        ClipRect = clipRect;
        Flags = flags;
        GroupOpacity = groupOpacity;
        GroupRevision = groupRevision;
        GroupMask = groupMask;
        GroupEffect = groupEffect;
        GroupEffectChain = groupEffectChain;
        GroupBlendMode = groupBlendMode;
        _initialized = 1;
    }

    public static NativeDrawState Default => new(1f);

    public readonly float Opacity;
    public readonly NativeImageRect ClipRect;
    public readonly NativeDrawStateFlags Flags;
    public readonly float GroupOpacity;
    public readonly uint GroupRevision;
    public readonly NativeGroupMask GroupMask;
    public readonly NativeGroupEffect GroupEffect;
    public readonly NativeGroupEffectChain? GroupEffectChain;
    public readonly GpuBlendMode GroupBlendMode;

    public NativeDrawState WithGroupBlendMode(GpuBlendMode groupBlendMode) =>
        new(
            EffectiveOpacity,
            ClipRect,
            Flags,
            EffectiveGroupOpacity,
            GroupRevision,
            GroupMask,
            GroupEffect,
            GroupEffectChain,
            groupBlendMode);

    private readonly byte _initialized;

    internal float EffectiveOpacity => _initialized == 0 ? 1f : Opacity;

    internal float EffectiveGroupOpacity =>
        _initialized == 0 ? 1f : GroupOpacity;

    internal GpuBlendMode EffectiveGroupBlendMode =>
        _initialized == 0 ? GpuBlendMode.SrcOver : GroupBlendMode;
}

[StructLayout(LayoutKind.Sequential)]
public readonly struct NativeSolidRectangle
{
    public NativeSolidRectangle(
        float x,
        float y,
        float width,
        float height,
        Vector4 color)
    {
        X = x;
        Y = y;
        Width = width;
        Height = height;
        Color = color;
    }

    public readonly float X;
    public readonly float Y;
    public readonly float Width;
    public readonly float Height;
    public readonly Vector4 Color;
}

[StructLayout(LayoutKind.Sequential)]
public readonly struct NativeAnalyticPrimitive
{
    public NativeAnalyticPrimitive(
        NativeAnalyticPrimitiveKind kind,
        float x,
        float y,
        float width,
        float height,
        Vector4 color,
        Matrix3x2 transform,
        float cornerRadius = 0f,
        float strokeThickness = 0f,
        NativeAnalyticPrimitiveFlags flags = NativeAnalyticPrimitiveFlags.None)
    {
        Kind = kind;
        Flags = flags;
        X = x;
        Y = y;
        Width = width;
        Height = height;
        CornerRadius = cornerRadius;
        StrokeThickness = strokeThickness;
        Color = color;
        Transform = transform;
    }

    public readonly NativeAnalyticPrimitiveKind Kind;
    public readonly NativeAnalyticPrimitiveFlags Flags;
    public readonly float X;
    public readonly float Y;
    public readonly float Width;
    public readonly float Height;
    public readonly float CornerRadius;
    public readonly float StrokeThickness;
    public readonly Vector4 Color;
    public readonly Matrix3x2 Transform;
}

[StructLayout(LayoutKind.Sequential)]
public readonly struct NativeGeometryPrimitive
{
    public NativeGeometryPrimitive(
        NativeGeometryPrimitiveKind kind,
        Vector2 p0,
        Vector2 p1,
        Vector4 color,
        Matrix3x2 transform,
        Vector2 p2 = default,
        Vector2 p3 = default,
        float strokeThickness = 0f,
        NativeGeometryPrimitiveFlags flags = NativeGeometryPrimitiveFlags.None,
        NativeStrokeCap startCap = NativeStrokeCap.Flat,
        NativeStrokeCap endCap = NativeStrokeCap.Flat)
    {
        if ((uint)startCap > (uint)NativeStrokeCap.Triangle)
            throw new ArgumentOutOfRangeException(nameof(startCap));
        if ((uint)endCap > (uint)NativeStrokeCap.Triangle)
            throw new ArgumentOutOfRangeException(nameof(endCap));
        Kind = kind;
        Flags = (flags & ~(
                NativeGeometryPrimitiveFlags.StartCapMask |
                NativeGeometryPrimitiveFlags.EndCapMask)) |
            (NativeGeometryPrimitiveFlags)((uint)startCap << 3) |
            (NativeGeometryPrimitiveFlags)((uint)endCap << 5);
        P0 = p0;
        P1 = p1;
        P2 = p2;
        P3 = p3;
        StrokeThickness = strokeThickness;
        Reserved = 0f;
        Color = color;
        Transform = transform;
    }

    public readonly NativeGeometryPrimitiveKind Kind;
    public readonly NativeGeometryPrimitiveFlags Flags;
    public readonly Vector2 P0;
    public readonly Vector2 P1;
    public readonly Vector2 P2;
    public readonly Vector2 P3;
    public readonly float StrokeThickness;
    private readonly float Reserved;
    public readonly Vector4 Color;
    public readonly Matrix3x2 Transform;

    public NativeStrokeCap StartCap =>
        (NativeStrokeCap)(((uint)Flags >> 3) & 3U);

    public NativeStrokeCap EndCap =>
        (NativeStrokeCap)(((uint)Flags >> 5) & 3U);
}

[StructLayout(LayoutKind.Sequential)]
public readonly struct NativePolyline
{
    public NativePolyline(
        nuint pointOffset,
        nuint pointCount,
        Vector4 color,
        Matrix3x2 transform,
        float strokeThickness,
        float miterLimit = 10f,
        NativePolylineFlags flags = NativePolylineFlags.None,
        NativeStrokeCap startCap = NativeStrokeCap.Flat,
        NativeStrokeCap endCap = NativeStrokeCap.Flat,
        NativeStrokeJoin lineJoin = NativeStrokeJoin.Miter,
        bool isClosed = false,
        uint dashStyle = 0)
    {
        if ((uint)startCap > (uint)NativeStrokeCap.Triangle)
            throw new ArgumentOutOfRangeException(nameof(startCap));
        if ((uint)endCap > (uint)NativeStrokeCap.Triangle)
            throw new ArgumentOutOfRangeException(nameof(endCap));
        if ((uint)lineJoin > (uint)NativeStrokeJoin.Round)
            throw new ArgumentOutOfRangeException(nameof(lineJoin));

        PointOffset = pointOffset;
        PointCount = pointCount;
        Color = color;
        Transform = transform;
        StrokeThickness = strokeThickness;
        MiterLimit = float.IsFinite(miterLimit) && miterLimit >= 1f
            ? miterLimit
            : 1f;
        Flags = (flags & ~(
                NativePolylineFlags.StartCapMask |
                NativePolylineFlags.EndCapMask |
                NativePolylineFlags.JoinMask |
                NativePolylineFlags.Closed)) |
            (NativePolylineFlags)((uint)startCap << 3) |
            (NativePolylineFlags)((uint)endCap << 5) |
            (NativePolylineFlags)((uint)lineJoin << 7) |
            (isClosed ? NativePolylineFlags.Closed : 0);
        DashStyle = dashStyle;
    }

    public readonly nuint PointOffset;
    public readonly nuint PointCount;
    public readonly Vector4 Color;
    public readonly Matrix3x2 Transform;
    public readonly float StrokeThickness;
    public readonly float MiterLimit;
    public readonly NativePolylineFlags Flags;
    public readonly uint DashStyle;

    public NativeStrokeCap StartCap =>
        (NativeStrokeCap)(((uint)Flags >> 3) & 3U);

    public NativeStrokeCap EndCap =>
        (NativeStrokeCap)(((uint)Flags >> 5) & 3U);

    public NativeStrokeJoin LineJoin =>
        (NativeStrokeJoin)(((uint)Flags >> 7) & 3U);

    public bool IsClosed => (Flags & NativePolylineFlags.Closed) != 0;
}

[StructLayout(LayoutKind.Sequential)]
public readonly struct NativeDashStyle
{
    public NativeDashStyle(
        nuint intervalOffset,
        nuint intervalCount,
        double offset,
        NativeStrokeCap cap = NativeStrokeCap.Flat)
    {
        if ((uint)cap > (uint)NativeStrokeCap.Triangle)
            throw new ArgumentOutOfRangeException(nameof(cap));

        IntervalOffset = intervalOffset;
        IntervalCount = intervalCount;
        Offset = offset;
        Cap = cap;
        Reserved = 0U;
    }

    public readonly nuint IntervalOffset;
    public readonly nuint IntervalCount;
    public readonly double Offset;
    public readonly NativeStrokeCap Cap;
    private readonly uint Reserved;
}

[StructLayout(LayoutKind.Sequential)]
public readonly struct NativeSpline
{
    public NativeSpline(
        NativePolyline stroke,
        nuint knotOffset,
        nuint knotCount,
        uint degree,
        nuint weightOffset = 0,
        nuint weightCount = 0)
    {
        Stroke = stroke;
        KnotOffset = knotOffset;
        KnotCount = knotCount;
        WeightOffset = weightOffset;
        WeightCount = weightCount;
        Degree = degree;
        Reserved = 0U;
    }

    public readonly NativePolyline Stroke;
    public readonly nuint KnotOffset;
    public readonly nuint KnotCount;
    public readonly nuint WeightOffset;
    public readonly nuint WeightCount;
    public readonly uint Degree;
    private readonly uint Reserved;
}

[StructLayout(LayoutKind.Sequential)]
public readonly struct NativePathSegment
{
    public NativePathSegment(
        NativePathSegmentKind kind,
        Vector2 p0,
        Vector2 p1,
        Vector2 p2 = default,
        Vector2 p3 = default,
        uint pad0 = 0,
        uint pad1 = 0,
        uint pad2 = 0)
    {
        Kind = kind;
        P0 = p0;
        P1 = p1;
        P2 = p2;
        P3 = p3;
        Pad0 = pad0;
        Pad1 = pad1;
        Pad2 = pad2;
    }

    public readonly Vector2 P0;
    public readonly Vector2 P1;
    public readonly Vector2 P2;
    public readonly Vector2 P3;
    public readonly NativePathSegmentKind Kind;
    public readonly uint Pad0;
    public readonly uint Pad1;
    public readonly uint Pad2;
}

[StructLayout(LayoutKind.Sequential)]
public readonly struct NativePathFill
{
    public NativePathFill(
        nuint segmentOffset,
        nuint segmentCount,
        Vector2 minimum,
        Vector2 maximum,
        Vector4 color,
        Matrix3x2 transform,
        NativeFillRule fillRule = NativeFillRule.NonZero,
        uint sampleGrid = 4)
    {
        SegmentOffset = segmentOffset;
        SegmentCount = segmentCount;
        Minimum = minimum;
        Maximum = maximum;
        Color = color;
        Transform = transform;
        FillRule = fillRule;
        SampleGrid = sampleGrid;
    }

    public readonly nuint SegmentOffset;
    public readonly nuint SegmentCount;
    public readonly Vector2 Minimum;
    public readonly Vector2 Maximum;
    public readonly Vector4 Color;
    public readonly Matrix3x2 Transform;
    public readonly NativeFillRule FillRule;
    public readonly uint SampleGrid;
}

[StructLayout(LayoutKind.Sequential)]
public readonly struct NativeClipPath
{
    public NativeClipPath(
        nuint segmentOffset,
        nuint segmentCount,
        Vector2 minimum,
        Vector2 maximum,
        Matrix3x2 transform,
        NativeClipOperation operation = NativeClipOperation.Intersect,
        NativeFillRule fillRule = NativeFillRule.NonZero,
        uint sampleGrid = 4)
    {
        SegmentOffset = segmentOffset;
        SegmentCount = segmentCount;
        Minimum = minimum;
        Maximum = maximum;
        Transform = transform;
        FillRule = fillRule;
        SampleGrid = sampleGrid;
        Operation = operation;
        Reserved = 0U;
    }

    public readonly nuint SegmentOffset;
    public readonly nuint SegmentCount;
    public readonly Vector2 Minimum;
    public readonly Vector2 Maximum;
    public readonly Matrix3x2 Transform;
    public readonly NativeFillRule FillRule;
    public readonly uint SampleGrid;
    public readonly NativeClipOperation Operation;
    private readonly uint Reserved;
}

/// <summary>
/// Owns one immutable, allocation-free-on-replay native vector clip payload.
/// </summary>
/// <remarks>
/// Construction copies the two arenas once into pinned-object-heap arrays.
/// Rendering then borrows stable typed pointers for the duration of one native
/// call; the C++ engine retains only the resulting GPU coverage and revision.
/// </remarks>
public sealed unsafe class NativeClipChain
{
    private readonly NativeClipPath[] _paths;
    private readonly NativePathSegment[] _segments;

    public NativeClipChain(
        ReadOnlySpan<NativeClipPath> paths,
        ReadOnlySpan<NativePathSegment> segments)
    {
        if (paths.IsEmpty)
            throw new ArgumentException("A native clip chain requires at least one path.", nameof(paths));
        if (segments.IsEmpty)
            throw new ArgumentException("A native clip chain requires path segments.", nameof(segments));

        nuint segmentLength = (nuint)segments.Length;
        for (int index = 0; index < paths.Length; index++)
        {
            NativeClipPath path = paths[index];
            if (path.SegmentCount == 0U ||
                path.SegmentOffset > segmentLength ||
                path.SegmentCount > segmentLength - path.SegmentOffset ||
                !IsFinite(path.Minimum) ||
                !IsFinite(path.Maximum) ||
                path.Maximum.X <= path.Minimum.X ||
                path.Maximum.Y <= path.Minimum.Y ||
                !IsFinite(path.Transform) ||
                MathF.Abs(path.Transform.GetDeterminant()) <= 0.000001f ||
                path.FillRule > NativeFillRule.EvenOdd ||
                path.SampleGrid is not (4U or 8U) ||
                path.Operation > NativeClipOperation.Difference)
            {
                throw new ArgumentException(
                    $"Clip path {index} is invalid or references segments outside the retained arena.",
                    nameof(paths));
            }
        }
        for (int index = 0; index < segments.Length; index++)
        {
            NativePathSegment segment = segments[index];
            if (segment.Kind > NativePathSegmentKind.Arc ||
                !IsFinite(segment.P0) ||
                !IsFinite(segment.P1) ||
                !IsFinite(segment.P2) ||
                !IsFinite(segment.P3) ||
                (segment.Kind == NativePathSegmentKind.Arc &&
                 (segment.P3.X <= 0f || segment.P3.Y <= 0f ||
                  !float.IsFinite(BitConverter.Int32BitsToSingle(
                      unchecked((int)segment.Pad0))) ||
                  !float.IsFinite(BitConverter.Int32BitsToSingle(
                      unchecked((int)segment.Pad1))) ||
                  !float.IsFinite(BitConverter.Int32BitsToSingle(
                      unchecked((int)segment.Pad2))))) ||
                (segment.Kind != NativePathSegmentKind.Arc &&
                 (segment.Pad0 != 0U || segment.Pad1 != 0U ||
                  segment.Pad2 != 0U)))
            {
                throw new ArgumentException(
                    $"Clip segment {index} is invalid.",
                    nameof(segments));
            }
        }

        _paths = GC.AllocateUninitializedArray<NativeClipPath>(
            paths.Length,
            pinned: true);
        _segments = GC.AllocateUninitializedArray<NativePathSegment>(
            segments.Length,
            pinned: true);
        paths.CopyTo(_paths);
        segments.CopyTo(_segments);
    }

    public int PathCount => _paths.Length;
    public int SegmentCount => _segments.Length;

    internal NativeClipPath* Paths =>
        (NativeClipPath*)Unsafe.AsPointer(
            ref MemoryMarshal.GetArrayDataReference(_paths));

    internal NativePathSegment* Segments =>
        (NativePathSegment*)Unsafe.AsPointer(
            ref MemoryMarshal.GetArrayDataReference(_segments));

    private static bool IsFinite(Vector2 value) =>
        float.IsFinite(value.X) && float.IsFinite(value.Y);

    private static bool IsFinite(Matrix3x2 value) =>
        float.IsFinite(value.M11) && float.IsFinite(value.M12) &&
        float.IsFinite(value.M21) && float.IsFinite(value.M22) &&
        float.IsFinite(value.M31) && float.IsFinite(value.M32);
}

[StructLayout(LayoutKind.Sequential)]
public readonly struct NativeGlyphOutline
{
    public NativeGlyphOutline(
        nuint segmentOffset,
        nuint segmentCount,
        Vector2 minimum,
        Vector2 maximum,
        float rasterScale,
        float subpixelX = 0f)
    {
        SegmentOffset = segmentOffset;
        SegmentCount = segmentCount;
        Minimum = minimum;
        Maximum = maximum;
        RasterScale = rasterScale;
        SubpixelX = subpixelX;
    }

    public readonly nuint SegmentOffset;
    public readonly nuint SegmentCount;
    public readonly Vector2 Minimum;
    public readonly Vector2 Maximum;
    public readonly float RasterScale;
    public readonly float SubpixelX;
}

[StructLayout(LayoutKind.Sequential)]
public readonly struct NativePositionedGlyph
{
    public NativePositionedGlyph(
        uint outlineIndex,
        Vector2 position,
        Vector2 basisX,
        Vector2 basisY,
        Vector4 color,
        float atlasToLogicalScale = 1f,
        float boldOffset = 0f,
        float italicSkew = 0f)
    {
        OutlineIndex = outlineIndex;
        Reserved = 0U;
        Position = position;
        BasisX = basisX;
        BasisY = basisY;
        Color = color;
        AtlasToLogicalScale = atlasToLogicalScale;
        BoldOffset = boldOffset;
        ItalicSkew = italicSkew;
        Reserved2 = 0f;
    }

    public readonly uint OutlineIndex;
    private readonly uint Reserved;
    public readonly Vector2 Position;
    public readonly Vector2 BasisX;
    public readonly Vector2 BasisY;
    public readonly Vector4 Color;
    public readonly float AtlasToLogicalScale;
    public readonly float BoldOffset;
    public readonly float ItalicSkew;
    private readonly float Reserved2;
}

public readonly record struct NativeFrameMetrics(
    uint DrawCallCount,
    uint VertexCount,
    ulong VertexUploadBytes,
    ulong UniformUploadBytes,
    ulong SubmissionCount);

public readonly record struct NativeAnalyticFrameMetrics(
    uint DrawCallCount,
    uint VertexCount,
    uint IndexCount,
    ulong VertexUploadBytes,
    ulong IndexUploadBytes,
    ulong UniformUploadBytes,
    ulong SubmissionCount);

public readonly record struct NativeGeometryFrameMetrics(
    uint DrawCallCount,
    uint VertexCount,
    uint IndexCount,
    ulong VertexUploadBytes,
    ulong IndexUploadBytes,
    ulong BrushUploadBytes,
    ulong UniformUploadBytes,
    ulong SubmissionCount,
    ulong PayloadHash);

public readonly record struct NativePathFrameMetrics(
    uint DrawCallCount,
    uint VertexCount,
    uint IndexCount,
    uint RasterizedPathCount,
    uint AtlasWidth,
    uint AtlasHeight,
    uint AtlasGeneration,
    ulong VertexUploadBytes,
    ulong IndexUploadBytes,
    ulong BrushUploadBytes,
    ulong PathUploadBytes,
    ulong CoverageStagingBytes,
    ulong UniformUploadBytes,
    ulong SubmissionCount,
    ulong PayloadHash);

public readonly record struct NativeGlyphFrameMetrics(
    uint DrawCallCount,
    uint GlyphCount,
    uint RasterizedGlyphCount,
    uint AtlasWidth,
    uint AtlasHeight,
    uint AtlasGeneration,
    uint AtlasGrowthCount,
    ulong InstanceUploadBytes,
    ulong OutlineUploadBytes,
    ulong CoverageStagingBytes,
    ulong UniformUploadBytes,
    ulong SubmissionCount,
    ulong PayloadHash);

public readonly record struct NativeImageFrameMetrics(
    uint DrawCallCount,
    uint VertexCount,
    uint IndexCount,
    uint TextureGeneration,
    ulong VertexUploadBytes,
    ulong IndexUploadBytes,
    ulong TextureUploadBytes,
    ulong UniformUploadBytes,
    ulong SubmissionCount,
    ulong PayloadHash);

public readonly record struct NativeLayerMetrics(
    uint TextureWidth,
    uint TextureHeight,
    uint TextureGeneration,
    uint AllocationCount,
    uint ContentPassCount,
    uint CompositePassCount,
    bool CacheHit,
    ulong TextureBytes,
    ulong VertexUploadBytes,
    ulong UniformUploadBytes,
    NativeGroupMaskKind MaskKind,
    uint MaskRevision,
    uint MaskBindGroupGeneration,
    bool MaskBindGroupCacheHit,
    ulong MaskUniformUploadBytes,
    uint ClipPathCount,
    uint ClipRasterizedPathCount,
    uint ClipPassCount,
    bool ClipCacheHit,
    ulong ClipPathUploadBytes,
    ulong ClipCoverageStagingBytes,
    ulong ClipTextureBytes,
    NativeGroupEffectKind EffectKind,
    uint EffectRevision,
    uint EffectPassCount,
    bool EffectCacheHit,
    ulong EffectUniformUploadBytes,
    ulong EffectTextureBytes,
    uint EffectCount,
    uint EffectChainRevision,
    uint EffectTextureGeneration,
    uint EffectAllocationCount,
    GpuBlendMode BlendMode,
    uint BlendSourcePassCount,
    bool BlendPipelineCacheHit,
    uint BlendSourceTextureGeneration,
    uint BlendSourceAllocationCount,
    ulong BlendSourceTextureBytes);

public readonly record struct NativeSceneUpdateMetrics(
    uint CommandCount,
    uint ResourceCount,
    uint DrawCount,
    uint MaximumStackDepth,
    NativeSceneValidationError ValidationError,
    uint ErrorOffset,
    ulong SceneId,
    ulong Generation,
    ulong SnapshotBytes,
    ulong PayloadBytes,
    bool SnapshotReused);

public readonly record struct NativeSceneFrameMetrics(
    uint CommandCount,
    uint DrawCallCount,
    uint FamilySwitchCount,
    ulong SubmissionCount,
    ulong VertexUploadBytes,
    ulong IndexUploadBytes,
    ulong TextureUploadBytes,
    ulong UniformUploadBytes,
    ulong CoverageStagingBytes,
    ulong PayloadHash,
    ulong BrushUploadBytes,
    ulong GradientStopUploadBytes,
    ulong TextStyleUploadBytes,
    ulong ColorGlyphUploadBytes);

public readonly record struct NativeRendererInfo(
    uint AbiVersion,
    uint BackendAbi,
    NativeRendererCapabilities Capabilities,
    string Name);
