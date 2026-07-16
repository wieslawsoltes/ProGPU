using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Threading;
using ProGPU.Vector;
using ProGPU.Text;
using ProGPU.Backend;
using ProGPU.Scene.Extensions;

namespace ProGPU.Scene;

public enum RenderCommandType
{
    DrawRect,
    DrawPath,
    DrawText,
    DrawTexture,
    PushClip,
    PopClip,
    PushOpacity,
    PopOpacity,
    DrawLine,
    DrawEllipse,
    DrawCircle,
    DrawRoundedRect,
    DrawBezier,
    DrawCubicBezier,
    DrawPolyline,
    DrawSpline,
    FillTriangle,
    FillQuad,
    DrawLine3D,
    DrawHatch,
    DrawAcisSolid,
    DrawStaticDxf,
    DrawGpuLineSeries,
    DrawGpuScatterSeries,
    DrawPicture, // New: Skia-like SKPicture command
    DrawExtension,
    PushGeometryClip,
    PopGeometryClip,
    PushOpacityMask,
    PopOpacityMask,
    PushBlendMode,
    PopBlendMode,
    DrawGlyphRun,
    DrawVertexMesh,
    DrawPointBatch
}

public enum VertexMeshTopology
{
    Triangles,
    TriangleStrip,
    TriangleFan
}

public enum VertexColorBlendMode
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

public sealed class VertexMesh2D
{
    internal Vector2[] PositionArray { get; }
    internal Vector2[] TextureCoordinateArray { get; }
    internal Vector4[] ColorArray { get; }
    internal ushort[] IndexArray { get; }

    public VertexMeshTopology Topology { get; }
    public ReadOnlyMemory<Vector2> Positions => PositionArray;
    public ReadOnlyMemory<Vector2> TextureCoordinates => TextureCoordinateArray;
    public ReadOnlyMemory<Vector4> Colors => ColorArray;
    public ReadOnlyMemory<ushort> Indices => IndexArray;

    public VertexMesh2D(
        VertexMeshTopology topology,
        ReadOnlySpan<Vector2> positions,
        ReadOnlySpan<Vector2> textureCoordinates = default,
        ReadOnlySpan<Vector4> colors = default,
        ReadOnlySpan<ushort> indices = default)
    {
        if (!textureCoordinates.IsEmpty && textureCoordinates.Length != positions.Length)
        {
            throw new ArgumentException(
                "The number of texture coordinates must match the number of vertices.",
                nameof(textureCoordinates));
        }

        if (!colors.IsEmpty && colors.Length != positions.Length)
        {
            throw new ArgumentException(
                "The number of colors must match the number of vertices.",
                nameof(colors));
        }

        Topology = topology;
        PositionArray = positions.ToArray();
        TextureCoordinateArray = textureCoordinates.ToArray();
        ColorArray = colors.ToArray();
        IndexArray = indices.ToArray();
    }

    internal static VertexMesh2D CreateOwned(
        VertexMeshTopology topology,
        Vector2[] positions,
        Vector2[] textureCoordinates,
        Vector4[] colors,
        ushort[] indices)
    {
        ArgumentNullException.ThrowIfNull(positions);
        ArgumentNullException.ThrowIfNull(textureCoordinates);
        ArgumentNullException.ThrowIfNull(colors);
        ArgumentNullException.ThrowIfNull(indices);
        if (textureCoordinates.Length != 0 && textureCoordinates.Length != positions.Length)
        {
            throw new ArgumentException(
                "The number of texture coordinates must match the number of vertices.",
                nameof(textureCoordinates));
        }
        if (colors.Length != 0 && colors.Length != positions.Length)
        {
            throw new ArgumentException(
                "The number of colors must match the number of vertices.",
                nameof(colors));
        }

        return new VertexMesh2D(topology, positions, textureCoordinates, colors, indices);
    }

    private VertexMesh2D(
        VertexMeshTopology topology,
        Vector2[] positions,
        Vector2[] textureCoordinates,
        Vector4[] colors,
        ushort[] indices)
    {
        Topology = topology;
        PositionArray = positions;
        TextureCoordinateArray = textureCoordinates;
        ColorArray = colors;
        IndexArray = indices;
    }

    internal int GetTriangleCount()
    {
        var elementCount = IndexArray.Length > 0 ? IndexArray.Length : PositionArray.Length;
        return Topology == VertexMeshTopology.Triangles
            ? elementCount / 3
            : Math.Max(0, elementCount - 2);
    }

    internal void GetTriangle(int triangleIndex, out int index0, out int index1, out int index2)
    {
        var indices = IndexArray;
        int GetIndex(int index) => indices.Length > 0 ? indices[index] : index;

        switch (Topology)
        {
            case VertexMeshTopology.TriangleStrip:
                if ((triangleIndex & 1) == 0)
                {
                    index0 = GetIndex(triangleIndex);
                    index1 = GetIndex(triangleIndex + 1);
                }
                else
                {
                    index0 = GetIndex(triangleIndex + 1);
                    index1 = GetIndex(triangleIndex);
                }
                index2 = GetIndex(triangleIndex + 2);
                break;
            case VertexMeshTopology.TriangleFan:
                index0 = GetIndex(0);
                index1 = GetIndex(triangleIndex + 1);
                index2 = GetIndex(triangleIndex + 2);
                break;
            default:
                var offset = triangleIndex * 3;
                index0 = GetIndex(offset);
                index1 = GetIndex(offset + 1);
                index2 = GetIndex(offset + 2);
                break;
        }
    }
}

public enum TextureSamplingMode
{
    Linear,
    Nearest,
    Cubic,
    LinearMipmap
}

public enum TexturePatchKind : byte
{
    Texture,
    FixedColor,
    AtlasColor
}

public readonly struct TexturePatch
{
    public TexturePatch(Rect source, Rect destination)
    {
        Source = source;
        Destination = destination;
        Color = default;
        Kind = TexturePatchKind.Texture;
        DestinationTransform = default;
        HasDestinationTransform = false;
        ColorBlendMode = default;
    }

    public TexturePatch(Rect destination, Vector4 color)
    {
        Source = default;
        Destination = destination;
        Color = color;
        Kind = TexturePatchKind.FixedColor;
        DestinationTransform = default;
        HasDestinationTransform = false;
        ColorBlendMode = default;
    }

    public TexturePatch(
        Rect source,
        Rect destination,
        Matrix3x2 destinationTransform,
        Vector4? color = null,
        VertexColorBlendMode colorBlendMode = VertexColorBlendMode.Dst)
    {
        Source = source;
        Destination = destination;
        Color = color.GetValueOrDefault();
        Kind = color.HasValue ? TexturePatchKind.AtlasColor : TexturePatchKind.Texture;
        DestinationTransform = destinationTransform;
        HasDestinationTransform = true;
        ColorBlendMode = colorBlendMode;
    }

    public Rect Source { get; }
    public Rect Destination { get; }
    public Vector4 Color { get; }
    public TexturePatchKind Kind { get; }
    public Matrix3x2 DestinationTransform { get; }
    public bool HasDestinationTransform { get; }
    public VertexColorBlendMode ColorBlendMode { get; }
}

public enum TextRenderingMode
{
    Grayscale,
    Aliased,
    ClearType
}

public enum TextHintingMode
{
    Auto,
    Fixed,
    Animated
}

public struct Line3D
{
    public Vector3 Start;
    public Vector3 End;

    public Line3D(Vector3 start, Vector3 end)
    {
        Start = start;
        End = end;
    }
}

public struct Rect
{
    public float X;
    public float Y;
    public float Width;
    public float Height;

    public Vector2 Position => new Vector2(X, Y);
    public Vector2 Size => new Vector2(Width, Height);

    public float Right => X + Width;
    public float Bottom => Y + Height;
    public bool IsEmpty => Width <= 0f || Height <= 0f;
    public static Rect Empty => new Rect(0f, 0f, 0f, 0f);

    public Rect(float x, float y, float width, float height)
    {
        X = x;
        Y = y;
        Width = width;
        Height = height;
    }

    public Rect(Vector2 position, Vector2 size)
    {
        X = position.X;
        Y = position.Y;
        Width = size.X;
        Height = size.Y;
    }

    public bool Contains(Vector2 p)
    {
        return p.X >= X && p.X <= X + Width && p.Y >= Y && p.Y <= Y + Height;
    }

    public bool Equals(Rect other)
    {
        return X == other.X && Y == other.Y && Width == other.Width && Height == other.Height;
    }

    public override bool Equals(object? obj)
    {
        return obj is Rect other && Equals(other);
    }

    public override int GetHashCode()
    {
        return HashCode.Combine(X, Y, Width, Height);
    }

