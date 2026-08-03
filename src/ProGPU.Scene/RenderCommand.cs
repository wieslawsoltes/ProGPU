using System;
using System.Buffers;
using System.Collections;
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
    DrawVisual,
    DrawExtension,
    PushGeometryClip,
    PopGeometryClip,
    PushOpacityMask,
    PopOpacityMask,
    PushBlendMode,
    PopBlendMode,
    DrawGlyphRun,
    DrawVertexMesh,
    DrawPointBatch,
    DrawDotGrid
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
    LinearMipmap,
    MagLinearMinLinearMipNearest,
    MagLinearMinNearestMipLinear,
    MagLinearMinNearestMipNearest,
    MagNearestMinLinearMipLinear,
    MagNearestMinLinearMipNearest,
    MagNearestMinNearestMipLinear
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

[Flags]
public enum RenderCommandPresentationDependencies : byte
{
    None = 0,
    TextureSampling = 1 << 0,
    TextRendering = 1 << 1,
    TextHinting = 1 << 2
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

internal interface IImageEffectDataProvider
{
    ImageEffectCommandData GetImageEffect(int index);
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
    public TextShapingOptions? TextShapingOptions;
    public TextAlignment TextAlignment;
    public Vector2 FontTransform;
    public bool HasFontTransform;
    public float Rotation;
    public TextRenderingMode TextRenderingMode;
    public TextHintingMode TextHintingMode;
    public RenderCommandPresentationDependencies PresentationDependencies;
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
    public bool SnapTextureToPixels;
    public bool HasImageEffect;
    private ImageEffectCommandDataBox? _imageEffect;
    internal int ImageEffectBufferIndex;
    internal bool HasBufferedImageEffect;

    public ImageEffectCommandData ImageEffect
    {
        readonly get => _imageEffect?.Value ?? default;
        set => _imageEffect = new ImageEffectCommandDataBox(value);
    }

    internal readonly ImageEffectCommandData ResolveImageEffect(
        IRenderDataProvider? provider)
    {
        if (HasBufferedImageEffect)
        {
            if (provider is not IImageEffectDataProvider effectProvider)
            {
                throw new InvalidOperationException(
                    "A buffered image-effect command requires its retained data provider.");
            }

            return effectProvider.GetImageEffect(ImageEffectBufferIndex);
        }

        return ImageEffect;
    }

    internal ImageEffectCommandDataBox? InlineImageEffectBox
    {
        readonly get => _imageEffect;
        set => _imageEffect = value;
    }

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

    // Borrowed retained-scene root. The owner must keep the visual alive and
    // immutable except through Visual's invalidating property/child APIs.
    public Visual? Visual;

    // Glyph run properties (Skia SKTextBlob compatibility)
    public ushort[]? GlyphIndices;
    public Vector2[]? GlyphPositions;
    public int GlyphRangeStart;
    public int GlyphRangeCount;

    // Batched two-dimensional vertex mesh properties
    public VertexMesh2D? VertexMesh;
    public VertexColorBlendMode VertexColorBlendMode;

