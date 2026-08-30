using Microsoft.Win32.SafeHandles;
using System.Numerics;
using System.Runtime.InteropServices;

namespace ProGPU.Direct2D;

[Flags]
public enum ProGpuDirect2DSurfaceFlags : uint
{
    None = 0,
    EnableDebug = 1U << 0,
    AllowWarpFallback = 1U << 1,
    ForceWarp = 1U << 2
}

[Flags]
public enum ProGpuDirect2DDescriptorFlags : uint
{
    None = 0,
    KeyedMutex = 1U << 0,
    NtHandle = 1U << 1,
    SoftwareAdapter = 1U << 2
}

[Flags]
public enum ProGpuDirect2DDeviceLossFlags : uint
{
    None = 0,
    RemovalEventRegistered = 1U << 0,
    RemovalEventSignaled = 1U << 1,
    DeviceLost = 1U << 2
}

public enum ProGpuDirect2DInterfaceKind
{
    D3D11Device = 1,
    D3D11DeviceContext = 2,
    DxgiAdapter1 = 3,
    DxgiDevice = 4,
    DxgiSurface = 5,
    DxgiKeyedMutex = 6,
    D3D11Texture2D = 7,
    D2D1Factory1 = 8,
    D2D1Factory2 = 9,
    D2D1Device = 10,
    D2D1Device1 = 11,
    D2D1DeviceContext = 12,
    D2D1DeviceContext1 = 13,
    D2D1Bitmap = 14,
    D2D1Bitmap1 = 15,
    WinRtDirect3D11Device = 16,
    Win2DCanvasDevice = 17,
    Win2DCanvasRenderTarget = 18,
    D2D1SolidColorBrush = 19,
    Win2DCanvasSolidColorBrush = 20,
    D2D1GradientStopCollection1 = 21,
    D2D1LinearGradientBrush = 22,
    Win2DCanvasLinearGradientBrush = 23,
    D2D1RadialGradientBrush = 24,
    Win2DCanvasRadialGradientBrush = 25,
    D2D1Geometry = 26,
    D2D1RectangleGeometry = 27,
    D2D1RoundedRectangleGeometry = 28,
    D2D1EllipseGeometry = 29,
    D2D1PathGeometry1 = 30,
    D2D1TransformedGeometry = 31,
    Win2DCanvasGeometry = 32,
    D2D1StrokeStyle1 = 33,
    Win2DCanvasStrokeStyle = 34,
    D2D1BitmapBrush1 = 35,
    Win2DCanvasBitmap = 36,
    Win2DCanvasImageBrush = 37,
    D2D1ImageBrush = 38,
    D2D1CommandList = 39,
    Win2DCanvasCommandList = 40,
    D2D1Effect = 41,
    D2D1Image = 42,
    D2D1Layer = 43,
    D2D1DrawingStateBlock1 = 44,
    DWriteFactory3 = 45,
    DWriteTextFormat1 = 46,
    Win2DCanvasTextFormat = 47,
    DWriteTextLayout4 = 48,
    Win2DCanvasTextLayout = 49,
    DWriteTypography = 50,
    Win2DCanvasTypography = 51,
    DWriteFontFaceReference = 52,
    Win2DCanvasFontFace = 53,
    DWriteFontFace5 = 54,
    D2D1DeviceContext4 = 55,
    D2D1DeviceContext7 = 56,
    D2D1DeviceContext5 = 57,
    D2D1SvgDocument = 58,
    Win2DCanvasSvgDocument = 59,
    D2D1GeometryRealization = 60
}

public enum ProGpuDirect2DColorGlyphPath : uint
{
    DeviceContext7 = 1,
    TranslatedDeviceContext4 = 2,
    MonochromeNoColor = 3
}

/// <summary>
/// Fixed-layout ID2D1Properties value kinds accepted by the native effect ABI.
/// Pointer-bearing string, IUnknown, array, and color-context values are
/// intentionally excluded.
/// </summary>
public enum ProGpuDirect2DEffectPropertyType : uint
{
    Bool = 2,
    UInt32 = 3,
    Int32 = 4,
    Float = 5,
    Vector2 = 6,
    Vector3 = 7,
    Vector4 = 8,
    Blob = 9,
    Enum = 11,
    Clsid = 13,
    Matrix3X2 = 14,
    Matrix4X3 = 15,
    Matrix4X4 = 16,
    Matrix5X4 = 17
}