    public static bool operator ==(Rect left, Rect right)
    {
        return left.Equals(right);
    }

    public static bool operator !=(Rect left, Rect right)
    {
        return !left.Equals(right);
    }
}

public interface IRenderDataProvider
{
    ReadOnlySpan<Vector2> GetPoints(int offset, int count);
    ReadOnlySpan<double> GetDoubles(int offset, int count);
    ReadOnlySpan<Line3D> GetLines3D(int offset, int count);
    ReadOnlySpan<float> GetFloats(int offset, int count);
}

public sealed class RenderCommandGeometryCache
{
    private int _dashedStrokeSignature;
    private PathGeometry? _dashedStrokePath;
    private Pen? _undashedStrokePen;

    private RenderCommandGeometryCache(
        PathGeometry? strokePath,
        PathGeometry? fillPath,
        PathGeometry? secondaryFillPath)
    {
        StrokePath = strokePath;
        FillPath = fillPath;
        SecondaryFillPath = secondaryFillPath;
    }

    public PathGeometry? StrokePath { get; }
    public PathGeometry? FillPath { get; }
    public PathGeometry? SecondaryFillPath { get; }

    public static RenderCommandGeometryCache ForPath(PathGeometry path)
    {
        ArgumentNullException.ThrowIfNull(path);
        return new RenderCommandGeometryCache(path, path, null);
    }

    public static RenderCommandGeometryCache ForStrokePath(PathGeometry path)
    {
        ArgumentNullException.ThrowIfNull(path);
        return new RenderCommandGeometryCache(path, null, null);
    }

    public static RenderCommandGeometryCache ForFillPath(PathGeometry path)
    {
        ArgumentNullException.ThrowIfNull(path);
        return new RenderCommandGeometryCache(null, path, null);
    }

    public static RenderCommandGeometryCache ForFillPaths(PathGeometry primaryPath, PathGeometry secondaryPath)
    {
        ArgumentNullException.ThrowIfNull(primaryPath);
        ArgumentNullException.ThrowIfNull(secondaryPath);
        return new RenderCommandGeometryCache(null, primaryPath, secondaryPath);
    }

    public bool TryGetDashedStrokePath(Pen pen, out PathGeometry dashedStrokePath, out Pen undashedStrokePen)
    {
        ArgumentNullException.ThrowIfNull(pen);

        if (StrokePath == null)
        {
            dashedStrokePath = null!;
            undashedStrokePen = null!;
            return false;
        }

        int signature = ComputeDashedStrokeSignature(pen);
        if (_dashedStrokePath != null &&
            _undashedStrokePen != null &&
            _dashedStrokeSignature == signature)
        {
            dashedStrokePath = _dashedStrokePath;
            undashedStrokePen = _undashedStrokePen;
            return true;
        }

        if (!Compositor.TryCreateDashedStrokePath(StrokePath, pen, out dashedStrokePath))
        {
            undashedStrokePen = null!;
            return false;
        }

        undashedStrokePen = Compositor.CreateUndashedPen(pen);
        _dashedStrokePath = dashedStrokePath;
        _undashedStrokePen = undashedStrokePen;
        _dashedStrokeSignature = signature;
        return true;
    }

    public static PathGeometry CreateLinePath(Vector2 start, Vector2 end)
    {
        var path = new PathGeometry();
        var figure = new PathFigure(start);
        figure.Segments.Add(new LineSegment(end));
        path.Figures.Add(figure);
        return path;
    }

    public static PathGeometry CreatePolylinePath(ReadOnlySpan<Vector2> points, bool isClosed)
    {
        var path = new PathGeometry();
        if (points.Length == 0)
        {
            return path;
        }

        var figure = new PathFigure(points[0], isClosed);
        for (int i = 1; i < points.Length; i++)
        {
            figure.Segments.Add(new LineSegment(points[i]));
        }

        path.Figures.Add(figure);
        return path;
    }

    public static PathGeometry CreateTrianglePath(Vector2 p1, Vector2 p2, Vector2 p3)
    {
        var path = new PathGeometry();
        var figure = new PathFigure(p1, isClosed: true);
        figure.Segments.Add(new LineSegment(p2));
        figure.Segments.Add(new LineSegment(p3));
        path.Figures.Add(figure);
        return path;
    }

    public static PathGeometry CreateQuadraticBezierPath(Vector2 start, Vector2 control, Vector2 end)
    {
        var path = new PathGeometry();
        var figure = new PathFigure(start);
        figure.Segments.Add(new QuadraticBezierSegment(control, end));
        path.Figures.Add(figure);
        return path;
    }

    public static PathGeometry CreateCubicBezierPath(Vector2 start, Vector2 control1, Vector2 control2, Vector2 end)
    {
        var path = new PathGeometry();
        var figure = new PathFigure(start);
        figure.Segments.Add(new CubicBezierSegment(control1, control2, end));
        path.Figures.Add(figure);
        return path;
    }

    public static PathGeometry CreateSplinePath(
        ReadOnlySpan<Vector2> controlPoints,
        ReadOnlySpan<double> knots,
        ReadOnlySpan<double> weights,
        int degree,
        bool isClosed)
    {
        return SplineGeometry.CreatePath(controlPoints, knots, weights, degree, isClosed);
    }

    private static int ComputeDashedStrokeSignature(Pen pen)
    {
        var hash = new HashCode();
        hash.Add(pen.Brush);
        hash.Add(pen.Thickness);
        hash.Add(pen.LineJoin);
        hash.Add(pen.MiterLimit);
        hash.Add(pen.StartLineCap);
        hash.Add(pen.EndLineCap);
        hash.Add(pen.DashCap);
        hash.Add(pen.DashOffset);

        var dashArray = pen.DashArray;
        if (dashArray != null)
        {
            hash.Add(dashArray.Length);
            for (int i = 0; i < dashArray.Length; i++)
            {
                hash.Add(dashArray[i]);
            }
        }
        else
        {
            hash.Add(0);
        }

        return hash.ToHashCode();
    }
}

public struct RenderCommand
{
    public RenderCommandType Type;
    public int HitTestId;
    public Rect Rect;
    public Brush? Brush;
    public Pen? Pen;
    public PathGeometry? Path;
    public RenderCommandGeometryCache? GeometryCache;
    
    // Typography properties
    public string? Text;
    public TtfFont? Font;
    public float FontSize;
    public Vector2 Position;
    public bool IsBold;
    public bool IsItalic;
    public Vector2 FontTransform;
    public bool HasFontTransform;
    public float Rotation;
    public TextRenderingMode TextRenderingMode;
    public TextHintingMode TextHintingMode;
    public bool UseVectorGlyphRendering;
    public bool PreferGlyphAtlas;
    public bool UseLogicalGlyphAtlasResolution;
    public bool IsTextAliased
    {
        readonly get => TextRenderingMode == TextRenderingMode.Aliased;
        set => TextRenderingMode = value ? TextRenderingMode.Aliased : TextRenderingMode.Grayscale;
    }

    // Texture properties
    public GpuTexture? Texture;
    public Rect SrcRect;
    public TexturePatch[]? TexturePatches;
    public TextureSamplingMode TextureSamplingMode;
    public byte TextureMaxAnisotropy;
    public Vector2 TextureCubicCoefficients;
    public bool HasTextureCubicCoefficients;

    // Vector render options
    public bool IsEdgeAliased;
    public bool IsPenThicknessLocal;
    public uint PathSampleGrid;
    public float PathCoverageGamma;

    // Advanced geometries
    public Vector2 Position2;
    public Vector2 Position3;
    public Vector2 Position4;
    public float RadiusX;
    public float RadiusY;
    public float CornerRadius;

    // Polyline properties (Retained for WinUI backward compatibility)
    public Vector2[]? PolylinePoints;
    public bool IsClosed;

    // Spline properties (Retained for WinUI backward compatibility)
    public double[]? SplineKnots;
    public double[]? SplineWeights;
    public int SplineDegree;

    // 3D properties
    public Vector3 Position3D1;
    public Vector3 Position3D2;

    // ACIS Solid properties
    public List<Line3D>? Edges3D;
    public Matrix4x4 Transform;

    // Static buffer property
    public object? StaticBuffer;

    // GPU Chart Series properties (Retained for backward compatibility)
    public float[]? GpuPoints;
    public int GpuPointsCount;

    // GPU Transform properties
    public bool UseGpuTransforms;
    public Matrix4x4 CameraView;

    // GPU Chart scaling parameters
    public Vector2 Scale;
    public Vector2 Translate;

    // Zero-allocation buffer offsets and counts
    public int PointBufferOffset;
    public int PointBufferCount;

    public int DoubleBufferOffset;
    public int DoubleBufferCount;

    public int Line3DBufferOffset;
    public int Line3DBufferCount;

