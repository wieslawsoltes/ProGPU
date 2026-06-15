using System;
using System.Collections.Generic;
using System.Numerics;
using ProGPU.Backend;
using ProGPU.Scene;
using ProGPU.Vector;

namespace SkiaSharp;

public class SKCanvas : IDisposable
{
    private readonly DrawingContext _context;
    private readonly float _width;
    private readonly float _height;
    private SKMatrix _currentMatrix = SKMatrix.Identity;
    private float _currentOpacity = 1f;

    public enum PushKind
    {
        RectClip,
        GeometryClip,
        Opacity
    }

    private readonly Stack<(SKMatrix Matrix, float Opacity, int PushedScopesCount)> _stateStack = new();
    private readonly Stack<PushKind> _pushedScopes = new();

    public SKMatrix TotalMatrix
    {
        get => _currentMatrix;
        set => SetMatrix(value);
    }

    public SKCanvas(DrawingContext context, float width, float height)
    {
        _context = context;
        _width = width;
        _height = height;
    }

    public void Clear(SKColor color)
    {
        var c = new Vector4(color.R / 255f, color.G / 255f, color.B / 255f, color.A / 255f);
        var brush = new SolidColorBrush(c);
        _context.PushBlendMode(GpuBlendMode.Src);
        _context.Commands.Add(new RenderCommand
        {
            Type = RenderCommandType.DrawRect,
            Rect = new Rect(0, 0, _width, _height),
            Brush = brush,
            Transform = Matrix4x4.Identity // Clear is always in identity screen space
        });
        _context.PopBlendMode();
    }

    public void Save()
    {
        _stateStack.Push((_currentMatrix, _currentOpacity, _pushedScopes.Count));
    }

    public int SaveLayer(SKRect bounds, SKPaint paint)
    {
        var restoreCount = _stateStack.Count;
        Save();
        if (paint != null)
        {
            float opacity = paint.Color.A / 255f;
            _currentOpacity *= opacity;
            _context.PushOpacity(opacity);
            _pushedScopes.Push(PushKind.Opacity);
        }
        return restoreCount;
    }

    public int SaveLayer(SKPaint paint)
    {
        return SaveLayer(new SKRect(0, 0, _width, _height), paint);
    }

    public void Restore()
    {
        if (_stateStack.Count > 0)
        {
            var state = _stateStack.Pop();
            _currentMatrix = state.Matrix;
            _currentOpacity = state.Opacity;

            // Pop any clips or layers pushed in this save frame
            while (_pushedScopes.Count > state.PushedScopesCount)
            {
                var kind = _pushedScopes.Pop();
                switch (kind)
                {
                    case PushKind.RectClip:
                        _context.PopClip();
                        break;
                    case PushKind.GeometryClip:
                        _context.PopGeometryClip();
                        break;
                    case PushKind.Opacity:
                        _context.PopOpacity();
                        break;
                }
            }
        }
    }

    public void RestoreToCount(int count)
    {
        while (_stateStack.Count > count)
        {
            Restore();
        }
    }

    public void Translate(float dx, float dy)
    {
        _currentMatrix.TransX += dx * _currentMatrix.ScaleX + dy * _currentMatrix.SkewX;
        _currentMatrix.TransY += dx * _currentMatrix.SkewY + dy * _currentMatrix.ScaleY;
    }

    public void Scale(float sx, float sy)
    {
        _currentMatrix.ScaleX *= sx;
        _currentMatrix.ScaleY *= sy;
    }

    public void SetMatrix(SKMatrix matrix)
    {
        _currentMatrix = matrix;
    }

    public void ResetMatrix()
    {
        _currentMatrix = SKMatrix.Identity;
    }

    public void ClipRect(SKRect rect, SKClipOperation operation = SKClipOperation.Intersect, bool antialias = true)
    {
        _context.Commands.Add(new RenderCommand
        {
            Type = RenderCommandType.PushClip,
            Rect = new Rect(rect.Left, rect.Top, rect.Width, rect.Height),
            Transform = _currentMatrix.ToMatrix4x4()
        });
        _pushedScopes.Push(PushKind.RectClip);
    }

    public void ClipPath(SKPath path, SKClipOperation operation = SKClipOperation.Intersect, bool antialias = true)
    {
        _context.PushGeometryClip(path.Geometry, _currentMatrix.ToMatrix4x4());
        _pushedScopes.Push(PushKind.GeometryClip);
    }

    public void DrawRect(float x, float y, float w, float h, SKPaint paint)
    {
        var brush = paint.ToBrush();
        var pen = paint.ToPen();
        _context.Commands.Add(new RenderCommand
        {
            Type = RenderCommandType.DrawRect,
            Rect = new Rect(x, y, w, h),
            Brush = brush,
            Pen = pen,
            Transform = _currentMatrix.ToMatrix4x4()
        });
    }