public enum ProGpuDirect2DGaussianBlurProperty : uint
{
    StandardDeviation = 0,
    Optimization = 1,
    BorderMode = 2
}

public enum ProGpuDirect2DShadowProperty : uint
{
    BlurStandardDeviation = 0,
    Color = 1,
    Optimization = 2
}

public static class ProGpuDirect2DBuiltInEffects
{
    public static readonly Guid GaussianBlur =
        new("1FEB6D69-2FE6-4AC9-8C58-1D7F93E7A6A5");

    public static readonly Guid Shadow =
        new("C67EA361-1863-4E69-89DB-695D3E9A5B6B");
}

public enum ProGpuDirect2DFillMode
{
    Alternate = 0,
    Winding = 1
}

public enum ProGpuDirect2DPathSegmentKind : uint
{
    Line = 0,
    Quadratic = 1,
    Cubic = 2,
    Arc = 3
}

public enum ProGpuDirect2DCombineMode
{
    Union = 0,
    Intersect = 1,
    Xor = 2,
    Exclude = 3
}

/// <summary>
/// Describes the spatial relation returned by ID2D1Geometry::CompareWithGeometry.
/// </summary>
public enum ProGpuDirect2DGeometryRelation
{
    Unknown = 0,
    Disjoint = 1,
    IsContained = 2,
    Contains = 3,
    Overlap = 4
}

public enum ProGpuDirect2DGeometrySimplificationOption
{
    CubicsAndLines = 0,
    Lines = 1
}

public enum ProGpuDirect2DCapStyle : uint
{
    Flat = 0,
    Square = 1,
    Round = 2,
    Triangle = 3
}

public enum ProGpuDirect2DLineJoin : uint
{
    Miter = 0,
    Bevel = 1,
    Round = 2,
    MiterOrBevel = 3
}

public enum ProGpuDirect2DDashStyle : uint
{
    Solid = 0,
    Dash = 1,
    Dot = 2,
    DashDot = 3,
    DashDotDot = 4,
    Custom = 5
}

public enum ProGpuDirect2DStrokeTransformType : uint
{
    Normal = 0,
    Fixed = 1,
    Hairline = 2
}

[Flags]
public enum ProGpuDirect2DPathSegmentFlags : uint
{
    None = 0,
    ForceUnstroked = 1U << 0,
    ForceRoundLineJoin = 1U << 1
}

[Flags]
public enum ProGpuDirect2DPathFigureFlags : uint
{
    None = 0,
    Filled = 1U << 0,
    Closed = 1U << 1
}

[Flags]
public enum ProGpuDirect2DArcFlags : uint
{
    None = 0,
    Clockwise = 1U << 0,
    Large = 1U << 1
}

public enum ProGpuDirect2DColorSpace
{
    Custom = 0,
    SRgb = 1,
    ScRgb = 2
}

public enum ProGpuDirect2DBufferPrecision
{
    Unknown = 0,
    Precision8UIntNormalized = 1,
    Precision8UIntNormalizedSrgb = 2,
    Precision16UIntNormalized = 3,
    Precision16Float = 4,
    Precision32Float = 5
}

public enum ProGpuDirect2DExtendMode
{
    Clamp = 0,
    Wrap = 1,
    Mirror = 2
}

public enum ProGpuDirect2DInterpolationMode : uint
{
    NearestNeighbor = 0,
    Linear = 1,
    Cubic = 2,
    MultiSampleLinear = 3,
    Anisotropic = 4,
    HighQualityCubic = 5
}

public enum ProGpuDirect2DAntialiasMode : uint
{
    PerPrimitive = 0,
    Aliased = 1
}

public enum ProGpuDirect2DTextAntialiasMode : uint
{
    Default = 0,
    ClearType = 1,
    Grayscale = 2,
    Aliased = 3
}