    // High performance custom drawing extension properties
    public int ExtensionId;
    public int IntParam;
    public float FloatParam;
    public object? DataParam;
}

internal enum RetainedCommandDataKind : byte
{
    Basic,
    Auxiliary,
    Text,
    Texture,
    SimpleTexture,
    SimpleRectangle,
    SimplePath,
    RectangleClip,
    GeometryClip,
    NoData,
    SimpleGlyphRun,
    ScalarState,
    SimpleRoundedRectangle,
    SimpleVisual
}

internal readonly struct RetainedTextCommandData
{
    private readonly string? _text;
    private readonly TtfFont? _font;
    private readonly float _fontSize;
    private readonly Vector2 _position;
    private readonly bool _isBold;
    private readonly bool _isItalic;
    private readonly TextShapingOptions? _textShapingOptions;
    private readonly TextAlignment _textAlignment;
    private readonly Vector2 _fontTransform;
    private readonly bool _hasFontTransform;
    private readonly float _rotation;
    private readonly TextRenderingMode _textRenderingMode;
    private readonly TextHintingMode _textHintingMode;
    private readonly bool _useVectorGlyphRendering;
    private readonly bool _preferGlyphAtlas;
    private readonly bool _useLogicalGlyphAtlasResolution;
    private readonly ushort[]? _glyphIndices;
    private readonly Vector2[]? _glyphPositions;
    private readonly int _glyphRangeStart;
    private readonly int _glyphRangeCount;

    public RetainedTextCommandData(in RenderCommand command)
    {
        _text = command.Text;
        _font = command.Font;
        _fontSize = command.FontSize;
        _position = command.Position;
        _isBold = command.IsBold;
        _isItalic = command.IsItalic;
        _textShapingOptions = command.TextShapingOptions;
        _textAlignment = command.TextAlignment;
        _fontTransform = command.FontTransform;
        _hasFontTransform = command.HasFontTransform;
        _rotation = command.Rotation;
        _textRenderingMode = command.TextRenderingMode;
        _textHintingMode = command.TextHintingMode;
        _useVectorGlyphRendering = command.UseVectorGlyphRendering;
        _preferGlyphAtlas = command.PreferGlyphAtlas;
        _useLogicalGlyphAtlasResolution = command.UseLogicalGlyphAtlasResolution;
        _glyphIndices = command.GlyphIndices;
        _glyphPositions = command.GlyphPositions;
        _glyphRangeStart = command.GlyphRangeStart;
        _glyphRangeCount = command.GlyphRangeCount;
    }

    public void Apply(ref RenderCommand command)
    {
        command.Text = _text;
        command.Font = _font;
        command.FontSize = _fontSize;
        command.Position = _position;
        command.IsBold = _isBold;
        command.IsItalic = _isItalic;
        command.TextShapingOptions = _textShapingOptions;
        command.TextAlignment = _textAlignment;
        command.FontTransform = _fontTransform;
        command.HasFontTransform = _hasFontTransform;
        command.Rotation = _rotation;
        command.TextRenderingMode = _textRenderingMode;
        command.TextHintingMode = _textHintingMode;
        command.UseVectorGlyphRendering = _useVectorGlyphRendering;
        command.PreferGlyphAtlas = _preferGlyphAtlas;
        command.UseLogicalGlyphAtlasResolution = _useLogicalGlyphAtlasResolution;
        command.GlyphIndices = _glyphIndices;
        command.GlyphPositions = _glyphPositions;
        command.GlyphRangeStart = _glyphRangeStart;
        command.GlyphRangeCount = _glyphRangeCount;
    }
}

internal readonly struct RetainedTextureCommandData
{
    private readonly GpuTexture? _texture;
    private readonly Rect _sourceRect;
    private readonly TexturePatch[]? _patches;
    private readonly TextureSamplingMode _samplingMode;
    private readonly byte _maxAnisotropy;
    private readonly Vector2 _cubicCoefficients;
    private readonly bool _hasCubicCoefficients;
    private readonly bool _snapToPixels;
    private readonly bool _hasImageEffect;
    private readonly ImageEffectCommandDataBox? _inlineImageEffect;
    private readonly int _imageEffectBufferIndex;
    private readonly bool _hasBufferedImageEffect;

    public RetainedTextureCommandData(in RenderCommand command)
    {
        _texture = command.Texture;
        _sourceRect = command.SrcRect;
        _patches = command.TexturePatches;
        _samplingMode = command.TextureSamplingMode;
        _maxAnisotropy = command.TextureMaxAnisotropy;
        _cubicCoefficients = command.TextureCubicCoefficients;
        _hasCubicCoefficients = command.HasTextureCubicCoefficients;
        _snapToPixels = command.SnapTextureToPixels;
        _hasImageEffect = command.HasImageEffect;
        _inlineImageEffect = command.InlineImageEffectBox;
        _imageEffectBufferIndex = command.ImageEffectBufferIndex;
        _hasBufferedImageEffect = command.HasBufferedImageEffect;
    }

    public void Apply(ref RenderCommand command)
    {
        command.Texture = _texture;
        command.SrcRect = _sourceRect;
        command.TexturePatches = _patches;
        command.TextureSamplingMode = _samplingMode;
        command.TextureMaxAnisotropy = _maxAnisotropy;
        command.TextureCubicCoefficients = _cubicCoefficients;
        command.HasTextureCubicCoefficients = _hasCubicCoefficients;
        command.SnapTextureToPixels = _snapToPixels;
        command.HasImageEffect = _hasImageEffect;
        command.InlineImageEffectBox = _inlineImageEffect;
        command.ImageEffectBufferIndex = _imageEffectBufferIndex;
        command.HasBufferedImageEffect = _hasBufferedImageEffect;
    }
}

internal readonly struct RetainedRenderCommand
{
    private readonly RenderCommandType _type;
    private readonly int _hitTestId;
    private readonly Rect _rect;
    private readonly Brush? _brush;
    private readonly Pen? _pen;
    private readonly PathGeometry? _path;
    private readonly RenderCommandGeometryCache? _geometryCache;
    private readonly int _transformIndex;
    private readonly RenderCommandPresentationDependencies _presentationDependencies;
    private readonly bool _isEdgeAliased;
    private readonly bool _isPenThicknessLocal;
    private readonly uint _pathSampleGrid;
    private readonly float _pathCoverageGamma;

    public RetainedRenderCommand(
        in RenderCommand command,
        int transformIndex)
    {
        _type = command.Type;
        _hitTestId = command.HitTestId;
        _rect = command.Rect;
        _brush = command.Brush;
        _pen = command.Pen;
        _path = command.Path;
        _geometryCache = command.GeometryCache;
        _transformIndex = transformIndex;
        _presentationDependencies = command.PresentationDependencies;
        _isEdgeAliased = command.IsEdgeAliased;
        _isPenThicknessLocal = command.IsPenThicknessLocal;
        _pathSampleGrid = command.PathSampleGrid;
        _pathCoverageGamma = command.PathCoverageGamma;
    }

    public RenderCommand Expand(Matrix4x4[] transforms)
    {
        var command = new RenderCommand
        {
            Type = _type,
            HitTestId = _hitTestId,
            Rect = _rect,
            Brush = _brush,
            Pen = _pen,
            Path = _path,
            GeometryCache = _geometryCache,
            Transform = transforms[_transformIndex],
            PresentationDependencies = _presentationDependencies,
            IsEdgeAliased = _isEdgeAliased,
            IsPenThicknessLocal = _isPenThicknessLocal,
            PathSampleGrid = _pathSampleGrid,
            PathCoverageGamma = _pathCoverageGamma
        };
        return command;
    }

    public static RetainedCommandDataKind Classify(in RenderCommand command)
    {
        switch (command.Type)
        {
            case RenderCommandType.DrawTexture:
                if (IsSimpleTexture(
                    in command,
                    HasTextData(in command),
                    HasOtherData(in command)))
                {
                    return RetainedCommandDataKind.SimpleTexture;
                }
                break;
            case RenderCommandType.DrawGlyphRun:
                if (IsSimpleGlyphRun(
                    in command,
                    HasTextureData(in command),
                    HasOtherData(in command)))
                {
                    return RetainedCommandDataKind.SimpleGlyphRun;
                }
                break;
            case RenderCommandType.DrawRoundedRect:
                if (IsSimpleRoundedRectangle(
                    in command,
                    HasTextData(in command),
                    HasTextureData(in command)))
                {
                    return RetainedCommandDataKind.SimpleRoundedRectangle;
                }
                break;
            case RenderCommandType.DrawVisual:
                if (IsSimpleVisual(
                    in command,
                    HasTextData(in command),
                    HasTextureData(in command)))
                {
                    return RetainedCommandDataKind.SimpleVisual;
                }
                break;
        }

        bool hasText = HasTextData(in command);
        bool hasTexture = HasTextureData(in command);
        bool hasOther = HasOtherData(in command);

        if (IsNoDataCommand(in command, hasText, hasTexture, hasOther))
        {
            return RetainedCommandDataKind.NoData;
        }

        if (IsSimpleRectangle(in command, hasText, hasTexture, hasOther))
        {
            return RetainedCommandDataKind.SimpleRectangle;
        }

        if (IsSimplePath(in command, hasText, hasTexture, hasOther))
        {
            return RetainedCommandDataKind.SimplePath;
        }

        if (IsRectangleClip(in command, hasText, hasTexture, hasOther))
        {
            return RetainedCommandDataKind.RectangleClip;
        }

        if (IsGeometryClip(in command, hasText, hasTexture, hasOther))
        {
            return RetainedCommandDataKind.GeometryClip;
        }

        if (IsScalarState(in command, hasText, hasTexture, hasOther))
        {
            return RetainedCommandDataKind.ScalarState;
        }

        if (!hasText && !hasTexture && !hasOther)
        {
            return RetainedCommandDataKind.Basic;
        }

        if (hasText && !hasTexture && !hasOther &&
            command.Type is RenderCommandType.DrawText or
                RenderCommandType.DrawGlyphRun)
        {
            return RetainedCommandDataKind.Text;
        }

        if (hasTexture && !hasText && !hasOther &&
            command.Type == RenderCommandType.DrawTexture)
        {
            return RetainedCommandDataKind.Texture;
        }

        return RetainedCommandDataKind.Auxiliary;
    }

    private static bool IsNoDataCommand(
        in RenderCommand command,
        bool hasText,
        bool hasTexture,
        bool hasOther) =>
        (command.Type is RenderCommandType.PopClip or
            RenderCommandType.PopOpacity or
            RenderCommandType.PopGeometryClip or
            RenderCommandType.PopOpacityMask or
            RenderCommandType.PopBlendMode) &&
        !hasText &&
        !hasTexture &&
        !hasOther &&
        HasDefaultCoreData(in command, allowBrush: false, allowPen: false, allowPath: false) &&
        command.Rect == default &&
        command.Transform == default;

    private static bool IsSimpleRectangle(
        in RenderCommand command,
        bool hasText,
        bool hasTexture,
        bool hasOther) =>
        command.Type == RenderCommandType.DrawRect &&
        !hasText &&
        !hasTexture &&
        !hasOther &&
        command.Path is null &&
        command.GeometryCache is null;

    private static bool IsSimplePath(
        in RenderCommand command,
        bool hasText,
        bool hasTexture,
        bool hasOther) =>
        command.Type == RenderCommandType.DrawPath &&
        !hasText &&
        !hasTexture &&
        !hasOther &&
        command.Rect == default;

    private static bool IsRectangleClip(
        in RenderCommand command,
        bool hasText,
        bool hasTexture,
        bool hasOther) =>
        command.Type == RenderCommandType.PushClip &&
        !hasText &&
        !hasTexture &&
        !hasOther &&
        HasDefaultCoreData(in command, allowBrush: false, allowPen: false, allowPath: false);

    private static bool IsGeometryClip(
        in RenderCommand command,
        bool hasText,
        bool hasTexture,
        bool hasOther) =>
        command.Type == RenderCommandType.PushGeometryClip &&
        !hasText &&
        !hasTexture &&
        !hasOther &&
        command.Rect == default &&
        HasDefaultCoreData(in command, allowBrush: false, allowPen: false, allowPath: true);

    private static bool IsSimpleTexture(
        in RenderCommand command,
        bool hasText,
        bool hasOther) =>
        command.Type == RenderCommandType.DrawTexture &&
        !hasText &&
        !hasOther &&
        command.Brush is null &&
        command.Pen is null &&
        command.Path is null &&
        command.GeometryCache is null &&
        command.TexturePatches is null &&
        !command.HasTextureCubicCoefficients &&
        !command.HasImageEffect &&
        command.InlineImageEffectBox is null &&
        command.ImageEffectBufferIndex == 0 &&
        !command.HasBufferedImageEffect &&
        !command.IsPenThicknessLocal &&
        command.PathSampleGrid == 0 &&
        command.PathCoverageGamma == 0f;

    private static bool IsSimpleGlyphRun(
        in RenderCommand command,
        bool hasTexture,
        bool hasOther) =>
        command.Type == RenderCommandType.DrawGlyphRun &&
        !hasTexture &&
        !hasOther &&
        command.Text is null &&
        command.TextShapingOptions is null &&
        command.TextAlignment == default &&
        command.Rotation == 0f &&
        command.Rect == default &&
        command.Pen is null &&
        command.Path is null &&
        command.GeometryCache is null &&
        !command.IsPenThicknessLocal &&
        command.PathSampleGrid == 0 &&
        command.PathCoverageGamma == 0f;

    private static bool IsScalarState(
        in RenderCommand command,
        bool hasText,
        bool hasTexture,
        bool hasOther)
    {
        if (hasTexture ||
            !HasDefaultCoreData(
                in command,
                allowBrush: false,
                allowPen: false,
                allowPath: false) ||
            command.Rect != default ||
            command.Transform != default)
        {
            return false;
        }

        return command.Type switch
        {
            RenderCommandType.PushOpacity =>
                hasText &&
                !hasOther &&
                HasOnlyFontSizeTextData(in command),
            RenderCommandType.PushBlendMode =>
                !hasText &&
                hasOther &&
                HasOnlyIntParamOtherData(in command),
            _ => false
        };
    }

    private static bool HasOnlyFontSizeTextData(in RenderCommand command) =>
        command.Text is null &&
        command.Font is null &&
        command.Position == default &&
        !command.IsBold &&
        !command.IsItalic &&
        command.TextShapingOptions is null &&
        command.TextAlignment == default &&
        command.FontTransform == default &&
        !command.HasFontTransform &&
        command.Rotation == 0f &&
        command.TextRenderingMode == default &&
        command.TextHintingMode == default &&
        !command.UseVectorGlyphRendering &&
        !command.PreferGlyphAtlas &&
        !command.UseLogicalGlyphAtlasResolution &&
        command.GlyphIndices is null &&
        command.GlyphPositions is null &&
        command.GlyphRangeStart == 0 &&
        command.GlyphRangeCount == 0;

    private static bool HasOnlyIntParamOtherData(in RenderCommand command) =>
        !HasOtherData(in command, allowIntParam: true);

    private static bool IsSimpleRoundedRectangle(
        in RenderCommand command,
        bool hasText,
        bool hasTexture) =>
        command.Type == RenderCommandType.DrawRoundedRect &&
        !hasText &&
        !hasTexture &&
        !HasOtherData(in command, allowRadii: true) &&
        command.Path is null &&
        command.GeometryCache is null;

    private static bool IsSimpleVisual(
        in RenderCommand command,
        bool hasText,
        bool hasTexture) =>
        command.Type == RenderCommandType.DrawVisual &&
        !hasText &&
        !hasTexture &&
        !HasOtherData(in command, allowVisual: true) &&
        command.Rect == default &&
        HasDefaultCoreData(
            in command,
            allowBrush: false,
            allowPen: false,
            allowPath: false);

    private static bool HasDefaultCoreData(
        in RenderCommand command,
        bool allowBrush,
        bool allowPen,
        bool allowPath) =>
        (allowBrush || command.Brush is null) &&
        (allowPen || command.Pen is null) &&
        (allowPath || command.Path is null) &&
        command.GeometryCache is null &&
        command.HitTestId == 0 &&
        command.PresentationDependencies == default &&
        !command.IsEdgeAliased &&
        !command.IsPenThicknessLocal &&
        command.PathSampleGrid == 0 &&
        command.PathCoverageGamma == 0f;

    private static bool HasTextData(in RenderCommand command) =>
        command.Text is not null ||
        command.Font is not null ||
        command.FontSize != 0f ||
        command.Position != default ||
        command.IsBold ||
        command.IsItalic ||
        command.TextShapingOptions is not null ||
        command.TextAlignment != default ||
        command.FontTransform != default ||
        command.HasFontTransform ||
        command.Rotation != 0f ||
        command.TextRenderingMode != default ||
        command.TextHintingMode != default ||
        command.UseVectorGlyphRendering ||
        command.PreferGlyphAtlas ||
        command.UseLogicalGlyphAtlasResolution ||
        command.GlyphIndices is not null ||
        command.GlyphPositions is not null ||
        command.GlyphRangeStart != 0 ||
        command.GlyphRangeCount != 0;

    private static bool HasTextureData(in RenderCommand command) =>
        command.Texture is not null ||
        command.SrcRect != default ||
        command.TexturePatches is not null ||
        command.TextureSamplingMode != default ||
        command.TextureMaxAnisotropy != 0 ||
        command.TextureCubicCoefficients != default ||
        command.HasTextureCubicCoefficients ||
        command.SnapTextureToPixels ||
        command.HasImageEffect ||
        command.InlineImageEffectBox is not null ||
        command.ImageEffectBufferIndex != 0 ||
        command.HasBufferedImageEffect;

    private static bool HasOtherData(
        in RenderCommand command,
        bool allowRadii = false,
        bool allowVisual = false,
        bool allowIntParam = false) =>
        command.Position2 != default ||
        command.Position3 != default ||
        command.Position4 != default ||
        (!allowRadii &&
            (command.RadiusX != 0f || command.RadiusY != 0f)) ||
        command.CornerRadius != 0f ||
        command.PolylinePoints is not null ||
        command.IsClosed ||
        command.SplineKnots is not null ||
        command.SplineWeights is not null ||
        command.SplineDegree != 0 ||
        command.Position3D1 != default ||
        command.Position3D2 != default ||
        command.Edges3D is not null ||
        command.StaticBuffer is not null ||
        command.GpuPoints is not null ||
        command.GpuPointsCount != 0 ||
        command.UseGpuTransforms ||
        command.CameraView != default ||
        command.Scale != default ||
        command.Translate != default ||
        command.PointBufferOffset != 0 ||
        command.PointBufferCount != 0 ||
        command.DoubleBufferOffset != 0 ||
        command.DoubleBufferCount != 0 ||
        command.Line3DBufferOffset != 0 ||
        command.Line3DBufferCount != 0 ||
        command.WeightBufferOffset != 0 ||
        command.WeightBufferCount != 0 ||
        command.FloatBufferOffset != 0 ||
        command.FloatBufferCount != 0 ||
        command.SeriesCacheKey is not null ||
        command.Picture is not null ||
        (!allowVisual && command.Visual is not null) ||
        command.VertexMesh is not null ||
        command.VertexColorBlendMode != default ||
        command.ExtensionId != 0 ||
        (!allowIntParam && command.IntParam != 0) ||
        command.FloatParam != 0f ||
        command.DataParam is not null;
}

internal readonly struct RetainedTextRenderCommand
{
    private readonly RetainedRenderCommand _core;
    private readonly RetainedTextCommandData _text;

    public RetainedTextRenderCommand(
        in RenderCommand command,
        int transformIndex)
    {
        _core = new RetainedRenderCommand(in command, transformIndex);
        _text = new RetainedTextCommandData(in command);
    }

    public RenderCommand Expand(Matrix4x4[] transforms)
    {
        RenderCommand command = _core.Expand(transforms);
        _text.Apply(ref command);
        return command;
    }
}

internal readonly struct RetainedTextureRenderCommand
{
    private readonly RetainedRenderCommand _core;
    private readonly RetainedTextureCommandData _texture;

    public RetainedTextureRenderCommand(
        in RenderCommand command,
        int transformIndex)
    {
        _core = new RetainedRenderCommand(in command, transformIndex);
        _texture = new RetainedTextureCommandData(in command);
    }

    public RenderCommand Expand(Matrix4x4[] transforms)
    {
        RenderCommand command = _core.Expand(transforms);
        _texture.Apply(ref command);
        return command;
    }
}

internal readonly struct RetainedSimpleTextureCommand
{
    private readonly GpuTexture? _texture;
    private readonly Rect _destination;
    private readonly Rect _source;
    private readonly int _hitTestId;
    private readonly int _transformIndex;
    private readonly RenderCommandPresentationDependencies _presentationDependencies;
    private readonly byte _samplingMode;
    private readonly byte _maxAnisotropy;
    private readonly bool _snapToPixels;
    private readonly bool _isEdgeAliased;

    public RetainedSimpleTextureCommand(
        in RenderCommand command,
        int transformIndex)
    {
        _texture = command.Texture;
        _destination = command.Rect;
        _source = command.SrcRect;
        _hitTestId = command.HitTestId;
        _transformIndex = transformIndex;
        _presentationDependencies = command.PresentationDependencies;
        _samplingMode = checked((byte)command.TextureSamplingMode);
        _maxAnisotropy = command.TextureMaxAnisotropy;
        _snapToPixels = command.SnapTextureToPixels;
        _isEdgeAliased = command.IsEdgeAliased;
    }

    public RenderCommand Expand(Matrix4x4[] transforms) =>
        new()
        {
            Type = RenderCommandType.DrawTexture,
            HitTestId = _hitTestId,
            Rect = _destination,
            Texture = _texture,
            SrcRect = _source,
            Transform = transforms[_transformIndex],
            PresentationDependencies = _presentationDependencies,
            TextureSamplingMode = (TextureSamplingMode)_samplingMode,
            TextureMaxAnisotropy = _maxAnisotropy,
            SnapTextureToPixels = _snapToPixels,
            IsEdgeAliased = _isEdgeAliased
        };
}

internal readonly struct RetainedSimpleRectangleCommand
{
    private readonly int _hitTestId;
    private readonly Rect _rectangle;
    private readonly Brush? _brush;
    private readonly Pen? _pen;
    private readonly int _transformIndex;
    private readonly RenderCommandPresentationDependencies _presentationDependencies;
    private readonly bool _isEdgeAliased;
    private readonly bool _isPenThicknessLocal;
    private readonly uint _pathSampleGrid;
    private readonly float _pathCoverageGamma;

    public RetainedSimpleRectangleCommand(
        in RenderCommand command,
        int transformIndex)
    {
        _hitTestId = command.HitTestId;
        _rectangle = command.Rect;
        _brush = command.Brush;
        _pen = command.Pen;
        _transformIndex = transformIndex;
        _presentationDependencies = command.PresentationDependencies;
        _isEdgeAliased = command.IsEdgeAliased;
        _isPenThicknessLocal = command.IsPenThicknessLocal;
        _pathSampleGrid = command.PathSampleGrid;
        _pathCoverageGamma = command.PathCoverageGamma;
    }

    public RenderCommand Expand(Matrix4x4[] transforms) =>
        new()
        {
            Type = RenderCommandType.DrawRect,
            HitTestId = _hitTestId,
            Rect = _rectangle,
            Brush = _brush,
            Pen = _pen,
            Transform = transforms[_transformIndex],
            PresentationDependencies = _presentationDependencies,
            IsEdgeAliased = _isEdgeAliased,
            IsPenThicknessLocal = _isPenThicknessLocal,
            PathSampleGrid = _pathSampleGrid,
            PathCoverageGamma = _pathCoverageGamma
        };
}

internal readonly struct RetainedSimplePathCommand
{
    private readonly int _hitTestId;
    private readonly Brush? _brush;
    private readonly Pen? _pen;
    private readonly PathGeometry? _path;
    private readonly RenderCommandGeometryCache? _geometryCache;
    private readonly int _transformIndex;
    private readonly RenderCommandPresentationDependencies _presentationDependencies;
    private readonly bool _isEdgeAliased;
    private readonly bool _isPenThicknessLocal;
    private readonly uint _pathSampleGrid;
    private readonly float _pathCoverageGamma;

    public RetainedSimplePathCommand(
        in RenderCommand command,
        int transformIndex)
    {
        _hitTestId = command.HitTestId;
        _brush = command.Brush;
        _pen = command.Pen;
        _path = command.Path;
        _geometryCache = command.GeometryCache;
        _transformIndex = transformIndex;
        _presentationDependencies = command.PresentationDependencies;
        _isEdgeAliased = command.IsEdgeAliased;
        _isPenThicknessLocal = command.IsPenThicknessLocal;
        _pathSampleGrid = command.PathSampleGrid;
        _pathCoverageGamma = command.PathCoverageGamma;
    }

    public RenderCommand Expand(Matrix4x4[] transforms) =>
        new()
        {
            Type = RenderCommandType.DrawPath,
            HitTestId = _hitTestId,
            Brush = _brush,
            Pen = _pen,
            Path = _path,
            GeometryCache = _geometryCache,
            Transform = transforms[_transformIndex],
            PresentationDependencies = _presentationDependencies,
            IsEdgeAliased = _isEdgeAliased,
            IsPenThicknessLocal = _isPenThicknessLocal,
            PathSampleGrid = _pathSampleGrid,
            PathCoverageGamma = _pathCoverageGamma
        };
}

internal readonly struct RetainedRectangleClipCommand
{
    private readonly Rect _rectangle;
    private readonly int _transformIndex;

    public RetainedRectangleClipCommand(
        in RenderCommand command,
        int transformIndex)
    {
        _rectangle = command.Rect;
        _transformIndex = transformIndex;
    }

    public RenderCommand Expand(Matrix4x4[] transforms) =>
        new()
        {
            Type = RenderCommandType.PushClip,
            Rect = _rectangle,
            Transform = transforms[_transformIndex]
        };
}

internal readonly struct RetainedGeometryClipCommand
{
    private readonly PathGeometry? _path;
    private readonly int _transformIndex;

    public RetainedGeometryClipCommand(
        in RenderCommand command,
        int transformIndex)
    {
        _path = command.Path;
        _transformIndex = transformIndex;
    }

    public RenderCommand Expand(Matrix4x4[] transforms) =>
        new()
        {
            Type = RenderCommandType.PushGeometryClip,
            Path = _path,
            Transform = transforms[_transformIndex]
        };
}

internal readonly struct RetainedSimpleGlyphRunCommand
{
    private const ushort BoldFlag = 1 << 0;
    private const ushort ItalicFlag = 1 << 1;
    private const ushort FontTransformFlag = 1 << 2;
    private const ushort VectorRenderingFlag = 1 << 3;
    private const ushort PreferAtlasFlag = 1 << 4;
    private const ushort LogicalAtlasResolutionFlag = 1 << 5;
    private const ushort EdgeAliasedFlag = 1 << 6;

    private readonly Brush? _brush;
    private readonly TtfFont? _font;
    private readonly ushort[]? _glyphIndices;
    private readonly Vector2[]? _glyphPositions;
    private readonly Vector2 _position;
    private readonly Vector2 _fontTransform;
    private readonly float _fontSize;
    private readonly int _glyphRangeStart;
    private readonly int _glyphRangeCount;
    private readonly int _hitTestId;
    private readonly int _transformIndex;
    private readonly RenderCommandPresentationDependencies _presentationDependencies;
    private readonly ushort _flags;
    private readonly byte _textRenderingMode;
    private readonly byte _textHintingMode;

    public RetainedSimpleGlyphRunCommand(
        in RenderCommand command,
        int transformIndex)
    {
        _brush = command.Brush;
        _font = command.Font;
        _glyphIndices = command.GlyphIndices;
        _glyphPositions = command.GlyphPositions;
        _position = command.Position;
        _fontTransform = command.FontTransform;
        _fontSize = command.FontSize;
        _glyphRangeStart = command.GlyphRangeStart;
        _glyphRangeCount = command.GlyphRangeCount;
        _hitTestId = command.HitTestId;
        _transformIndex = transformIndex;
        _presentationDependencies = command.PresentationDependencies;
        _flags = (ushort)(
            (command.IsBold ? BoldFlag : 0) |
            (command.IsItalic ? ItalicFlag : 0) |
            (command.HasFontTransform ? FontTransformFlag : 0) |
            (command.UseVectorGlyphRendering ? VectorRenderingFlag : 0) |
            (command.PreferGlyphAtlas ? PreferAtlasFlag : 0) |
            (command.UseLogicalGlyphAtlasResolution
                ? LogicalAtlasResolutionFlag
                : 0) |
            (command.IsEdgeAliased ? EdgeAliasedFlag : 0));
        _textRenderingMode = checked((byte)command.TextRenderingMode);
        _textHintingMode = checked((byte)command.TextHintingMode);
    }

    public RenderCommand Expand(Matrix4x4[] transforms) =>
        new()
        {
            Type = RenderCommandType.DrawGlyphRun,
            HitTestId = _hitTestId,
            Brush = _brush,
            Font = _font,
            FontSize = _fontSize,
            Position = _position,
            FontTransform = _fontTransform,
            Transform = transforms[_transformIndex],
            PresentationDependencies = _presentationDependencies,
            IsBold = (_flags & BoldFlag) != 0,
            IsItalic = (_flags & ItalicFlag) != 0,
            HasFontTransform = (_flags & FontTransformFlag) != 0,
            UseVectorGlyphRendering = (_flags & VectorRenderingFlag) != 0,
            PreferGlyphAtlas = (_flags & PreferAtlasFlag) != 0,
            UseLogicalGlyphAtlasResolution =
                (_flags & LogicalAtlasResolutionFlag) != 0,
            IsEdgeAliased = (_flags & EdgeAliasedFlag) != 0,
            TextRenderingMode = (TextRenderingMode)_textRenderingMode,
            TextHintingMode = (TextHintingMode)_textHintingMode,
            GlyphIndices = _glyphIndices,
            GlyphPositions = _glyphPositions,
            GlyphRangeStart = _glyphRangeStart,
            GlyphRangeCount = _glyphRangeCount
        };
}

internal readonly struct RetainedScalarStateCommand
{
    private readonly RenderCommandType _type;
    private readonly int _value;

    public RetainedScalarStateCommand(in RenderCommand command)
    {
        _type = command.Type;
        _value = command.Type == RenderCommandType.PushOpacity
            ? BitConverter.SingleToInt32Bits(command.FontSize)
            : command.IntParam;
    }

    public RenderCommand Expand() =>
        _type == RenderCommandType.PushOpacity
            ? new RenderCommand
            {
                Type = _type,
                FontSize = BitConverter.Int32BitsToSingle(_value)
            }
            : new RenderCommand
            {
                Type = _type,
                IntParam = _value
            };
}

internal readonly struct RetainedSimpleRoundedRectangleCommand
{
    private readonly Rect _rectangle;
    private readonly Brush? _brush;
    private readonly Pen? _pen;
    private readonly float _radiusX;
    private readonly float _radiusY;
    private readonly int _hitTestId;
    private readonly int _transformIndex;
    private readonly RenderCommandPresentationDependencies _presentationDependencies;
    private readonly bool _isEdgeAliased;
    private readonly bool _isPenThicknessLocal;
    private readonly uint _pathSampleGrid;
    private readonly float _pathCoverageGamma;

    public RetainedSimpleRoundedRectangleCommand(
        in RenderCommand command,
        int transformIndex)
    {
        _rectangle = command.Rect;
        _brush = command.Brush;
        _pen = command.Pen;
        _radiusX = command.RadiusX;
        _radiusY = command.RadiusY;
        _hitTestId = command.HitTestId;
        _transformIndex = transformIndex;
        _presentationDependencies = command.PresentationDependencies;
        _isEdgeAliased = command.IsEdgeAliased;
        _isPenThicknessLocal = command.IsPenThicknessLocal;
        _pathSampleGrid = command.PathSampleGrid;
        _pathCoverageGamma = command.PathCoverageGamma;
    }

    public RenderCommand Expand(Matrix4x4[] transforms) =>
        new()
        {
            Type = RenderCommandType.DrawRoundedRect,
            HitTestId = _hitTestId,
            Rect = _rectangle,
            Brush = _brush,
            Pen = _pen,
            RadiusX = _radiusX,
            RadiusY = _radiusY,
            Transform = transforms[_transformIndex],
            PresentationDependencies = _presentationDependencies,
            IsEdgeAliased = _isEdgeAliased,
            IsPenThicknessLocal = _isPenThicknessLocal,
            PathSampleGrid = _pathSampleGrid,
            PathCoverageGamma = _pathCoverageGamma
        };
}

internal readonly struct RetainedSimpleVisualCommand
{
    private readonly Visual? _visual;
    private readonly int _transformIndex;

    public RetainedSimpleVisualCommand(
        in RenderCommand command,
        int transformIndex)
    {
        _visual = command.Visual;
        _transformIndex = transformIndex;
    }

    public RenderCommand Expand(Matrix4x4[] transforms) =>
        new()
        {
            Type = RenderCommandType.DrawVisual,
            Visual = _visual,
            Transform = transforms[_transformIndex]
        };
}

/// <summary>
/// Immutable command snapshot with an ordered 32-bit token stream plus typed
/// rectangle, path, clip, text, texture, transform, and uncommon-command
/// storage. Construction is O(C) average and O(C²) only for
/// adversarial transform-hash collisions, with O(C) bounded scratch and retained
/// storage for C commands. Indexing and replay expansion are allocation-free O(1).
/// </summary>
internal sealed class GpuPictureCommandCollection : IReadOnlyList<RenderCommand>
{
    private const int TokenKindShift = 28;
    private const uint TokenIndexMask = (1u << TokenKindShift) - 1u;

