namespace ProGPU.Backend.Native;

public enum NativeMilBackend : byte
{
    WgpuNative,
    Dawn
}

public enum NativeMilResourceType : uint
{
    MediaPlayer = 1,
    AxisAngleRotation3D = 3,
    QuaternionRotation3D = 4,
    PerspectiveCamera = 7,
    OrthographicCamera = 8,
    MatrixCamera = 9,
    Model3DGroup = 11,
    AmbientLight = 13,
    DirectionalLight = 14,
    PointLight = 16,
    SpotLight = 17,
    GeometryModel3D = 18,
    MeshGeometry3D = 20,
    MaterialGroup = 22,
    DiffuseMaterial = 23,
    SpecularMaterial = 24,
    EmissiveMaterial = 25,
    Transform3DGroup = 27,
    TranslateTransform3D = 29,
    ScaleTransform3D = 30,
    RotateTransform3D = 31,
    MatrixTransform3D = 32,
    BlurEffect = 36,
    DropShadowEffect = 37,
    Visual = 39,
    Viewport3DVisual = 40,
    Visual3D = 41,
    GlyphRun = 42,
    RenderData = 43,
    RenderTarget = 45,
    HwndRenderTarget = 46,
    GenericRenderTarget = 47,
    DoubleResource = 49,
    ColorResource = 50,
    PointResource = 51,
    RectResource = 52,
    SizeResource = 53,
    MatrixResource = 54,
    Point3DResource = 55,
    Vector3DResource = 56,
    QuaternionResource = 57,
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
    VideoDrawing = 90,
    DrawingGroup = 91,
    GuidelineSet = 92,
    BitmapCache = 94,
    BitmapSource = 95,
    DoubleBufferedBitmap = 96,
    D3DImage = 97
}

public enum NativeMilWindowLayerType : uint
{
    NotLayered,
    SystemManagedLayer,
    ApplicationManagedLayer
}

[Flags]
public enum NativeMilTransparencyMode : uint
{
    Opaque = 0,
    ConstantAlpha = 1U << 0,
    PerPixelAlpha = 1U << 1,
    ColorKey = 1U << 2
}

public readonly record struct NativeMilWindowRect(
    int Left,
    int Top,
    int Right,
    int Bottom);

/// <summary>
/// Canonical HWND-target presentation policy without an HWND or another
/// process-local Windows handle.
/// </summary>
public readonly record struct NativeMilWindowSettings(
    NativeMilWindowRect WindowRect,
    NativeMilWindowLayerType LayerType,
    NativeMilTransparencyMode TransparencyMode,
    float ConstantAlpha,
    bool IsChild,
    bool IsRtl,
    bool RenderingEnabled,
    NativeMilColor ColorKey,
    uint DisableCookie,
    bool GdiBlt);

public enum NativeMilEffectRenderingBias : uint
{
    Performance,
    Quality
}

public enum NativeMilBlurKernelType : uint
{
    Gaussian,
    Box
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

/// <summary>Canonical retained WPF BitmapCache resource state.</summary>
public readonly record struct NativeMilBitmapCache(
    double RenderAtScale = 1.0,
    bool SnapsToDevicePixels = false,
    bool EnableClearType = false,
    uint RenderAtScaleAnimationHandle = 0);

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

public readonly record struct NativeMilSize(double Width, double Height);

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

public enum NativeMilTextRenderingMode : uint
{
    Auto,
    Aliased,
    Grayscale,
    ClearType
}

public enum NativeMilTextHintingMode : uint
{
    Auto,
    Fixed,
    Animated
}

[Flags]
public enum NativeMilRenderOptionFlags : uint
{
    None = 0,
    BitmapScalingMode = 0x01,
    EdgeMode = 0x02,
    CompositingMode = 0x04,
    ClearTypeHint = 0x08,
    TextRenderingMode = 0x10,
    TextHintingMode = 0x20
}

public readonly record struct NativeMilRenderOptions(
    NativeMilRenderOptionFlags Flags,
    NativeMilEdgeMode EdgeMode = NativeMilEdgeMode.Unspecified,
    NativeMilBitmapScalingMode BitmapScalingMode =
        NativeMilBitmapScalingMode.Unspecified,
    NativeMilClearTypeHint ClearTypeHint = NativeMilClearTypeHint.Auto,
    NativeMilTextRenderingMode TextRenderingMode =
        NativeMilTextRenderingMode.Auto,
    NativeMilTextHintingMode TextHintingMode =
        NativeMilTextHintingMode.Auto);

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

[Flags]
public enum NativeMilSceneBuildRequestFlags : uint
{
    None = 0,
    VisualBrush = 1U << 0
}

[Flags]
public enum NativeMilSceneBuildResultFlags : uint
{
    None = 0,
    NeedsMoreCycles = 1U << 0
}

/// <summary>
/// Versioned frame context for stateful MIL scene compilation. Reuse one
/// nonzero request serial for the size-query and copy calls of a frame.
/// </summary>
public readonly record struct NativeMilSceneBuildRequest(
    uint TargetHandle,
    ulong SceneId,
    ulong Generation,
    ulong MonotonicTimeNanoseconds,
    ulong RequestSerial,
    double DpiScaleX = 1.0,
    double DpiScaleY = 1.0,
    NativeMilSceneBuildRequestFlags Flags =
        NativeMilSceneBuildRequestFlags.None);

public readonly record struct NativeMilSceneBuildResult(
    NativeMilSceneBuildResultFlags Flags,
    ulong RequestSerial,
    ulong NextDueTimeNanoseconds,
    ulong StreamBytes);

/// <summary>
/// Interprets stateful MIL scheduler feedback without depending on a
/// framework-specific dispatcher or timer implementation.
/// </summary>
public static class NativeMilSceneBuildTiming
{
    private const ulong NanosecondsPerTimeSpanTick = 100;

