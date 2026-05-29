using System.Collections.Generic;
using System.Numerics;
using ProGPU.Vector;
using ProGPU.Text;
using ProGPU.Backend;

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
    DrawGpuScatterSeries
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

    // Texture properties
    public GpuTexture? Texture;

    // Advanced geometries
    public Vector2 Position2;
    public Vector2 Position3;
    public Vector2 Position4;
    public float RadiusX;
    public float RadiusY;
    public float CornerRadius;

    // Polyline properties
    public Vector2[]? PolylinePoints;
    public bool IsClosed;

    // Spline properties
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

    // GPU Chart Series properties
    public float[]? GpuPoints;
    public int GpuPointsCount;

    // GPU Transform properties
    public bool UseGpuTransforms;
    public Matrix4x4 CameraView;

    // GPU Chart scaling parameters
    public Vector2 Scale;
    public Vector2 Translate;
}


public class DrawingContext
{
    public List<RenderCommand> Commands { get; } = new();

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

    public void DrawText(string text, TtfFont font, float fontSize, Brush brush, Vector2 position, bool isBold = false, bool isItalic = false, float rotation = 0f)
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
            Rotation = rotation
        });
    }

    public void DrawTexture(GpuTexture texture, Rect rect)
    {
        Commands.Add(new RenderCommand
        {
            Type = RenderCommandType.DrawTexture,
            Rect = rect,
            Texture = texture
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
        Commands.Add(new RenderCommand
        {
            Type = RenderCommandType.DrawLine3D,
            Pen = pen,
            Position3D1 = p1,
            Position3D2 = p2
        });
    }

    public void DrawHatch(Brush brush, PathGeometry boundaries)
    {
        Commands.Add(new RenderCommand
        {
            Type = RenderCommandType.DrawHatch,
            Brush = brush,
            Path = boundaries
        });
    }

    public void DrawAcisSolid(Pen pen, List<Line3D> edges, Matrix4x4 modelTransform)
    {
        Commands.Add(new RenderCommand
        {
            Type = RenderCommandType.DrawAcisSolid,
            Pen = pen,
            Edges3D = edges,
            Transform = modelTransform
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
        Commands.Add(new RenderCommand
        {
            Type = RenderCommandType.DrawRoundedRect,
            Brush = brush,
            Pen = pen,
            Rect = rect,
            RadiusX = radius
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

    public void DrawPolyline(Pen pen, Vector2[] points, bool isClosed = false)
    {
        Commands.Add(new RenderCommand
        {
            Type = RenderCommandType.DrawPolyline,
            Pen = pen,
            PolylinePoints = points,
            IsClosed = isClosed
        });
    }

    public void DrawSpline(Pen pen, Vector2[] controlPoints, double[] knots, int degree)
    {
        DrawSpline(pen, controlPoints, knots, null, degree, false);
    }

    public void DrawSpline(Pen pen, Vector2[] controlPoints, double[] knots, double[]? weights, int degree, bool isClosed)
    {
        Commands.Add(new RenderCommand
        {
            Type = RenderCommandType.DrawSpline,
            Pen = pen,
            PolylinePoints = controlPoints,
            SplineKnots = knots,
            SplineWeights = weights,
            SplineDegree = degree,
            IsClosed = isClosed
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
        Commands.Add(new RenderCommand
        {
            Type = RenderCommandType.DrawStaticDxf,
            StaticBuffer = staticBuffer
        });
    }

    public void DrawGpuLineSeries(float[] interleavedCoords, int pointsCount, float thickness, Brush brush)
    {
        Commands.Add(new RenderCommand
        {
            Type = RenderCommandType.DrawGpuLineSeries,
            GpuPoints = interleavedCoords,
            GpuPointsCount = pointsCount,
            RadiusX = thickness,
            Brush = brush
        });
    }

    public void DrawGpuLineSeries(object staticBuffer, float thickness, Brush brush)
    {
        Commands.Add(new RenderCommand
        {
            Type = RenderCommandType.DrawGpuLineSeries,
            StaticBuffer = staticBuffer,
            RadiusX = thickness,
            Brush = brush
        });
    }

    public void DrawGpuLineSeries(object staticBuffer, float thickness, Brush brush, Vector2 scale, Vector2 translate)
    {
        Commands.Add(new RenderCommand
        {
            Type = RenderCommandType.DrawGpuLineSeries,
            StaticBuffer = staticBuffer,
            RadiusX = thickness,
            Brush = brush,
            Scale = scale,
            Translate = translate
        });
    }

    public void DrawGpuScatterSeries(float[] interleavedCoords, int pointsCount, float radius, Brush brush)
    {
        Commands.Add(new RenderCommand
        {
            Type = RenderCommandType.DrawGpuScatterSeries,
            GpuPoints = interleavedCoords,
            GpuPointsCount = pointsCount,
            RadiusX = radius,
            Brush = brush
        });
    }

    public void DrawGpuScatterSeries(object staticBuffer, float radius, Brush brush)
    {
        Commands.Add(new RenderCommand
        {
            Type = RenderCommandType.DrawGpuScatterSeries,
            StaticBuffer = staticBuffer,
            RadiusX = radius,
            Brush = brush
        });
    }

    public void DrawGpuScatterSeries(object staticBuffer, float radius, Brush brush, Vector2 scale, Vector2 translate)
    {
        Commands.Add(new RenderCommand
        {
            Type = RenderCommandType.DrawGpuScatterSeries,
            StaticBuffer = staticBuffer,
            RadiusX = radius,
            Brush = brush,
            Scale = scale,
            Translate = translate
        });
    }

    public void Clear()
    {
        Commands.Clear();
    }
}
