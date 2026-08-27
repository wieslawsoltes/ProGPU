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

public enum NativeTextDirection : uint
{
    Unspecified = 0,
    LeftToRight = 1,
    RightToLeft = 2,
    TopToBottom = 3,
    BottomToTop = 4
}

public enum NativeTextClusterLevel : uint
{
    MonotoneGraphemes = 0,
    MonotoneCharacters = 1,
    Characters = 2,
    Graphemes = 3
}

[Flags]
public enum NativeTextBufferFlags : uint
{
    None = 0,
    BeginningOfText = 1U << 0,
    EndOfText = 1U << 1,
    PreserveDefaultIgnorables = 1U << 2,
    RemoveDefaultIgnorables = 1U << 3,
    DoNotInsertDottedCircle = 1U << 4,
    Verify = 1U << 5,
    ProduceUnsafeToConcat = 1U << 6,
    ProduceSafeToInsertTatweel = 1U << 7
}

[Flags]
public enum NativeTextShapeFlags : uint
{
    None = 0,
    ZeroMarkAdvances = 1U << 0
}

public enum NativeTextFontError : uint
{
    None = 0,
    InvalidArgument = 1,
    UnsupportedContainer = 2,
    InvalidCollection = 3,
    InvalidFace = 4,
    TruncatedDirectory = 5,
    InvalidGlyph = 6,
    InsufficientBuffer = 7,
    InvalidContainer = 8,
    InvalidCompressedData = 9,
    VerificationFailed = 10
}

public enum NativeTextLineBreakKind : byte
{
    Prohibited = 0,
    Opportunity = 1,
    Mandatory = 2
}

public enum NativeTextUnicodeError : uint
{
    None = 0,
    InvalidArgument = 1,
    InvalidEncoding = 2,
    InsufficientBuffer = 3
}

public enum NativeTextParagraphStage : uint
{
    None = 0,
    Bidi = 1,
    LineBreak = 2,
    Shaping = 3,
    ClusterMap = 4,
    Layout = 5
}

public enum NativeTextTrimming : uint
{
    None = 0,
    CharacterEllipsis = 1,
    WordEllipsis = 2
}

public enum NativeTextAlignment : uint
{
    Left = 0,
    Center = 1,
    Right = 2,
    Justify = 3
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
    DotGrid = 5,
    Arc = 6,
    PathCap = 7,
    PathJoin = 8
}

[Flags]
public enum NativePointBatchFlags : uint
{
    None = 0,
    EdgeAliased = 1U << 0,
    Round = 1U << 1,
    Hairline = 1U << 2,
    FixedDeviceRadius = 1U << 3
}

public enum NativeVertexMeshTopology : uint
{
    Triangles = 0,
    TriangleStrip = 1,
    TriangleFan = 2
}

public enum NativeSceneStrokeKind : uint
{
    Polyline = 0,
    Spline = 1
}

public enum NativeVertexColorBlendMode : uint
{
    Clear,
    Src,
    Dst,
    SrcOver,
    DstOver,
    SrcIn,
    DstIn,
    SrcOut,
    DstOut,
    SrcATop,
    DstATop,
    Xor,
    Plus,
    Modulate,
    Screen,
    Overlay,
    Darken,
    Lighten,
    ColorDodge,
    ColorBurn,
    HardLight,
    SoftLight,
    Difference,
    Exclusion,
    Multiply,
    Hue,
    Saturation,
    Color,
    Luminosity
}

[Flags]
public enum NativeVertexMeshFlags : uint
{
    None = 0,
    EdgeAliased = 1U << 0
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
    Cubic = 2,
    LinearMipmap = 3,
    MagLinearMinLinearMipNearest = 4,
    MagLinearMinNearestMipLinear = 5,
    MagLinearMinNearestMipNearest = 6,
    MagNearestMinLinearMipLinear = 7,
    MagNearestMinLinearMipNearest = 8,
    MagNearestMinNearestMipLinear = 9,
    /// <summary>
    /// WPF Fant/HighQuality bounded area-prefilter minification.
    /// </summary>
    Fant = 10
}

public enum NativeSceneImagePatchKind : uint
{
    Texture = 0,
    FixedColor = 1,
    AtlasColor = 2
}

public enum NativeImagePatchColorBlendMode : uint
{
    Clear,
    Src,
    Dst,
    SrcOver,
    DstOver,
    SrcIn,
    DstIn,
    SrcOut,
    DstOut,
    SrcATop,
    DstATop,
    Xor,
    Plus,
    Modulate,
    Screen,
    Overlay,
    Darken,
    Lighten,
    ColorDodge,
    ColorBurn,
    HardLight,
    SoftLight,
    Difference,
    Exclusion,
    Multiply,
    Hue,
    Saturation,
    Color,
    Luminosity
}

[Flags]
public enum NativeSceneImageFlags : uint
{
    None = 0,
    ColorMatrix = 1U << 0,
    Effect = 1U << 1,
    SnapToPixels = 1U << 2,
    SourcePremultiplied = 1U << 3,
    PatchBatch = 1U << 4
}

[Flags]
public enum NativeSceneImageColorMatrixFlags : uint
{
    None = 0,
    LuminanceToAlpha = 1U << 0
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

public enum NativePathBooleanNodeKind : uint
{
    Leaf = 0,
    Empty = 1,
    Difference = 2,
    Intersect = 3,
    Union = 4,
    Xor = 5,
    ReverseDifference = 6
}

public enum NativeGroupEffectKind : uint
{
    None = 0,
    GaussianBlur = 1,
    DropShadow = 2,
    BoxBlur = 3
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
    SemanticGeometryBatch = 1UL << 35,
    SemanticPointBatch = 1UL << 36,
    SemanticVertexMesh = 1UL << 37,
    SemanticStrokeBatch = 1UL << 38,
    SemanticLine3DBatch = 1UL << 39,
    SemanticMesh3DBatch = 1UL << 40,
    BulkTextShaping = 1UL << 41,
    BulkTextLayout = 1UL << 42,
    BulkTextLineBreaking = 1UL << 43,
    BulkTextBidi = 1UL << 44,
    BulkTextParagraph = 1UL << 45,
    BulkTextVerticalLayout = 1UL << 46,
    SemanticImagePatchBatch = 1UL << 47,
    SemanticImageMipmapSampling = 1UL << 48,
    ImageFrameMipmapSampling = 1UL << 49,
    SemanticVectorClipMask = 1UL << 50,
    RetainedGpuHitTesting = 1UL << 51,
    WpfMilChannel = 1UL << 52,
    GroupBoxBlur = 1UL << 53,
    SemanticMesh3DMaterials = 1UL << 54
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
    GeometryBatch = 10,
    PointBatch = 11,
    VertexMesh = 12,
    StrokeBatch = 13,
    Line3DBatch = 14,
    Mesh3DBatch = 15,
    HitTestIndex = 16,
    GuidelineSet = 17
}

public enum NativeGpuHitTestPrimitiveKind : uint
{
    AxisAlignedBounds = 0,
    RectangleFill = 1,
    RectangleStroke = 2,
    EllipseFill = 3,
    EllipseStroke = 4,
    LineStroke = 5,
    PathFill = 6,
    PathStroke = 7
}

[Flags]
public enum NativeGpuHitTestPrimitiveFlags : uint
{
    None = 0,
    Visible = 1U << 0,
    HitTestVisible = 1U << 1
}

public enum NativeGpuHitTestIntersectionDetail : uint
{
    NotCalculated = 0,
    Empty = 1,
    FullyInside = 2,
    FullyContains = 3,
    Intersects = 4
}

[Flags]
public enum NativeGpuHitTestQueryFlags : uint
{
    None = 0,
    ResultCapacityMask = 0x0000_FFFF,
    EllipseRegion = 0x4000_0000,
    BoundsRegion = 0x8000_0000
}

public partial struct NativeGpuHitTestQuery
{
    public const int MaximumResultCount = 256;