    private readonly uint[] _order;
    private readonly RetainedRenderCommand[] _basic;
    private readonly RenderCommand[] _auxiliary;
    private readonly RetainedTextRenderCommand[] _text;
    private readonly RetainedTextureRenderCommand[] _texture;
    private readonly RetainedSimpleTextureCommand[] _simpleTextures;
    private readonly RetainedSimpleRectangleCommand[] _simpleRectangles;
    private readonly RetainedSimplePathCommand[] _simplePaths;
    private readonly RetainedRectangleClipCommand[] _rectangleClips;
    private readonly RetainedGeometryClipCommand[] _geometryClips;
    private readonly RetainedSimpleGlyphRunCommand[] _simpleGlyphRuns;
    private readonly RetainedScalarStateCommand[] _scalarStates;
    private readonly RetainedSimpleRoundedRectangleCommand[] _simpleRoundedRectangles;
    private readonly RetainedSimpleVisualCommand[] _simpleVisuals;
    private readonly Matrix4x4[] _transforms;
    private readonly Visual[] _embeddedVisuals;

    internal GpuPictureCommandCollection(ReadOnlySpan<RenderCommand> commands)
    {
        if (commands.IsEmpty)
        {
            _order = [];
            _basic = [];
            _auxiliary = [];
            _text = [];
            _texture = [];
            _simpleTextures = [];
            _simpleRectangles = [];
            _simplePaths = [];
            _rectangleClips = [];
            _geometryClips = [];
            _simpleGlyphRuns = [];
            _scalarStates = [];
            _simpleRoundedRectangles = [];
            _simpleVisuals = [];
            _transforms = [];
            _embeddedVisuals = [];
            return;
        }

        int basicCount = 0;
        int auxiliaryCount = 0;
        int textCount = 0;
        int textureCount = 0;
        int simpleTextureCount = 0;
        int simpleRectangleCount = 0;
        int simplePathCount = 0;
        int rectangleClipCount = 0;
        int geometryClipCount = 0;
        int simpleGlyphRunCount = 0;
        int scalarStateCount = 0;
        int simpleRoundedRectangleCount = 0;
        int simpleVisualCount = 0;
        int embeddedVisualCount = 0;
        byte[] classifications =
            ArrayPool<byte>.Shared.Rent(commands.Length);
        int[] transformIndices =
            ArrayPool<int>.Shared.Rent(commands.Length);
        Matrix4x4[] transformScratch =
            ArrayPool<Matrix4x4>.Shared.Rent(commands.Length);
        int transformTableCapacity = GetTransformTableCapacity(commands.Length);
        int[] transformTable =
            ArrayPool<int>.Shared.Rent(transformTableCapacity);
        Array.Clear(transformTable, 0, transformTableCapacity);
        try
        {
            int transformCount = 0;
            for (int index = 0; index < commands.Length; index++)
            {
                RetainedCommandDataKind dataKind =
                    RetainedRenderCommand.Classify(in commands[index]);
                classifications[index] = (byte)dataKind;
                transformIndices[index] = RequiresTransform(dataKind)
                    ? GetOrAddTransform(
                        in commands[index].Transform,
                        transformScratch,
                        transformTable,
                        transformTableCapacity - 1,
                        ref transformCount)
                    : -1;
                switch (dataKind)
                {
                    case RetainedCommandDataKind.Basic:
                        basicCount++;
                        break;
                    case RetainedCommandDataKind.Auxiliary:
                        auxiliaryCount++;
                        break;
                    case RetainedCommandDataKind.Text:
                        textCount++;
                        break;
                    case RetainedCommandDataKind.Texture:
                        textureCount++;
                        break;
                    case RetainedCommandDataKind.SimpleTexture:
                        simpleTextureCount++;
                        break;
                    case RetainedCommandDataKind.SimpleRectangle:
                        simpleRectangleCount++;
                        break;
                    case RetainedCommandDataKind.SimplePath:
                        simplePathCount++;
                        break;
                    case RetainedCommandDataKind.RectangleClip:
                        rectangleClipCount++;
                        break;
                    case RetainedCommandDataKind.GeometryClip:
                        geometryClipCount++;
                        break;
                    case RetainedCommandDataKind.SimpleGlyphRun:
                        simpleGlyphRunCount++;
                        break;
                    case RetainedCommandDataKind.ScalarState:
                        scalarStateCount++;
                        break;
                    case RetainedCommandDataKind.SimpleRoundedRectangle:
                        simpleRoundedRectangleCount++;
                        break;
                    case RetainedCommandDataKind.SimpleVisual:
                        simpleVisualCount++;
                        break;
                }
                if (commands[index].Type == RenderCommandType.DrawVisual &&
                    commands[index].Visual != null)
                {
                    embeddedVisualCount++;
                }
            }

            _order = new uint[commands.Length];
            _basic = basicCount == 0
                ? []
                : new RetainedRenderCommand[basicCount];
            _auxiliary = auxiliaryCount == 0
                ? []
                : new RenderCommand[auxiliaryCount];
            _text = textCount == 0
                ? []
                : new RetainedTextRenderCommand[textCount];
            _texture = textureCount == 0
                ? []
                : new RetainedTextureRenderCommand[textureCount];
            _simpleTextures = simpleTextureCount == 0
                ? []
                : new RetainedSimpleTextureCommand[simpleTextureCount];
            _simpleRectangles = simpleRectangleCount == 0
                ? []
                : new RetainedSimpleRectangleCommand[simpleRectangleCount];
            _simplePaths = simplePathCount == 0
                ? []
                : new RetainedSimplePathCommand[simplePathCount];
            _rectangleClips = rectangleClipCount == 0
                ? []
                : new RetainedRectangleClipCommand[rectangleClipCount];
            _geometryClips = geometryClipCount == 0
                ? []
                : new RetainedGeometryClipCommand[geometryClipCount];
            _simpleGlyphRuns = simpleGlyphRunCount == 0
                ? []
                : new RetainedSimpleGlyphRunCommand[simpleGlyphRunCount];
            _scalarStates = scalarStateCount == 0
                ? []
                : new RetainedScalarStateCommand[scalarStateCount];
            _simpleRoundedRectangles = simpleRoundedRectangleCount == 0
                ? []
                : new RetainedSimpleRoundedRectangleCommand[simpleRoundedRectangleCount];
            _simpleVisuals = simpleVisualCount == 0
                ? []
                : new RetainedSimpleVisualCommand[simpleVisualCount];
            _transforms = new Matrix4x4[transformCount];
            _embeddedVisuals = embeddedVisualCount == 0
                ? []
                : new Visual[embeddedVisualCount];
            transformScratch.AsSpan(0, transformCount).CopyTo(_transforms);
            int basicIndex = 0;
            int auxiliaryIndex = 0;
            int textIndex = 0;
            int textureIndex = 0;
            int simpleTextureIndex = 0;
            int simpleRectangleIndex = 0;
            int simplePathIndex = 0;
            int rectangleClipIndex = 0;
            int geometryClipIndex = 0;
            int simpleGlyphRunIndex = 0;
            int scalarStateIndex = 0;
            int simpleRoundedRectangleIndex = 0;
            int simpleVisualIndex = 0;
            int embeddedVisualIndex = 0;
            for (int index = 0; index < commands.Length; index++)
            {
                ref readonly RenderCommand command = ref commands[index];
                if (command.Type == RenderCommandType.DrawVisual &&
                    command.Visual != null)
                {
                    _embeddedVisuals[embeddedVisualIndex++] = command.Visual;
                }
                RetainedCommandDataKind dataKind =
                    (RetainedCommandDataKind)classifications[index];
                int dataIndex = -1;
                switch (dataKind)
                {
                    case RetainedCommandDataKind.Basic:
                        dataIndex = basicIndex;
                        _basic[basicIndex++] = new RetainedRenderCommand(
                            in command,
                            transformIndices[index]);
                        break;
                    case RetainedCommandDataKind.Auxiliary:
                        dataIndex = auxiliaryIndex;
                        _auxiliary[auxiliaryIndex++] = command;
                        break;
                    case RetainedCommandDataKind.Text:
                        dataIndex = textIndex;
                        _text[textIndex++] =
                            new RetainedTextRenderCommand(
                                in command,
                                transformIndices[index]);
                        break;
                    case RetainedCommandDataKind.Texture:
                        dataIndex = textureIndex;
                        _texture[textureIndex++] =
                            new RetainedTextureRenderCommand(
                                in command,
                                transformIndices[index]);
                        break;
                    case RetainedCommandDataKind.SimpleTexture:
                        dataIndex = simpleTextureIndex;
                        _simpleTextures[simpleTextureIndex++] =
                            new RetainedSimpleTextureCommand(
                                in command,
                                transformIndices[index]);
                        break;
                    case RetainedCommandDataKind.SimpleRectangle:
                        dataIndex = simpleRectangleIndex;
                        _simpleRectangles[simpleRectangleIndex++] =
                            new RetainedSimpleRectangleCommand(
                                in command,
                                transformIndices[index]);
                        break;
                    case RetainedCommandDataKind.SimplePath:
                        dataIndex = simplePathIndex;
                        _simplePaths[simplePathIndex++] =
                            new RetainedSimplePathCommand(
                                in command,
                                transformIndices[index]);
                        break;
                    case RetainedCommandDataKind.RectangleClip:
                        dataIndex = rectangleClipIndex;
                        _rectangleClips[rectangleClipIndex++] =
                            new RetainedRectangleClipCommand(
                                in command,
                                transformIndices[index]);
                        break;
                    case RetainedCommandDataKind.GeometryClip:
                        dataIndex = geometryClipIndex;
                        _geometryClips[geometryClipIndex++] =
                            new RetainedGeometryClipCommand(
                                in command,
                                transformIndices[index]);
                        break;
                    case RetainedCommandDataKind.SimpleGlyphRun:
                        dataIndex = simpleGlyphRunIndex;
                        _simpleGlyphRuns[simpleGlyphRunIndex++] =
                            new RetainedSimpleGlyphRunCommand(
                                in command,
                                transformIndices[index]);
                        break;
                    case RetainedCommandDataKind.ScalarState:
                        dataIndex = scalarStateIndex;
                        _scalarStates[scalarStateIndex++] =
                            new RetainedScalarStateCommand(in command);
                        break;
                    case RetainedCommandDataKind.SimpleRoundedRectangle:
                        dataIndex = simpleRoundedRectangleIndex;
                        _simpleRoundedRectangles[simpleRoundedRectangleIndex++] =
                            new RetainedSimpleRoundedRectangleCommand(
                                in command,
                                transformIndices[index]);
                        break;
                    case RetainedCommandDataKind.SimpleVisual:
                        dataIndex = simpleVisualIndex;
                        _simpleVisuals[simpleVisualIndex++] =
                            new RetainedSimpleVisualCommand(
                                in command,
                                transformIndices[index]);
                        break;
                    case RetainedCommandDataKind.NoData:
                        dataIndex = (int)command.Type;
                        break;
                }

                _order[index] = PackToken(dataKind, dataIndex);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(classifications);
            ArrayPool<int>.Shared.Return(transformIndices);
            ArrayPool<Matrix4x4>.Shared.Return(transformScratch);
            ArrayPool<int>.Shared.Return(transformTable);
        }
    }

    public int Count => _order.Length;

    public int Length => _order.Length;

    internal ReadOnlySpan<Visual> EmbeddedVisuals => _embeddedVisuals;

    public RenderCommand this[int index]
    {
        get
        {
            ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(
                (uint)index,
                (uint)_order.Length,
                nameof(index));
            uint token = _order[index];
            var dataKind = (RetainedCommandDataKind)(token >> TokenKindShift);
            int dataIndex = (int)(token & TokenIndexMask);
            return dataKind switch
            {
                RetainedCommandDataKind.Basic =>
                    _basic[dataIndex].Expand(_transforms),
                RetainedCommandDataKind.Auxiliary => _auxiliary[dataIndex],
                RetainedCommandDataKind.Text =>
                    _text[dataIndex].Expand(_transforms),
                RetainedCommandDataKind.Texture =>
                    _texture[dataIndex].Expand(_transforms),
                RetainedCommandDataKind.SimpleTexture =>
                    _simpleTextures[dataIndex].Expand(_transforms),
                RetainedCommandDataKind.SimpleRectangle =>
                    _simpleRectangles[dataIndex].Expand(_transforms),
                RetainedCommandDataKind.SimplePath =>
                    _simplePaths[dataIndex].Expand(_transforms),
                RetainedCommandDataKind.RectangleClip =>
                    _rectangleClips[dataIndex].Expand(_transforms),
                RetainedCommandDataKind.GeometryClip =>
                    _geometryClips[dataIndex].Expand(_transforms),
                RetainedCommandDataKind.SimpleGlyphRun =>
                    _simpleGlyphRuns[dataIndex].Expand(_transforms),
                RetainedCommandDataKind.ScalarState =>
                    _scalarStates[dataIndex].Expand(),
                RetainedCommandDataKind.SimpleRoundedRectangle =>
                    _simpleRoundedRectangles[dataIndex].Expand(_transforms),
                RetainedCommandDataKind.SimpleVisual =>
                    _simpleVisuals[dataIndex].Expand(_transforms),
                RetainedCommandDataKind.NoData => new RenderCommand
                {
                    Type = (RenderCommandType)dataIndex
                },
                _ => throw new InvalidOperationException(
                    $"Unknown retained command data kind: {dataKind}.")
            };
        }
    }

    internal long ApproximateStorageBytes =>
        (long)_order.Length * sizeof(uint) +
        (long)_basic.Length *
            System.Runtime.CompilerServices.Unsafe.SizeOf<RetainedRenderCommand>() +
        (long)_auxiliary.Length *
            System.Runtime.CompilerServices.Unsafe.SizeOf<RenderCommand>() +
        (long)_text.Length *
            System.Runtime.CompilerServices.Unsafe.SizeOf<RetainedTextRenderCommand>() +
        (long)_texture.Length *
            System.Runtime.CompilerServices.Unsafe.SizeOf<RetainedTextureRenderCommand>() +
        (long)_simpleTextures.Length *
            System.Runtime.CompilerServices.Unsafe.SizeOf<RetainedSimpleTextureCommand>() +
        (long)_simpleRectangles.Length *
            System.Runtime.CompilerServices.Unsafe.SizeOf<RetainedSimpleRectangleCommand>() +
        (long)_simplePaths.Length *
            System.Runtime.CompilerServices.Unsafe.SizeOf<RetainedSimplePathCommand>() +
        (long)_rectangleClips.Length *
            System.Runtime.CompilerServices.Unsafe.SizeOf<RetainedRectangleClipCommand>() +
        (long)_geometryClips.Length *
            System.Runtime.CompilerServices.Unsafe.SizeOf<RetainedGeometryClipCommand>() +
        (long)_simpleGlyphRuns.Length *
            System.Runtime.CompilerServices.Unsafe.SizeOf<RetainedSimpleGlyphRunCommand>() +
        (long)_scalarStates.Length *
            System.Runtime.CompilerServices.Unsafe.SizeOf<RetainedScalarStateCommand>() +
        (long)_simpleRoundedRectangles.Length *
            System.Runtime.CompilerServices.Unsafe.SizeOf<RetainedSimpleRoundedRectangleCommand>() +
        (long)_simpleVisuals.Length *
            System.Runtime.CompilerServices.Unsafe.SizeOf<RetainedSimpleVisualCommand>() +
        (long)_transforms.Length *
            System.Runtime.CompilerServices.Unsafe.SizeOf<Matrix4x4>() +
        (long)_embeddedVisuals.Length * IntPtr.Size;

    internal RenderCommand[] Clone()
    {
        if (_order.Length == 0)
        {
            return [];
        }

        var result = new RenderCommand[_order.Length];
        for (int index = 0; index < result.Length; index++)
        {
            result[index] = this[index];
        }

        return result;
    }

    public Enumerator GetEnumerator() => new(this);

    IEnumerator<RenderCommand> IEnumerable<RenderCommand>.GetEnumerator() =>
        GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    private static uint PackToken(
        RetainedCommandDataKind dataKind,
        int dataIndex)
    {
        if ((uint)dataKind >= (1u << (32 - TokenKindShift)))
        {
            throw new InvalidOperationException(
                $"Retained command data kind {dataKind} exceeds the token format.");
        }
        if ((uint)dataIndex > TokenIndexMask)
        {
            throw new InvalidOperationException(
                "Retained command data exceeds the 28-bit token index capacity.");
        }

        return ((uint)dataKind << TokenKindShift) | (uint)dataIndex;
    }

    private static bool RequiresTransform(
        RetainedCommandDataKind dataKind) =>
        dataKind is not RetainedCommandDataKind.Auxiliary and
            not RetainedCommandDataKind.NoData and
            not RetainedCommandDataKind.ScalarState;

    private static int GetTransformTableCapacity(int commandCount)
    {
        int required = checked(commandCount * 2);
        int capacity = 4;
        while (capacity < required)
        {
            capacity = checked(capacity * 2);
        }

        return capacity;
    }

    private static int GetOrAddTransform(
        in Matrix4x4 transform,
        Matrix4x4[] transforms,
        int[] table,
        int tableMask,
        ref int count)
    {
        if (count != 0 && transforms[count - 1].Equals(transform))
        {
            return count - 1;
        }

        int slot = (int)((uint)transform.GetHashCode() * 2654435761u) &
            tableMask;
        while (true)
        {
            int index = table[slot] - 1;
            if (index < 0)
            {
                index = count++;
                transforms[index] = transform;
                table[slot] = index + 1;
                return index;
            }

            if (transforms[index].Equals(transform))
            {
                return index;
            }

            slot = (slot + 1) & tableMask;
        }
    }

    public struct Enumerator : IEnumerator<RenderCommand>
    {
        private readonly GpuPictureCommandCollection _commands;
        private int _index;

        internal Enumerator(GpuPictureCommandCollection commands)
        {
            _commands = commands;
            _index = -1;
        }

        public RenderCommand Current => _commands[_index];

        object IEnumerator.Current => Current;

        public bool MoveNext()
        {
            int next = _index + 1;
            if (next >= _commands.Count)
            {
                return false;
            }

            _index = next;
            return true;
        }

        public void Reset() => _index = -1;

        public void Dispose()
        {
        }
    }
}

internal sealed class ImageEffectCommandDataBox
{
    public ImageEffectCommandDataBox(in ImageEffectCommandData value)
    {
        Value = value;
    }

