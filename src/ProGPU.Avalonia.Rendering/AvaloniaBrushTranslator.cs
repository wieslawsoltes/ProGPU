using System;
using System.Numerics;
using Avalonia.Media;
using Avalonia.Platform;
using ProGPU.Backend;
using ProGPU.Scene;
using ProGPU.Vector;
using SceneBrush = ProGPU.Vector.Brush;
using ScenePen = ProGPU.Vector.Pen;
using SceneRect = ProGPU.Scene.Rect;
using VectorGradientSpread = ProGPU.Vector.GradientSpreadMethod;
using VectorPenCap = ProGPU.Vector.PenLineCap;
using VectorPenJoin = ProGPU.Vector.PenLineJoin;
using VectorGradientStop = ProGPU.Vector.GradientStop;
using VectorLinearGradient = ProGPU.Vector.LinearGradientBrush;
using VectorRadialGradient = ProGPU.Vector.RadialGradientBrush;
using AColor = Avalonia.Media.Color;

namespace Avalonia.ProGpu;

partial class DrawingContextImpl
{
    private SceneBrush? ConvertBrush(
        IBrush? source,
        Rect target)
    {
        if (source is null)
            return null;
        float opacity = ClampUnit(source.Opacity);
        if (opacity <= 0f)
            return null;

        if (source is ISolidColorBrush solid)
        {
            AColor color = solid.Color;
            return _resources.GetSolidBrush(
                color.R,
                color.G,
                color.B,
                color.A,
                opacity);
        }

        VectorGradientStop[]? stops =
            source is IGradientBrush gradient
                ? ConvertStops(gradient)
                : null;
        if (stops is null || stops.Length == 0)
            return null;

        Size size = target.Size;
        Matrix4x4 coordinateTransform =
            ToProGpuMatrix(GetBrushTransform(source, target));
        VectorGradientSpread spread =
            ToSpread(((IGradientBrush)source).SpreadMethod);

        if (source is ILinearGradientBrush linear)
        {
            return new VectorLinearGradient(
                ToVector(linear.StartPoint.ToPixels(size) +
                         (Vector)target.Position),
                ToVector(linear.EndPoint.ToPixels(size) +
                         (Vector)target.Position),
                stops)
            {
                Opacity = opacity,
                SpreadMethod = spread,
                CoordinateTransform = coordinateTransform
            };
        }

        if (source is IRadialGradientBrush radial)
        {
            Point center =
                radial.Center.ToPixels(size) +
                (Vector)target.Position;
            Point origin =
                radial.GradientOrigin.ToPixels(size) +
                (Vector)target.Position;
            return new VectorRadialGradient(
                ToVector(center),
                ToVector(origin),
                (float)radial.RadiusX.ToValue(size.Width),
                (float)radial.RadiusY.ToValue(size.Height),
                stops)
            {
                Opacity = opacity,
                SpreadMethod = spread,
                CoordinateTransform = coordinateTransform
            };
        }

        if (source is IConicGradientBrush conic)
        {
            Point center =
                conic.Center.ToPixels(size) +
                (Vector)target.Position;
            return new SweepGradientBrush(
                ToVector(center),
                stops)
            {
                Opacity = opacity,
                SpreadMethod = spread,
                StartAngle = (float)conic.Angle,
                EndAngle = (float)conic.Angle + 360f,
                CoordinateTransform = coordinateTransform
            };
        }

        return null;
    }

    internal SceneBrush? ConvertRetainedCompositionBrush(
        IBrush brush,
        Rect target) =>
        ConvertBrush(brush, target);

    internal static bool SupportsRetainedCompositionBrush(
        IBrush brush) =>
        brush is ISolidColorBrush or
            ILinearGradientBrush or
            IRadialGradientBrush or
            IConicGradientBrush;

    internal static bool SupportsRetainedCompositionOpacityMask(
        IBrush brush) =>
        SupportsRetainedCompositionBrush(brush) ||
        brush is IImageBrush or ISceneBrush;