    public readonly int RequestedResultCapacity =>
        checked((int)(Flags & (uint)NativeGpuHitTestQueryFlags.ResultCapacityMask));

    public static NativeGpuHitTestQuery PointQuery(
        Vector2 point,
        int resultCapacity = 0) => new()
        {
            Point = point,
            RegionMax = point,
            RootNodeIndex = 0,
            Flags = ValidateResultCapacity(resultCapacity)
        };

    public static NativeGpuHitTestQuery BoundsQuery(
        Vector2 minimum,
        Vector2 maximum,
        int resultCapacity) => new()
        {
            Point = minimum,
            RegionMax = maximum,
            RootNodeIndex = 0,
            Flags = (uint)NativeGpuHitTestQueryFlags.BoundsRegion |
                ValidateResultCapacity(resultCapacity)
        };

    public static NativeGpuHitTestQuery EllipseQuery(
        Vector2 minimum,
        Vector2 maximum,
        int resultCapacity) => new()
        {
            Point = minimum,
            RegionMax = maximum,
            RootNodeIndex = 0,
            Flags = (uint)(NativeGpuHitTestQueryFlags.BoundsRegion |
                NativeGpuHitTestQueryFlags.EllipseRegion) |
                ValidateResultCapacity(resultCapacity)
        };

    private static uint ValidateResultCapacity(int resultCapacity)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(resultCapacity);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(
            resultCapacity,
            MaximumResultCount);
        return (uint)resultCapacity;
    }
}

public partial struct NativeGpuHitTestResult
{
    public readonly bool HasHit => Hit != 0;

    public readonly NativeGpuHitTestIntersectionDetail Detail =>
        (NativeGpuHitTestIntersectionDetail)IntersectionDetail;
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
    HatchPattern = 3,
    CrossHatch = 4,
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
    CoverageBitmap = 2,
    AnalyticChain = 3,
    VectorClipChain = 4,
    Brush = 5,
    Composite = 6,
    Geometry = 7,
    Picture = 8
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
    DrawGeometry = 20,
    DrawPointBatch = 21,
    DrawVertexMesh = 22,
    DrawStrokeBatch = 23,
    DrawLine3DBatch = 24,
    DrawMesh3DBatch = 25
}

public enum NativeMesh3DTopology : uint
{
    Triangles = 0,
    TriangleStrip = 1
}

public enum NativeMesh3DRenderMode : uint
{
    Solid = 0,
    Wireframe = 1,
    SolidWireframe = 2
}

[Flags]
public enum NativeMesh3DFlags : uint
{
    TwoSided = 0,
    FrontFace = 1U << 0,
    BackFace = 1U << 1,
    SpecularMaterial = 1U << 2
}

public enum NativeLight3DKind : uint
{
    Ambient = 0,
    Directional = 1,
    Point = 2,
    Spot = 3
}

[Flags]
public enum NativeSceneRecordFlags : uint
{
    None = 0,
    Required = 1U << 0,
    StyledGlyphs = 1U << 1,
    ColorGlyphBitmaps = 1U << 2,
    ExternalImage = 1U << 3
}

/// <summary>
/// Binds one retained pointer-free scene image resource to a live same-device
/// WebGPU texture view. The native compositor retains the view until the full
/// binding table is replaced.
/// </summary>
public readonly struct NativeSceneExternalImageBinding
{
    public NativeSceneExternalImageBinding(
        ulong resourceId,
        ulong generation,
        GpuTexture texture,
        NativeSceneExternalImageRole role = NativeSceneExternalImageRole.Primary)
    {
        ResourceId = resourceId;
        Generation = generation;
        Texture = texture ?? throw new ArgumentNullException(nameof(texture));
        Role = role;
    }

    public ulong ResourceId { get; }

    public ulong Generation { get; }

    public GpuTexture Texture { get; }

    public NativeSceneExternalImageRole Role { get; }
}

public enum NativeSceneExternalImageRole : uint
{
    Primary = 0,
    Chroma = 1,
    Mask = 2
}

[Flags]
public enum NativeSceneStateFlags : uint
{
    None = 0,
    ClipRect = 1U << 0,
    Mask = 1U << 1,
    GuidelineSet = 1U << 2
}

[Flags]
public enum NativeSceneGuidelineSetFlags : uint
{
    None = 0,

    /// <summary>
    /// Allows multiple sorted static guides only when the containing State is
    /// used by a local retained-cache composite.
    /// </summary>
    CompositeOnly = 1U << 0,

    /// <summary>
    /// Applies multiple sorted static guides independently to each supported
    /// draw path point after the complete target transform.
    /// </summary>
    PerPoint = 1U << 1
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
    ForceIsolation = 1U << 2,

    /// <summary>
    /// Retains this isolated output by stable composite revision and refreshes
    /// its pixels only when the nonzero content revision changes.
    /// </summary>
    CacheContent = 1U << 3,

    /// <summary>
    /// Rasterizes cached content in a zero-origin local page and composites it
    /// through <see cref="NativeSceneLayer.CompositeStateResourceIndex"/>.
    /// </summary>
    CacheLocalSpace = 1U << 4,

    /// <summary>
    /// Samples a local cached layer with exact nearest-neighbor filtering.
    /// This flag is invalid without <see cref="CacheLocalSpace"/>.
    /// </summary>
    CacheNearest = 1U << 5,

    /// <summary>
    /// Uses bounded area-prefilter reconstruction for WPF Fant/HighQuality
    /// minification and linear reconstruction when no prefilter is required.
    /// This flag is invalid without <see cref="CacheLocalSpace"/> and is
    /// mutually exclusive with <see cref="CacheNearest"/>.
    /// </summary>
    CacheFant = 1U << 6,

    /// <summary>
    /// Applies the clip-only state referenced by
    /// <see cref="NativeSceneLayer.CompositeStateResourceIndex"/> while
    /// compositing a materialized non-local layer.
    /// </summary>
    CompositeState = 1U << 7
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
/// stop values consistently with the production managed compositor. A brush
/// returned by <see cref="WithPadOutsideColors"/> instead uses the first two
/// inline colors for its start/end Pad extension while the authoritative
/// gradient stops remain in the auxiliary table.
/// </remarks>
[StructLayout(LayoutKind.Explicit, Size = 256)]
public struct NativeSceneBrush
{
    public const uint PerlinTableRecordCount = 512U;
    public const uint MaximumPerlinOctaves = 255U;
    public const uint GradientSpreadMask = 0x3FFFFFFFU;
    public const uint PadOutsideColorsFlag = 0x40000000U;
    public const uint ConicalOutsideColorFlag = 0x80000000U;

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

    /// <summary>
    /// Gets whether <see cref="Color0"/> and <see cref="Color1"/> are the
    /// colors sampled before and after a Pad gradient. Exact endpoint
    /// coordinates continue to sample the first and last gradient stops.
    /// </summary>
    public readonly bool HasPadOutsideColors =>
        ((uint)Spread & PadOutsideColorsFlag) != 0U;