    public int WeightBufferOffset;
    public int WeightBufferCount;

    public int FloatBufferOffset;
    public int FloatBufferCount;

    // GPU series cache key
    public object? SeriesCacheKey;

    // Picture property
    public GpuPicture? Picture;

    // Glyph run properties (Skia SKTextBlob compatibility)
    public ushort[]? GlyphIndices;
    public Vector2[]? GlyphPositions;

    // Batched two-dimensional vertex mesh properties
    public VertexMesh2D? VertexMesh;
    public VertexColorBlendMode VertexColorBlendMode;

    // High performance custom drawing extension properties
    public int ExtensionId;
    public int IntParam;
    public float FloatParam;
    public object? DataParam;
}

internal sealed class RetainedResourceLease : IDisposable
{
    private RetainedResourceOwner? _owner;

    private RetainedResourceLease(RetainedResourceOwner owner)
    {
        _owner = owner;
    }

    public object? Identity => _owner?.Identity;

    public static RetainedResourceLease Create(IDisposable resource, object? identity = null)
    {
        return new RetainedResourceLease(new RetainedResourceOwner(resource, identity));
    }

    public RetainedResourceLease AddRef()
    {
        var owner = _owner ?? throw new ObjectDisposedException(nameof(RetainedResourceLease));
        owner.AddRef();
        return new RetainedResourceLease(owner);
    }

    public void Dispose()
    {
        Interlocked.Exchange(ref _owner, null)?.Release();
    }

    private sealed class RetainedResourceOwner
    {
        private readonly IDisposable _resource;
        private int _refCount = 1;
        private int _disposed;

        public RetainedResourceOwner(IDisposable resource, object? identity)
        {
            _resource = resource;
            Identity = identity;
        }

        public object? Identity { get; }

        public void AddRef()
        {
            while (true)
            {
                int count = Volatile.Read(ref _refCount);
                if (count <= 0)
                {
                    throw new ObjectDisposedException(nameof(RetainedResourceOwner));
                }

                if (Interlocked.CompareExchange(ref _refCount, count + 1, count) == count)
                {
                    return;
                }
            }
        }

        public void Release()
        {
            if (Interlocked.Decrement(ref _refCount) == 0 &&
                Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                _resource.Dispose();
            }
        }
    }
}

public class GpuPicture : IRenderDataProvider, IDisposable
{
    public RenderCommand[] Commands { get; }
    public Vector2[] PointBuffer { get; }
    public double[] DoubleBuffer { get; }
    public Line3D[] Line3DBuffer { get; }
    public float[] FloatBuffer { get; }
    private readonly RetainedResourceLease[] _retainedResources;
    private bool _disposed;

    public int RetainedResourceCount => _retainedResources.Length;

    public GpuPicture(
        RenderCommand[] commands,
        Vector2[] pointBuffer,
        double[] doubleBuffer,
        Line3D[] line3dBuffer,
        float[] floatBuffer) : this(
            commands,
            pointBuffer,
            doubleBuffer,
            line3dBuffer,
            floatBuffer,
            Array.Empty<RetainedResourceLease>())
    {
    }

    internal GpuPicture(
        RenderCommand[] commands,
        Vector2[] pointBuffer,
        double[] doubleBuffer,
        Line3D[] line3dBuffer,
        float[] floatBuffer,
        RetainedResourceLease[] retainedResources)
    {
        Commands = commands;
        PointBuffer = pointBuffer;
        DoubleBuffer = doubleBuffer;
        Line3DBuffer = line3dBuffer;
        FloatBuffer = floatBuffer;
        _retainedResources = retainedResources;
    }

    public ReadOnlySpan<Vector2> GetPoints(int offset, int count) => 
        new ReadOnlySpan<Vector2>(PointBuffer, offset, count);

    public ReadOnlySpan<double> GetDoubles(int offset, int count) => 
        new ReadOnlySpan<double>(DoubleBuffer, offset, count);

    public ReadOnlySpan<Line3D> GetLines3D(int offset, int count) => 
        new ReadOnlySpan<Line3D>(Line3DBuffer, offset, count);

    public ReadOnlySpan<float> GetFloats(int offset, int count) => 
        new ReadOnlySpan<float>(FloatBuffer, offset, count);

    public GpuPicture Clone()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(GpuPicture));
        }

        return new GpuPicture(
            Commands,
            PointBuffer,
            DoubleBuffer,
            Line3DBuffer,
            FloatBuffer,
            CloneRetainedResources());
    }

    internal RetainedResourceLease[] CloneRetainedResources()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(GpuPicture));
        }

        var leases = new RetainedResourceLease[_retainedResources.Length];
        for (int i = 0; i < leases.Length; i++)
        {
            leases[i] = _retainedResources[i].AddRef();
        }

        return leases;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        DisposeRetainedResources(_retainedResources);
    }

    private static void DisposeRetainedResources(RetainedResourceLease[] resources)
    {
        for (int i = 0; i < resources.Length; i++)
        {
            resources[i].Dispose();
        }
    }
}

public class GpuPictureRecorder
{
    private readonly DrawingContext _recordingContext = new();

    public DrawingContext BeginRecording(Rect bounds)
    {
        _recordingContext.Clear();
        return _recordingContext;
    }

    public GpuPicture EndRecording()
    {
        var picture = new GpuPicture(
            CopyList(_recordingContext.Commands),
            CopyList(_recordingContext.PointBuffer),
            CopyList(_recordingContext.DoubleBuffer),
            CopyList(_recordingContext.Line3DBuffer),
            CopyList(_recordingContext.FloatBuffer),
            _recordingContext.CloneRetainedResources()
        );
        _recordingContext.Clear();
        return picture;
    }

    private static T[] CopyList<T>(List<T> values)
    {
        if (values.Count == 0)
        {
            return Array.Empty<T>();
        }

        var result = new T[values.Count];
        for (int i = 0; i < result.Length; i++)
        {
            result[i] = values[i];
        }

        return result;
    }
}

public class DrawingContext : IRenderDataProvider
{
    public List<RenderCommand> Commands { get; } = new();
    private readonly List<RetainedResourceLease> _retainedResources = new();

    // Reusable continuous pools to eliminate heap array allocations
    public List<Vector2> PointBuffer { get; } = new();
    public List<double> DoubleBuffer { get; } = new();
    public List<Line3D> Line3DBuffer { get; } = new();
    public List<float> FloatBuffer { get; } = new();

    public int RetainedResourceCount => _retainedResources.Count;

