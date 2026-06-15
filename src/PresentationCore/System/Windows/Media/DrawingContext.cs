using ProGPU.Scene;
using System;
using System.Collections.Generic;
using System.Numerics;

namespace System.Windows.Media;

public class DrawingContext : IDisposable
{
    private readonly ProGPU.Scene.DrawingContext _nativeContext;
    private readonly Stack<Matrix4x4> _transformStack = new();
    private readonly Stack<float> _opacityStack = new();
    
    private enum PushType
    {
        Transform,
        Opacity,
        Clip,
        GeometryClip
    }
    private readonly Stack<PushType> _pushStack = new();

    public ProGPU.Scene.DrawingContext NativeContext => _nativeContext;

    public DrawingContext(ProGPU.Scene.DrawingContext nativeContext)
    {
        _nativeContext = nativeContext;
        _transformStack.Push(Matrix4x4.Identity);
        _opacityStack.Push(1f);
    }

    private Matrix4x4 CurrentTransform => _transformStack.Peek();
    private float CurrentOpacity => _opacityStack.Peek();

    private void ApplyContextStateToLastCommands(int startCount)
    {
        int endCount = _nativeContext.Commands.Count;
        for (int i = startCount; i < endCount; i++)
        {
            var cmd = _nativeContext.Commands[i];
            
            // Apply transform: if the command already has a transform, multiply it
            if (cmd.Transform.IsIdentity || cmd.Transform == default)
            {
                cmd.Transform = CurrentTransform;
            }
            else
            {
                cmd.Transform = cmd.Transform * CurrentTransform;
            }

            _nativeContext.Commands[i] = cmd;
        }
    }

    public void DrawLine(Pen? pen, Point point0, Point point1)
    {
        if (pen == null) return;
        var nativePen = pen.ToNative();
        if (nativePen == null) return;
        var p0 = new Vector2((float)point0.X, (float)point0.Y);
        var p1 = new Vector2((float)point1.X, (float)point1.Y);

        int start = _nativeContext.Commands.Count;
        _nativeContext.DrawLine(nativePen, p0, p1);
        ApplyContextStateToLastCommands(start);
    }

    public void DrawRectangle(Brush? brush, Pen? pen, Rect rectangle)
    {
        var nativeBrush = brush?.ToNative();
        var nativePen = pen?.ToNative();
        var nativeRect = new ProGPU.Scene.Rect((float)rectangle.X, (float)rectangle.Y, (float)rectangle.Width, (float)rectangle.Height);

        int start = _nativeContext.Commands.Count;
        _nativeContext.DrawRectangle(nativeBrush, nativePen, nativeRect);
        ApplyContextStateToLastCommands(start);
    }

    public void DrawRoundedRectangle(Brush? brush, Pen? pen, Rect rectangle, double radiusX, double radiusY)
    {
        var nativeBrush = brush?.ToNative();
        var nativePen = pen?.ToNative();
        var nativeRect = new ProGPU.Scene.Rect((float)rectangle.X, (float)rectangle.Y, (float)rectangle.Width, (float)rectangle.Height);

        int start = _nativeContext.Commands.Count;
        _nativeContext.DrawRoundedRectangle(nativeBrush, nativePen, nativeRect, (float)radiusX);
        ApplyContextStateToLastCommands(start);
    }

    public void DrawEllipse(Brush? brush, Pen? pen, Point center, double radiusX, double radiusY)
    {
        var nativeBrush = brush?.ToNative();
        var nativePen = pen?.ToNative();
        var c = new Vector2((float)center.X, (float)center.Y);

        int start = _nativeContext.Commands.Count;
        _nativeContext.DrawEllipse(nativeBrush, nativePen, c, (float)radiusX, (float)radiusY);
        ApplyContextStateToLastCommands(start);
    }

    public void DrawGeometry(Brush? brush, Pen? pen, Geometry geometry)
    {
        if (geometry == null) return;
        var nativeBrush = brush?.ToNative();
        var nativePen = pen?.ToNative();

        int start = _nativeContext.Commands.Count;
        geometry.Draw(_nativeContext, nativeBrush, nativePen);
        ApplyContextStateToLastCommands(start);
    }