    public ImageEffectCommandData Value { get; }
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

public class GpuPicture :
    IRenderDataProvider,
    IImageEffectDataProvider,
    IDisposable
{
    private readonly GpuPictureCommandCollection _retainedCommands;
    private RenderCommand[]? _materializedCommands;
    public RenderCommand[] Commands =>
        _materializedCommands ??= _retainedCommands.Clone();
    public Vector2[] PointBuffer { get; }
    public double[] DoubleBuffer { get; }
    public Line3D[] Line3DBuffer { get; }
    public float[] FloatBuffer { get; }
    private readonly ImageEffectCommandData[] _imageEffectBuffer;
    private readonly RetainedResourceLease[] _retainedResources;
    private bool _disposed;

    public int RetainedResourceCount => _retainedResources.Length;
    internal int ImageEffectCount => _imageEffectBuffer.Length;
    internal GpuPictureCommandCollection RetainedCommands =>
        _retainedCommands;
    internal long CommandStorageBytes =>
        _retainedCommands.ApproximateStorageBytes;

    public GpuPicture(
        RenderCommand[] commands,
        Vector2[] pointBuffer,
        double[] doubleBuffer,
        Line3D[] line3dBuffer,
        float[] floatBuffer) : this(
            new GpuPictureCommandCollection(commands),
            pointBuffer,
            doubleBuffer,
            line3dBuffer,
            floatBuffer,
            Array.Empty<ImageEffectCommandData>(),
            Array.Empty<RetainedResourceLease>())
    {
    }

    internal GpuPicture(
        ReadOnlySpan<RenderCommand> commands,
        Vector2[] pointBuffer,
        double[] doubleBuffer,
        Line3D[] line3dBuffer,
        float[] floatBuffer,
        ImageEffectCommandData[] imageEffectBuffer,
        RetainedResourceLease[] retainedResources)
    {
        _retainedCommands = new GpuPictureCommandCollection(commands);
        PointBuffer = pointBuffer;
        DoubleBuffer = doubleBuffer;
        Line3DBuffer = line3dBuffer;
        FloatBuffer = floatBuffer;
        _imageEffectBuffer = imageEffectBuffer;
        _retainedResources = retainedResources;
    }

    private GpuPicture(
        GpuPictureCommandCollection commands,
        Vector2[] pointBuffer,
        double[] doubleBuffer,
        Line3D[] line3dBuffer,
        float[] floatBuffer,
        ImageEffectCommandData[] imageEffectBuffer,
        RetainedResourceLease[] retainedResources)
    {
        _retainedCommands = commands;
        PointBuffer = pointBuffer;
        DoubleBuffer = doubleBuffer;
        Line3DBuffer = line3dBuffer;
        FloatBuffer = floatBuffer;
        _imageEffectBuffer = imageEffectBuffer;
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

    ImageEffectCommandData IImageEffectDataProvider.GetImageEffect(int index) =>
        _imageEffectBuffer[index];

    public GpuPicture Clone()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(GpuPicture));
        }

