using System;
using System.Collections.Generic;
using System.Numerics;
using ProGPU.Backend;
using ProGPU.Scene;
using ProGPU.Vector;
using Silk.NET.WebGPU;

namespace SkiaSharp;

public class SKCanvas : IDisposable
{
    private DrawingContext _context;
    private readonly float _width;
    private readonly float _height;
    private readonly WgpuContext? _gpuContext;
    private SKMatrix _currentMatrix = SKMatrix.Identity;
    private float _currentOpacity = 1f;
    private readonly List<GpuTexture> _ownedLayerTextures = new();
    private static readonly Dictionary<WgpuContext, Compositor> s_compositorCache = new();

    static SKCanvas()
    {
        WgpuContext.Disposing += RemoveCachedCompositor;
    }

    public enum PushKind
    {
        RectClip,
        GeometryClip,
        Opacity
    }

    private readonly Stack<(SKMatrix Matrix, float Opacity, int PushedScopesCount)> _stateStack = new();
    private readonly Stack<PushKind> _pushedScopes = new();
    private readonly Stack<LayerFrame> _layerStack = new();

    private sealed class LayerFrame
    {
        public LayerFrame(
            DrawingContext parentContext,
            DrawingContext layerContext,
            SKPaint? paint,
            int stateDepth,
            SKRect bounds,
            SKMatrix boundsMatrix)
        {
            ParentContext = parentContext;
            LayerContext = layerContext;
            Paint = paint;
            StateDepth = stateDepth;
            Bounds = bounds;
            BoundsMatrix = boundsMatrix;
        }

        public DrawingContext ParentContext { get; }
        public DrawingContext LayerContext { get; }
        public SKPaint? Paint { get; }
        public int StateDepth { get; }
        public SKRect Bounds { get; }
        public SKMatrix BoundsMatrix { get; }
    }

    public SKMatrix TotalMatrix
    {
        get => _currentMatrix;
        set => SetMatrix(value);
    }