    /// <summary>
    /// Returns a canonical gradient brush with distinct colors for coordinates
    /// before and after its Pad interval. The 256-byte ABI is unchanged.
    /// </summary>
    public readonly NativeSceneBrush WithPadOutsideColors(
        Vector4 startColor,
        Vector4 endColor)
    {
        uint spread = (uint)Spread;
        bool gradient = Kind is NativeSceneBrushKind.LinearGradient or
            NativeSceneBrushKind.RadialGradient or
            NativeSceneBrushKind.TwoPointConicalGradient or
            NativeSceneBrushKind.SweepGradient;
        if (!gradient ||
            (spread & GradientSpreadMask) !=
                (uint)NativeSceneGradientSpread.Pad)
        {
            throw new InvalidOperationException(
                "Distinct outside colors require a Pad gradient brush.");
        }

        var result = this;
        result.Spread = (NativeSceneGradientSpread)(
            spread | PadOutsideColorsFlag);
        result.Color0 = startColor;
        result.Color1 = endColor;
        return result;
    }

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

    public static NativeSceneBrush HatchPattern(
        float angle,
        float spacing,
        float thickness,
        Vector4 color,
        bool crossHatch = false,
        float opacity = 1f)
    {
        var brush = CreateBase(
            crossHatch
                ? NativeSceneBrushKind.CrossHatch
                : NativeSceneBrushKind.HatchPattern,
            opacity,
            Matrix3x2.Identity);
        brush.Radius = angle;
        brush.Center = new Vector2(spacing, thickness);
        brush.Color0 = color;
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
                (uint)brush.Spread | ConicalOutsideColorFlag);
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
/// Describes one upload-backed or externally bound image draw stored inside a
/// semantic scene.
/// </summary>
/// <remarks>
/// An upload-backed resource owns RGBA8 bytes; an external resource resolves a
/// same-device view from the compositor binding table. Source and destination
/// rectangles use logical image and target coordinates. The record remains
/// pointer-free in both modes.
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
        NativeSceneImageFlags flags = NativeSceneImageFlags.None,
        byte maxAnisotropy = 1)
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
        MaxAnisotropy = maxAnisotropy;
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
    public readonly uint MaxAnisotropy;

    internal bool HasCanonicalSampling =>
        Sampling <= NativeImageSampling.Fant &&
        MaxAnisotropy <= 16U &&
        (Sampling == NativeImageSampling.LinearMipmap ||
            MaxAnisotropy is 0U or 1U);
}

[StructLayout(LayoutKind.Sequential)]
public readonly struct NativeSceneImagePatchBatch
{
    public NativeSceneImagePatchBatch(uint patchCount)
    {
        StructSize = (uint)Unsafe.SizeOf<NativeSceneImagePatchBatch>();
        Flags = 0U;
        PatchCount = patchCount;
        Reserved = 0U;
    }

    public readonly uint StructSize;
    public readonly uint Flags;
    public readonly uint PatchCount;
    private readonly uint Reserved;

    internal bool HasCanonicalFields =>
        StructSize == Unsafe.SizeOf<NativeSceneImagePatchBatch>() &&
        Flags == 0U && Reserved == 0U;
}