    /// <summary>
    /// Returns the delay until the next native MIL phase cycle. The delay is
    /// rounded up to the next <see cref="TimeSpan"/> tick so a host never asks
    /// the compositor to advance before its absolute monotonic due time.
    /// </summary>
    public static bool TryGetContinuationDelay(
        NativeMilSceneBuildRequest request,
        NativeMilSceneBuildResult result,
        out TimeSpan delay)
    {
        const NativeMilSceneBuildResultFlags knownFlags =
            NativeMilSceneBuildResultFlags.NeedsMoreCycles;
        if ((result.Flags & ~knownFlags) != 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(result),
                result.Flags,
                "The native MIL scene result contains unknown scheduler flags.");
        }
        if (request.RequestSerial == 0 ||
            result.RequestSerial != request.RequestSerial)
        {
            throw new ArgumentException(
                "The native MIL scene result does not match the frame request serial.",
                nameof(result));
        }
        if ((result.Flags &
                NativeMilSceneBuildResultFlags.NeedsMoreCycles) == 0)
        {
            delay = TimeSpan.Zero;
            return false;
        }

        ulong remainingNanoseconds =
            result.NextDueTimeNanoseconds > request.MonotonicTimeNanoseconds
                ? result.NextDueTimeNanoseconds -
                    request.MonotonicTimeNanoseconds
                : 0;
        ulong ticks = remainingNanoseconds / NanosecondsPerTimeSpanTick;
        if (remainingNanoseconds % NanosecondsPerTimeSpanTick != 0)
        {
            ++ticks;
        }
        delay = TimeSpan.FromTicks((long)ticks);
        return true;
    }
}

public sealed record NativeMilCompiledScene(
    byte[] Stream,
    NativeMilSceneMetrics Metrics);

public sealed record NativeMilStatefulCompiledScene(
    byte[] Stream,
    NativeMilSceneMetrics Metrics,
    NativeMilSceneBuildResult BuildResult);

/// <summary>
/// Pointer-free flattened 3D payload bound to one canonical retained
/// Viewport3DVisual handle.
/// </summary>
public sealed record NativeMilViewport3DScene(
    NativeSceneCamera3D Camera,
    NativeImageRect Viewport,
    NativeSceneMesh3D[] Meshes,
    NativeSceneMesh3DVertex[] Vertices,
    uint[] Indices,
    NativeSceneLight3D[] Lights)
{
    /// <summary>
    /// Optional canonical material table with exactly one solid, linear, or
    /// radial brush per mesh. An empty table preserves the legacy white
    /// material multiplier.
    /// </summary>
    public NativeSceneBrush[] Materials { get; init; } = [];

    /// <summary>
    /// Gradient stops addressed by <see cref="Materials"/> stop ranges.
    /// </summary>
    public NativeSceneGradientStop[] GradientStops { get; init; } = [];
}

public sealed class NativeMilException : Exception
{
    public NativeMilException(NativeMilStatus status, string message)
        : base(message)
    {
        Status = status;
    }

    public NativeMilStatus Status { get; }
}