public enum ProGpuDirect2DPrimitiveBlend : uint
{
    SourceOver = 0,
    Copy = 1,
    Minimum = 2,
    Add = 3,
    Maximum = 4
}

public enum ProGpuDirect2DUnitMode : uint
{
    Dips = 0,
    Pixels = 1
}

public enum ProGpuDirect2DCompositeMode : uint
{
    SourceOver = 0,
    DestinationOver = 1,
    SourceIn = 2,
    DestinationIn = 3,
    SourceOut = 4,
    DestinationOut = 5,
    SourceAtop = 6,
    DestinationAtop = 7,
    Xor = 8,
    Plus = 9,
    SourceCopy = 10,
    BoundedSourceCopy = 11,
    MaskInvert = 12
}

public enum ProGpuDirect2DFontStyle : uint
{
    Normal = 0,
    Oblique = 1,
    Italic = 2
}

public enum ProGpuDirect2DFontStretch : uint
{
    UltraCondensed = 1,
    ExtraCondensed = 2,
    Condensed = 3,
    SemiCondensed = 4,
    Normal = 5,
    SemiExpanded = 6,
    Expanded = 7,
    ExtraExpanded = 8,
    UltraExpanded = 9
}

[Flags]
public enum ProGpuDirect2DTextRangeFormatFlags : uint
{
    None = 0,
    FontSize = 1U << 0,
    FontWeight = 1U << 1,
    FontStyle = 1U << 2,
    FontStretch = 1U << 3,
    Underline = 1U << 4,
    Strikethrough = 1U << 5,
    DrawingEffect = 1U << 6
}

public enum ProGpuDirect2DTextAlignment : uint
{
    Leading = 0,
    Trailing = 1,
    Center = 2,
    Justified = 3
}

public enum ProGpuDirect2DParagraphAlignment : uint
{
    Near = 0,
    Far = 1,
    Center = 2
}

public enum ProGpuDirect2DWordWrapping : uint
{
    Wrap = 0,
    NoWrap = 1,
    EmergencyBreak = 2,
    WholeWord = 3,
    Character = 4
}

public enum ProGpuDirect2DReadingDirection : uint
{
    LeftToRight = 0,
    RightToLeft = 1,
    TopToBottom = 2,
    BottomToTop = 3
}

public enum ProGpuDirect2DFlowDirection : uint
{
    TopToBottom = 0,
    BottomToTop = 1,
    LeftToRight = 2,
    RightToLeft = 3
}

public enum ProGpuDirect2DMeasuringMode : uint
{
    Natural = 0,
    GdiClassic = 1,
    GdiNatural = 2
}

[Flags]
public enum ProGpuDirect2DDrawTextOptions : uint
{
    None = 0,
    NoSnap = 1U << 0,
    Clip = 1U << 1,
    EnableColorFont = 1U << 2,
    DisableColorBitmapSnapping = 1U << 3
}

[Flags]
public enum ProGpuDirect2DLayerOptions : uint
{
    None = 0,
    InitializeFromBackground = 1U << 0,
    IgnoreAlpha = 1U << 1
}

public enum ProGpuDirect2DColorInterpolationMode
{
    Straight = 0,
    Premultiplied = 1
}

public enum ProGpuDirect2DStatus
{
    Success = 0,
    InvalidArgument = 1,
    OutOfMemory = 2,
    AdapterNotFound = 3,
    DeviceCreationFailed = 4,
    ResourceCreationFailed = 5,
    SynchronizationFailed = 6,
    AccessAlreadyAcquired = 7,
    AccessNotAcquired = 8,
    DeviceLost = 9,
    DrawAlreadyActive = 10,
    DrawNotActive = 11,
    DrawFailed = 12,
    InterfaceNotSupported = 13,
    Win2DRuntimeUnavailable = 14,
    WindowsRuntimeNotInitialized = 15,
    DrawingStateMismatch = 16,
    InsufficientBuffer = 17
}