[StructLayout(LayoutKind.Sequential)]
public readonly struct NativeSceneImagePatch
{
    public NativeSceneImagePatch(
        NativeSceneImagePatchKind kind,
        NativeImageRect sourceRect,
        NativeImageRect destinationRect,
        Matrix3x2 transform,
        Vector4 color = default,
        NativeImagePatchColorBlendMode colorBlendMode =
            NativeImagePatchColorBlendMode.Dst)
    {
        StructSize = (uint)Unsafe.SizeOf<NativeSceneImagePatch>();
        Kind = kind;
        ColorBlendMode = colorBlendMode;
        Flags = 0U;
        SourceRect = sourceRect;
        DestinationRect = destinationRect;
        Transform = transform;
        Color = color;
    }

    public readonly uint StructSize;
    public readonly NativeSceneImagePatchKind Kind;
    public readonly NativeImagePatchColorBlendMode ColorBlendMode;
    private readonly uint Flags;
    public readonly NativeImageRect SourceRect;
    public readonly NativeImageRect DestinationRect;
    public readonly Matrix3x2 Transform;
    public readonly Vector4 Color;

    internal bool HasCanonicalFields =>
        StructSize == Unsafe.SizeOf<NativeSceneImagePatch>() && Flags == 0U;
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
        Vector4 offset,
        NativeSceneImageColorMatrixFlags flags =
            NativeSceneImageColorMatrixFlags.None)
    {
        StructSize = (uint)Unsafe.SizeOf<NativeSceneImageColorMatrix>();
        Flags = flags;
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
    public readonly NativeSceneImageColorMatrixFlags Flags;
    public readonly Vector4 Red;
    public readonly Vector4 Green;
    public readonly Vector4 Blue;
    public readonly Vector4 Alpha;
    public readonly Vector4 Offset;
    private readonly uint Reserved0;
    private readonly uint Reserved1;

    internal bool HasCanonicalFields =>
        StructSize == Unsafe.SizeOf<NativeSceneImageColorMatrix>() &&
        (Flags & ~NativeSceneImageColorMatrixFlags.LuminanceToAlpha) == 0 &&
        Reserved0 == 0U && Reserved1 == 0U &&
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
/// Exact pointer-free uniform payload consumed by the shared
/// <c>ImageEffect.wgsl</c> pipeline.
/// </summary>
[Flags]
public enum NativeSceneImageEffectFlags : uint
{
    None = 0,
    UnfilterablePlanar = 1U << 0
}

[StructLayout(LayoutKind.Sequential)]
public readonly struct NativeSceneImageEffect
{
    public NativeSceneImageEffect(
        Vector4 colorMatrixRed,
        Vector4 colorMatrixGreen,
        Vector4 colorMatrixBlue,
        Vector4 colorMatrixAlpha,
        Vector4 colorMatrixOffset,
        Vector4 effects0,
        Vector4 effects1,
        Vector4 texture0,
        Vector4 flags0,
        Vector4 yuvRange,
        Vector4 yuvRed,
        Vector4 yuvGreen,
        Vector4 yuvBlue,
        Vector4 spherical0,
        Vector4 sphericalUvRect,
        Vector4 sphericalRotation0,
        Vector4 sphericalRotation1,
        Vector4 sphericalRotation2,
        NativeSceneImageEffectFlags flags =
            NativeSceneImageEffectFlags.None)
    {
        ColorMatrixRed = colorMatrixRed;
        ColorMatrixGreen = colorMatrixGreen;
        ColorMatrixBlue = colorMatrixBlue;
        ColorMatrixAlpha = colorMatrixAlpha;
        ColorMatrixOffset = colorMatrixOffset;
        Effects0 = effects0;
        Effects1 = effects1;
        Texture0 = texture0;
        Flags0 = flags0;
        YuvRange = yuvRange;
        YuvRed = yuvRed;
        YuvGreen = yuvGreen;
        YuvBlue = yuvBlue;
        Spherical0 = spherical0;
        SphericalUvRect = sphericalUvRect;
        SphericalRotation0 = sphericalRotation0;
        SphericalRotation1 = sphericalRotation1;
        SphericalRotation2 = sphericalRotation2;
        StructSize = (uint)Unsafe.SizeOf<NativeSceneImageEffect>();
        Flags = flags;
        Reserved0 = 0U;
        Reserved1 = 0U;
    }

    public readonly Vector4 ColorMatrixRed;
    public readonly Vector4 ColorMatrixGreen;
    public readonly Vector4 ColorMatrixBlue;
    public readonly Vector4 ColorMatrixAlpha;
    public readonly Vector4 ColorMatrixOffset;
    public readonly Vector4 Effects0;
    public readonly Vector4 Effects1;
    public readonly Vector4 Texture0;
    public readonly Vector4 Flags0;
    public readonly Vector4 YuvRange;
    public readonly Vector4 YuvRed;
    public readonly Vector4 YuvGreen;
    public readonly Vector4 YuvBlue;
    public readonly Vector4 Spherical0;
    public readonly Vector4 SphericalUvRect;
    public readonly Vector4 SphericalRotation0;
    public readonly Vector4 SphericalRotation1;
    public readonly Vector4 SphericalRotation2;
    public readonly uint StructSize;
    internal readonly NativeSceneImageEffectFlags Flags;
    private readonly uint Reserved0;
    private readonly uint Reserved1;

    internal bool HasCanonicalFields =>
        StructSize == Unsafe.SizeOf<NativeSceneImageEffect>() &&
        (Flags & ~NativeSceneImageEffectFlags.UnfilterablePlanar) == 0 &&
        Reserved0 == 0U && Reserved1 == 0U &&
        IsFinite(ColorMatrixRed) && IsFinite(ColorMatrixGreen) &&
        IsFinite(ColorMatrixBlue) && IsFinite(ColorMatrixAlpha) &&
        IsFinite(ColorMatrixOffset) && IsFinite(Effects0) &&
        IsFinite(Effects1) && IsFinite(Texture0) && IsFinite(Flags0) &&
        IsFinite(YuvRange) && IsFinite(YuvRed) && IsFinite(YuvGreen) &&
        IsFinite(YuvBlue) && IsFinite(Spherical0) &&
        IsFinite(SphericalUvRect) && IsFinite(SphericalRotation0) &&
        IsFinite(SphericalRotation1) && IsFinite(SphericalRotation2) &&
        Effects1.Z >= 0f && Effects1.Z <=
            GpuTextureGaussianBlur.MaximumStandardDeviation &&
        IsBinary(Effects1.W) &&
        Texture0.X > 0f && Texture0.Y > 0f &&
        Texture0.Z == 0f && Texture0.W == 0f &&
        IsBinary(Flags0.X) && IsBinary(Flags0.Y) &&
        IsBinary(Flags0.Z) && IsBinary(Flags0.W) &&
        IsBinary(Spherical0.X);

    private static bool IsFinite(Vector4 value) =>
        float.IsFinite(value.X) && float.IsFinite(value.Y) &&
        float.IsFinite(value.Z) && float.IsFinite(value.W);

    private static bool IsBinary(float value) => value == 0f || value == 1f;
}

/// <summary>
/// Pointer-free state referenced by semantic save and draw commands.
/// </summary>
/// <remarks>
/// The transform and opacity are absolute. A save command makes its referenced
/// state current until the matching restore; a draw command uses its state for
/// that draw only. Clip coordinates are logical target coordinates. A mask
/// reference names a preceding typed layer-mask resource and applies coverage
/// independently to each draw using this state.
/// </remarks>
[StructLayout(LayoutKind.Sequential)]
public readonly struct NativeSceneState
{
    public NativeSceneState(
        Matrix3x2 transform,
        float opacity = 1f,
        NativeSceneStateFlags flags = NativeSceneStateFlags.None,
        NativeImageRect clipRect = default,
        uint maskResourceIndex = 0U,
        uint guidelineResourceIndex = 0U)
    {
        StructSize = (uint)Unsafe.SizeOf<NativeSceneState>();
        Flags = flags;
        Transform = transform;
        Opacity = opacity;
        Reserved = 0U;
        ClipRect = clipRect;
        MaskResourceIndex = maskResourceIndex;
        GuidelineResourceIndex = guidelineResourceIndex;
    }

    public static NativeSceneState Identity => new(Matrix3x2.Identity);

    public readonly uint StructSize;
    public readonly NativeSceneStateFlags Flags;
    public readonly Matrix3x2 Transform;
    public readonly float Opacity;
    private readonly uint Reserved;
    public readonly NativeImageRect ClipRect;
    public readonly uint MaskResourceIndex;
    public readonly uint GuidelineResourceIndex;

    internal bool HasCanonicalReservedFields =>
        Reserved == 0U;
}

[StructLayout(LayoutKind.Sequential)]
internal readonly struct NativeSceneGuidelineSetHeader
{
    internal NativeSceneGuidelineSetHeader(
        uint xCount,
        uint yCount,
        NativeSceneGuidelineSetFlags flags)
    {
        StructSize = (uint)Unsafe.SizeOf<NativeSceneGuidelineSetHeader>();
        Flags = flags;
        GuidelineXCount = xCount;
        GuidelineYCount = yCount;
    }

    internal readonly uint StructSize;
    internal readonly NativeSceneGuidelineSetFlags Flags;
    internal readonly uint GuidelineXCount;
    internal readonly uint GuidelineYCount;
}

/// <summary>
/// Pointer-free state for one semantic isolated-layer scope.
/// </summary>
/// <remarks>
/// Bounds are logical target coordinates. Mask and effect indices reference
/// preceding typed resources or use <see cref="uint.MaxValue"/> to disable the
/// feature. Revisions are retained identity hints; zero disables the
/// corresponding hint. For <see cref="NativeSceneLayerFlags.CacheContent"/>,
/// composite revision is the stable owner identity and content revision is the
/// subtree pixel version; both must be nonzero. A local-space cache uses
/// <see cref="CompositeStateResourceIndex"/> to reference a preceding
/// transform/clip/guideline <see cref="NativeSceneState"/> resource while
/// retaining the exact 64-byte layer ABI. Its optional
/// <see cref="MaskResourceIndex"/> is
/// applied while compositing the retained page and does not invalidate cached
/// content; effects remain unsupported on local cached layers.
/// <see cref="NativeSceneLayerFlags.CacheNearest"/> selects nearest-neighbor
/// filtering, while <see cref="NativeSceneLayerFlags.CacheFant"/> selects
/// bounded WPF-compatible high-quality minification for that cached-page
/// composite. <see cref="NativeSceneLayerFlags.CompositeState"/> applies an
/// identity-transform, clip-only state from
/// <see cref="CompositeStateResourceIndex"/> when a materialized non-local
/// layer is restored, allowing effects to receive unclipped input and clip only
/// their final output.
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
        ulong compositeRevision = 0U,
        uint compositeStateResourceIndex = 0U)
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
        CompositeStateResourceIndex = compositeStateResourceIndex;
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
    public readonly uint CompositeStateResourceIndex;
    private readonly uint Reserved1;

    internal bool HasCanonicalReservedFields =>
        ((Flags & (NativeSceneLayerFlags.CacheLocalSpace |
                NativeSceneLayerFlags.CompositeState)) != 0 ||
            CompositeStateResourceIndex == 0U) && Reserved1 == 0U;
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
/// Fixed-capacity intersection of two to four analytic scene masks.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public readonly struct NativeSceneLayerMaskChain
{
    public const int MaximumMaskCount = 4;

    public NativeSceneLayerMaskChain(scoped ReadOnlySpan<NativeSceneLayerMask> masks)
    {
        if (masks.Length is < 2 or > MaximumMaskCount)
        {
            throw new ArgumentOutOfRangeException(nameof(masks));
        }
        StructSize = (uint)Unsafe.SizeOf<NativeSceneLayerMaskChain>();
        Kind = NativeSceneLayerMaskKind.AnalyticChain;
        Flags = 0U;
        MaskCount = (uint)masks.Length;
        Mask0 = masks[0];
        Mask1 = masks[1];
        Mask2 = masks.Length > 2 ? masks[2] : default;
        Mask3 = masks.Length > 3 ? masks[3] : default;
    }

    public readonly uint StructSize;
    public readonly NativeSceneLayerMaskKind Kind;
    public readonly uint Flags;
    public readonly uint MaskCount;
    public readonly NativeSceneLayerMask Mask0;
    public readonly NativeSceneLayerMask Mask1;
    public readonly NativeSceneLayerMask Mask2;
    public readonly NativeSceneLayerMask Mask3;

    internal bool HasCanonicalTrailingMasks =>
        (MaskCount >= 3U || IsZero(in Mask2)) &&
        (MaskCount >= 4U || IsZero(in Mask3));

    internal NativeSceneLayerMask GetMask(int index) => index switch
    {
        0 => Mask0,
        1 => Mask1,
        2 => Mask2,
        3 => Mask3,
        _ => throw new ArgumentOutOfRangeException(nameof(index))
    };

    private static bool IsZero(in NativeSceneLayerMask mask)
    {
        ReadOnlySpan<NativeSceneLayerMask> value =
            MemoryMarshal.CreateReadOnlySpan(ref Unsafe.AsRef(in mask), 1);
        foreach (byte item in MemoryMarshal.AsBytes(value))
        {
            if (item != 0)
            {
                return false;
            }
        }
        return true;
    }
}