    public SKCanvas(DrawingContext context, float width, float height, WgpuContext? gpuContext = null)
    {
        _context = context;
        _width = width;
        _height = height;
        _gpuContext = gpuContext;
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

        var parentContext = _context;
        var layerContext = new DrawingContext();
        _layerStack.Push(new LayerFrame(parentContext, layerContext, paint?.Clone(), _stateStack.Count, bounds, _currentMatrix));
        _context = layerContext;

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
            var layerFrame = _layerStack.Count > 0 && _layerStack.Peek().StateDepth == _stateStack.Count
                ? _layerStack.Pop()
                : null;

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

            if (layerFrame != null)
            {
                RestoreLayer(layerFrame);
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

    private void RestoreLayer(LayerFrame layerFrame)
    {
        _context = layerFrame.ParentContext;
        if (layerFrame.LayerContext.Commands.Count == 0 || !IsValidLayerBounds(layerFrame.Bounds))
        {
            layerFrame.LayerContext.Clear();
            return;
        }

        var pushedBlendMode = PushPaintBlendMode(layerFrame.Paint);
        var opacity = layerFrame.Paint?.Color.A / 255f ?? 1f;

        try
        {
            var pushedOpacity = false;
            if (opacity < 1f)
            {
                _context.PushOpacity(opacity);
                pushedOpacity = true;
            }

            try
            {
                var pushedLayerBoundsClip = PushLayerBoundsClip(_context, layerFrame);
                try
                {
                    DrawRestoredLayerTexture(layerFrame, RenderLayerToTexture(layerFrame));
                }
                finally
                {
                    if (pushedLayerBoundsClip)
                    {
                        _context.PopClip();
                    }
                }
            }
            finally
            {
                if (pushedOpacity)
                {
                    _context.PopOpacity();
                }
            }
        }
        finally
        {
            PopPaintBlendMode(pushedBlendMode);
        }
    }

    private void DrawRestoredLayerTexture(LayerFrame layerFrame, GpuTexture texture)
    {
        var rect = new Rect(0f, 0f, _width, _height);
        var imageFilter = layerFrame.Paint?.ImageFilter;
        if (imageFilter is { IsBlur: true })
        {
            _context.DrawImageWithEffect(
                texture,
                rect,
                blurSigma: MathF.Max(imageFilter.SigmaX, imageFilter.SigmaY));
            return;
        }

        if (imageFilter is { IsDropShadow: true })
        {
            texture = RenderFilteredLayerToTexture(
                texture,
                new DropShadowEffect(
                    MathF.Max(imageFilter.SigmaX, imageFilter.SigmaY),
                    new Vector2(imageFilter.Dx, imageFilter.Dy),
                    ToVector4(imageFilter.ShadowColor)));
        }

        DrawRestoredLayerTexture(texture, rect);
    }

    private void DrawRestoredLayerTexture(GpuTexture texture, Rect rect)
    {
        _context.Commands.Add(new RenderCommand
        {
            Type = RenderCommandType.DrawTexture,
            Texture = texture,
            Rect = rect,
            Transform = Matrix4x4.Identity,
            TextureSamplingMode = TextureSamplingMode.Linear
        });
    }

    private GpuTexture RenderFilteredLayerToTexture(GpuTexture sourceTexture, EffectBase effect)
    {
        var context = _gpuContext != null && !_gpuContext.IsDisposed
            ? _gpuContext
            : SKContextHelper.GetContext();
        var texture = new GpuTexture(
            context,
            (uint)_width,
            (uint)_height,
            TextureFormat.Rgba8Unorm,
            TextureUsage.RenderAttachment | TextureUsage.CopySrc | TextureUsage.CopyDst | TextureUsage.TextureBinding,
            "SKCanvas SaveLayer Filtered Texture",
            alphaMode: GpuTextureAlphaMode.Premultiplied);

        var visual = new DrawingVisual
        {
            Size = new Vector2(_width, _height),
            Effect = effect
        };
        visual.Context.DrawTexture(sourceTexture, new Rect(0f, 0f, _width, _height));

        GetCompositorForContext(context).RenderOffscreen(
            visual,
            (uint)_width,
            (uint)_height,
            texture,
            padding: 0f,
            dpiScale: 1f);

        _ownedLayerTextures.Add(texture);
        return texture;
    }

    private static Vector4 ToVector4(SKColor color)
    {
        return new Vector4(
            color.R / 255f,
            color.G / 255f,
            color.B / 255f,
            color.A / 255f);
    }

    private GpuTexture RenderLayerToTexture(LayerFrame layerFrame)
    {
        var context = _gpuContext != null && !_gpuContext.IsDisposed
            ? _gpuContext
            : SKContextHelper.GetContext();
        var texture = new GpuTexture(
            context,
            (uint)_width,
            (uint)_height,
            TextureFormat.Rgba8Unorm,
            TextureUsage.RenderAttachment | TextureUsage.CopySrc | TextureUsage.CopyDst | TextureUsage.TextureBinding,
            "SKCanvas SaveLayer Texture",
            alphaMode: GpuTextureAlphaMode.Premultiplied);

        var visual = new DrawingVisual { Size = new Vector2(_width, _height) };
        var pushedLayerBoundsClip = PushLayerBoundsClip(visual.Context, layerFrame);
        visual.Context.Append(layerFrame.LayerContext);
        if (pushedLayerBoundsClip)
        {
            visual.Context.PopClip();
        }

        try
        {
            GetCompositorForContext(context).RenderOffscreen(
                visual,
                (uint)_width,
                (uint)_height,
                texture,
                padding: 0f,
                dpiScale: 1f);
        }
        finally
        {
            visual.Context.Clear();
        }

        layerFrame.LayerContext.Clear();
        _ownedLayerTextures.Add(texture);
        return texture;
    }

    private bool PushLayerBoundsClip(DrawingContext context, LayerFrame layerFrame)
    {
        if (IsFullCanvasLayerBounds(layerFrame.Bounds))
        {
            return false;
        }

        context.Commands.Add(new RenderCommand
        {
            Type = RenderCommandType.PushClip,
            Rect = new Rect(
                layerFrame.Bounds.Left,
                layerFrame.Bounds.Top,
                layerFrame.Bounds.Width,
                layerFrame.Bounds.Height),
            Transform = layerFrame.BoundsMatrix.ToMatrix4x4()
        });
        return true;
    }

    private static bool IsValidLayerBounds(SKRect bounds)
    {
        return float.IsFinite(bounds.Left) &&
            float.IsFinite(bounds.Top) &&
            float.IsFinite(bounds.Right) &&
            float.IsFinite(bounds.Bottom) &&
            bounds.Width > 0f &&
            bounds.Height > 0f;
    }

    private bool IsFullCanvasLayerBounds(SKRect bounds)
    {
        return MathF.Abs(bounds.Left) < 0.0001f &&
            MathF.Abs(bounds.Top) < 0.0001f &&
            MathF.Abs(bounds.Width - _width) < 0.0001f &&
            MathF.Abs(bounds.Height - _height) < 0.0001f;
    }

    private static Compositor GetCompositorForContext(WgpuContext context)
    {
        lock (s_compositorCache)
        {
            if (!s_compositorCache.TryGetValue(context, out var compositor))
            {
                compositor = new Compositor(context, TextureFormat.Rgba8Unorm);
                s_compositorCache[context] = compositor;
            }

            return compositor;
        }
    }

    private static void RemoveCachedCompositor(WgpuContext context)
    {
        Compositor? compositor = null;
        lock (s_compositorCache)
        {
            if (s_compositorCache.TryGetValue(context, out compositor))
            {
                s_compositorCache.Remove(context);
            }
        }

        compositor?.Dispose();
    }

    private static GpuBlendMode MapBlendMode(SKBlendMode blendMode)
    {
        return blendMode switch
        {
            SKBlendMode.Clear => GpuBlendMode.Clear,
            SKBlendMode.Src => GpuBlendMode.Src,
            SKBlendMode.Dst => GpuBlendMode.Dst,
            SKBlendMode.DstOver => GpuBlendMode.DstOver,
            SKBlendMode.Plus => GpuBlendMode.Plus,
            SKBlendMode.Screen => GpuBlendMode.Screen,
            SKBlendMode.Multiply => GpuBlendMode.Multiply,
            _ => GpuBlendMode.SrcOver
        };
    }

    private bool PushPaintBlendMode(SKPaint? paint)
    {
        var blendMode = MapBlendMode(paint?.BlendMode ?? SKBlendMode.SrcOver);
        if (blendMode == GpuBlendMode.SrcOver)
        {
            return false;
        }

        _context.PushBlendMode(blendMode);
        return true;
    }

    private void PopPaintBlendMode(bool pushedBlendMode)
    {
        if (pushedBlendMode)
        {
            _context.PopBlendMode();
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
        _currentMatrix.SkewY *= sx;
        _currentMatrix.SkewX *= sy;
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
        if (operation == SKClipOperation.Difference)
        {
            var excluded = CreateRectGeometry(rect).CreateTransformed(_currentMatrix.ToMatrix4x4());
            _context.PushGeometryClip(CreateCanvasDifferenceGeometry(excluded), Matrix4x4.Identity);
            _pushedScopes.Push(PushKind.GeometryClip);
            return;
        }

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
        if (operation == SKClipOperation.Difference)
        {
            if (IsInverseFillType(path.FillType))
            {
                _context.PushGeometryClip(path.Geometry, _currentMatrix.ToMatrix4x4());
            }
            else
            {
                var excluded = path.Geometry.CreateTransformed(_currentMatrix.ToMatrix4x4());
                _context.PushGeometryClip(CreateCanvasDifferenceGeometry(excluded), Matrix4x4.Identity);
            }

            _pushedScopes.Push(PushKind.GeometryClip);
            return;
        }

        if (IsInverseFillType(path.FillType))
        {
            var excluded = path.Geometry.CreateTransformed(_currentMatrix.ToMatrix4x4());
            _context.PushGeometryClip(CreateCanvasDifferenceGeometry(excluded), Matrix4x4.Identity);
            _pushedScopes.Push(PushKind.GeometryClip);
            return;
        }

        _context.PushGeometryClip(path.Geometry, _currentMatrix.ToMatrix4x4());
        _pushedScopes.Push(PushKind.GeometryClip);
    }

    public void DrawRect(float x, float y, float w, float h, SKPaint paint)
    {
        var pushedBlendMode = PushPaintBlendMode(paint);
        try
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
        finally
        {
            PopPaintBlendMode(pushedBlendMode);
        }
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

        var pushedBlendMode = PushPaintBlendMode(paint);
        try
        {
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
        finally
        {
            PopPaintBlendMode(pushedBlendMode);
        }
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
        var pushedBlendMode = PushPaintBlendMode(paint);
        try
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
        finally
        {
            PopPaintBlendMode(pushedBlendMode);
        }
    }

    public void DrawCircle(float cx, float cy, float radius, SKPaint paint)
    {
        var pushedBlendMode = PushPaintBlendMode(paint);
        try
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
        finally
        {
            PopPaintBlendMode(pushedBlendMode);
        }
    }

    public void DrawPath(SKPath path, SKPaint paint)
    {
        var pushedBlendMode = PushPaintBlendMode(paint);
        try
        {
            var brush = paint.ToBrush();
            var pen = paint.ToPen();

            if (IsInverseFillType(path.FillType))
            {
                if (brush != null)
                {
                    var excluded = path.Geometry.CreateTransformed(_currentMatrix.ToMatrix4x4());
                    AddDrawPathCommand(CreateCanvasDifferenceGeometry(excluded), brush, null, Matrix4x4.Identity);
                }

                if (pen != null)
                {
                    AddDrawPathCommand(path.Geometry, null, pen, _currentMatrix.ToMatrix4x4());
                }

                return;
            }

            AddDrawPathCommand(path.Geometry, brush, pen, _currentMatrix.ToMatrix4x4());
        }
        finally
        {
            PopPaintBlendMode(pushedBlendMode);
        }
    }

    private void AddDrawPathCommand(PathGeometry path, Brush? brush, Pen? pen, Matrix4x4 transform)
    {
        _context.Commands.Add(new RenderCommand
        {
            Type = RenderCommandType.DrawPath,
            Path = path,
            Brush = brush,
            Pen = pen,
            Transform = transform
        });
    }

    private PathGeometry CreateCanvasDifferenceGeometry(PathGeometry excluded)
    {
        return new PathGeometry
        {
            IsCombined = true,
            PathA = CreateCanvasBoundsGeometry(),
            PathB = excluded,
            Op = (int)SKPathOp.Difference,
            FillRule = FillRule.Nonzero
        };
    }

    private PathGeometry CreateCanvasBoundsGeometry()
    {
        return CreateRectGeometry(new SKRect(0f, 0f, _width, _height));
    }

    private static PathGeometry CreateRectGeometry(SKRect rect)
    {
        var geometry = new PathGeometry();
        var figure = new PathFigure(new Vector2(rect.Left, rect.Top), isClosed: true);
        figure.Segments.Add(new LineSegment(new Vector2(rect.Right, rect.Top)));
        figure.Segments.Add(new LineSegment(new Vector2(rect.Right, rect.Bottom)));
        figure.Segments.Add(new LineSegment(new Vector2(rect.Left, rect.Bottom)));
        geometry.Figures.Add(figure);
        return geometry;
    }

    private static bool IsInverseFillType(SKPathFillType fillType)
    {
        return fillType is SKPathFillType.InverseWinding or SKPathFillType.InverseEvenOdd;
    }

    public void DrawImage(SKImage image, SKRect source, SKRect dest, SKPaint paint)
    {
        paint?.ThrowIfImageColorFilter();
        var pushedBlendMode = PushPaintBlendMode(paint);
        var opacity = paint != null ? paint.Color.A / 255f : 1f;
        var retainedTexture = RetainImageTexture(image);
        try
        {
            if (opacity < 1f)
            {
                _context.PushOpacity(opacity);
            }

            _context.Commands.Add(new RenderCommand
            {
                Type = RenderCommandType.DrawTexture,
                Texture = retainedTexture,
                Rect = new Rect(dest.Left, dest.Top, dest.Width, dest.Height),
                SrcRect = new Rect(source.Left, source.Top, source.Width, source.Height),
                Transform = _currentMatrix.ToMatrix4x4()
            });

            if (opacity < 1f)
            {
                _context.PopOpacity();
            }
        }
        finally
        {
            PopPaintBlendMode(pushedBlendMode);
        }
    }

    public void DrawImage(SKImage image, float x, float y, SKPaint paint)
    {
        DrawImage(image, new SKRect(0, 0, image.Width, image.Height), new SKRect(x, y, x + image.Width, y + image.Height), paint);
    }

    private GpuTexture RetainImageTexture(SKImage image)
    {
        var source = image.Texture;
        var targetContext = _gpuContext != null && !_gpuContext.IsDisposed
            ? _gpuContext
            : source.Context;
        if (!ReferenceEquals(source.Context, targetContext))
        {
            throw new InvalidOperationException(
                "SKCanvas.DrawImage cannot draw an SKImage from a different WebGPU context. " +
                "Create the image in the same GRContext/SKSurface context before recording the draw.");
        }

        var retainedTexture = new GpuTexture(
            targetContext,
            source.Width,
            source.Height,
            source.Format,
            TextureUsage.TextureBinding | TextureUsage.CopyDst | TextureUsage.CopySrc,
            "SKCanvas DrawImage Retained Source Texture",
            alphaMode: source.AlphaMode);
        retainedTexture.CopyFrom(source);
        _context.RetainResource(retainedTexture);
        return retainedTexture;
    }

    public void DrawTextBlob(SKTextBlob textBlob, float x, float y, SKPaint paint)
    {
        var brush = paint.ToBrush();
        if (brush == null)
        {
            return;
        }

        var pushedBlendMode = PushPaintBlendMode(paint);
        try
        {
            foreach (var run in textBlob.Runs)
            {
                var positions = new Vector2[run.GlyphPositions.Length];
                for (int i = 0; i < positions.Length; i++)
                {
                    positions[i] = new Vector2(run.GlyphPositions[i].X, run.GlyphPositions[i].Y);
                }

                _context.DrawGlyphRun(
                    run.GlyphIndices,
                    positions,
                    run.Font.Typeface.Font,
                    run.Font.Size,
                    brush,
                    new Vector2(x, y),
                    _currentMatrix.ToMatrix4x4()
                );
            }
        }
        finally
        {
            PopPaintBlendMode(pushedBlendMode);
        }
    }

    internal void ReleaseLayerTexturesAfterFlush()
    {
        foreach (var texture in _ownedLayerTextures)
        {
            texture.Dispose();
        }

        _ownedLayerTextures.Clear();
    }

    public void Dispose()
    {
        ReleaseLayerTexturesAfterFlush();
    }
}