public sealed record ProGpuDirect2DSurfaceOptions(
    uint Width,
    uint Height,
    float DpiX = 96.0F,
    float DpiY = 96.0F,
    ProGpuDirect2DSurfaceFlags Flags =
        ProGpuDirect2DSurfaceFlags.AllowWarpFallback,
    long? AdapterLuid = null);

public readonly record struct ProGpuDirect2DSurfaceDescriptor(
    ProGpuDirect2DDescriptorFlags Flags,
    uint Width,
    uint Height,
    float DpiX,
    float DpiY,
    uint DxgiFormat,
    uint AlphaMode,
    long AdapterLuid,
    nint SharedNtHandle,
    ulong InitialAcquireKey,
    ulong InitialReleaseKey,
    ulong ContentVersion);

public readonly record struct ProGpuDirect2DDeviceLossState(
    ProGpuDirect2DDeviceLossFlags Flags,
    int ReasonHResult,
    ulong ResourceGeneration)
{
    public bool IsDeviceLost =>
        (Flags & ProGpuDirect2DDeviceLossFlags.DeviceLost) != 0;
}

public sealed class ProGpuDirect2DDeviceLostEventArgs : EventArgs
{
    internal ProGpuDirect2DDeviceLostEventArgs(
        int reasonHResult,
        ulong resourceGeneration)
    {
        ReasonHResult = reasonHResult;
        ResourceGeneration = resourceGeneration;
    }

    public int ReasonHResult { get; }

    public ulong ResourceGeneration { get; }
}

internal sealed class ProGpuDirect2DResourceDomain
{
    private int _deviceLost;
    private int _reasonHResult;

    internal ProGpuDirect2DResourceDomain(ulong generation)
    {
        Generation = generation;
    }

    internal ulong Generation { get; }

    internal bool IsDeviceLost =>
        Volatile.Read(ref _deviceLost) != 0;

    internal int ReasonHResult =>
        Volatile.Read(ref _reasonHResult);

    internal void MarkDeviceLost(int reasonHResult)
    {
        Volatile.Write(ref _reasonHResult, reasonHResult);
        Volatile.Write(ref _deviceLost, 1);
    }
}

/// <summary>
/// Linear floating-point color for a genuine Direct2D resource. Finite HDR
/// channel values outside zero to one are preserved.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public readonly record struct ProGpuDirect2DColor(
    float Red,
    float Green,
    float Blue,
    float Alpha = 1.0F)
{
    public static ProGpuDirect2DColor FromArgb(
        byte alpha,
        byte red,
        byte green,
        byte blue) =>
        new(
            red / 255.0F,
            green / 255.0F,
            blue / 255.0F,
            alpha / 255.0F);
}

[StructLayout(LayoutKind.Sequential)]
public readonly record struct ProGpuDirect2DTags(
    ulong Tag1,
    ulong Tag2);

[StructLayout(LayoutKind.Sequential)]
public readonly record struct ProGpuDirect2DGradientStop(
    float Position,
    ProGpuDirect2DColor Color);

/// <summary>
/// Blittable tiling and sampling state for a genuine ID2D1BitmapBrush1.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public readonly record struct ProGpuDirect2DBitmapBrushProperties(
    ProGpuDirect2DExtendMode ExtendModeX = ProGpuDirect2DExtendMode.Clamp,
    ProGpuDirect2DExtendMode ExtendModeY = ProGpuDirect2DExtendMode.Clamp,
    ProGpuDirect2DInterpolationMode InterpolationMode =
        ProGpuDirect2DInterpolationMode.Linear);

[StructLayout(LayoutKind.Sequential)]
public readonly record struct ProGpuDirect2DRect(
    float X,
    float Y,
    float Width,
    float Height);

/// <summary>
/// One point and unit tangent sampled from a genuine ID2D1Geometry.
/// </summary>
public readonly record struct ProGpuDirect2DPointAndTangent(
    Vector2 Point,
    Vector2 UnitTangent);

/// <summary>
/// One blittable triangle emitted by ID2D1Geometry::Tessellate.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public readonly record struct ProGpuDirect2DTriangle(
    Vector2 Point1,
    Vector2 Point2,
    Vector2 Point3);

