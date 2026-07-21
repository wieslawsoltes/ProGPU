using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System;
using System.Numerics;
using ProGPU.Layout;
using ProGPU.Vector;
using ProGPU.Scene;

namespace Microsoft.UI.Xaml.Controls;

public class PathIcon : IconElement
{
    private PathGeometry? _parsedGeometry;

    public static readonly DependencyProperty DataProperty =
        DependencyProperty.Register(
            "Data",
            typeof(object),
            typeof(PathIcon),
            new PropertyMetadata(null, (d, e) => {
                var pi = (PathIcon)d;
                pi.UpdateGeometry();
                pi.InvalidateMeasure();
                pi.Invalidate();
            }));

    public object? Data
    {
        get => GetValue(DataProperty);
        set => SetValue(DataProperty, value);
    }

    public PathIcon()
    {
        // Default bounds
        WidthConstraint = 20f;
        HeightConstraint = 20f;
    }

    private void UpdateGeometry()
    {
        var data = Data;
        if (data is PathGeometry pg)
        {
            _parsedGeometry = pg;
        }
        else if (data is string s)
        {
            try
            {
                _parsedGeometry = PathGeometry.Parse(s);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[PathIcon] Error parsing path: {ex.Message}");
                _parsedGeometry = null;
            }
        }
        else
        {
            _parsedGeometry = null;
        }
    }

    protected override Vector2 MeasureOverride(Vector2 availableSize)
    {
        float w = WidthConstraint ?? 20f;
        float h = HeightConstraint ?? 20f;
        return new Vector2(w, h);
    }

    protected override void ArrangeOverride(Rect arrangeRect)
    {
        Size = new Vector2(arrangeRect.Width, arrangeRect.Height);
    }

    public override void OnRender(DrawingContext context)
    {
        base.OnRender(context);
        if (_parsedGeometry != null)
        {
            var brush = GetCurrentForeground() ?? ThemeManager.GetBrush("TextPrimary");
            if (FlowDirection == FlowDirection.RightToLeft)
            {
                Matrix4x4 mirror = Matrix4x4.CreateScale(-1f, 1f, 1f) *
                    Matrix4x4.CreateTranslation(Size.X, 0f, 0f);
                context.DrawPath(brush, null, _parsedGeometry, mirror);
            }
            else
            {
                context.DrawPath(brush, null, _parsedGeometry);
            }
        }
    }
}
