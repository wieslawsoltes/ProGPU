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
    PopOpacity
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

    // Texture properties
    public GpuTexture? Texture;
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

    public void DrawText(string text, TtfFont font, float fontSize, Brush brush, Vector2 position)
    {
        Commands.Add(new RenderCommand
        {
            Type = RenderCommandType.DrawText,
            Text = text,
            Font = font,
            FontSize = fontSize,
            Brush = brush,
            Position = position
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
            FontSize = opacity // Reuse float field for opacity value
        });
    }

    public void PopOpacity()
    {
        Commands.Add(new RenderCommand { Type = RenderCommandType.PopOpacity });
    }

    public void Clear()
    {
        Commands.Clear();
    }
}