/// <summary>
/// Pointer-free state for one ID2D1DeviceContext layer push. Optional geometry
/// mask, opacity brush, and backing ID2D1Layer references are passed separately
/// so this metadata remains typed and AOT-safe.
/// </summary>
public readonly record struct ProGpuDirect2DLayerParameters(
    ProGpuDirect2DRect ContentBounds,
    float Opacity = 1.0F,
    ProGpuDirect2DAntialiasMode MaskAntialiasMode =
        ProGpuDirect2DAntialiasMode.PerPrimitive,
    Matrix3x2? MaskTransform = null,
    ProGpuDirect2DLayerOptions Options = ProGpuDirect2DLayerOptions.None);

/// <summary>
/// Typed, pointer-free state used to create one genuine IDWriteTextFormat1.
/// Font weight follows DirectWrite's open numeric range from 1 through 999.
/// </summary>
public readonly record struct ProGpuDirect2DTextFormatProperties(
    float FontSize,
    uint FontWeight = 400U,
    ProGpuDirect2DFontStyle FontStyle = ProGpuDirect2DFontStyle.Normal,
    ProGpuDirect2DFontStretch FontStretch =
        ProGpuDirect2DFontStretch.Normal,
    ProGpuDirect2DTextAlignment TextAlignment =
        ProGpuDirect2DTextAlignment.Leading,
    ProGpuDirect2DParagraphAlignment ParagraphAlignment =
        ProGpuDirect2DParagraphAlignment.Near,
    ProGpuDirect2DWordWrapping WordWrapping =
        ProGpuDirect2DWordWrapping.Wrap,
    ProGpuDirect2DReadingDirection ReadingDirection =
        ProGpuDirect2DReadingDirection.LeftToRight,
    ProGpuDirect2DFlowDirection FlowDirection =
        ProGpuDirect2DFlowDirection.TopToBottom,
    float IncrementalTabStop = 0.0F);

/// <summary>
/// Typed mutable formatting for one UTF-16 range in a genuine
/// IDWriteTextLayout4. <see cref="Flags"/> selects the values to apply.
/// A drawing-effect brush is supplied separately so this state stays
/// pointer-free and AOT-safe.
/// </summary>
public readonly record struct ProGpuDirect2DTextRangeFormat(
    ProGpuDirect2DTextRangeFormatFlags Flags,
    uint FontWeight = 400U,
    ProGpuDirect2DFontStyle FontStyle = ProGpuDirect2DFontStyle.Normal,
    ProGpuDirect2DFontStretch FontStretch =
        ProGpuDirect2DFontStretch.Normal,
    float FontSize = 12.0F,
    bool Underline = false,
    bool Strikethrough = false);

/// <summary>
/// One OpenType feature for a genuine IDWriteTypography. <see
/// cref="NameTag"/> uses DirectWrite's little-endian four-byte tag layout.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public readonly record struct ProGpuDirect2DTypographyFeature(
    uint NameTag,
    uint Parameter = 1U)
{
    public static uint CreateTag(char first, char second, char third, char fourth)
    {
        ValidateTagCharacter(first, nameof(first));
        ValidateTagCharacter(second, nameof(second));
        ValidateTagCharacter(third, nameof(third));
        ValidateTagCharacter(fourth, nameof(fourth));
        return (uint)first |
            (uint)second << 8 |
            (uint)third << 16 |
            (uint)fourth << 24;
    }

    private static void ValidateTagCharacter(char value, string parameterName)
    {
        if (value is < ' ' or > '~')
        {
            throw new ArgumentOutOfRangeException(
                parameterName,
                "OpenType feature tags require printable ASCII characters.");
        }
    }
}

/// <summary>
/// Typed system-font matching state for a genuine
/// IDWriteFontFaceReference. Font weight follows DirectWrite's open numeric
/// range from 1 through 999.
/// </summary>
public readonly record struct ProGpuDirect2DFontFaceProperties(
    uint FontWeight = 400U,
    ProGpuDirect2DFontStyle FontStyle = ProGpuDirect2DFontStyle.Normal,
    ProGpuDirect2DFontStretch FontStretch =
        ProGpuDirect2DFontStretch.Normal);