    public void DrawRect(SKRect rect, SKPaint paint) => DrawRect(rect.Left, rect.Top, rect.Width, rect.Height, paint);

    public void DrawRoundRect(SKRoundRect rect, SKPaint paint)
    {
        if (!TryGetUniformRadii(rect, out var radiusX, out var radiusY))
        {
            using var path = new SKPath();
            path.AddRoundRect(rect);
            DrawPath(path, paint);
            return;
        }

        var brush = paint.ToBrush();
        var pen = paint.ToPen();
        _context.Commands.Add(new RenderCommand
        {
            Type = RenderCommandType.DrawRoundedRect,
            Rect = new Rect(rect.Rect.Left, rect.Rect.Top, rect.Rect.Width, rect.Rect.Height),
            RadiusX = radiusX,
            RadiusY = radiusY,
            Brush = brush,
            Pen = pen,
            Transform = _currentMatrix.ToMatrix4x4()
        });
    }

    public void DrawRoundRect(SKRect rect, float rx, float ry, SKPaint paint)
    {
        DrawRoundRect(new SKRoundRect(rect, rx, ry), paint);
    }

    private static bool TryGetUniformRadii(SKRoundRect rect, out float radiusX, out float radiusY)
    {
        radiusX = rect.CornerRadii[0].X;
        radiusY = rect.CornerRadii[0].Y;
        for (int i = 1; i < rect.CornerRadii.Length; i++)
        {
            if (MathF.Abs(rect.CornerRadii[i].X - radiusX) > 0.0001f ||
                MathF.Abs(rect.CornerRadii[i].Y - radiusY) > 0.0001f)
            {
                return false;
            }
        }

        return true;
    }

    public void DrawOval(SKRect rect, SKPaint paint)
    {
        var brush = paint.ToBrush();
        var pen = paint.ToPen();
        _context.Commands.Add(new RenderCommand
        {
            Type = RenderCommandType.DrawEllipse,
            Position2 = new Vector2(rect.MidX, rect.MidY),
            RadiusX = rect.Width / 2f,
            RadiusY = rect.Height / 2f,
            Brush = brush,
            Pen = pen,
            Transform = _currentMatrix.ToMatrix4x4()
        });
    }

    public void DrawCircle(float cx, float cy, float radius, SKPaint paint)
    {
        var brush = paint.ToBrush();
        var pen = paint.ToPen();
        _context.Commands.Add(new RenderCommand
        {
            Type = RenderCommandType.DrawCircle,
            Position2 = new Vector2(cx, cy),
            RadiusX = radius,
            Brush = brush,
            Pen = pen,
            Transform = _currentMatrix.ToMatrix4x4()
        });
    }

    public void DrawPath(SKPath path, SKPaint paint)
    {
        var brush = paint.ToBrush();
        var pen = paint.ToPen();
        _context.Commands.Add(new RenderCommand
        {
            Type = RenderCommandType.DrawPath,
            Path = path.Geometry,
            Brush = brush,
            Pen = pen,
            Transform = _currentMatrix.ToMatrix4x4()
        });
    }

    public void DrawImage(SKImage image, SKRect source, SKRect dest, SKPaint paint)
    {
        var opacity = paint != null ? paint.Color.A / 255f : 1f;
        if (opacity < 1f)
        {
            _context.PushOpacity(opacity);
        }

        _context.Commands.Add(new RenderCommand
        {
            Type = RenderCommandType.DrawTexture,
            Texture = image.Texture,
            Rect = new Rect(dest.Left, dest.Top, dest.Width, dest.Height),
            SrcRect = new Rect(source.Left, source.Top, source.Width, source.Height),
            Transform = _currentMatrix.ToMatrix4x4()
        });

        if (opacity < 1f)
        {
            _context.PopOpacity();
        }
    }

    public void DrawImage(SKImage image, float x, float y, SKPaint paint)
    {
        DrawImage(image, new SKRect(0, 0, image.Width, image.Height), new SKRect(x, y, x + image.Width, y + image.Height), paint);
    }

    public void DrawTextBlob(SKTextBlob textBlob, float x, float y, SKPaint paint)
    {
        var brush = paint.ToBrush();
        if (brush == null)
        {
            return;
        }

        // Convert positions from SKPoint to Vector2
        var positions = new Vector2[textBlob.GlyphPositions.Length];
        for (int i = 0; i < positions.Length; i++)
        {
            positions[i] = new Vector2(textBlob.GlyphPositions[i].X, textBlob.GlyphPositions[i].Y);
        }

        _context.DrawGlyphRun(
            textBlob.GlyphIndices,
            positions,
            textBlob.Font.Typeface.Font,
            textBlob.Font.Size,
            brush,
            new Vector2(x, y),
            _currentMatrix.ToMatrix4x4()
        );
    }

    public void Dispose() { }
}