    private ScenePen? ConvertPen(
        IPen? source,
        Rect? target = null)
    {
        if (source?.Brush is null ||
            !double.IsFinite(source.Thickness) ||
            source.Thickness <= 0d)
        {
            return null;
        }

        if (source.Brush is ISolidColorBrush solid &&
            source.DashStyle is null)
        {
            AColor color = solid.Color;
            return _resources.GetSolidPen(
                color.R,
                color.G,
                color.B,
                color.A,
                ClampUnit(source.Brush.Opacity),
                (float)source.Thickness,
                ToJoin(source.LineJoin),
                FiniteMiter(source.MiterLimit),
                ToCap(source.LineCap));
        }

        SceneBrush? brush = ConvertBrush(
            source.Brush,
            target ??
            new Rect(0, 0, _size.Width, _size.Height));
        if (brush is null)
            return null;

        double[]? dashes = null;
        double dashOffset = 0d;
        if (source.DashStyle is { } dashStyle &&
            dashStyle.Dashes is { Count: > 0 } dashValues)
        {
            dashes = new double[dashValues.Count];
            for (int index = 0; index < dashes.Length; index++)
                dashes[index] = dashValues[index];
            dashOffset = double.IsFinite(dashStyle.Offset)
                ? dashStyle.Offset
                : 0d;
        }

        VectorPenCap cap = ToCap(source.LineCap);
        return new ScenePen(
            brush,
            (float)source.Thickness,
            ToJoin(source.LineJoin),
            FiniteMiter(source.MiterLimit),
            cap,
            cap,
            cap,
            dashes,
            dashOffset);
    }

    private bool TryDrawBrushContent(
        IBrush brush,
        Rect target,
        ProGPU.Vector.PathGeometry? geometryClip)
    {
        if (brush is IImageBrush image)
            return DrawImageBrush(image, target, geometryClip);
        if (brush is ISceneBrush scene)
            return DrawSceneBrush(scene, target, geometryClip);
        return false;
    }

    private bool DrawImageBrush(
        IImageBrush brush,
        Rect target,
        ProGPU.Vector.PathGeometry? geometryClip)
    {
        IBitmapImpl? bitmap =
            ProGpuImageBrushSource.GetBitmap(brush.Source);
        if (bitmap is null ||
            !TryResolveTexture(bitmap, out GpuTexture? texture))
        {
            return false;
        }

        PushContentClip(target, geometryClip);
        try
        {
            var mapping = new AvaloniaTileBrushMapping(
                brush,
                bitmap.PixelSize.ToSize(96),
                target.Size);
            Rect firstDestination =
                mapping.DestinationRect.Translate(
                    (Vector)target.Position);
            DrawImageTiles(
                brush,
                texture!,
                mapping.SourceRect,
                firstDestination,
                target);
        }
        finally
        {
            PopContentClip(geometryClip);
        }
        return true;
    }

    private void DrawImageTiles(
        IImageBrush brush,
        GpuTexture texture,
        Rect source,
        Rect firstDestination,
        Rect target)
    {
        if (firstDestination.Width <= 0d ||
            firstDestination.Height <= 0d)
        {
            return;
        }

        if (brush.TileMode == TileMode.None)
        {
            DrawImageTile(
                texture,
                source,
                firstDestination,
                brush.Opacity);
            return;
        }

        int startX = (int)Math.Floor(
            (target.Left - firstDestination.Left) /
            firstDestination.Width);
        int endX = (int)Math.Ceiling(
            (target.Right - firstDestination.Left) /
            firstDestination.Width);
        int startY = (int)Math.Floor(
            (target.Top - firstDestination.Top) /
            firstDestination.Height);
        int endY = (int)Math.Ceiling(
            (target.Bottom - firstDestination.Top) /
            firstDestination.Height);
        int tileBudget = 4096;

        for (int y = startY; y < endY && tileBudget > 0; y++)
        {
            for (int x = startX; x < endX && tileBudget > 0; x++)
            {
                Rect tile = firstDestination.Translate(
                    new Vector(
                        x * firstDestination.Width,
                        y * firstDestination.Height));
                Rect tileSource = source;
                if (ShouldFlipX(brush.TileMode, x))
                {
                    tileSource = new Rect(
                        tileSource.Right,
                        tileSource.Y,
                        -tileSource.Width,
                        tileSource.Height);
                }
                if (ShouldFlipY(brush.TileMode, y))
                {
                    tileSource = new Rect(
                        tileSource.X,
                        tileSource.Bottom,
                        tileSource.Width,
                        -tileSource.Height);
                }
                DrawImageTile(
                    texture,
                    tileSource,
                    tile,
                    brush.Opacity);
                tileBudget--;
            }
        }
    }

    private void DrawImageTile(
        GpuTexture texture,
        Rect source,
        Rect destination,
        double opacity)
    {
        float alpha = ClampUnit(opacity);
        if (alpha < 1f)
            DrawingContext.PushOpacity(alpha);
        DrawingContext.DrawTexture(
            texture,
            ToLocalRect(destination),
            ToLocalRect(source),
            ToProGpuMatrix(CommandTransform),
            GetTextureSampling());
        MarkLastCommandPresentationDependencies(
            _presentationDependencies &
            RenderCommandPresentationDependencies.TextureSampling);
        if (alpha < 1f)
            DrawingContext.PopOpacity();
    }