/// <summary>
/// Pointer-free prefix for an ordered retained vector coverage mask. Its
/// auxiliary span stores <see cref="NativeSceneClipPath"/> records followed
/// by their shared <see cref="NativePathSegment"/> arena.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public readonly struct NativeSceneLayerVectorMask
{
    public NativeSceneLayerVectorMask(
        uint pathCount,
        uint segmentCount,
        float opacity = 1f,
        uint booleanNodeCount = 0U)
    {
        StructSize = (uint)Unsafe.SizeOf<NativeSceneLayerVectorMask>();
        Kind = NativeSceneLayerMaskKind.VectorClipChain;
        Flags = 0U;
        PathCount = pathCount;
        SegmentCount = segmentCount;
        Opacity = opacity;
        BooleanNodeCount = booleanNodeCount;
        Reserved1 = 0U;
    }

    public readonly uint StructSize;
    public readonly NativeSceneLayerMaskKind Kind;
    public readonly uint Flags;
    public readonly uint PathCount;
    public readonly uint SegmentCount;
    public readonly float Opacity;
    public readonly uint BooleanNodeCount;
    private readonly uint Reserved1;

    internal bool HasCanonicalReservedFields => Reserved1 == 0U;
}

/// <summary>
/// Pointer-free retained brush opacity mask. The auxiliary resource span owns
/// exactly <see cref="GradientStopCount"/> canonical gradient-stop records.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public readonly struct NativeSceneLayerBrushMask
{
    public NativeSceneLayerBrushMask(
        NativeImageRect bounds,
        Matrix3x2 transform,
        in NativeSceneBrush brush,
        uint gradientStopCount,
        float opacity = 1f)
    {
        StructSize = (uint)Unsafe.SizeOf<NativeSceneLayerBrushMask>();
        Kind = NativeSceneLayerMaskKind.Brush;
        Flags = 0U;
        GradientStopCount = gradientStopCount;
        Bounds = bounds;
        Transform = transform;
        Opacity = opacity;
        Reserved0 = 0U;
        Brush = brush;
    }

    public readonly uint StructSize;
    public readonly NativeSceneLayerMaskKind Kind;
    public readonly uint Flags;
    public readonly uint GradientStopCount;
    public readonly NativeImageRect Bounds;
    public readonly Matrix3x2 Transform;
    public readonly float Opacity;
    private readonly uint Reserved0;
    public readonly NativeSceneBrush Brush;

    internal bool HasCanonicalReservedFields => Reserved0 == 0U;
}

/// <summary>
/// Pointer-free retained stroked-geometry opacity mask. Primitive and stop
/// offsets address the owning resource auxiliary arenas.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public readonly struct NativeSceneLayerGeometryMask
{
    public NativeSceneLayerGeometryMask(
        uint primitiveOffset,
        uint primitiveCount,
        NativeImageRect bounds,
        Matrix3x2 transform,
        in NativeSceneBrush brush,
        uint gradientStopCount,
        float opacity = 1f)
    {
        StructSize = (uint)Unsafe.SizeOf<NativeSceneLayerGeometryMask>();
        Kind = NativeSceneLayerMaskKind.Geometry;
        Flags = 0U;
        PrimitiveOffset = primitiveOffset;
        PrimitiveCount = primitiveCount;
        GradientStopCount = gradientStopCount;
        Reserved0 = 0U;
        Reserved1 = 0U;
        Bounds = bounds;
        Transform = transform;
        Opacity = opacity;
        Reserved2 = 0U;
        Brush = brush;
    }

    public readonly uint StructSize;
    public readonly NativeSceneLayerMaskKind Kind;
    public readonly uint Flags;
    public readonly uint PrimitiveOffset;
    public readonly uint PrimitiveCount;
    public readonly uint GradientStopCount;
    private readonly uint Reserved0;
    private readonly uint Reserved1;
    public readonly NativeImageRect Bounds;
    public readonly Matrix3x2 Transform;
    public readonly float Opacity;
    private readonly uint Reserved2;
    public readonly NativeSceneBrush Brush;

    internal bool HasCanonicalReservedFields =>
        Reserved0 == 0U && Reserved1 == 0U && Reserved2 == 0U;
}

/// <summary>
/// Pointer-free retained picture opacity mask. The stream range addresses one
/// independently validated nested semantic scene in the resource auxiliary
/// arena.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public readonly struct NativeSceneLayerPictureMask
{
    public NativeSceneLayerPictureMask(
        uint streamOffset,
        uint streamSize,
        NativeImageRect bounds,
        Matrix3x2 transform,
        float opacity = 1f)
    {
        StructSize = (uint)Unsafe.SizeOf<NativeSceneLayerPictureMask>();
        Kind = NativeSceneLayerMaskKind.Picture;
        Flags = 0U;
        StreamOffset = streamOffset;
        StreamSize = streamSize;
        Reserved0 = 0U;
        Bounds = bounds;
        Transform = transform;
        Opacity = opacity;
        Reserved1 = 0U;
    }

    public readonly uint StructSize;
    public readonly NativeSceneLayerMaskKind Kind;
    public readonly uint Flags;
    public readonly uint StreamOffset;
    public readonly uint StreamSize;
    private readonly uint Reserved0;
    public readonly NativeImageRect Bounds;
    public readonly Matrix3x2 Transform;
    public readonly float Opacity;
    private readonly uint Reserved1;

    internal bool HasCanonicalReservedFields =>
        Reserved0 == 0U && Reserved1 == 0U;
}