    public void DrawImage(ImageSource imageSource, Rect rectangle)
    {
        if (imageSource is Imaging.BitmapSource bitmapSource)
        {
            var texture = bitmapSource.GpuTexture;
            var nativeRect = new ProGPU.Scene.Rect((float)rectangle.X, (float)rectangle.Y, (float)rectangle.Width, (float)rectangle.Height);

            int start = _nativeContext.Commands.Count;
            _nativeContext.DrawTexture(texture, nativeRect);
            ApplyContextStateToLastCommands(start);
        }
    }

    public void DrawText(FormattedText formattedText, Point origin)
    {
        if (formattedText == null) return;
        int start = _nativeContext.Commands.Count;
        formattedText.Draw(this, origin);
        ApplyContextStateToLastCommands(start);
    }

    public void DrawGlyphRun(Brush? foregroundBrush, GlyphRun glyphRun)
    {
        if (glyphRun == null || foregroundBrush == null) return;
        var nativeBrush = foregroundBrush.ToNative();

        int start = _nativeContext.Commands.Count;
        _nativeContext.DrawGlyphRun(
            glyphRun.GlyphIndices,
            glyphRun.GlyphPositions,
            glyphRun.Font,
            glyphRun.FontSize,
            nativeBrush,
            glyphRun.Position,
            glyphRun.Transform,
            glyphRun.IsBold,
            glyphRun.IsItalic
        );
        ApplyContextStateToLastCommands(start);
    }

    public void PushClip(Geometry clipGeometry)
    {
        if (clipGeometry == null) return;

        if (clipGeometry.TryGetPathGeometry(out var clipPath, out var geometryTransform))
        {
            var combinedTransform = geometryTransform * CurrentTransform;
            if (combinedTransform.IsIdentity)
            {
                _nativeContext.PushGeometryClip(clipPath);
            }
            else
            {
                _nativeContext.PushGeometryClip(clipPath, combinedTransform);
            }

            _pushStack.Push(PushType.GeometryClip);
            return;
        }

        var bounds = clipGeometry.Bounds;
        var currentT = CurrentTransform;
        var p0 = Vector2.Transform(new Vector2((float)bounds.X, (float)bounds.Y), currentT);
        var p1 = Vector2.Transform(new Vector2((float)(bounds.X + bounds.Width), (float)bounds.Y), currentT);
        var p2 = Vector2.Transform(new Vector2((float)(bounds.X + bounds.Width), (float)(bounds.Y + bounds.Height)), currentT);
        var p3 = Vector2.Transform(new Vector2((float)bounds.X, (float)(bounds.Y + bounds.Height)), currentT);

        var minX = MathF.Min(MathF.Min(p0.X, p1.X), MathF.Min(p2.X, p3.X));
        var minY = MathF.Min(MathF.Min(p0.Y, p1.Y), MathF.Min(p2.Y, p3.Y));
        var maxX = MathF.Max(MathF.Max(p0.X, p1.X), MathF.Max(p2.X, p3.X));
        var maxY = MathF.Max(MathF.Max(p0.Y, p1.Y), MathF.Max(p2.Y, p3.Y));
        
        var nativeRect = new ProGPU.Scene.Rect(minX, minY, maxX - minX, maxY - minY);
        
        _nativeContext.PushClip(nativeRect);
        _pushStack.Push(PushType.Clip);
    }

    public void PushOpacity(double opacity)
    {
        var current = _opacityStack.Peek();
        var next = current * (float)opacity;
        _opacityStack.Push(next);
        _nativeContext.PushOpacity((float)opacity);
        _pushStack.Push(PushType.Opacity);
    }

    public void PushTransform(Transform transform)
    {
        if (transform == null) return;
        var current = _transformStack.Peek();
        var next = transform.Value * current;
        _transformStack.Push(next);
        _pushStack.Push(PushType.Transform);
    }

    public void Pop()
    {
        if (_pushStack.Count == 0) return;
        var type = _pushStack.Pop();
        switch (type)
        {
            case PushType.Transform:
                _transformStack.Pop();
                break;
            case PushType.Opacity:
                _opacityStack.Pop();
                _nativeContext.PopOpacity();
                break;
            case PushType.Clip:
                _nativeContext.PopClip();
                break;
            case PushType.GeometryClip:
                _nativeContext.PopGeometryClip();
                break;
        }
    }

    public void Close()
    {
        // No-op
    }

    public void Dispose()
    {
        Close();
    }
}