    /// <summary>
    /// Reserves storage for a known upper bound of retained commands. Repeated
    /// recording then reuses the same backing array without a late capacity
    /// growth in an animation frame.
    /// </summary>
    public void EnsureCommandCapacity(int capacity)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(capacity);
        Commands.EnsureCapacity(capacity);
    }

    public void RetainResource(IDisposable resource)
    {
        ArgumentNullException.ThrowIfNull(resource);
        _retainedResources.Add(RetainedResourceLease.Create(resource));
    }

    /// <summary>
    /// Retains the current texture from <paramref name="source"/> for deferred
    /// command replay. A context keeps at most one lease for a given texture,
    /// so repeated draws reuse both the texture and its lifetime token.
    /// </summary>
    public bool TryRetainTexture(
        IProGpuTextureLeaseSource source,
        WgpuContext requiredContext,
        out GpuTexture texture)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(requiredContext);

        bool hasCurrentTexture = source is IProGpuContextTextureLeaseSource contextSource
            ? contextSource.TryGetGpuTexture(requiredContext, out var currentTexture)
            : source.TryGetGpuTexture(out currentTexture);

        if (hasCurrentTexture
            && currentTexture is not null
            && !currentTexture.IsDisposed)
        {
            ValidateTextureContext(currentTexture, requiredContext);
            if (HasRetainedResourceIdentity(currentTexture))
            {
                texture = currentTexture;
                return true;
            }
        }

        bool hasTextureLease = source is IProGpuContextTextureLeaseSource contextLeaseSource
            ? contextLeaseSource.TryAcquireGpuTextureLease(requiredContext, out var textureLease)
            : source.TryAcquireGpuTextureLease(out textureLease);

        if (!hasTextureLease)
        {
            texture = null!;
            return false;
        }

        var leasedTexture = textureLease.Texture;
        if (leasedTexture == null || leasedTexture.IsDisposed)
        {
            textureLease.Dispose();
            texture = null!;
            return false;
        }

        try
        {
            ValidateTextureContext(leasedTexture, requiredContext);
        }
        catch
        {
            textureLease.Dispose();
            throw;
        }

        if (HasRetainedResourceIdentity(leasedTexture))
        {
            textureLease.Dispose();
        }
        else
        {
            _retainedResources.Add(RetainedResourceLease.Create(textureLease, leasedTexture));
        }

        texture = leasedTexture;
        return true;
    }

    private bool HasRetainedResourceIdentity(object identity)
    {
        for (int i = 0; i < _retainedResources.Count; i++)
        {
            if (ReferenceEquals(_retainedResources[i].Identity, identity))
            {
                return true;
            }
        }

        return false;
    }

    private static void ValidateTextureContext(GpuTexture texture, WgpuContext requiredContext)
    {
        if (!ReferenceEquals(texture.Context, requiredContext))
        {
            throw new InvalidOperationException(
                "Cannot retain a texture from a different WebGPU context for deferred command replay.");
        }
    }

    internal RetainedResourceLease[] CloneRetainedResources()
    {
        var leases = new RetainedResourceLease[_retainedResources.Count];
        for (int i = 0; i < leases.Length; i++)
        {
            leases[i] = _retainedResources[i].AddRef();
        }

        return leases;
    }

    public ReadOnlySpan<Vector2> GetPoints(int offset, int count) => 
        CollectionsMarshal.AsSpan(PointBuffer).Slice(offset, count);

    public ReadOnlySpan<double> GetDoubles(int offset, int count) => 
        CollectionsMarshal.AsSpan(DoubleBuffer).Slice(offset, count);

    public ReadOnlySpan<Line3D> GetLines3D(int offset, int count) => 
        CollectionsMarshal.AsSpan(Line3DBuffer).Slice(offset, count);

    public ReadOnlySpan<float> GetFloats(int offset, int count) => 
        CollectionsMarshal.AsSpan(FloatBuffer).Slice(offset, count);

    public void DrawRectangle(Brush? brush, Pen? pen, Rect rect)
    {
        if (brush is BackdropMaterialBrush backdropMaterial)
        {
            this.DrawBackdropMaterial(backdropMaterial, rect);
            if (pen == null)
            {
                return;
            }

            brush = null;
        }

        Commands.Add(new RenderCommand
        {
            Type = RenderCommandType.DrawRect,
            Rect = rect,
            Brush = brush,
            Pen = pen
        });
    }

    public void DrawRectangle(Brush? brush, Pen? pen, Rect rect, Matrix4x4 transform)
    {
        if (brush is BackdropMaterialBrush backdropMaterial)
        {
            this.DrawBackdropMaterial(backdropMaterial, rect, transform: transform);
            if (pen == null)
            {
                return;
            }

            brush = null;
        }

        Commands.Add(new RenderCommand
        {
            Type = RenderCommandType.DrawRect,
            Rect = rect,
            Brush = brush,
            Pen = pen,
            Transform = transform
        });
    }

    public void DrawPath(Brush? brush, Pen? pen, PathGeometry path)
    {
        Commands.Add(new RenderCommand
        {
            Type = RenderCommandType.DrawPath,
            Brush = brush,
            Pen = pen,
            Path = path,
            GeometryCache = RenderCommandGeometryCache.ForPath(path)
        });
    }

    /// <summary>
    /// Records a retained path using a cache previously created for the same geometry.
    /// Animated callers can reuse the cache while changing grouping or style without
    /// allocating a new cache object for every recorded command.
    /// </summary>
    public void DrawPath(
        Brush? brush,
        Pen? pen,
        PathGeometry path,
        RenderCommandGeometryCache geometryCache)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(geometryCache);
        if ((brush != null && !ReferenceEquals(geometryCache.FillPath, path)) ||
            (pen != null && !ReferenceEquals(geometryCache.StrokePath, path)))
        {
            throw new ArgumentException("The retained geometry cache does not match the path.", nameof(geometryCache));
        }

        Commands.Add(new RenderCommand
        {
            Type = RenderCommandType.DrawPath,
            Brush = brush,
            Pen = pen,
            Path = path,
            GeometryCache = geometryCache
        });
    }

    public void DrawPath(Brush? brush, Pen? pen, PathGeometry path, Matrix4x4 transform)
    {
        Commands.Add(new RenderCommand
        {
            Type = RenderCommandType.DrawPath,
            Brush = brush,
            Pen = pen,
            Path = path,
            Transform = transform,
            GeometryCache = RenderCommandGeometryCache.ForPath(path)
        });
    }

    public void DrawText(
        string text,
        TtfFont font,
        float fontSize,
        Brush brush,
        Vector2 position,
        bool isBold = false,
        bool isItalic = false,
        float rotation = 0f,
        TextRenderingMode textRenderingMode = TextRenderingMode.Grayscale,
        TextHintingMode textHintingMode = TextHintingMode.Auto,
        bool useVectorGlyphRendering = false)
    {
        Commands.Add(new RenderCommand
        {
            Type = RenderCommandType.DrawText,
            Text = text,
            Font = font,
            FontSize = fontSize,
            Brush = brush,
            Position = position,
            IsBold = isBold,
            IsItalic = isItalic,
            Rotation = rotation,
            TextRenderingMode = textRenderingMode,
            TextHintingMode = textHintingMode,
            UseVectorGlyphRendering = useVectorGlyphRendering
        });
    }

    public void DrawText(
        string text,
        TtfFont font,
        float fontSize,
        Brush brush,
        Vector2 position,
        Matrix4x4 transform,
        Rect layoutBounds,
        bool isBold = false,
        bool isItalic = false,
        float rotation = 0f,
        TextRenderingMode textRenderingMode = TextRenderingMode.Grayscale,
        TextHintingMode textHintingMode = TextHintingMode.Auto,
        bool useVectorGlyphRendering = false)
    {
        Commands.Add(new RenderCommand
        {
            Type = RenderCommandType.DrawText,
            Text = text,
            Font = font,
            FontSize = fontSize,
            Brush = brush,
            Position = position,
            Rect = layoutBounds,
            Transform = transform,
            IsBold = isBold,
            IsItalic = isItalic,
            Rotation = rotation,
            TextRenderingMode = textRenderingMode,
            TextHintingMode = textHintingMode,
            UseVectorGlyphRendering = useVectorGlyphRendering
        });
    }

    public void DrawText(
        string text,
        TtfFont font,
        float fontSize,
        Brush brush,
        Vector2 position,
        Matrix4x4 transform,
        bool isBold = false,
        bool isItalic = false,
        float rotation = 0f,
        TextRenderingMode textRenderingMode = TextRenderingMode.Grayscale,
        TextHintingMode textHintingMode = TextHintingMode.Auto,
        bool useVectorGlyphRendering = false)
    {
        Commands.Add(new RenderCommand
        {
            Type = RenderCommandType.DrawText,
            Text = text,
            Font = font,
            FontSize = fontSize,
            Brush = brush,
            Position = position,
            Transform = transform,
            IsBold = isBold,
            IsItalic = isItalic,
            Rotation = rotation,
            TextRenderingMode = textRenderingMode,
            TextHintingMode = textHintingMode,
            UseVectorGlyphRendering = useVectorGlyphRendering
        });
    }

    public void DrawGlyphRun(
        ushort[] glyphIndices,
        Vector2[] glyphPositions,
        TtfFont font,
        float fontSize,
        Brush brush,
        Vector2 position,
        Matrix4x4 transform = default,
        bool isBold = false,
        bool isItalic = false,
        TextRenderingMode textRenderingMode = TextRenderingMode.Grayscale,
        TextHintingMode textHintingMode = TextHintingMode.Auto,
        bool useVectorGlyphRendering = false,
        bool preferGlyphAtlas = false,
        bool useLogicalGlyphAtlasResolution = false)
    {
        DrawTransformedGlyphRun(
            glyphIndices,
            glyphPositions,
            font,
            fontSize,
            brush,
            position,
            transform,
            isBold,
            isItalic,
            textRenderingMode,
            textHintingMode,
            useVectorGlyphRendering,
            preferGlyphAtlas,
            useLogicalGlyphAtlasResolution,
            fontScaleX: 1f,
            fontSkewX: 0f);
    }

    public void DrawTransformedGlyphRun(
        ushort[] glyphIndices,
        Vector2[] glyphPositions,
        TtfFont font,
        float fontSize,
        Brush brush,
        Vector2 position,
        Matrix4x4 transform = default,
        bool isBold = false,
        bool isItalic = false,
        TextRenderingMode textRenderingMode = TextRenderingMode.Grayscale,
        TextHintingMode textHintingMode = TextHintingMode.Auto,
        bool useVectorGlyphRendering = false,
        bool preferGlyphAtlas = false,
        bool useLogicalGlyphAtlasResolution = false,
        float fontScaleX = 1f,
        float fontSkewX = 0f)
    {
        Commands.Add(new RenderCommand
        {
            Type = RenderCommandType.DrawGlyphRun,
            GlyphIndices = glyphIndices,
            GlyphPositions = glyphPositions,
            Font = font,
            FontSize = fontSize,
            Brush = brush,
            Position = position,
            Transform = transform,
            IsBold = isBold,
            IsItalic = isItalic,
            FontTransform = new Vector2(fontScaleX, fontSkewX),
            HasFontTransform = fontScaleX != 1f || fontSkewX != 0f,
            TextRenderingMode = textRenderingMode,
            TextHintingMode = textHintingMode,
            UseVectorGlyphRendering = useVectorGlyphRendering,
            PreferGlyphAtlas = preferGlyphAtlas,
            UseLogicalGlyphAtlasResolution = useLogicalGlyphAtlasResolution
        });
    }

    public void DrawTexture(GpuTexture texture, Rect rect)
    {
        Commands.Add(new RenderCommand
        {
            Type = RenderCommandType.DrawTexture,
            Rect = rect,
            Texture = texture,
            TextureSamplingMode = TextureSamplingMode.Linear
        });
    }

    public void DrawTexture(
        GpuTexture texture,
        Rect rect,
        Rect sourceRect,
        Matrix4x4 transform,
        TextureSamplingMode samplingMode = TextureSamplingMode.Linear,
        Vector2? cubicCoefficients = null)
    {
        Commands.Add(new RenderCommand
        {
            Type = RenderCommandType.DrawTexture,
            Rect = rect,
            SrcRect = sourceRect,
            Transform = transform,
            Texture = texture,
            TextureSamplingMode = samplingMode,
            TextureCubicCoefficients = cubicCoefficients.GetValueOrDefault(),
            HasTextureCubicCoefficients = cubicCoefficients.HasValue
        });
    }

    public void PushClip(Rect clipRect)
    {
        Commands.Add(new RenderCommand
        {
            Type = RenderCommandType.PushClip,
            Rect = clipRect
        });
    }

    public void PushClip(Rect clipRect, Matrix4x4 transform)
    {
        Commands.Add(new RenderCommand
        {
            Type = RenderCommandType.PushClip,
            Rect = clipRect,
            Transform = transform
        });
    }

    public void PopClip()
    {
        Commands.Add(new RenderCommand { Type = RenderCommandType.PopClip });
    }

    public void PushOpacity(float opacity)
    {
        Commands.Add(new RenderCommand
        {
            Type = RenderCommandType.PushOpacity,
            FontSize = opacity
        });
    }

    public void PopOpacity()
    {
        Commands.Add(new RenderCommand { Type = RenderCommandType.PopOpacity });
    }

    public void PushGeometryClip(PathGeometry geometry)
    {
        Commands.Add(new RenderCommand
        {
            Type = RenderCommandType.PushGeometryClip,
            Path = geometry
        });
    }

    public void PushGeometryClip(PathGeometry geometry, Matrix4x4 transform)
    {
        Commands.Add(new RenderCommand
        {
            Type = RenderCommandType.PushGeometryClip,
            Path = geometry,
            Transform = transform
        });
    }

    public void PopGeometryClip()
    {
        Commands.Add(new RenderCommand { Type = RenderCommandType.PopGeometryClip });
    }

    public void PushOpacityMask(Brush maskBrush, Rect bounds)
    {
        ArgumentNullException.ThrowIfNull(maskBrush);
        Commands.Add(new RenderCommand
        {
            Type = RenderCommandType.PushOpacityMask,
            Brush = maskBrush,
            Rect = bounds
        });
    }

    public void PushOpacityMask(GpuPicture maskPicture, Rect bounds)
    {
        ArgumentNullException.ThrowIfNull(maskPicture);
        Commands.Add(new RenderCommand
        {
            Type = RenderCommandType.PushOpacityMask,
            Picture = maskPicture,
            Rect = bounds
        });
    }

    public void PopOpacityMask()
    {
        Commands.Add(new RenderCommand { Type = RenderCommandType.PopOpacityMask });
    }

    public void PushBlendMode(GpuBlendMode blendMode)
    {
        Commands.Add(new RenderCommand
        {
            Type = RenderCommandType.PushBlendMode,
            IntParam = (int)blendMode
        });
    }

    public void PopBlendMode()
    {
        Commands.Add(new RenderCommand { Type = RenderCommandType.PopBlendMode });
    }

    public void DrawLine(Pen pen, Vector2 p1, Vector2 p2)
    {
        Commands.Add(new RenderCommand
        {
            Type = RenderCommandType.DrawLine,
            Pen = pen,
            Position = p1,
            Position2 = p2,
            GeometryCache = RenderCommandGeometryCache.ForStrokePath(
                RenderCommandGeometryCache.CreateLinePath(p1, p2))
        });
    }

    public void DrawLine3D(Pen pen, Vector3 p1, Vector3 p2)
    {
        int floatOffset = FloatBuffer.Count;
        FloatBuffer.Add(p1.X);
        FloatBuffer.Add(p1.Y);
        FloatBuffer.Add(p1.Z);
        FloatBuffer.Add(p2.X);
        FloatBuffer.Add(p2.Y);
        FloatBuffer.Add(p2.Z);
        
        DrawExtension(CompositorBuiltInExtensions.Line3D, dataParam: pen, floatOffset: floatOffset, floatCount: 6);
    }

    public void DrawHatch(Brush brush, PathGeometry boundaries)
    {
        Commands.Add(new RenderCommand
        {
            Type = RenderCommandType.DrawExtension,
            ExtensionId = CompositorBuiltInExtensions.Hatch,
            Brush = brush,
            Path = boundaries
        });
    }

    public void DrawEllipse(Brush? brush, Pen? pen, Vector2 center, float radiusX, float radiusY)
    {
        Commands.Add(new RenderCommand
        {
            Type = RenderCommandType.DrawEllipse,
            Brush = brush,
            Pen = pen,
            Position2 = center,
            RadiusX = radiusX,
            RadiusY = radiusY
        });
    }

    public void DrawEllipse(
        Brush? brush,
        Pen? pen,
        Vector2 center,
        float radiusX,
        float radiusY,
        Matrix4x4 transform)
    {
        Commands.Add(new RenderCommand
        {
            Type = RenderCommandType.DrawEllipse,
            Brush = brush,
            Pen = pen,
            Position2 = center,
            RadiusX = radiusX,
            RadiusY = radiusY,
            Transform = transform
        });
    }

    public void FillEllipse(Brush brush, Vector2 center, float radiusX, float radiusY)
    {
        DrawEllipse(brush, null, center, radiusX, radiusY);
    }

    public void DrawCircle(Brush? brush, Pen? pen, Vector2 center, float radius)
    {
        Commands.Add(new RenderCommand
        {
            Type = RenderCommandType.DrawCircle,
            Brush = brush,
            Pen = pen,
            Position2 = center,
            RadiusX = radius
        });
    }

    public void FillCircle(Brush brush, Vector2 center, float radius)
    {
        DrawCircle(brush, null, center, radius);
    }

    public void DrawRoundedRectangle(Brush? brush, Pen? pen, Rect rect, float radius)
    {
        DrawRoundedRectangle(brush, pen, rect, radius, radius);
    }

    public void DrawRoundedRectangle(Brush? brush, Pen? pen, Rect rect, float radiusX, float radiusY)
    {
        if (brush is BackdropMaterialBrush backdropMaterial)
        {
            this.DrawBackdropMaterial(
                backdropMaterial,
                rect,
                new Vector4(radiusX),
                new Vector4(radiusY));
            if (pen == null)
            {
                return;
            }

            brush = null;
        }

        Commands.Add(new RenderCommand
        {
            Type = RenderCommandType.DrawRoundedRect,
            Brush = brush,
            Pen = pen,
            Rect = rect,
            RadiusX = radiusX,
            RadiusY = radiusY
        });
    }

    public void DrawRoundedRectangle(
        Brush? brush,
        Pen? pen,
        Rect rect,
        float radiusX,
        float radiusY,
        Matrix4x4 transform)
    {
        if (brush is BackdropMaterialBrush backdropMaterial)
        {
            this.DrawBackdropMaterial(
                backdropMaterial,
                rect,
                new Vector4(radiusX),
                new Vector4(radiusY),
                transform);
            if (pen == null)
            {
                return;
            }

            brush = null;
        }

        Commands.Add(new RenderCommand
        {
            Type = RenderCommandType.DrawRoundedRect,
            Brush = brush,
            Pen = pen,
            Rect = rect,
            RadiusX = radiusX,
            RadiusY = radiusY,
            Transform = transform
        });
    }

    public void FillRoundedRectangle(Brush brush, Rect rect, float radius)
    {
        DrawRoundedRectangle(brush, null, rect, radius);
    }

    public void DrawQuadraticBezier(Pen pen, Vector2 p0, Vector2 p1, Vector2 p2)
    {
        Commands.Add(new RenderCommand
        {
            Type = RenderCommandType.DrawBezier,
            Pen = pen,
            Position = p0,
            Position2 = p1,
            Position3 = p2,
            GeometryCache = RenderCommandGeometryCache.ForStrokePath(
                RenderCommandGeometryCache.CreateQuadraticBezierPath(p0, p1, p2))
        });
    }

    public void DrawCubicBezier(Pen pen, Vector2 p0, Vector2 p1, Vector2 p2, Vector2 p3)
    {
        Commands.Add(new RenderCommand
        {
            Type = RenderCommandType.DrawCubicBezier,
            Pen = pen,
            Position = p0,
            Position2 = p1,
            Position3 = p2,
            Position4 = p3,
            GeometryCache = RenderCommandGeometryCache.ForStrokePath(
                RenderCommandGeometryCache.CreateCubicBezierPath(p0, p1, p2, p3))
        });
    }

    public void FillTriangle(Brush brush, Vector2 p1, Vector2 p2, Vector2 p3)
    {
        Commands.Add(new RenderCommand
        {
            Type = RenderCommandType.FillTriangle,
            Brush = brush,
            Position = p1,
            Position2 = p2,
            Position3 = p3,
            GeometryCache = RenderCommandGeometryCache.ForFillPath(
                RenderCommandGeometryCache.CreateTrianglePath(p1, p2, p3))
        });
    }

    public void FillQuad(Brush brush, Vector2 p1, Vector2 p2, Vector2 p3, Vector2 p4)
    {
        Commands.Add(new RenderCommand
        {
            Type = RenderCommandType.FillQuad,
            Brush = brush,
            Position = p1,
            Position2 = p2,
            Position3 = p3,
            Position4 = p4,
            GeometryCache = RenderCommandGeometryCache.ForFillPaths(
                RenderCommandGeometryCache.CreateTrianglePath(p1, p2, p3),
                RenderCommandGeometryCache.CreateTrianglePath(p1, p3, p4))
        });
    }

    public void DrawVertexMesh(
        Brush brush,
        VertexMesh2D mesh,
        VertexColorBlendMode colorBlendMode = VertexColorBlendMode.Modulate,
        Matrix4x4 transform = default,
        bool isEdgeAliased = false)
    {
        ArgumentNullException.ThrowIfNull(brush);
        ArgumentNullException.ThrowIfNull(mesh);
        Commands.Add(new RenderCommand
        {
            Type = RenderCommandType.DrawVertexMesh,
            Brush = brush,
            VertexMesh = mesh,
            VertexColorBlendMode = colorBlendMode,
            Transform = transform,
            IsEdgeAliased = isEdgeAliased
        });
    }

    public void DrawPointBatch(
        Brush brush,
        Vector2[] points,
        float radius,
        bool round,
        Matrix4x4 transform = default,
        bool isEdgeAliased = false)
    {
        ArgumentNullException.ThrowIfNull(brush);
        ArgumentNullException.ThrowIfNull(points);
        Commands.Add(new RenderCommand
        {
            Type = RenderCommandType.DrawPointBatch,
            Brush = brush,
            PolylinePoints = points,
            RadiusX = radius,
            IntParam = round ? 1 : 0,
            Transform = transform,
            IsEdgeAliased = isEdgeAliased
        });
    }

    public void DrawStaticDxf(object staticBuffer)
    {
        DrawExtension(CompositorBuiltInExtensions.StaticDxf, dataParam: staticBuffer);
    }

    // --- Modern Zero-Allocation Span-Based APIs ---

    public void DrawPolyline(Pen pen, ReadOnlySpan<Vector2> points, bool isClosed = false)
    {
        int offset = PointBuffer.Count;
        int count = points.Length;
        int required = offset + count;
        if (PointBuffer.Capacity < required)
            PointBuffer.Capacity = Math.Max(required, PointBuffer.Capacity * 2);
        CollectionsMarshal.SetCount(PointBuffer, required);
        points.CopyTo(CollectionsMarshal.AsSpan(PointBuffer).Slice(offset, count));

        Commands.Add(new RenderCommand
        {
            Type = RenderCommandType.DrawPolyline,
            Pen = pen,
            PointBufferOffset = offset,
            PointBufferCount = count,
            IsClosed = isClosed,
            GeometryCache = RenderCommandGeometryCache.ForStrokePath(
                RenderCommandGeometryCache.CreatePolylinePath(points, isClosed))
        });
    }

    public void DrawSpline(Pen pen, ReadOnlySpan<Vector2> controlPoints, ReadOnlySpan<double> knots, int degree)
    {
        DrawSpline(pen, controlPoints, knots, default, degree, false);
    }

    public void DrawSpline(Pen pen, ReadOnlySpan<Vector2> controlPoints, ReadOnlySpan<double> knots, ReadOnlySpan<double> weights, int degree, bool isClosed)
    {
        int ptOffset = PointBuffer.Count;
        int ptCount = controlPoints.Length;
        int ptRequired = ptOffset + ptCount;
        if (PointBuffer.Capacity < ptRequired)
            PointBuffer.Capacity = Math.Max(ptRequired, PointBuffer.Capacity * 2);
        CollectionsMarshal.SetCount(PointBuffer, ptRequired);
        controlPoints.CopyTo(CollectionsMarshal.AsSpan(PointBuffer).Slice(ptOffset, ptCount));

        int knotOffset = DoubleBuffer.Count;
        int knotCount = knots.Length;
        int knotRequired = knotOffset + knotCount;
        if (DoubleBuffer.Capacity < knotRequired)
            DoubleBuffer.Capacity = Math.Max(knotRequired, DoubleBuffer.Capacity * 2);
        CollectionsMarshal.SetCount(DoubleBuffer, knotRequired);
        knots.CopyTo(CollectionsMarshal.AsSpan(DoubleBuffer).Slice(knotOffset, knotCount));

        int weightOffset = 0;
        int weightCount = 0;
        if (!weights.IsEmpty)
        {
            weightOffset = DoubleBuffer.Count;
            weightCount = weights.Length;
            int weightRequired = weightOffset + weightCount;
            if (DoubleBuffer.Capacity < weightRequired)
                DoubleBuffer.Capacity = Math.Max(weightRequired, DoubleBuffer.Capacity * 2);
            CollectionsMarshal.SetCount(DoubleBuffer, weightRequired);
            weights.CopyTo(CollectionsMarshal.AsSpan(DoubleBuffer).Slice(weightOffset, weightCount));
        }

        Commands.Add(new RenderCommand
        {
            Type = RenderCommandType.DrawExtension,
            ExtensionId = CompositorBuiltInExtensions.Spline,
            Pen = pen,
            PointBufferOffset = ptOffset,
            PointBufferCount = ptCount,
            DoubleBufferOffset = knotOffset,
            DoubleBufferCount = knotCount,
            WeightBufferOffset = weightOffset,
            WeightBufferCount = weightCount,
            SplineDegree = degree,
            IsClosed = isClosed,
            GeometryCache = RenderCommandGeometryCache.ForStrokePath(
                RenderCommandGeometryCache.CreateSplinePath(controlPoints, knots, weights, degree, isClosed))
        });
    }

    public void DrawAcisSolid(Pen pen, ReadOnlySpan<Line3D> edges, Matrix4x4 modelTransform)
    {
        int offset = Line3DBuffer.Count;
        int count = edges.Length;
        int required = offset + count;
        if (Line3DBuffer.Capacity < required)
            Line3DBuffer.Capacity = Math.Max(required, Line3DBuffer.Capacity * 2);
        CollectionsMarshal.SetCount(Line3DBuffer, required);
        edges.CopyTo(CollectionsMarshal.AsSpan(Line3DBuffer).Slice(offset, count));

        Commands.Add(new RenderCommand
        {
            Type = RenderCommandType.DrawExtension,
            ExtensionId = CompositorBuiltInExtensions.AcisSolid,
            Pen = pen,
            Line3DBufferOffset = offset,
            Line3DBufferCount = count,
            Transform = modelTransform
        });
    }

    public void DrawGpuLineSeries(ReadOnlySpan<float> interleavedCoords, int pointsCount, float thickness, Brush brush)
    {
        int offset = FloatBuffer.Count;
        int count = interleavedCoords.Length;
        int required = offset + count;
        if (FloatBuffer.Capacity < required)
            FloatBuffer.Capacity = Math.Max(required, FloatBuffer.Capacity * 2);
        CollectionsMarshal.SetCount(FloatBuffer, required);
        interleavedCoords.CopyTo(CollectionsMarshal.AsSpan(FloatBuffer).Slice(offset, count));

        Commands.Add(new RenderCommand
        {
            Type = RenderCommandType.DrawExtension,
            ExtensionId = CompositorBuiltInExtensions.GpuLineSeries,
            FloatBufferOffset = offset,
            FloatBufferCount = count,
            GpuPointsCount = pointsCount,
            RadiusX = thickness,
            Brush = brush,
            SeriesCacheKey = new object(),
            Scale = Vector2.One,
            Translate = Vector2.Zero,
            Transform = Matrix4x4.Identity
        });
    }

    public void DrawGpuScatterSeries(ReadOnlySpan<float> interleavedCoords, int pointsCount, float radius, Brush brush)
    {
        int offset = FloatBuffer.Count;
        int count = interleavedCoords.Length;
        int required = offset + count;
        if (FloatBuffer.Capacity < required)
            FloatBuffer.Capacity = Math.Max(required, FloatBuffer.Capacity * 2);
        CollectionsMarshal.SetCount(FloatBuffer, required);
        interleavedCoords.CopyTo(CollectionsMarshal.AsSpan(FloatBuffer).Slice(offset, count));

        Commands.Add(new RenderCommand
        {
            Type = RenderCommandType.DrawExtension,
            ExtensionId = CompositorBuiltInExtensions.GpuScatterSeries,
            FloatBufferOffset = offset,
            FloatBufferCount = count,
            GpuPointsCount = pointsCount,
            RadiusX = radius,
            Brush = brush,
            SeriesCacheKey = new object(),
            Scale = Vector2.One,
            Translate = Vector2.Zero,
            Transform = Matrix4x4.Identity
        });
    }

    // --- Skia-like Picture drawing commands ---

    public void DrawPicture(GpuPicture picture)
    {
        RetainPictureResources(picture);
        Commands.Add(new RenderCommand
        {
            Type = RenderCommandType.DrawPicture,
            Picture = picture
        });
    }

    public void DrawPicture(GpuPicture picture, Matrix4x4 cameraView)
    {
        RetainPictureResources(picture);
        Commands.Add(new RenderCommand
        {
            Type = RenderCommandType.DrawPicture,
            Picture = picture,
            UseGpuTransforms = true,
            CameraView = cameraView
        });
    }

    public void DrawPictureTransformed(GpuPicture picture, Matrix4x4 transform)
    {
        RetainPictureResources(picture);
        Commands.Add(new RenderCommand
        {
            Type = RenderCommandType.DrawPicture,
            Picture = picture,
            Transform = transform
        });
    }

    private void RetainPictureResources(GpuPicture picture)
    {
        ArgumentNullException.ThrowIfNull(picture);
        AppendRetainedResources(picture.CloneRetainedResources());
    }

    public void DrawExtension(
        int extensionId,
        int intParam = 0,
        float floatParam = 0f,
        object? dataParam = null,
        int pointOffset = 0,
        int pointCount = 0,
        int floatOffset = 0,
        int floatCount = 0,
        Matrix4x4 transform = default)
    {
        Commands.Add(new RenderCommand
        {
            Type = RenderCommandType.DrawExtension,
            ExtensionId = extensionId,
            IntParam = intParam,
            FloatParam = floatParam,
            DataParam = dataParam,
            PointBufferOffset = pointOffset,
            PointBufferCount = pointCount,
            FloatBufferOffset = floatOffset,
            FloatBufferCount = floatCount,
            Transform = transform
        });
    }

    // --- Backward Compatible Overloads (Forward to Spans) ---

    public void DrawPolyline(Pen pen, Vector2[] points, bool isClosed = false)
    {
        DrawPolyline(pen, new ReadOnlySpan<Vector2>(points), isClosed);
        if (Commands.Count > 0)
        {
            var cmd = Commands[Commands.Count - 1];
            cmd.PolylinePoints = points;
            Commands[Commands.Count - 1] = cmd;
        }
    }

    public void DrawSpline(Pen pen, Vector2[] controlPoints, double[] knots, int degree)
    {
        DrawSpline(pen, new ReadOnlySpan<Vector2>(controlPoints), new ReadOnlySpan<double>(knots), degree);
    }

    public void DrawSpline(Pen pen, Vector2[] controlPoints, double[] knots, double[]? weights, int degree, bool isClosed)
    {
        DrawSpline(pen, new ReadOnlySpan<Vector2>(controlPoints), new ReadOnlySpan<double>(knots), weights == null ? default : new ReadOnlySpan<double>(weights), degree, isClosed);
    }

    public void DrawAcisSolid(Pen pen, List<Line3D> edges, Matrix4x4 modelTransform)
    {
        DrawAcisSolid(pen, CollectionsMarshal.AsSpan(edges), modelTransform);
    }

    public void DrawGpuLineSeries(float[] interleavedCoords, int pointsCount, float thickness, Brush brush)
    {
        DrawGpuLineSeries(new ReadOnlySpan<float>(interleavedCoords), pointsCount, thickness, brush);
    }

    public void DrawGpuLineSeries(object staticBuffer, float thickness, Brush brush)
    {
        DrawGpuLineSeries(staticBuffer, thickness, brush, Vector2.One, Vector2.Zero);
    }

    public void DrawGpuLineSeries(object staticBuffer, float thickness, Brush brush, Vector2 scale, Vector2 translate)
    {
        Commands.Add(new RenderCommand
        {
            Type = RenderCommandType.DrawExtension,
            ExtensionId = CompositorBuiltInExtensions.GpuLineSeries,
            StaticBuffer = staticBuffer,
            RadiusX = thickness,
            Brush = brush,
            Scale = scale,
            Translate = translate,
            Transform = Matrix4x4.Identity
        });
    }

    public void DrawGpuScatterSeries(float[] interleavedCoords, int pointsCount, float radius, Brush brush)
    {
        DrawGpuScatterSeries(new ReadOnlySpan<float>(interleavedCoords), pointsCount, radius, brush);
    }

    public void DrawGpuScatterSeries(object staticBuffer, float radius, Brush brush)
    {
        DrawGpuScatterSeries(staticBuffer, radius, brush, Vector2.One, Vector2.Zero);
    }

    public void DrawGpuScatterSeries(object staticBuffer, float radius, Brush brush, Vector2 scale, Vector2 translate)
    {
        Commands.Add(new RenderCommand
        {
            Type = RenderCommandType.DrawExtension,
            ExtensionId = CompositorBuiltInExtensions.GpuScatterSeries,
            StaticBuffer = staticBuffer,
            RadiusX = radius,
            Brush = brush,
            Scale = scale,
            Translate = translate,
            Transform = Matrix4x4.Identity
        });
    }

    // --- Bulk Scene Context Manipulation ---

    public void Append(DrawingContext other)
    {
        Append(other, Vector2.Zero);
    }

    public void Append(DrawingContext other, Vector2 translation)
    {
        int pointOffset = PointBuffer.Count;
        int doubleOffset = DoubleBuffer.Count;
        int line3dOffset = Line3DBuffer.Count;
        int floatOffset = FloatBuffer.Count;

        AppendList(PointBuffer, other.PointBuffer);
        AppendList(DoubleBuffer, other.DoubleBuffer);
        AppendList(Line3DBuffer, other.Line3DBuffer);
        AppendList(FloatBuffer, other.FloatBuffer);

        var otherCommands = other.Commands;
        int otherCommandCount = otherCommands.Count;
        for (int commandIndex = 0; commandIndex < otherCommandCount; commandIndex++)
        {
            var cmd = otherCommands[commandIndex];
            var adjustedCmd = cmd;
            if (adjustedCmd.PointBufferCount > 0)
                adjustedCmd.PointBufferOffset += pointOffset;
            if (adjustedCmd.DoubleBufferCount > 0)
                adjustedCmd.DoubleBufferOffset += doubleOffset;
            if (adjustedCmd.Line3DBufferCount > 0)
                adjustedCmd.Line3DBufferOffset += line3dOffset;
            if (adjustedCmd.FloatBufferCount > 0)
                adjustedCmd.FloatBufferOffset += floatOffset;
            if (adjustedCmd.WeightBufferCount > 0)
                adjustedCmd.WeightBufferOffset += doubleOffset;

            if (translation != Vector2.Zero)
            {
                if (adjustedCmd.Type == RenderCommandType.PushOpacityMask && adjustedCmd.Picture != null)
                {
                    ComposeAppendTranslation(ref adjustedCmd, translation);
                }
                else if (adjustedCmd.Type == RenderCommandType.DrawRect ||
                    adjustedCmd.Type == RenderCommandType.DrawTexture ||
                    adjustedCmd.Type == RenderCommandType.DrawRoundedRect ||
                    adjustedCmd.Type == RenderCommandType.PushClip ||
                    adjustedCmd.Type == RenderCommandType.PushOpacityMask)
                {
                    TranslateRectBackedCommand(ref adjustedCmd, translation);
                }
                else if (adjustedCmd.Type == RenderCommandType.PushGeometryClip ||
                         adjustedCmd.Type == RenderCommandType.DrawPath ||
                         adjustedCmd.Type == RenderCommandType.DrawVertexMesh ||
                         adjustedCmd.Type == RenderCommandType.DrawPointBatch)
                {
                    ComposeAppendTranslation(ref adjustedCmd, translation);
                }
                else if (IsGpuSeriesCommand(adjustedCmd))
                {
                    TranslateGpuSeriesCommand(ref adjustedCmd, translation);
                }
                else if (HasNonIdentityTransform(adjustedCmd))
                {
                    ComposeAppendTranslation(ref adjustedCmd, translation);
                }
                else
                {
                    if (adjustedCmd.Type == RenderCommandType.DrawExtension &&
                        IsRectBackedExtensionDataParam(adjustedCmd.DataParam))
                    {
                        adjustedCmd.DataParam = TranslateExtensionDataParam(adjustedCmd.DataParam, translation);
                    }

                    adjustedCmd.Position += translation;
                    adjustedCmd.Position2 += translation;
                    adjustedCmd.Position3 += translation;
                    adjustedCmd.Position4 += translation;

                    TranslatePointBufferSlice(adjustedCmd.PointBufferOffset, adjustedCmd.PointBufferCount, translation);
                    TranslateLine3DBufferSlice(adjustedCmd.Line3DBufferOffset, adjustedCmd.Line3DBufferCount, translation);

                    if (adjustedCmd.PolylinePoints != null)
                    {
                        var newPoints = new Vector2[adjustedCmd.PolylinePoints.Length];
                        for (int i = 0; i < adjustedCmd.PolylinePoints.Length; i++)
                        {
                            newPoints[i] = adjustedCmd.PolylinePoints[i] + translation;
                        }
                        adjustedCmd.PolylinePoints = newPoints;
                    }
                }

                adjustedCmd.GeometryCache = null;
            }

            Commands.Add(adjustedCmd);
        }

        var retainedResources = other.CloneRetainedResources();
        AppendRetainedResources(retainedResources);
    }

    private static void AppendList<T>(List<T> destination, List<T> source)
    {
        int sourceCount = source.Count;
        if (sourceCount == 0)
        {
            return;
        }

        destination.EnsureCapacity(checked(destination.Count + sourceCount));
        for (int sourceIndex = 0; sourceIndex < sourceCount; sourceIndex++)
        {
            destination.Add(source[sourceIndex]);
        }
    }

    private void AppendRetainedResources(RetainedResourceLease[] resources)
    {
        if (resources.Length == 0)
        {
            return;
        }

        _retainedResources.EnsureCapacity(checked(_retainedResources.Count + resources.Length));
        for (int resourceIndex = 0; resourceIndex < resources.Length; resourceIndex++)
        {
            var resource = resources[resourceIndex];
            var identity = resource.Identity;
            if (identity is not null && HasRetainedResourceIdentity(identity))
            {
                resource.Dispose();
                continue;
            }

            _retainedResources.Add(resource);
        }
    }

    private void TranslatePointBufferSlice(int offset, int count, Vector2 translation)
    {
        for (int i = 0; i < count; i++)
        {
            PointBuffer[offset + i] += translation;
        }
    }

    private void TranslateLine3DBufferSlice(int offset, int count, Vector2 translation)
    {
        var trans3D = new Vector3(translation.X, translation.Y, 0f);
        for (int i = 0; i < count; i++)
        {
            var line = Line3DBuffer[offset + i];
            line.Start += trans3D;
            line.End += trans3D;
            Line3DBuffer[offset + i] = line;
        }
    }

    private static void TranslateRectBackedCommand(ref RenderCommand command, Vector2 translation)
    {
        if (HasNonIdentityTransform(command))
        {
            ComposeAppendTranslation(ref command, translation);
        }
        else
        {
            command.Rect = TranslateRect(command.Rect, translation);
        }
    }

    private static void ComposeAppendTranslation(ref RenderCommand command, Vector2 translation)
    {
        var translationTransform = Matrix4x4.CreateTranslation(translation.X, translation.Y, 0f);
        var commandTransform = command.Transform == default
            ? Matrix4x4.Identity
            : command.Transform;
        command.Transform = commandTransform * translationTransform;
    }

    private static bool HasNonIdentityTransform(RenderCommand command)
    {
        return command.Transform != default && command.Transform != Matrix4x4.Identity;
    }

    private static bool IsGpuSeriesCommand(RenderCommand command)
    {
        return command.Type == RenderCommandType.DrawGpuLineSeries ||
               command.Type == RenderCommandType.DrawGpuScatterSeries ||
               (command.Type == RenderCommandType.DrawExtension &&
                (command.ExtensionId == CompositorBuiltInExtensions.GpuLineSeries ||
                 command.ExtensionId == CompositorBuiltInExtensions.GpuScatterSeries));
    }

    private static void TranslateGpuSeriesCommand(ref RenderCommand command, Vector2 translation)
    {
        if (HasNonIdentityTransform(command))
        {
            ComposeAppendTranslation(ref command, translation);
        }
        else
        {
            command.Translate += translation;
        }
    }

    private static bool IsRectBackedExtensionDataParam(object? dataParam)
    {
        return dataParam is ImageEffectParams or WpfShaderEffectParams or ShaderToyParams or BackdropMaterialParams;
    }

    private static object? TranslateExtensionDataParam(object? dataParam, Vector2 translation)
    {
        return dataParam switch
        {
            ImageEffectParams imageEffect => new ImageEffectParams
            {
                Texture = imageEffect.Texture,
                Rect = TranslateRect(imageEffect.Rect, translation),
                SourceRect = imageEffect.SourceRect,
                SamplingMode = imageEffect.SamplingMode,
                Brightness = imageEffect.Brightness,
                Contrast = imageEffect.Contrast,
                Saturation = imageEffect.Saturation,
                Grayscale = imageEffect.Grayscale,
                Sepia = imageEffect.Sepia,
                Invert = imageEffect.Invert,
                BlurSigma = imageEffect.BlurSigma,
                ColorMatrix = imageEffect.ColorMatrix,
                LuminanceToAlpha = imageEffect.LuminanceToAlpha,
                MaskTexture = imageEffect.MaskTexture,
                LastError = imageEffect.LastError
            },
            WpfShaderEffectParams wpfShaderEffect => new WpfShaderEffectParams
            {
                Texture = wpfShaderEffect.Texture,
                Rect = TranslateRect(wpfShaderEffect.Rect, translation),
                ShaderSource = wpfShaderEffect.ShaderSource,
                ShaderKey = wpfShaderEffect.ShaderKey,
                Constants = wpfShaderEffect.Constants,
                Samplers = wpfShaderEffect.Samplers,
                SamplingMode = wpfShaderEffect.SamplingMode,
                IsFailed = wpfShaderEffect.IsFailed,
                LastError = wpfShaderEffect.LastError,
                SourceTextureRegisterIndex = wpfShaderEffect.SourceTextureRegisterIndex,
                SourceTextureOverridesSampler = wpfShaderEffect.SourceTextureOverridesSampler
            },
            ShaderToyParams shaderToy => new ShaderToyParams
            {
                Rect = TranslateRect(shaderToy.Rect, translation),
                ShaderSource = shaderToy.ShaderSource,
                ShaderKey = shaderToy.ShaderKey,
                OldShaderKey = shaderToy.OldShaderKey,
                IsFailed = shaderToy.IsFailed,
                Resolution = shaderToy.Resolution,
                Time = shaderToy.Time,
                TimeDelta = shaderToy.TimeDelta,
                Frame = shaderToy.Frame,
                FrameRate = shaderToy.FrameRate,
                Mouse = shaderToy.Mouse,
                Date = shaderToy.Date
            },
            BackdropMaterialParams backdropMaterial => backdropMaterial.Translate(translation),
            _ => dataParam
        };
    }

    private static Rect TranslateRect(Rect rect, Vector2 translation)
    {
        return new Rect(rect.Position + translation, rect.Size);
    }

    public void Clear()
    {
        Commands.Clear();
        PointBuffer.Clear();
        DoubleBuffer.Clear();
        Line3DBuffer.Clear();
        FloatBuffer.Clear();
        DisposeRetainedResources();
    }

    private void DisposeRetainedResources()
    {
        for (int i = 0; i < _retainedResources.Count; i++)
        {
            _retainedResources[i].Dispose();
        }

        _retainedResources.Clear();
    }
}