/// <summary>
/// Pointer-free retained intersection of arbitrary GPU-generated vector,
/// brush, and stroked-geometry masks. Its auxiliary span owns the mask
/// descriptors, geometry primitives, vector records, and one shared
/// resource-local gradient-stop arena.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public readonly struct NativeSceneLayerCompositeMask
{
    public const uint MaximumComponentCount = 64U;

    public NativeSceneLayerCompositeMask(
        uint componentCount,
        uint brushMaskCount,
        uint pathCount,
        uint segmentCount,
        uint booleanNodeCount,
        uint gradientStopCount,
        uint geometryMaskCount = 0U,
        uint geometryPrimitiveCount = 0U,
        uint pictureMaskCount = 0U,
        uint pictureStreamBytes = 0U,
        float opacity = 1f)
    {
        StructSize = (uint)Unsafe.SizeOf<NativeSceneLayerCompositeMask>();
        Kind = NativeSceneLayerMaskKind.Composite;
        Flags = 0U;
        ComponentCount = componentCount;
        BrushMaskCount = brushMaskCount;
        PathCount = pathCount;
        SegmentCount = segmentCount;
        BooleanNodeCount = booleanNodeCount;
        GradientStopCount = gradientStopCount;
        Opacity = opacity;
        GeometryMaskCount = geometryMaskCount;
        GeometryPrimitiveCount = geometryPrimitiveCount;
        PictureMaskCount = pictureMaskCount;
        PictureStreamBytes = pictureStreamBytes;
        Reserved0 = 0U;
        Reserved1 = 0U;
    }

    public readonly uint StructSize;
    public readonly NativeSceneLayerMaskKind Kind;
    public readonly uint Flags;
    public readonly uint ComponentCount;
    public readonly uint BrushMaskCount;
    public readonly uint PathCount;
    public readonly uint SegmentCount;
    public readonly uint BooleanNodeCount;
    public readonly uint GradientStopCount;
    public readonly float Opacity;
    public readonly uint GeometryMaskCount;
    public readonly uint GeometryPrimitiveCount;
    public readonly uint PictureMaskCount;
    public readonly uint PictureStreamBytes;
    private readonly uint Reserved0;
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

    public static NativeSceneEffect BoxBlur(
        float radius,
        uint revision) => BoxBlur(radius, radius, revision);

    public static NativeSceneEffect BoxBlur(
        float radiusX,
        float radiusY,
        uint revision) => new(
            NativeGroupEffectKind.BoxBlur,
            radiusX,
            radiusY,
            default,
            default,
            revision);

    public static NativeSceneEffect DropShadow(
        float sigma,
        Vector2 offset,
        Vector4 color,
        uint revision) => DropShadow(
            sigma,
            sigma,
            offset,
            color,
            revision);

    public static NativeSceneEffect DropShadow(
        float sigmaX,
        float sigmaY,
        Vector2 offset,
        Vector4 color,
        uint revision) => new(
            NativeGroupEffectKind.DropShadow,
            sigmaX,
            sigmaY,
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
        uint sampleGrid = 4,
        ulong booleanNodeOffset = 0U,
        ulong booleanNodeCount = 0U)
    {
        SegmentOffset = segmentOffset;
        SegmentCount = segmentCount;
        BooleanNodeOffset = booleanNodeOffset;
        BooleanNodeCount = booleanNodeCount;
        Minimum = minimum;
        Maximum = maximum;
        Color = color;
        Transform = transform;
        FillRule = fillRule;
        SampleGrid = sampleGrid;
    }

    public readonly ulong SegmentOffset;
    public readonly ulong SegmentCount;
    public readonly ulong BooleanNodeOffset;
    public readonly ulong BooleanNodeCount;
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

    public static NativeGroupEffect BoxBlur(
        float radius,
        uint revision) => BoxBlur(radius, radius, revision);

    public static NativeGroupEffect BoxBlur(
        float radiusX,
        float radiusY,
        uint revision) => new(
            NativeGroupEffectKind.BoxBlur,
            radiusX,
            radiusY,
            default,
            default,
            revision);

    public static NativeGroupEffect DropShadow(
        float blurSigma,
        Vector2 offset,
        Vector4 color,
        uint revision) => DropShadow(
            blurSigma,
            blurSigma,
            offset,
            color,
            revision);

    public static NativeGroupEffect DropShadow(
        float blurSigmaX,
        float blurSigmaY,
        Vector2 offset,
        Vector4 color,
        uint revision) => new(
            NativeGroupEffectKind.DropShadow,
            blurSigmaX,
            blurSigmaY,
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

/// <summary>
/// Identifies one asynchronous retained GPU hit-test request.
/// </summary>
public readonly struct NativeGpuHitTestRequestToken :
    IEquatable<NativeGpuHitTestRequestToken>
{
    internal NativeGpuHitTestRequestToken(ulong value, nint owner)
    {
        Value = value;
        Owner = owner;
    }

    public ulong Value { get; }

    internal nint Owner { get; }

    public bool IsValid => Value != 0 && Owner != 0;

    public bool Equals(NativeGpuHitTestRequestToken other) =>
        Value == other.Value && Owner == other.Owner;

    public override bool Equals(object? obj) =>
        obj is NativeGpuHitTestRequestToken other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(Value, Owner);

    public static bool operator ==(
        NativeGpuHitTestRequestToken left,
        NativeGpuHitTestRequestToken right) => left.Equals(right);

    public static bool operator !=(
        NativeGpuHitTestRequestToken left,
        NativeGpuHitTestRequestToken right) => !left.Equals(right);
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

/// <summary>
/// Compact retained point-batch metadata. The point range addresses the
/// owning resource's auxiliary <see cref="Vector2"/> array.
/// </summary>
[StructLayout(LayoutKind.Explicit, Size = 64)]
public readonly struct NativeScenePointBatch
{
    public NativeScenePointBatch(
        uint pointOffset,
        uint pointCount,
        float radius,
        Vector4 color,
        Matrix3x2 transform,
        NativePointBatchFlags flags = NativePointBatchFlags.None)
    {
        StructSize = (uint)Unsafe.SizeOf<NativeScenePointBatch>();
        Flags = flags;
        PointOffset = pointOffset;
        PointCount = pointCount;
        Radius = radius;
        Reserved = 0f;
        Color = color;
        Transform = transform;
    }

    [FieldOffset(0)] public readonly uint StructSize;
    [FieldOffset(4)] public readonly NativePointBatchFlags Flags;
    [FieldOffset(8)] public readonly uint PointOffset;
    [FieldOffset(12)] public readonly uint PointCount;
    [FieldOffset(16)] public readonly float Radius;
    [FieldOffset(20)] private readonly float Reserved;
    [FieldOffset(24)] public readonly Vector4 Color;
    [FieldOffset(40)] public readonly Matrix3x2 Transform;

    internal readonly bool HasCanonicalReservedField => Reserved == 0f;
}

/// <summary>
/// Compact retained vertex-mesh metadata. Vertex and 16-bit index ranges
/// address the two packed arrays in the owning resource auxiliary arena.
/// </summary>
[StructLayout(LayoutKind.Explicit, Size = 64)]
public readonly struct NativeSceneVertexMesh
{
    public NativeSceneVertexMesh(
        uint vertexOffset,
        uint vertexCount,
        uint indexOffset,
        uint indexCount,
        Matrix3x2 transform,
        NativeVertexMeshTopology topology = NativeVertexMeshTopology.Triangles,
        NativeVertexColorBlendMode colorBlendMode =
            NativeVertexColorBlendMode.Modulate,
        NativeVertexMeshFlags flags = NativeVertexMeshFlags.None)
    {
        StructSize = (uint)Unsafe.SizeOf<NativeSceneVertexMesh>();
        Flags = flags;
        Topology = topology;
        ColorBlendMode = colorBlendMode;
        VertexOffset = vertexOffset;
        VertexCount = vertexCount;
        IndexOffset = indexOffset;
        IndexCount = indexCount;
        Transform = transform;
        Reserved0 = 0U;
        Reserved1 = 0U;
    }

    [FieldOffset(0)] public readonly uint StructSize;
    [FieldOffset(4)] public readonly NativeVertexMeshFlags Flags;
    [FieldOffset(8)] public readonly NativeVertexMeshTopology Topology;
    [FieldOffset(12)] public readonly NativeVertexColorBlendMode ColorBlendMode;
    [FieldOffset(16)] public readonly uint VertexOffset;
    [FieldOffset(20)] public readonly uint VertexCount;
    [FieldOffset(24)] public readonly uint IndexOffset;
    [FieldOffset(28)] public readonly uint IndexCount;
    [FieldOffset(32)] public readonly Matrix3x2 Transform;
    [FieldOffset(56)] private readonly uint Reserved0;
    [FieldOffset(60)] private readonly uint Reserved1;

    internal readonly bool HasCanonicalReservedFields =>
        Reserved0 == 0U && Reserved1 == 0U;
}

[StructLayout(LayoutKind.Sequential)]
public readonly struct NativeSceneMeshVertex
{
    public NativeSceneMeshVertex(
        Vector2 position,
        Vector2 textureCoordinate,
        Vector4 color)
    {
        Position = position;
        TextureCoordinate = textureCoordinate;
        Color = color;
    }

    public readonly Vector2 Position;
    public readonly Vector2 TextureCoordinate;
    public readonly Vector4 Color;
}

[StructLayout(LayoutKind.Explicit, Size = 160)]
public readonly struct NativeSceneStroke
{
    public NativeSceneStroke(
        NativeSceneStrokeKind kind,
        ulong pointOffset,
        ulong pointCount,
        Matrix3x2 transform,
        float strokeThickness,
        float miterLimit,
        NativePolylineFlags flags = NativePolylineFlags.None,
        uint degree = 0U,
        ulong knotOffset = 0U,
        ulong knotCount = 0U,
        ulong weightOffset = 0U,
        ulong weightCount = 0U,
        ulong dashIntervalOffset = 0U,
        ulong dashIntervalCount = 0U,
        double dashOffset = 0.0,
        NativeStrokeCap startCap = NativeStrokeCap.Flat,
        NativeStrokeCap endCap = NativeStrokeCap.Flat,
        NativeStrokeJoin lineJoin = NativeStrokeJoin.Miter,
        NativeStrokeCap dashCap = NativeStrokeCap.Flat,
        Vector4 color = default)
    {
        StructSize = (uint)Unsafe.SizeOf<NativeSceneStroke>();
        Kind = kind;
        Flags = flags & ~(
            NativePolylineFlags.StartCapMask |
            NativePolylineFlags.EndCapMask |
            NativePolylineFlags.JoinMask);
        Degree = degree;
        PointOffset = pointOffset;
        PointCount = pointCount;
        KnotOffset = knotOffset;
        KnotCount = knotCount;
        WeightOffset = weightOffset;
        WeightCount = weightCount;
        DashIntervalOffset = dashIntervalOffset;
        DashIntervalCount = dashIntervalCount;
        Color = color;
        Transform = transform;
        StrokeThickness = strokeThickness;
        MiterLimit = miterLimit;
        DashOffset = dashOffset;
        StartCap = startCap;
        EndCap = endCap;
        LineJoin = lineJoin;
        DashCap = dashCap;
        Reserved0 = 0U;
        Reserved1 = 0U;
    }

    [FieldOffset(0)] public readonly uint StructSize;
    [FieldOffset(4)] public readonly NativeSceneStrokeKind Kind;
    [FieldOffset(8)] public readonly NativePolylineFlags Flags;
    [FieldOffset(12)] public readonly uint Degree;
    [FieldOffset(16)] public readonly ulong PointOffset;
    [FieldOffset(24)] public readonly ulong PointCount;
    [FieldOffset(32)] public readonly ulong KnotOffset;
    [FieldOffset(40)] public readonly ulong KnotCount;
    [FieldOffset(48)] public readonly ulong WeightOffset;
    [FieldOffset(56)] public readonly ulong WeightCount;
    [FieldOffset(64)] public readonly ulong DashIntervalOffset;
    [FieldOffset(72)] public readonly ulong DashIntervalCount;
    [FieldOffset(80)] public readonly Vector4 Color;
    [FieldOffset(96)] public readonly Matrix3x2 Transform;
    [FieldOffset(120)] public readonly float StrokeThickness;
    [FieldOffset(124)] public readonly float MiterLimit;
    [FieldOffset(128)] public readonly double DashOffset;
    [FieldOffset(136)] public readonly NativeStrokeCap StartCap;
    [FieldOffset(140)] public readonly NativeStrokeCap EndCap;
    [FieldOffset(144)] public readonly NativeStrokeJoin LineJoin;
    [FieldOffset(148)] public readonly NativeStrokeCap DashCap;
    [FieldOffset(152)] private readonly uint Reserved0;
    [FieldOffset(156)] private readonly uint Reserved1;

    internal readonly bool HasCanonicalReservedFields =>
        Reserved0 == 0U && Reserved1 == 0U;
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
        uint sampleGrid = 4,
        nuint booleanNodeOffset = 0U,
        nuint booleanNodeCount = 0U)
    {
        SegmentOffset = segmentOffset;
        SegmentCount = segmentCount;
        BooleanNodeOffset = booleanNodeOffset;
        BooleanNodeCount = booleanNodeCount;
        Minimum = minimum;
        Maximum = maximum;
        Color = color;
        Transform = transform;
        FillRule = fillRule;
        SampleGrid = sampleGrid;
    }

    public readonly nuint SegmentOffset;
    public readonly nuint SegmentCount;
    public readonly nuint BooleanNodeOffset;
    public readonly nuint BooleanNodeCount;
    public readonly Vector2 Minimum;
    public readonly Vector2 Maximum;
    public readonly Vector4 Color;
    public readonly Matrix3x2 Transform;
    public readonly NativeFillRule FillRule;
    public readonly uint SampleGrid;
}

[StructLayout(LayoutKind.Sequential)]
public readonly struct NativeSceneClipPath
{
    public NativeSceneClipPath(
        ulong segmentOffset,
        ulong segmentCount,
        Vector2 minimum,
        Vector2 maximum,
        Matrix3x2 transform,
        NativeClipOperation operation = NativeClipOperation.Intersect,
        NativeFillRule fillRule = NativeFillRule.NonZero,
        uint sampleGrid = 4,
        ulong booleanNodeOffset = 0U,
        ulong booleanNodeCount = 0U)
    {
        SegmentOffset = segmentOffset;
        SegmentCount = segmentCount;
        BooleanNodeOffset = booleanNodeOffset;
        BooleanNodeCount = booleanNodeCount;
        Minimum = minimum;
        Maximum = maximum;
        Transform = transform;
        FillRule = fillRule;
        SampleGrid = sampleGrid;
        Operation = operation;
        Reserved = 0U;
    }

    public readonly ulong SegmentOffset;
    public readonly ulong SegmentCount;
    public readonly ulong BooleanNodeOffset;
    public readonly ulong BooleanNodeCount;
    public readonly Vector2 Minimum;
    public readonly Vector2 Maximum;
    public readonly Matrix3x2 Transform;
    public readonly NativeFillRule FillRule;
    public readonly uint SampleGrid;
    public readonly NativeClipOperation Operation;
    private readonly uint Reserved;

    internal bool HasCanonicalReservedField => Reserved == 0U;
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
        uint sampleGrid = 4,
        nuint booleanNodeOffset = 0U,
        nuint booleanNodeCount = 0U)
    {
        SegmentOffset = segmentOffset;
        SegmentCount = segmentCount;
        BooleanNodeOffset = booleanNodeOffset;
        BooleanNodeCount = booleanNodeCount;
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
    public readonly nuint BooleanNodeOffset;
    public readonly nuint BooleanNodeCount;
    public readonly Vector2 Minimum;
    public readonly Vector2 Maximum;
    public readonly Matrix3x2 Transform;
    public readonly NativeFillRule FillRule;
    public readonly uint SampleGrid;
    public readonly NativeClipOperation Operation;
    private readonly uint Reserved;
}

[StructLayout(LayoutKind.Sequential)]
public readonly struct NativeScenePathBooleanNode
{
    public NativeScenePathBooleanNode(
        ulong segmentOffset,
        ulong segmentCount,
        Vector2 minimum,
        Vector2 maximum,
        NativeFillRule fillRule,
        NativePathBooleanNodeKind kind)
    {
        SegmentOffset = segmentOffset;
        SegmentCount = segmentCount;
        Minimum = minimum;
        Maximum = maximum;
        FillRule = fillRule;
        Kind = kind;
        Reserved0 = 0U;
        Reserved1 = 0U;
    }

    public readonly ulong SegmentOffset;
    public readonly ulong SegmentCount;
    public readonly Vector2 Minimum;
    public readonly Vector2 Maximum;
    public readonly NativeFillRule FillRule;
    public readonly NativePathBooleanNodeKind Kind;
    private readonly uint Reserved0;
    private readonly uint Reserved1;

    internal bool HasCanonicalReservedFields =>
        Reserved0 == 0U && Reserved1 == 0U;
}

[StructLayout(LayoutKind.Sequential)]
public readonly struct NativePathBooleanNode
{
    public NativePathBooleanNode(
        nuint segmentOffset,
        nuint segmentCount,
        Vector2 minimum,
        Vector2 maximum,
        NativeFillRule fillRule,
        NativePathBooleanNodeKind kind)
    {
        SegmentOffset = segmentOffset;
        SegmentCount = segmentCount;
        Minimum = minimum;
        Maximum = maximum;
        FillRule = fillRule;
        Kind = kind;
        Reserved0 = 0U;
        Reserved1 = 0U;
    }

    public readonly nuint SegmentOffset;
    public readonly nuint SegmentCount;
    public readonly Vector2 Minimum;
    public readonly Vector2 Maximum;
    public readonly NativeFillRule FillRule;
    public readonly NativePathBooleanNodeKind Kind;
    private readonly uint Reserved0;
    private readonly uint Reserved1;
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
    private readonly NativePathBooleanNode[] _booleanNodes;

    public NativeClipChain(
        ReadOnlySpan<NativeClipPath> paths,
        ReadOnlySpan<NativePathSegment> segments,
        ReadOnlySpan<NativePathBooleanNode> booleanNodes = default)
    {
        if (paths.IsEmpty)
            throw new ArgumentException("A native clip chain requires at least one path.", nameof(paths));
        if (paths.Length > 64)
            throw new ArgumentOutOfRangeException(nameof(paths));
        if (segments.IsEmpty)
            throw new ArgumentException("A native clip chain requires path segments.", nameof(segments));
        if (booleanNodes.Length > 64 * 63)
            throw new ArgumentOutOfRangeException(nameof(booleanNodes));

        nuint segmentLength = (nuint)segments.Length;
        nuint booleanNodeLength = (nuint)booleanNodes.Length;
        nuint expectedBooleanNodeOffset = 0U;
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
                path.Operation > NativeClipOperation.Difference ||
                (path.BooleanNodeCount != 0U &&
                    path.BooleanNodeOffset != expectedBooleanNodeOffset) ||
                !IsValidBooleanProgram(
                    in path,
                    booleanNodes,
                    segmentLength,
                    booleanNodeLength))
            {
                throw new ArgumentException(
                    $"Clip path {index} is invalid or references segments outside the retained arena.",
                    nameof(paths));
            }
            expectedBooleanNodeOffset += path.BooleanNodeCount;
        }
        if (expectedBooleanNodeOffset != booleanNodeLength)
            throw new ArgumentException(
                "Every retained boolean node must belong to exactly one clip path.",
                nameof(booleanNodes));
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
        _booleanNodes = GC.AllocateUninitializedArray<NativePathBooleanNode>(
            booleanNodes.Length,
            pinned: true);
        paths.CopyTo(_paths);
        segments.CopyTo(_segments);
        booleanNodes.CopyTo(_booleanNodes);
    }

    public int PathCount => _paths.Length;
    public int SegmentCount => _segments.Length;
    public int BooleanNodeCount => _booleanNodes.Length;

    internal NativeClipPath* Paths =>
        (NativeClipPath*)Unsafe.AsPointer(
            ref MemoryMarshal.GetArrayDataReference(_paths));

    internal NativePathSegment* Segments =>
        (NativePathSegment*)Unsafe.AsPointer(
            ref MemoryMarshal.GetArrayDataReference(_segments));

    internal NativePathBooleanNode* BooleanNodes =>
        _booleanNodes.Length == 0
            ? null
            : (NativePathBooleanNode*)Unsafe.AsPointer(
                ref MemoryMarshal.GetArrayDataReference(_booleanNodes));

    private static bool IsFinite(Vector2 value) =>
        float.IsFinite(value.X) && float.IsFinite(value.Y);

    private static bool IsFinite(Matrix3x2 value) =>
        float.IsFinite(value.M11) && float.IsFinite(value.M12) &&
        float.IsFinite(value.M21) && float.IsFinite(value.M22) &&
        float.IsFinite(value.M31) && float.IsFinite(value.M32);

    private static bool IsValidBooleanProgram(
        in NativeClipPath path,
        ReadOnlySpan<NativePathBooleanNode> nodes,
        nuint segmentCount,
        nuint nodeCount)
    {
        if (path.BooleanNodeCount == 0U)
            return path.BooleanNodeOffset == 0U;
        if (path.BooleanNodeCount > 63U ||
            path.BooleanNodeOffset > nodeCount ||
            path.BooleanNodeCount > nodeCount - path.BooleanNodeOffset)
            return false;
        int stackDepth = 0;
        nuint pathSegmentEnd = path.SegmentOffset + path.SegmentCount;
        int start = checked((int)path.BooleanNodeOffset);
        int end = checked(start + (int)path.BooleanNodeCount);
        for (int index = start; index < end; index++)
        {
            NativePathBooleanNode node = nodes[index];
            if (node.Kind > NativePathBooleanNodeKind.ReverseDifference)
                return false;
            if (node.Kind == NativePathBooleanNodeKind.Leaf)
            {
                if (stackDepth == 16 || node.SegmentCount == 0U ||
                    node.SegmentOffset < path.SegmentOffset ||
                    node.SegmentOffset > pathSegmentEnd ||
                    node.SegmentCount > pathSegmentEnd - node.SegmentOffset ||
                    !IsFinite(node.Minimum) || !IsFinite(node.Maximum) ||
                    node.Maximum.X <= node.Minimum.X ||
                    node.Maximum.Y <= node.Minimum.Y ||
                    node.FillRule > NativeFillRule.EvenOdd)
                    return false;
                stackDepth++;
            }
            else if (node.Kind == NativePathBooleanNodeKind.Empty)
            {
                if (stackDepth == 16 || node.SegmentOffset != 0U ||
                    node.SegmentCount != 0U || node.Minimum != Vector2.Zero ||
                    node.Maximum != Vector2.Zero ||
                    node.FillRule != NativeFillRule.NonZero)
                    return false;
                stackDepth++;
            }
            else
            {
                if (stackDepth < 2 || node.SegmentOffset != 0U ||
                    node.SegmentCount != 0U || node.Minimum != Vector2.Zero ||
                    node.Maximum != Vector2.Zero ||
                    node.FillRule != NativeFillRule.NonZero)
                    return false;
                stackDepth--;
            }
        }
        return stackDepth == 1;
    }
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

/// <summary>
/// Logical-coordinate rectangle whose existing target contents are preserved
/// outside the damaged area during flat semantic scene replay. Replay does not
/// clear the damaged area; callers must ensure it is repainted opaquely before
/// translucent or blended content, or request a full frame.
/// </summary>
public readonly record struct NativeSceneDamageRect(
    float X,
    float Y,
    float Width,
    float Height);

/// <summary>
/// A host-owned WebGPU texture view used as a semantic-scene render target.
/// </summary>
/// <remarks>
/// The view must be a live, single-sample render attachment with the format
/// configured on the <see cref="NativeCompositor"/> and must belong to that
/// compositor's device. The caller retains ownership and must keep the view
/// alive through the <c>RenderScene</c> call and its queue submission, then
/// follow the owning surface API's present/release contract.
/// </remarks>
public readonly record struct NativeSceneExternalTarget(
    nuint TextureView,
    uint Width,
    uint Height);

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
