using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using System;
using System.Numerics;
using ProGPU.Vector;
using ProGPU.Scene;

namespace Microsoft.UI.Xaml.Shapes;

public abstract class Shape : FrameworkElement
{
    public static readonly DependencyProperty FillProperty =
        DependencyProperty.Register(
            "Fill",
            typeof(Brush),
            typeof(Shape),
            new PropertyMetadata(null) { AffectsRender = true });

    public Brush? Fill
    {
        get => GetValue(FillProperty) as Brush;
        set => SetValue(FillProperty, value);
    }

    public static readonly DependencyProperty StrokeProperty =
        DependencyProperty.Register(
            "Stroke",
            typeof(Brush),
            typeof(Shape),
            new PropertyMetadata(null) { AffectsRender = true });

    public Brush? Stroke
    {
        get => GetValue(StrokeProperty) as Brush;
        set => SetValue(StrokeProperty, value);
    }

    public static readonly DependencyProperty StrokeThicknessProperty =
        DependencyProperty.Register(
            "StrokeThickness",
            typeof(float),
            typeof(Shape),
            new PropertyMetadata(1f) { AffectsMeasure = true, AffectsArrange = true, AffectsRender = true });

    public float StrokeThickness
    {
        get => (float)(GetValue(StrokeThicknessProperty) ?? 1f);
        set => SetValue(StrokeThicknessProperty, value);
    }

    public static readonly DependencyProperty StretchProperty =
        DependencyProperty.Register(
            "Stretch",
            typeof(Stretch),
            typeof(Shape),
            new PropertyMetadata(Stretch.None) { AffectsMeasure = true, AffectsArrange = true, AffectsRender = true });

    public Stretch Stretch
    {
        get => (Stretch)(GetValue(StretchProperty) ?? Stretch.None);
        set => SetValue(StretchProperty, value);
    }

    public abstract Geometry? DefiningGeometry { get; }

    protected override Vector2 MeasureOverride(Vector2 availableSize)
    {
        var geom = DefiningGeometry;
        if (geom == null) return Vector2.Zero;

        Rect bounds = geom.Bounds;
        // A vertical or horizontal line has width or height equal to 0, which is still a valid measurable shape
        if (bounds.Width < 0f || bounds.Height < 0f || (bounds.Width == 0f && bounds.Height == 0f))
        {
            return Vector2.Zero;
        }

        float thickness = Stroke != null ? StrokeThickness : 0f;
        float w = bounds.Width + thickness;
        float h = bounds.Height + thickness;

        // Take explicit framework sizes into account if specified
        float width = Width;
        float height = Height;

        if (!float.IsNaN(width)) w = width;
        if (!float.IsNaN(height)) h = height;

        return new Vector2(w, h);
    }

    protected override void ArrangeOverride(Rect arrangeRect)
    {
        base.ArrangeOverride(arrangeRect);
    }

    public override void OnRender(DrawingContext context)
    {
        var geom = DefiningGeometry;
        if (geom == null) return;

        Matrix4x4 flowMirror = FlowDirection == FlowDirection.RightToLeft
            ? Matrix4x4.CreateScale(-1f, 1f, 1f) * Matrix4x4.CreateTranslation(Size.X, 0f, 0f)
            : Matrix4x4.Identity;

        Pen? pen = null;
        if (Stroke != null && StrokeThickness > 0f)
        {
            pen = new Pen(Stroke, StrokeThickness);
        }

        // Fit defining geometry stretch factor if Stretch is not None
        if (Stretch != Stretch.None)
        {
            Rect bounds = geom.Bounds;
            if (!bounds.IsEmpty && bounds.Width > 0f && bounds.Height > 0f)
            {
                float targetW = Size.X;
                float targetH = Size.Y;
                float thickness = Stroke != null ? StrokeThickness : 0f;

                targetW = Math.Max(0f, targetW - thickness);
                targetH = Math.Max(0f, targetH - thickness);

                float scaleX = targetW / bounds.Width;
                float scaleY = targetH / bounds.Height;

                if (Stretch == Stretch.Uniform)
                {
                    scaleX = scaleY = Math.Min(scaleX, scaleY);
                }
                else if (Stretch == Stretch.UniformToFill)
                {
                    scaleX = scaleY = Math.Max(scaleX, scaleY);
                }

                // Compose temporary scaling onto the geometry's transform
                var localScale = Matrix4x4.CreateTranslation(-bounds.X, -bounds.Y, 0f) *
                                 Matrix4x4.CreateScale(scaleX, scaleY, 1f) *
                                 Matrix4x4.CreateTranslation(thickness / 2f, thickness / 2f, 0f);

                geom.ParentTransformMatrix = localScale * flowMirror;
                geom.Draw(context, Fill, pen);
                geom.ParentTransformMatrix = null;
                return;
            }
        }

        if (FlowDirection == FlowDirection.RightToLeft)
        {
            geom.ParentTransformMatrix = flowMirror;
        }
        geom.Draw(context, Fill, pen);
        geom.ParentTransformMatrix = null;
    }
}