        return new GpuPicture(
            _retainedCommands,
            PointBuffer,
            DoubleBuffer,
            Line3DBuffer,
            FloatBuffer,
            _imageEffectBuffer,
            CloneRetainedResources());
    }

    internal GpuPicture CloneWithCommands(RenderCommand[] commands)
    {
        ArgumentNullException.ThrowIfNull(commands);
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(GpuPicture));
        }

        return new GpuPicture(
            commands,
            PointBuffer,
            DoubleBuffer,
            Line3DBuffer,
            FloatBuffer,
            _imageEffectBuffer,
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

    internal void AppendRetainedResourcesTo(List<RetainedResourceLease> destination)
    {
        for (int index = 0; index < _retainedResources.Length; index++)
        {
            RetainedResourceLease resource = _retainedResources[index];
            object? identity = resource.Identity;
            if (identity is not null &&
                HasRetainedResourceIdentity(destination, identity))
            {
                continue;
            }

            destination.Add(resource.AddRef());
        }
    }

    private static bool HasRetainedResourceIdentity(
        List<RetainedResourceLease> resources,
        object identity)
    {
        for (int index = 0; index < resources.Count; index++)
        {
            if (ReferenceEquals(resources[index].Identity, identity))
            {
                return true;
            }
        }

        return false;
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
            _recordingContext.Commands.AsSpan(),
            CopyList(_recordingContext.PointBuffer),
            CopyList(_recordingContext.DoubleBuffer),
            CopyList(_recordingContext.Line3DBuffer),
            CopyList(_recordingContext.FloatBuffer),
            _recordingContext.CopyImageEffects(),
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

public sealed class RenderCommandList :
    IList<RenderCommand>,
    IReadOnlyList<RenderCommand>
{
    private const int DefaultCapacity = 4;
    private const int RetainedCapacityLimit = 256;
    private RenderCommand[] _items = Array.Empty<RenderCommand>();
    private int _count;
    private bool _pooled;
    private readonly DrawingContext? _owner;
    internal Func<int, bool>? CommandInterceptor;
    internal event Action<int>? CommandAdded;

    public RenderCommandList()
    {
    }

    internal RenderCommandList(DrawingContext owner)
    {
        _owner = owner;
    }

    public int Count => _count;

    public bool IsReadOnly => false;

    public int Capacity
    {
        get => _items.Length;
        set
        {
            if (value < _count)
            {
                throw new ArgumentOutOfRangeException(nameof(value));
            }

            if (value == _items.Length)
            {
                return;
            }

            if (value == 0)
            {
                ReplaceStorage(Array.Empty<RenderCommand>(), pooled: false);
                return;
            }

            var replacement = new RenderCommand[value];
            AsSpan().CopyTo(replacement);
            ReplaceStorage(replacement, pooled: false);
        }
    }

    public RenderCommand this[int index]
    {
        get
        {
            ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(
                (uint)index,
                (uint)_count,
                nameof(index));
            return _items[index];
        }
        set
        {
            ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(
                (uint)index,
                (uint)_count,
                nameof(index));
            _items[index] = value;
        }
    }

    public RenderCommand this[Index index]
    {
        get => this[index.GetOffset(_count)];
        set => this[index.GetOffset(_count)] = value;
    }

    public void Add(RenderCommand command)
    {
        EnsureCapacity(checked(_count + 1));
        int index = _count;
        _items[index] = command;
        _count = index + 1;
        var interceptor = CommandInterceptor;
        if (interceptor is not null &&
            (command.Brush is IRetainedCommandInterceptBrush ||
             command.Pen?.Brush is IRetainedCommandInterceptBrush) &&
            interceptor(index))
        {
            return;
        }

        CommandAdded?.Invoke(index);
    }

    public void AddRange(IEnumerable<RenderCommand> commands)
    {
        ArgumentNullException.ThrowIfNull(commands);
        if (commands is RenderCommandList commandList)
        {
            AddRange(commandList);
            return;
        }

        if (ReferenceEquals(commands, this))
        {
            commands = ToArray();
        }

        if (commands is ICollection<RenderCommand> collection)
        {
            EnsureCapacity(checked(_count + collection.Count));
        }

        foreach (RenderCommand command in commands)
        {
            EnsureCapacity(checked(_count + 1));
            _items[_count++] = command;
        }
    }

    public void AddRange(RenderCommandList commands)
    {
        ArgumentNullException.ThrowIfNull(commands);
        if (commands._count == 0)
        {
            return;
        }

        if (ReferenceEquals(commands, this))
        {
            RenderCommand[] snapshot = ToArray();
            AddRange(snapshot);
            return;
        }

        EnsureCapacity(checked(_count + commands._count));
        commands.AsSpan().CopyTo(_items.AsSpan(_count));
        _count += commands._count;
    }

    public void Clear()
    {
        if (_count != 0)
        {
            Array.Clear(_items, 0, _count);
            _count = 0;
        }

        if (_pooled && _items.Length > RetainedCapacityLimit)
        {
            ReturnPooledStorage();
        }

        _owner?.ClearCommandSideBuffers();
    }

    public bool Contains(RenderCommand command) => IndexOf(command) >= 0;

    public void CopyTo(RenderCommand[] array, int arrayIndex) =>
        AsSpan().CopyTo(array.AsSpan(arrayIndex));

    public bool Exists(Predicate<RenderCommand> match) =>
        FindIndex(match) >= 0;

    public int FindIndex(Predicate<RenderCommand> match)
    {
        ArgumentNullException.ThrowIfNull(match);
        for (int index = 0; index < _count; index++)
        {
            if (match(_items[index]))
            {
                return index;
            }
        }

        return -1;
    }

    public int IndexOf(RenderCommand command) =>
        Array.IndexOf(_items, command, 0, _count);

    public void Insert(int index, RenderCommand command)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThan(
            (uint)index,
            (uint)_count,
            nameof(index));
        EnsureCapacity(checked(_count + 1));
        if (index < _count)
        {
            Array.Copy(_items, index, _items, index + 1, _count - index);
        }

        _items[index] = command;
        _count++;
    }

    public void InsertRange(int index, IEnumerable<RenderCommand> commands)
    {
        ArgumentNullException.ThrowIfNull(commands);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(
            (uint)index,
            (uint)_count,
            nameof(index));
        if (commands is RenderCommandList commandList &&
            !ReferenceEquals(commandList, this))
        {
            InsertRange(index, commandList);
            return;
        }

        var insertionList = new List<RenderCommand>();
        insertionList.AddRange(commands);
        RenderCommand[] insertion = insertionList.ToArray();
        if (insertion.Length == 0)
        {
            return;
        }

        EnsureCapacity(checked(_count + insertion.Length));
        Array.Copy(
            _items,
            index,
            _items,
            index + insertion.Length,
            _count - index);
        insertion.CopyTo(_items, index);
        _count += insertion.Length;
    }

    public void InsertRange(int index, RenderCommandList commands)
    {
        ArgumentNullException.ThrowIfNull(commands);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(
            (uint)index,
            (uint)_count,
            nameof(index));
        if (commands._count == 0)
        {
            return;
        }

        if (ReferenceEquals(commands, this))
        {
            InsertRange(index, (IEnumerable<RenderCommand>)ToArray());
            return;
        }

        EnsureCapacity(checked(_count + commands._count));
        Array.Copy(
            _items,
            index,
            _items,
            index + commands._count,
            _count - index);
        commands.AsSpan().CopyTo(_items.AsSpan(index));
        _count += commands._count;
    }

    public bool Remove(RenderCommand command)
    {
        int index = IndexOf(command);
        if (index < 0)
        {
            return false;
        }

        RemoveAt(index);
        return true;
    }

    public void RemoveAt(int index)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(
            (uint)index,
            (uint)_count,
            nameof(index));
        int moved = _count - index - 1;
        if (moved != 0)
        {
            Array.Copy(_items, index + 1, _items, index, moved);
        }

        _count--;
        _items[_count] = default;
    }

    public void RemoveRange(int index, int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        if (_count - index < count)
        {
            throw new ArgumentException("The range exceeds the command count.");
        }

        if (count == 0)
        {
            return;
        }

        int moved = _count - index - count;
        if (moved != 0)
        {
            Array.Copy(_items, index + count, _items, index, moved);
        }

        Array.Clear(_items, _count - count, count);
        _count -= count;
    }

    public int EnsureCapacity(int capacity)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(capacity);
        if (_items.Length >= capacity)
        {
            return _items.Length;
        }

        int nextCapacity = _items.Length == 0
            ? DefaultCapacity
            : checked(_items.Length * 2);
        if ((uint)nextCapacity > 0X7FEFFFFF)
        {
            nextCapacity = 0X7FEFFFFF;
        }

        if (nextCapacity < capacity)
        {
            nextCapacity = capacity;
        }

        // ArrayPool<T> rounds a first request up to its minimum bucket. Since
        // RenderCommand is intentionally wide, renting for the overwhelmingly
        // common one-to-four-command recording would retain substantially more
        // storage than the commands themselves. Keep that first exact-sized
        // array owned by the list; growth beyond it switches to pooled scratch
        // storage and preserves the amortized O(1) append contract.
        bool pooled = nextCapacity > DefaultCapacity;
        RenderCommand[] replacement = pooled
            ? ArrayPool<RenderCommand>.Shared.Rent(nextCapacity)
            : new RenderCommand[nextCapacity];
        AsSpan().CopyTo(replacement);
        ReplaceStorage(replacement, pooled);
        return _items.Length;
    }