/// <summary>
/// Blittable per-glyph DirectWrite offset. AdvanceOffset follows the baseline;
/// AscenderOffset moves toward the font ascender.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public readonly record struct ProGpuDirect2DGlyphOffset(
    float AdvanceOffset,
    float AscenderOffset);

/// <summary>
/// Blittable source, tiling, and sampling state for a genuine
/// ID2D1ImageBrush. The source rectangle uses image-space coordinates.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public readonly record struct ProGpuDirect2DImageBrushProperties(
    ProGpuDirect2DRect SourceRectangle,
    ProGpuDirect2DExtendMode ExtendModeX = ProGpuDirect2DExtendMode.Clamp,
    ProGpuDirect2DExtendMode ExtendModeY = ProGpuDirect2DExtendMode.Clamp,
    ProGpuDirect2DInterpolationMode InterpolationMode =
        ProGpuDirect2DInterpolationMode.Linear);

/// <summary>
/// Blittable metadata for a genuine ID2D1StrokeStyle1. Custom dash values are
/// expressed separately as a caller-owned span in CreateStrokeStyle.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public readonly record struct ProGpuDirect2DStrokeStyleProperties(
    ProGpuDirect2DCapStyle StartCap = ProGpuDirect2DCapStyle.Flat,
    ProGpuDirect2DCapStyle EndCap = ProGpuDirect2DCapStyle.Flat,
    ProGpuDirect2DCapStyle DashCap = ProGpuDirect2DCapStyle.Flat,
    ProGpuDirect2DLineJoin LineJoin = ProGpuDirect2DLineJoin.Miter,
    float MiterLimit = 10.0F,
    ProGpuDirect2DDashStyle DashStyle = ProGpuDirect2DDashStyle.Solid,
    float DashOffset = 0.0F,
    ProGpuDirect2DStrokeTransformType TransformType =
        ProGpuDirect2DStrokeTransformType.Normal);

[StructLayout(LayoutKind.Sequential)]
public readonly record struct ProGpuDirect2DPathFigure(
    Vector2 StartPoint,
    uint FirstSegment,
    uint SegmentCount,
    ProGpuDirect2DPathFigureFlags Flags,
    uint Reserved = 0U);

[StructLayout(LayoutKind.Sequential)]
public readonly record struct ProGpuDirect2DPathSegment(
    Vector2 Point1,
    Vector2 Point2,
    Vector2 Point3,
    Vector2 Size,
    float RotationAngle,
    ProGpuDirect2DPathSegmentKind Kind,
    ProGpuDirect2DPathSegmentFlags Flags,
    ProGpuDirect2DArcFlags ArcFlags)
{
    public static ProGpuDirect2DPathSegment Line(
        Vector2 point,
        ProGpuDirect2DPathSegmentFlags flags =
            ProGpuDirect2DPathSegmentFlags.None) =>
        new(
            point,
            default,
            default,
            default,
            0.0F,
            ProGpuDirect2DPathSegmentKind.Line,
            flags,
            ProGpuDirect2DArcFlags.None);

    public static ProGpuDirect2DPathSegment Quadratic(
        Vector2 controlPoint,
        Vector2 endPoint,
        ProGpuDirect2DPathSegmentFlags flags =
            ProGpuDirect2DPathSegmentFlags.None) =>
        new(
            controlPoint,
            endPoint,
            default,
            default,
            0.0F,
            ProGpuDirect2DPathSegmentKind.Quadratic,
            flags,
            ProGpuDirect2DArcFlags.None);

    public static ProGpuDirect2DPathSegment Cubic(
        Vector2 controlPoint1,
        Vector2 controlPoint2,
        Vector2 endPoint,
        ProGpuDirect2DPathSegmentFlags flags =
            ProGpuDirect2DPathSegmentFlags.None) =>
        new(
            controlPoint1,
            controlPoint2,
            endPoint,
            default,
            0.0F,
            ProGpuDirect2DPathSegmentKind.Cubic,
            flags,
            ProGpuDirect2DArcFlags.None);

    public static ProGpuDirect2DPathSegment Arc(
        Vector2 endPoint,
        Vector2 radius,
        float rotationAngle,
        ProGpuDirect2DArcFlags arcFlags,
        ProGpuDirect2DPathSegmentFlags flags =
            ProGpuDirect2DPathSegmentFlags.None) =>
        new(
            endPoint,
            default,
            default,
            radius,
            rotationAngle,
            ProGpuDirect2DPathSegmentKind.Arc,
            flags,
            arcFlags);
}

