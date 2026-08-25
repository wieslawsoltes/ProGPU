namespace ProGPU.Backend.Native;

public enum NativeMilBackend : byte
{
    WgpuNative,
    Dawn
}

public enum NativeMilResourceType : uint
{
    Visual = 39,
    Viewport3DVisual = 40,
    GlyphRun = 42,
    RenderData = 43,
    RenderTarget = 45,
    HwndRenderTarget = 46,
    GenericRenderTarget = 47,
    DoubleResource = 49,
    PointResource = 51,
    MatrixResource = 54,
    DrawingImage = 59,
    TransformGroup = 61,
    TranslateTransform = 62,
    ScaleTransform = 63,
    SkewTransform = 64,
    RotateTransform = 65,
    MatrixTransform = 66,
    LineGeometry = 68,
    RectangleGeometry = 69,
    EllipseGeometry = 70,
    GeometryGroup = 71,
    CombinedGeometry = 72,
    PathGeometry = 73,
    SolidColorBrush = 75,
    LinearGradientBrush = 77,
    RadialGradientBrush = 78,
    DashStyle = 84,
    Pen = 85,
    GeometryDrawing = 87,
    GlyphRunDrawing = 88,
    ImageDrawing = 89,
    DrawingGroup = 91,
    BitmapSource = 95
}

[Flags]
public enum NativeMilGlyphStyleSimulations : uint
{
    None = 0,
    Bold = 1U << 0,
    Italic = 1U << 1
}

public enum NativeMilTextMeasuringMethod : ushort
{
    Natural,
    GdiClassic,
    GdiNatural
}

public readonly record struct NativeMilRect(
    double X,
    double Y,
    double Width,
    double Height);

public readonly record struct NativeMilGlyphRun(
    NativeMilPoint Origin,
    float EmSize,
    NativeMilRect ManagedBounds,
    ushort BidiLevel = 0,
    NativeMilTextMeasuringMethod MeasuringMethod =
        NativeMilTextMeasuringMethod.Natural,
    bool IsSideways = false);

public enum NativeMilPathFillRule : uint
{
    EvenOdd = 0,
    Nonzero = 1
}

public enum NativeMilGeometryCombineMode : uint
{
    Union = 0,
    Intersect = 1,
    Xor = 2,
    Exclude = 3
}

public enum NativeMilPathSegmentKind : uint
{
    Line = 1,
    CubicBezier = 2,
    QuadraticBezier = 3,
    Arc = 4
}

public readonly record struct NativeMilPoint(double X, double Y);

public readonly record struct NativeMilPathSegment(
    NativeMilPathSegmentKind Kind,
    NativeMilPoint Point1,
    NativeMilPoint Point2,
    NativeMilPoint Point3,
    double RadiusX = 0,
    double RadiusY = 0,
    double RotationAngle = 0,
    bool IsLargeArc = false,
    bool IsClockwise = false,
    bool IsStroked = true,
    bool IsSmoothJoin = false)
{
    public static NativeMilPathSegment Line(
        NativeMilPoint point,
        bool isStroked = true,
        bool isSmoothJoin = false) => new(
            NativeMilPathSegmentKind.Line,
            point,
            default,
            default,
            IsStroked: isStroked,
            IsSmoothJoin: isSmoothJoin);

    public static NativeMilPathSegment QuadraticBezier(
        NativeMilPoint control,
        NativeMilPoint point,
        bool isStroked = true,
        bool isSmoothJoin = false) => new(
            NativeMilPathSegmentKind.QuadraticBezier,
            control,
            point,
            default,
            IsStroked: isStroked,
            IsSmoothJoin: isSmoothJoin);

    public static NativeMilPathSegment CubicBezier(
        NativeMilPoint control1,
        NativeMilPoint control2,
        NativeMilPoint point,
        bool isStroked = true,
        bool isSmoothJoin = false) => new(
            NativeMilPathSegmentKind.CubicBezier,
            control1,
            control2,
            point,
            IsStroked: isStroked,
            IsSmoothJoin: isSmoothJoin);

    public static NativeMilPathSegment Arc(
        NativeMilPoint point,
        double radiusX,
        double radiusY,
        double rotationAngle,
        bool isLargeArc,
        bool isClockwise,
        bool isStroked = true,
        bool isSmoothJoin = false) => new(
            NativeMilPathSegmentKind.Arc,
            point,
            default,
            default,
            radiusX,
            radiusY,
            rotationAngle,
            isLargeArc,
            isClockwise,
            isStroked,
            isSmoothJoin);
}

public sealed record NativeMilPathFigure(
    NativeMilPoint StartPoint,
    bool IsFilled,
    bool IsClosed,
    IReadOnlyList<NativeMilPathSegment> Segments);

public sealed record NativeMilPathGeometry(
    NativeMilPathFillRule FillRule,
    double X,
    double Y,
    double Width,
    double Height,
    IReadOnlyList<NativeMilPathFigure> Figures);

public readonly record struct NativeMilMatrix3x2(
    double M11,
    double M12,
    double M21,
    double M22,
    double OffsetX,
    double OffsetY)
{
    public static NativeMilMatrix3x2 Identity { get; } = new(
        1.0,
        0.0,
        0.0,
        1.0,
        0.0,
        0.0);
}

public readonly record struct NativeMilColor(
    float Red,
    float Green,
    float Blue,
    float Alpha);