    internal int EnsureRetainedCapacity(int capacity)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(capacity);
        if (_items.Length >= capacity && !_pooled)
        {
            return _items.Length;
        }

        int retainedCapacity = Math.Max(capacity, _count);
        var replacement = retainedCapacity == 0
            ? Array.Empty<RenderCommand>()
            : new RenderCommand[retainedCapacity];
        AsSpan().CopyTo(replacement);
        ReplaceStorage(replacement, pooled: false);
        return _items.Length;
    }

    public Span<RenderCommand> AsSpan() => _items.AsSpan(0, _count);

    public RenderCommand[] ToArray()
    {
        if (_count == 0)
        {
            return Array.Empty<RenderCommand>();
        }

        var result = new RenderCommand[_count];
        AsSpan().CopyTo(result);
        return result;
    }

    public Enumerator GetEnumerator() => new(_items, _count);

    IEnumerator<RenderCommand> IEnumerable<RenderCommand>.GetEnumerator() =>
        GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    private void ReplaceStorage(RenderCommand[] replacement, bool pooled)
    {
        RenderCommand[] previous = _items;
        bool previousPooled = _pooled;
        _items = replacement;
        _pooled = pooled;
        if (previousPooled)
        {
            ArrayPool<RenderCommand>.Shared.Return(previous, clearArray: true);
        }
    }

    private void ReturnPooledStorage()
    {
        RenderCommand[] previous = _items;
        _items = Array.Empty<RenderCommand>();
        _pooled = false;
        ArrayPool<RenderCommand>.Shared.Return(previous, clearArray: true);
    }

    public struct Enumerator : IEnumerator<RenderCommand>
    {
        private readonly RenderCommand[] _items;
        private readonly int _count;
        private int _index;

        internal Enumerator(RenderCommand[] items, int count)
        {
            _items = items;
            _count = count;
            _index = -1;
        }

        public RenderCommand Current => _items[_index];

        object IEnumerator.Current => Current;

        public bool MoveNext()
        {
            int next = _index + 1;
            if (next >= _count)
            {
                return false;
            }

            _index = next;
            return true;
        }

        public void Reset() => _index = -1;

        public void Dispose()
        {
        }
    }
}

