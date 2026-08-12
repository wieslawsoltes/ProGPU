using System.Numerics;
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
    CubicBezier = 4
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
    PathFillAtlas = 1UL << 13
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
    ulong VertexUploadBytes,
    ulong IndexUploadBytes,
    ulong BrushUploadBytes,
    ulong PathUploadBytes,
    ulong CoverageStagingBytes,
    ulong UniformUploadBytes,
    ulong SubmissionCount,
    ulong PayloadHash);

public readonly record struct NativeRendererInfo(
    uint AbiVersion,
    uint BackendAbi,
    NativeRendererCapabilities Capabilities,
    string Name);
