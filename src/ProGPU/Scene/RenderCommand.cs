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
    DrawCubicBezier
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

    // Texture properties
    public GpuTexture? Texture;

    // Advanced geometries
    public Vector2 Position2;
    public Vector2 Position3;
    public Vector2 Position4;
    public float RadiusX;
    public float RadiusY;
    public float CornerRadius;
    public Vector4 AnimAmp;
    public Vector4 AnimFreqPhase;
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

    public void DrawText(string text, TtfFont font, float fontSize, Brush brush, Vector2 position, bool isBold = false, bool isItalic = false)
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
            IsItalic = isItalic
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

    public void DrawLine(Pen pen, Vector2 p1, Vector2 p2, Vector4 animAmp = default, Vector4 animFreqPhase = default)
    {
        Commands.Add(new RenderCommand
        {
            Type = RenderCommandType.DrawLine,
            Pen = pen,
            Position = p1,
            Position2 = p2,
            AnimAmp = animAmp,
            AnimFreqPhase = animFreqPhase
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

    public void DrawQuadraticBezier(Pen pen, Vector2 p0, Vector2 p1, Vector2 p2, Vector4 animAmp = default, Vector4 animFreqPhase = default)
    {
        Commands.Add(new RenderCommand
        {
            Type = RenderCommandType.DrawBezier,
            Pen = pen,
            Position = p0,
            Position2 = p1,
            Position3 = p2,
            AnimAmp = animAmp,
            AnimFreqPhase = animFreqPhase
        });
    }

    public void DrawCubicBezier(Pen pen, Vector2 p0, Vector2 p1, Vector2 p2, Vector2 p3, Vector4 animAmp = default, Vector4 animFreqPhase = default)
    {
        Commands.Add(new RenderCommand
        {
            Type = RenderCommandType.DrawCubicBezier,
            Pen = pen,
            Position = p0,
            Position2 = p1,
            Position3 = p2,
            Position4 = p3,
            AnimAmp = animAmp,
            AnimFreqPhase = animFreqPhase
        });
    }

    public void Clear()
    {
        Commands.Clear();
    }
}