    private bool DrawSceneBrush(
        ISceneBrush brush,
        Rect target,
        ProGPU.Vector.PathGeometry? geometryClip)
    {
        using ISceneBrushContent? content =
            brush.CreateContent();
        if (content is null ||
            content.Rect.Width <= 0 ||
            content.Rect.Height <= 0)
        {
            return false;
        }

        var mapping = new AvaloniaTileBrushMapping(
            brush,
            content.Rect.Size,
            target.Size);
        PushContentClip(target, geometryClip);
        Matrix saved = Transform;
        try
        {
            Matrix placement =
                Matrix.CreateTranslation(
                    target.X - content.Rect.X,
                    target.Y - content.Rect.Y) *
                mapping.IntermediateTransform *
                saved;
            content.Render(this, placement);
        }
        finally
        {
            Transform = saved;
            PopContentClip(geometryClip);
        }
        return true;
    }

    private void PushContentClip(
        Rect target,
        ProGPU.Vector.PathGeometry? geometryClip)
    {
        if (geometryClip is not null)
        {
            DrawingContext.PushGeometryClip(
                geometryClip,
                ToProGpuMatrix(CommandTransform));
        }
        else
        {
            DrawingContext.PushClip(
                ToLocalRect(target),
                ToProGpuMatrix(CommandTransform));
        }
    }

    private void PopContentClip(
        ProGPU.Vector.PathGeometry? geometryClip)
    {
        if (geometryClip is not null)
            DrawingContext.PopGeometryClip();
        else
            DrawingContext.PopClip();
    }

    private GpuPicture RecordBrushPicture(
        IBrush brush,
        Rect bounds)
    {
        var recorder = new GpuPictureRecorder();
        ProGPU.Scene.DrawingContext recorded =
            recorder.BeginRecording(ToLocalRect(bounds));
        ProGPU.Scene.DrawingContext owner = DrawingContext;
        Matrix savedTransform = _transform;
        try
        {
            DrawingContext = recorded;
            _transform = Matrix.Identity;
            TryDrawBrushContent(
                brush,
                bounds,
                geometryClip: null);
            return recorder.EndRecording();
        }
        finally
        {
            DrawingContext = owner;
            _transform = savedTransform;
        }
    }

    internal GpuPicture RecordRetainedCompositionOpacityMask(
        IBrush brush,
        Rect bounds) =>
        RecordBrushPicture(brush, bounds);

    private static VectorGradientStop[] ConvertStops(
        IGradientBrush source)
    {
        var result =
            new VectorGradientStop[source.GradientStops.Count];
        for (int index = 0; index < result.Length; index++)
        {
            IGradientStop stop = source.GradientStops[index];
            result[index] = new VectorGradientStop(
                ToColor(stop.Color),
                (float)stop.Offset);
        }
        return result;
    }

    private static Matrix GetBrushTransform(
        IBrush brush,
        Rect target)
    {
        if (brush.Transform is null)
            return Matrix.Identity;
        Point origin =
            brush.TransformOrigin.ToPixels(target.Size) +
            (Vector)target.Position;
        return
            Matrix.CreateTranslation(-(Vector)origin) *
            brush.Transform.Value *
            Matrix.CreateTranslation((Vector)origin);
    }

    private static VectorGradientSpread ToSpread(
        Avalonia.Media.GradientSpreadMethod spread) =>
        spread switch
        {
            Avalonia.Media.GradientSpreadMethod.Reflect =>
                VectorGradientSpread.Reflect,
            Avalonia.Media.GradientSpreadMethod.Repeat =>
                VectorGradientSpread.Repeat,
            _ => VectorGradientSpread.Pad
        };

    private static VectorPenJoin ToJoin(
        Avalonia.Media.PenLineJoin join) =>
        join switch
        {
            Avalonia.Media.PenLineJoin.Bevel =>
                VectorPenJoin.Bevel,
            Avalonia.Media.PenLineJoin.Round =>
                VectorPenJoin.Round,
            _ => VectorPenJoin.Miter
        };

    private static VectorPenCap ToCap(
        Avalonia.Media.PenLineCap cap) =>
        cap switch
        {
            Avalonia.Media.PenLineCap.Round =>
                VectorPenCap.Round,
            Avalonia.Media.PenLineCap.Square =>
                VectorPenCap.Square,
            _ => VectorPenCap.Flat
        };

    private static float FiniteMiter(double value) =>
        double.IsFinite(value) && value >= 1d
            ? (float)value
            : 1f;

    private static bool ShouldFlipX(TileMode mode, int x) =>
        (mode == TileMode.FlipX ||
         mode == TileMode.FlipXY) &&
        (x & 1) != 0;

    private static bool ShouldFlipY(TileMode mode, int y) =>
        (mode == TileMode.FlipY ||
         mode == TileMode.FlipXY) &&
        (y & 1) != 0;

    private static Vector2 ToVector(Vector value) =>
        new((float)value.X, (float)value.Y);
}