public sealed class ProGpuDirect2DException : Exception
{
    internal ProGpuDirect2DException(
        string operation,
        ProGpuDirect2DStatus status,
        int nativeHResult)
        : base($"Direct2D {operation} failed with {status} (0x{nativeHResult:X8}).")
    {
        Status = status;
        NativeHResult = nativeHResult;
        HResult = nativeHResult;
    }

    public ProGpuDirect2DStatus Status { get; }

    public int NativeHResult { get; }
}

/// <summary>
/// Owns one caller reference to a genuine Windows COM interface.
/// </summary>
public sealed class ProGpuDirect2DComReference : SafeHandleZeroOrMinusOneIsInvalid
{
    internal ProGpuDirect2DComReference(
        nint value,
        ProGpuDirect2DInterfaceKind kind,
        ProGpuDirect2DResourceDomain resourceDomain)
        : this(value, kind, resourceDomain, null)
    {
    }

    private ProGpuDirect2DComReference(
        nint value,
        ProGpuDirect2DInterfaceKind kind,
        ProGpuDirect2DResourceDomain resourceDomain,
        Guid? queriedInterfaceId)
        : base(ownsHandle: true)
    {
        InterfaceKind = kind;
        _resourceDomain = resourceDomain;
        QueriedInterfaceId = queriedInterfaceId;
        SetHandle(value);
    }

    public ProGpuDirect2DInterfaceKind InterfaceKind { get; }

    private readonly ProGpuDirect2DResourceDomain _resourceDomain;

    /// <summary>
    /// Identifies the native Direct2D/D3D11 device domain that created this
    /// resource. References from a lost generation fail closed on a new
    /// surface and must be recreated.
    /// </summary>
    public ulong ResourceGeneration => _resourceDomain.Generation;

    public Guid? QueriedInterfaceId { get; }

    /// <summary>
    /// Queries this genuine COM object for any interface supported by the
    /// installed Windows runtime. The returned safe handle owns one reference.
    /// </summary>
    public unsafe ProGpuDirect2DComReference QueryInterface(Guid interfaceId)
    {
        if (_resourceDomain.IsDeviceLost)
        {
            throw new ProGpuDirect2DException(
                $"QueryInterface({interfaceId:D}) on lost resource generation {ResourceGeneration}",
                ProGpuDirect2DStatus.DeviceLost,
                _resourceDomain.ReasonHResult);
        }
        bool referenceAdded = false;
        try
        {
            DangerousAddRef(ref referenceAdded);
            ProGpuDirect2DNative.NativeGuid nativeInterfaceId =
                ProGpuDirect2DNative.NativeGuid.FromGuid(interfaceId);
            nint result = 0;
            int nativeHResult = 0;
            ProGpuDirect2DStatus status =
                ProGpuDirect2DNative.ComQueryInterface(
                    DangerousGetHandle(),
                    &nativeInterfaceId,
                    &result,
                    &nativeHResult);
            if (status != ProGpuDirect2DStatus.Success)
            {
                throw new ProGpuDirect2DException(
                    $"QueryInterface({interfaceId:D})",
                    status,
                    nativeHResult);
            }
            if (result == 0)
            {
                throw new InvalidOperationException(
                    "COM QueryInterface succeeded without returning an interface.");
            }
            return new ProGpuDirect2DComReference(
                result,
                InterfaceKind,
                _resourceDomain,
                interfaceId);
        }
        finally
        {
            if (referenceAdded)
            {
                DangerousRelease();
            }
        }
    }

    protected override bool ReleaseHandle()
    {
        ProGpuDirect2DNative.ComRelease(handle);
        return true;
    }
}