public class DrawingContext :
    IRenderDataProvider,
    IImageEffectDataProvider,
    IProGpuDrawingContextSource
{
    public RenderCommandList Commands { get; }
    private List<RetainedResourceLease>? _retainedResources;
    private List<Vector2>? _pointBuffer;
    private List<double>? _doubleBuffer;
    private List<Line3D>? _line3DBuffer;
    private List<float>? _floatBuffer;
    private List<ImageEffectCommandData>? _imageEffectBuffer;

    // Reusable continuous pools to eliminate heap array allocations
    public List<Vector2> PointBuffer => _pointBuffer ??= new();
    public List<double> DoubleBuffer => _doubleBuffer ??= new();
    public List<Line3D> Line3DBuffer => _line3DBuffer ??= new();
    public List<float> FloatBuffer => _floatBuffer ??= new();

    internal List<ImageEffectCommandData> ImageEffectBuffer =>
        _imageEffectBuffer ??= new();

    public int RetainedResourceCount => _retainedResources?.Count ?? 0;

    internal bool HasCommandSideBuffers =>
        _pointBuffer is { Count: > 0 } ||
        _doubleBuffer is { Count: > 0 } ||
        _line3DBuffer is { Count: > 0 } ||
        _floatBuffer is { Count: > 0 } ||
        _imageEffectBuffer is { Count: > 0 };

    public DrawingContext()
    {
        Commands = new RenderCommandList(this);
    }

    public bool TryGetProGpuDrawingContext(
        out ProGpuDrawingContextState state)
    {
        state = new ProGpuDrawingContextState(
            this,
            Matrix4x4.Identity);
        return true;
    }

    /// <summary>
    /// Reserves storage for a known upper bound of retained commands. Repeated
    /// recording then reuses the same backing array without a late capacity
    /// growth in an animation frame.
    /// </summary>
    public void EnsureCommandCapacity(int capacity)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(capacity);
        Commands.EnsureRetainedCapacity(capacity);
    }

    /// <summary>
    /// Compacts the command backing array after a retained recording has
    /// reached a stable command count. Subsequent recordings with the same
    /// count reuse this exact capacity.
    /// </summary>
    public void TrimRetainedCommandCapacity()
    {
        if (Commands.Capacity != Commands.Count)
        {
            Commands.Capacity = Commands.Count;
        }
    }

    public void RetainResource(IDisposable resource)
    {
        ArgumentNullException.ThrowIfNull(resource);
        (_retainedResources ??= new List<RetainedResourceLease>())
            .Add(RetainedResourceLease.Create(resource));
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
            (_retainedResources ??= new List<RetainedResourceLease>())
                .Add(RetainedResourceLease.Create(
                    textureLease,
                    leasedTexture));
        }

        texture = leasedTexture;
        return true;
    }

    /// <summary>
    /// Retains the current same-device texture without selecting a consumer
    /// context. This is used by retained composition surfaces whose command
    /// recording can occur before the host compositor begins a frame. Device
    /// compatibility remains validated by the compositor before binding.
    /// </summary>
    public bool TryRetainTexture(
        IProGpuTextureLeaseSource source,
        out GpuTexture texture)
    {
        ArgumentNullException.ThrowIfNull(source);

        if (source.TryGetGpuTexture(out GpuTexture currentTexture) &&
            currentTexture is not null &&
            !currentTexture.IsDisposed &&
            HasRetainedResourceIdentity(currentTexture))
        {
            texture = currentTexture;
            return true;
        }

        if (!source.TryAcquireGpuTextureLease(out IProGpuTextureLease lease))
        {
            texture = null!;
            return false;
        }

        GpuTexture leasedTexture = lease.Texture;
        if (leasedTexture is null || leasedTexture.IsDisposed)
        {
            lease.Dispose();
            texture = null!;
            return false;
        }

        if (HasRetainedResourceIdentity(leasedTexture))
        {
            lease.Dispose();
        }
        else
        {
            (_retainedResources ??= new List<RetainedResourceLease>())
                .Add(RetainedResourceLease.Create(lease, leasedTexture));
        }

        texture = leasedTexture;
        return true;
    }

    /// <summary>
    /// Transfers an already acquired texture lease into this retained drawing
    /// context. Duplicate texture identities release the extra lease
    /// immediately.
    /// </summary>
    public bool TryRetainTextureLease(
        IProGpuTextureLease textureLease,
        WgpuContext requiredContext,
        out GpuTexture texture)
    {
        ArgumentNullException.ThrowIfNull(textureLease);
        ArgumentNullException.ThrowIfNull(requiredContext);

        GpuTexture leasedTexture = textureLease.Texture;
        if (leasedTexture is null || leasedTexture.IsDisposed)
        {
            textureLease.Dispose();
            texture = null!;
            return false;
        }

        try
        {
            ValidateTextureContext(
                leasedTexture,
                requiredContext);
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
            RetainedResourceLease retained =
                RetainedResourceLease.Create(
                    textureLease,
                    leasedTexture);
            try
            {
                (_retainedResources ??=
                    new List<RetainedResourceLease>())
                    .Add(retained);
            }
            catch
            {
                retained.Dispose();
                throw;
            }
        }

        texture = leasedTexture;
        return true;
    }

    private bool HasRetainedResourceIdentity(object identity)
    {
        if (_retainedResources == null)
            return false;

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
        if (!texture.Context.SharesDeviceWith(requiredContext))
        {
            throw new InvalidOperationException(
                "Cannot retain a texture from a different WebGPU context/device domain for deferred command replay.");
        }
    }

    internal RetainedResourceLease[] CloneRetainedResources()
    {
        if (_retainedResources == null)
            return Array.Empty<RetainedResourceLease>();

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

    ImageEffectCommandData IImageEffectDataProvider.GetImageEffect(int index) =>
        _imageEffectBuffer is { } values
            ? values[index]
            : throw new ArgumentOutOfRangeException(nameof(index));

    internal ImageEffectCommandData GetImageEffect(
        in RenderCommand command) =>
        command.ResolveImageEffect(this);

    internal void AddImageEffectCommand(
        RenderCommand command,
        in ImageEffectCommandData effect)
    {
        command.HasImageEffect = true;
        command.HasBufferedImageEffect = true;
        command.ImageEffectBufferIndex = ImageEffectBuffer.Count;
        ImageEffectBuffer.Add(effect);
        Commands.Add(command);
    }

    internal ImageEffectCommandData[] CopyImageEffects()
    {
        if (_imageEffectBuffer == null || _imageEffectBuffer.Count == 0)
        {
            return Array.Empty<ImageEffectCommandData>();
        }

        var result = new ImageEffectCommandData[_imageEffectBuffer.Count];
        CollectionsMarshal.AsSpan(_imageEffectBuffer).CopyTo(result);
        return result;
    }

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

    /// <summary>
    /// Records a transformed retained path using a cache previously created for the same
    /// geometry. The transform remains command-local and does not affect cache identity.
    /// </summary>
    public void DrawPath(
        Brush? brush,
        Pen? pen,
        PathGeometry path,
        Matrix4x4 transform,
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
            Transform = transform,
            GeometryCache = geometryCache
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
        bool useVectorGlyphRendering = false,
        TextShapingOptions? textShapingOptions = null,
        TextAlignment textAlignment = TextAlignment.Left)
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
            UseVectorGlyphRendering = useVectorGlyphRendering,
            PreferGlyphAtlas = true,
            TextShapingOptions = textShapingOptions,
            TextAlignment = textAlignment
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
        bool useVectorGlyphRendering = false,
        TextShapingOptions? textShapingOptions = null,
        TextAlignment textAlignment = TextAlignment.Left)
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
            UseVectorGlyphRendering = useVectorGlyphRendering,
            PreferGlyphAtlas = true,
            TextShapingOptions = textShapingOptions,
            TextAlignment = textAlignment
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
        bool useVectorGlyphRendering = false,
        TextShapingOptions? textShapingOptions = null,
        TextAlignment textAlignment = TextAlignment.Left)
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
            UseVectorGlyphRendering = useVectorGlyphRendering,
            PreferGlyphAtlas = true,
            TextShapingOptions = textShapingOptions,
            TextAlignment = textAlignment
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
        AddGlyphRun(
            glyphIndices,
            glyphPositions,
            0,
            glyphIndices.Length,
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

    /// <summary>
    /// Records a range of an existing shaped glyph run without allocating
    /// sliced glyph or position arrays.
    /// </summary>
    public void DrawGlyphRunRange(
        ushort[] glyphIndices,
        Vector2[] glyphPositions,
        int glyphRangeStart,
        int glyphRangeCount,
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
        ArgumentNullException.ThrowIfNull(glyphIndices);
        ArgumentNullException.ThrowIfNull(glyphPositions);
        ArgumentOutOfRangeException.ThrowIfNegative(glyphRangeStart);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(glyphRangeCount);
        if (glyphRangeStart > glyphIndices.Length - glyphRangeCount ||
            glyphRangeStart > glyphPositions.Length - glyphRangeCount)
        {
            throw new ArgumentOutOfRangeException(nameof(glyphRangeCount));
        }

        AddGlyphRun(
            glyphIndices,
            glyphPositions,
            glyphRangeStart,
            glyphRangeCount,
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
        AddGlyphRun(
            glyphIndices,
            glyphPositions,
            0,
            glyphIndices.Length,
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
            fontScaleX,
            fontSkewX);
    }

    private void AddGlyphRun(
        ushort[] glyphIndices,
        Vector2[] glyphPositions,
        int glyphRangeStart,
        int glyphRangeCount,
        TtfFont font,
        float fontSize,
        Brush brush,
        Vector2 position,
        Matrix4x4 transform,
        bool isBold,
        bool isItalic,
        TextRenderingMode textRenderingMode,
        TextHintingMode textHintingMode,
        bool useVectorGlyphRendering,
        bool preferGlyphAtlas,
        bool useLogicalGlyphAtlasResolution,
        float fontScaleX,
        float fontSkewX)
    {
        Commands.Add(new RenderCommand
        {
            Type = RenderCommandType.DrawGlyphRun,
            GlyphIndices = glyphIndices,
            GlyphPositions = glyphPositions,
            GlyphRangeStart = glyphRangeStart,
            GlyphRangeCount = glyphRangeCount,
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

    public void PushOpacityMask(
        PathGeometry geometry,
        Pen pen,
        Rect bounds,
        Matrix4x4 transform)
    {
        ArgumentNullException.ThrowIfNull(geometry);
        ArgumentNullException.ThrowIfNull(pen);
        Commands.Add(new RenderCommand
        {
            Type = RenderCommandType.PushOpacityMask,
            Path = geometry,
            Pen = pen,
            Rect = bounds,
            Transform = transform
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
            GeometryCache = pen.HasDashPattern
                ? RenderCommandGeometryCache.ForStrokePath(
                    RenderCommandGeometryCache.CreateLinePath(p1, p2))
                : null
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

    /// <summary>
    /// Records a periodic antialiased dot grid as one analytic quad.
    /// Grid centers are snapped to quarter physical pixels by the vector shader.
    /// </summary>
    public void DrawDotGrid(Brush brush, Rect bounds, float spacing, float radius, Vector2 phase)
    {
        ArgumentNullException.ThrowIfNull(brush);
        if (!float.IsFinite(spacing) || spacing <= 0f)
            throw new ArgumentOutOfRangeException(nameof(spacing));
        if (!float.IsFinite(radius) || radius <= 0f)
            throw new ArgumentOutOfRangeException(nameof(radius));

        Commands.Add(new RenderCommand
        {
            Type = RenderCommandType.DrawDotGrid,
            Brush = brush,
            Rect = bounds,
            Position2 = phase,
            RadiusX = spacing,
            RadiusY = radius
        });
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
            Position3 = p2
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
            Position4 = p3
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
            Position3 = p3
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
            Position4 = p4
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
            IsClosed = isClosed
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
            IsClosed = isClosed
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

    /// <summary>
    /// Inserts a retained visual subtree at this point in the command stream.
    /// The subtree remains owned by the caller and is compiled with the current
    /// command transform. Changes must flow through <see cref="Visual"/>
    /// invalidation so compiled-scene dependency validation can observe them.
    /// </summary>
    public void DrawVisual(Visual visual, Matrix4x4 transform = default)
    {
        ArgumentNullException.ThrowIfNull(visual);
        Commands.Add(new RenderCommand
        {
            Type = RenderCommandType.DrawVisual,
            Visual = visual,
            Transform = transform
        });
    }

    private void RetainPictureResources(GpuPicture picture)
    {
        ArgumentNullException.ThrowIfNull(picture);
        if (picture.RetainedResourceCount == 0)
            return;

        picture.AppendRetainedResourcesTo(
            _retainedResources ??= new List<RetainedResourceLease>());
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
        int imageEffectOffset = _imageEffectBuffer?.Count ?? 0;

        AppendList(PointBuffer, other.PointBuffer);
        AppendList(DoubleBuffer, other.DoubleBuffer);
        AppendList(Line3DBuffer, other.Line3DBuffer);
        AppendList(FloatBuffer, other.FloatBuffer);
        if (other._imageEffectBuffer is { Count: > 0 } otherImageEffects)
        {
            AppendList(ImageEffectBuffer, otherImageEffects);
        }

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
            if (adjustedCmd.HasBufferedImageEffect)
                adjustedCmd.ImageEffectBufferIndex += imageEffectOffset;

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
                        (adjustedCmd.HasImageEffect ||
                         IsRectBackedExtensionDataParam(adjustedCmd.DataParam)))
                    {
                        if (adjustedCmd.HasImageEffect)
                        {
                            adjustedCmd.Rect = TranslateRect(
                                adjustedCmd.Rect,
                                translation);
                        }
                        else
                        {
                            adjustedCmd.DataParam =
                                TranslateExtensionDataParam(
                                    adjustedCmd.DataParam,
                                    translation);
                        }
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

    internal void AppendCommand(DrawingContext other, int commandIndex)
    {
        AppendCommand(other, commandIndex, other.Commands[commandIndex]);
    }

    internal void AppendCommand(
        DrawingContext other,
        int commandIndex,
        RenderCommand command)
    {
        ArgumentNullException.ThrowIfNull(other);
        ArgumentOutOfRangeException.ThrowIfNegative(commandIndex);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(
            commandIndex,
            other.Commands.Count);

        if (command.PointBufferCount > 0)
        {
            command.PointBufferOffset = CopySlice(
                PointBuffer,
                other.PointBuffer,
                command.PointBufferOffset,
                command.PointBufferCount);
        }

        if (command.DoubleBufferCount > 0)
        {
            command.DoubleBufferOffset = CopySlice(
                DoubleBuffer,
                other.DoubleBuffer,
                command.DoubleBufferOffset,
                command.DoubleBufferCount);
        }

        if (command.Line3DBufferCount > 0)
        {
            command.Line3DBufferOffset = CopySlice(
                Line3DBuffer,
                other.Line3DBuffer,
                command.Line3DBufferOffset,
                command.Line3DBufferCount);
        }

        if (command.FloatBufferCount > 0)
        {
            command.FloatBufferOffset = CopySlice(
                FloatBuffer,
                other.FloatBuffer,
                command.FloatBufferOffset,
                command.FloatBufferCount);
        }

        if (command.WeightBufferCount > 0)
        {
            command.WeightBufferOffset = CopySlice(
                DoubleBuffer,
                other.DoubleBuffer,
                command.WeightBufferOffset,
                command.WeightBufferCount);
        }

        if (command.HasBufferedImageEffect)
        {
            ImageEffectCommandData effect =
                ((IImageEffectDataProvider)other).GetImageEffect(
                    command.ImageEffectBufferIndex);
            command.ImageEffectBufferIndex = ImageEffectBuffer.Count;
            ImageEffectBuffer.Add(effect);
        }

        AppendRetainedResources(other.CloneRetainedResources());
        Commands.Add(command);
    }

    internal void SubscribeCommandAdded(Action<int> handler) =>
        Commands.CommandAdded += handler;

    internal void UnsubscribeCommandAdded(Action<int> handler) =>
        Commands.CommandAdded -= handler;

    internal void SetCommandInterceptor(Func<int, bool> interceptor)
    {
        ArgumentNullException.ThrowIfNull(interceptor);
        if (Commands.CommandInterceptor != null &&
            !ReferenceEquals(Commands.CommandInterceptor, interceptor))
        {
            throw new InvalidOperationException(
                "A drawing context can have only one command interceptor.");
        }

        Commands.CommandInterceptor = interceptor;
    }

    internal void ClearCommandInterceptor(Func<int, bool> interceptor)
    {
        if (Commands.CommandInterceptor == interceptor)
        {
            Commands.CommandInterceptor = null;
        }
    }

    private static int CopySlice<T>(
        List<T> destination,
        List<T> source,
        int offset,
        int count)
    {
        var destinationOffset = destination.Count;
        for (var index = 0; index < count; index++)
        {
            destination.Add(source[offset + index]);
        }

        return destinationOffset;
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

        var retainedResources =
            _retainedResources ??= new List<RetainedResourceLease>();
        retainedResources.EnsureCapacity(
            checked(retainedResources.Count + resources.Length));
        for (int resourceIndex = 0; resourceIndex < resources.Length; resourceIndex++)
        {
            var resource = resources[resourceIndex];
            var identity = resource.Identity;
            if (identity is not null && HasRetainedResourceIdentity(identity))
            {
                resource.Dispose();
                continue;
            }

            retainedResources.Add(resource);
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
        _pointBuffer?.Clear();
        _doubleBuffer?.Clear();
        _line3DBuffer?.Clear();
        _floatBuffer?.Clear();
        DisposeRetainedResources();
    }

    internal void ClearCommandSideBuffers()
    {
        _imageEffectBuffer?.Clear();
    }

    private void DisposeRetainedResources()
    {
        if (_retainedResources == null)
            return;

        for (int i = 0; i < _retainedResources.Count; i++)
        {
            _retainedResources[i].Dispose();
        }

        _retainedResources.Clear();
    }

    /// <summary>
    /// Transfers deferred-command resource ownership to the active compositor
    /// frame. This is O(R) for R retained resources and does not allocate or
    /// increment reference counts. Duplicate identities are released.
    /// </summary>
    internal void MoveRetainedResourcesTo(
        List<RetainedResourceLease> destination)
    {
        ArgumentNullException.ThrowIfNull(destination);
        if (_retainedResources == null ||
            _retainedResources.Count == 0)
        {
            return;
        }

        destination.EnsureCapacity(
            checked(
                destination.Count +
                _retainedResources.Count));
        for (int index = 0;
             index < _retainedResources.Count;
             index++)
        {
            RetainedResourceLease resource =
                _retainedResources[index];
            object? identity = resource.Identity;
            if (identity is not null &&
                ContainsRetainedResourceIdentity(
                    destination,
                    identity))
            {
                resource.Dispose();
            }
            else
            {
                destination.Add(resource);
            }
        }
        _retainedResources.Clear();
    }

    internal void MoveRetainedResourcesTo(DrawingContext destination)
    {
        ArgumentNullException.ThrowIfNull(destination);
        if (_retainedResources == null || _retainedResources.Count == 0)
        {
            return;
        }

        MoveRetainedResourcesTo(
            destination._retainedResources ??=
                new List<RetainedResourceLease>(_retainedResources.Count));
    }

    private static bool ContainsRetainedResourceIdentity(
        List<RetainedResourceLease> resources,
        object identity)
    {
        for (int index = 0;
             index < resources.Count;
             index++)
        {
            if (ReferenceEquals(
                    resources[index].Identity,
                    identity))
            {
                return true;
            }
        }
        return false;
    }
}
