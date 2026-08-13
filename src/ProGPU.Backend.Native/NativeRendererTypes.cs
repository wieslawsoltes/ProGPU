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

public enum NativeImageSampling : uint
{
    Nearest = 0,
    Linear = 1
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
    BoundedGroupEffectChain = 1UL << 27
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
            default(NativeGroupEffect))
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
            default(NativeGroupEffect))
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
            null)
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
            groupEffectChain)
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
        NativeGroupEffectChain? groupEffectChain)
    {
        Opacity = opacity;
        ClipRect = clipRect;
        Flags = flags;
        GroupOpacity = groupOpacity;
        GroupRevision = groupRevision;
        GroupMask = groupMask;
        GroupEffect = groupEffect;
        GroupEffectChain = groupEffectChain;
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

    private readonly byte _initialized;

    internal float EffectiveOpacity => _initialized == 0 ? 1f : Opacity;

    internal float EffectiveGroupOpacity =>
        _initialized == 0 ? 1f : GroupOpacity;
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
    uint EffectAllocationCount);

public readonly record struct NativeRendererInfo(
    uint AbiVersion,
    uint BackendAbi,
    NativeRendererCapabilities Capabilities,
    string Name);