public enum NativeMilGradientInterpolation : uint
{
    ScRgb,
    SRgb
}

public enum NativeMilBrushMappingMode : uint
{
    Absolute,
    RelativeToBoundingBox
}

public enum NativeMilGradientSpreadMethod : uint
{
    Pad,
    Reflect,
    Repeat
}

public readonly record struct NativeMilGradientStop(
    double Offset,
    NativeMilColor Color);

public readonly record struct NativeMilLinearGradientBrush(
    NativeMilPoint StartPoint,
    NativeMilPoint EndPoint,
    double Opacity = 1.0,
    NativeMilGradientInterpolation Interpolation =
        NativeMilGradientInterpolation.SRgb,
    NativeMilBrushMappingMode MappingMode =
        NativeMilBrushMappingMode.RelativeToBoundingBox,
    NativeMilGradientSpreadMethod SpreadMethod =
        NativeMilGradientSpreadMethod.Pad,
    uint OpacityAnimationHandle = 0,
    uint TransformHandle = 0,
    uint RelativeTransformHandle = 0,
    uint StartPointAnimationHandle = 0,
    uint EndPointAnimationHandle = 0);

public readonly record struct NativeMilRadialGradientBrush(
    NativeMilPoint Center,
    NativeMilPoint GradientOrigin,
    double RadiusX,
    double RadiusY,
    double Opacity = 1.0,
    NativeMilGradientInterpolation Interpolation =
        NativeMilGradientInterpolation.SRgb,
    NativeMilBrushMappingMode MappingMode =
        NativeMilBrushMappingMode.RelativeToBoundingBox,
    NativeMilGradientSpreadMethod SpreadMethod =
        NativeMilGradientSpreadMethod.Pad,
    uint OpacityAnimationHandle = 0,
    uint TransformHandle = 0,
    uint RelativeTransformHandle = 0,
    uint CenterAnimationHandle = 0,
    uint RadiusXAnimationHandle = 0,
    uint RadiusYAnimationHandle = 0,
    uint GradientOriginAnimationHandle = 0);

public enum NativeMilPenLineCap : uint
{
    Flat,
    Square,
    Round,
    Triangle
}

public enum NativeMilPenLineJoin : uint
{
    Miter,
    Bevel,
    Round
}

public readonly record struct NativeMilPen(
    uint BrushHandle,
    double Thickness,
    NativeMilPenLineCap StartLineCap = NativeMilPenLineCap.Flat,
    NativeMilPenLineCap EndLineCap = NativeMilPenLineCap.Flat,
    NativeMilPenLineCap DashCap = NativeMilPenLineCap.Square,
    NativeMilPenLineJoin LineJoin = NativeMilPenLineJoin.Miter,
    double MiterLimit = 10.0,
    uint DashStyleHandle = 0);

public enum NativeMilEdgeMode : uint
{
    Unspecified,
    Aliased
}

public enum NativeMilBitmapScalingMode : uint
{
    Unspecified,
    Linear,
    Fant,
    NearestNeighbor
}

public enum NativeMilClearTypeHint : uint
{
    Auto,
    Enabled
}

public readonly record struct NativeMilDrawingGroup(
    double Opacity = 1.0,
    uint ClipGeometryHandle = 0,
    uint OpacityAnimationHandle = 0,
    uint OpacityMaskHandle = 0,
    uint TransformHandle = 0,
    uint GuidelineSetHandle = 0,
    NativeMilEdgeMode EdgeMode = NativeMilEdgeMode.Unspecified,
    NativeMilBitmapScalingMode BitmapScalingMode =
        NativeMilBitmapScalingMode.Unspecified,
    NativeMilClearTypeHint ClearTypeHint = NativeMilClearTypeHint.Auto);

public enum NativeMilStatus : uint
{
    Success,
    EndOfBatch,
    InvalidArgument,
    MalformedBatch,
    UnknownCommand,
    UnsupportedCommand,
    DuplicateHandle,
    InvalidHandle,
    InvalidResourceType,
    ResourceTypeMismatch,
    InvalidGraph,
    CapacityExceeded
}

public readonly record struct NativeMilBatchMetrics(
    uint CommandCount,
    uint SupportedCommandCount,
    uint UnsupportedCommandCount,
    uint CreatedResourceCount,
    uint DeletedResourceCount,
    uint UpdatedResourceCount,
    uint TotalBytes);

public readonly record struct NativeMilVisualSnapshot(
    uint Handle,
    double OffsetX,
    double OffsetY,
    double Opacity,
    uint ContentHandle,
    uint ChildCount);

public readonly record struct NativeMilTargetSnapshot(
    uint Handle,
    uint RootHandle,
    float ClearRed,
    float ClearGreen,
    float ClearBlue,
    float ClearAlpha,
    uint Flags);

public readonly record struct NativeMilSceneMetrics(
    uint VisualCount,
    uint RectangleCount,
    uint EllipseCount,
    uint RoundedRectangleCount,
    uint LineCount,
    uint BrushCount,
    uint MaximumVisualDepth,
    ulong StreamBytes);

public sealed record NativeMilCompiledScene(
    byte[] Stream,
    NativeMilSceneMetrics Metrics);

public sealed class NativeMilException : Exception
{
    public NativeMilException(NativeMilStatus status, string message)
        : base(message)
    {
        Status = status;
    }

    public NativeMilStatus Status { get; }
}
