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
    DrawGlyphRun
}

public enum TextureSamplingMode
{
    Linear,
    Nearest,
    Cubic
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

public struct RenderCommand
{
    public RenderCommandType Type;
    public Rect Rect;
    public Brush? Brush;
    public Pen? Pen;
    public PathGeometry? Path;
    
    // Typography properties
    public string? Text;
    public TtfFont? Font;
    public float FontSize;
    public Vector2 Position;
    public bool IsBold;
    public bool IsItalic;
    public float Rotation;
    public TextRenderingMode TextRenderingMode;
    public TextHintingMode TextHintingMode;
    public bool IsTextAliased
    {
        readonly get => TextRenderingMode == TextRenderingMode.Aliased;
        set => TextRenderingMode = value ? TextRenderingMode.Aliased : TextRenderingMode.Grayscale;
    }

    // Texture properties
    public GpuTexture? Texture;
    public Rect SrcRect;
    public TextureSamplingMode TextureSamplingMode;

    // Vector render options
    public bool IsEdgeAliased;

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

    public static RetainedResourceLease Create(IDisposable resource)
    {
        return new RetainedResourceLease(new RetainedResourceOwner(resource));
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

        public RetainedResourceOwner(IDisposable resource)
        {
            _resource = resource;
        }

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
        foreach (var resource in resources)
        {
            resource.Dispose();
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
        return new GpuPicture(
            _recordingContext.Commands.ToArray(),
            _recordingContext.PointBuffer.ToArray(),
            _recordingContext.DoubleBuffer.ToArray(),
            _recordingContext.Line3DBuffer.ToArray(),
            _recordingContext.FloatBuffer.ToArray(),
            _recordingContext.CloneRetainedResources()
        );
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

    public void RetainResource(IDisposable resource)
    {
        ArgumentNullException.ThrowIfNull(resource);
        _retainedResources.Add(RetainedResourceLease.Create(resource));
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
        Commands.Add(new RenderCommand
        {
            Type = RenderCommandType.DrawRect,
            Rect = rect,
            Brush = brush,
            Pen = pen
        });
    }

    public void DrawPath(Brush? brush, Pen? pen, PathGeometry path)
    {
        Commands.Add(new RenderCommand
        {
            Type = RenderCommandType.DrawPath,
            Brush = brush,
            Pen = pen,
            Path = path
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
            Transform = transform
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
        TextHintingMode textHintingMode = TextHintingMode.Auto)
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
            TextHintingMode = textHintingMode
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
        TextHintingMode textHintingMode = TextHintingMode.Auto)
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
            TextHintingMode = textHintingMode
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
        TextHintingMode textHintingMode = TextHintingMode.Auto)
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
            TextRenderingMode = textRenderingMode,
            TextHintingMode = textHintingMode
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

    public void PushClip(Rect clipRect)
    {
        Commands.Add(new RenderCommand
        {
            Type = RenderCommandType.PushClip,
            Rect = clipRect
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
        Commands.Add(new RenderCommand
        {
            Type = RenderCommandType.PushOpacityMask,
            Brush = maskBrush,
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
            Position2 = p2
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
            Transform = Matrix4x4.Identity
        });
    }

    // --- Skia-like Picture drawing commands ---

    public void DrawPicture(GpuPicture picture)
    {
        Commands.Add(new RenderCommand
        {
            Type = RenderCommandType.DrawPicture,
            Picture = picture
        });
    }

    public void DrawPicture(GpuPicture picture, Matrix4x4 cameraView)
    {
        Commands.Add(new RenderCommand
        {
            Type = RenderCommandType.DrawPicture,
            Picture = picture,
            UseGpuTransforms = true,
            CameraView = cameraView
        });
    }

    public void DrawExtension(
        int extensionId,
        int intParam = 0,
        float floatParam = 0f,
        object? dataParam = null,
        int pointOffset = 0,
        int pointCount = 0,
        int floatOffset = 0,
        int floatCount = 0)
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
            FloatBufferCount = floatCount
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

        if (translation == Vector2.Zero)
        {
            PointBuffer.AddRange(other.PointBuffer);
        }
        else
        {
            int required = PointBuffer.Count + other.PointBuffer.Count;
            if (PointBuffer.Capacity < required)
                PointBuffer.Capacity = Math.Max(required, PointBuffer.Capacity * 2);
            for (int i = 0; i < other.PointBuffer.Count; i++)
            {
                PointBuffer.Add(other.PointBuffer[i] + translation);
            }
        }

        DoubleBuffer.AddRange(other.DoubleBuffer);

        if (translation == Vector2.Zero)
        {
            Line3DBuffer.AddRange(other.Line3DBuffer);
        }
        else
        {
            var trans3D = new Vector3(translation.X, translation.Y, 0f);
            int required = Line3DBuffer.Count + other.Line3DBuffer.Count;
            if (Line3DBuffer.Capacity < required)
                Line3DBuffer.Capacity = Math.Max(required, Line3DBuffer.Capacity * 2);
            for (int i = 0; i < other.Line3DBuffer.Count; i++)
            {
                var l = other.Line3DBuffer[i];
                l.Start += trans3D;
                l.End += trans3D;
                Line3DBuffer.Add(l);
            }
        }

        FloatBuffer.AddRange(other.FloatBuffer);

        foreach (var cmd in other.Commands)
        {
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
                if (adjustedCmd.Type == RenderCommandType.DrawRect || 
                    adjustedCmd.Type == RenderCommandType.DrawTexture || 
                    adjustedCmd.Type == RenderCommandType.DrawRoundedRect)
                {
                    adjustedCmd.Rect = new Rect(adjustedCmd.Rect.Position + translation, adjustedCmd.Rect.Size);
                }
                else
                {
                    adjustedCmd.Position += translation;
                    adjustedCmd.Position2 += translation;
                    adjustedCmd.Position3 += translation;
                    adjustedCmd.Position4 += translation;

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
            }

            Commands.Add(adjustedCmd);
        }

        _retainedResources.AddRange(other.CloneRetainedResources());
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
        foreach (var resource in _retainedResources)
        {
            resource.Dispose();
        }

        _retainedResources.Clear();
    }
}
