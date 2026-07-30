using Microsoft.UI.Xaml.Media;
using ProGPU.Scene;
using ProGPU.Vector;

namespace Microsoft.UI.Xaml.Controls.Primitives;

/// <summary>Represents a slider tick mark supplied by a control template.</summary>
public sealed class TickBar : FrameworkElement
{
    public static readonly DependencyProperty FillProperty = DependencyProperty.Register(
        nameof(Fill), typeof(Brush), typeof(TickBar),
        new PropertyMetadata(null) { AffectsRender = true });

    public Brush? Fill
    {
        get => GetValue(FillProperty) as Brush;
        set => SetValue(FillProperty, value);
    }

    public override void OnRender(DrawingContext context)
    {
        if (Fill != null)
        {
            context.DrawRectangle(Fill, null, new Rect(global::System.Numerics.Vector2.Zero, Size));
        }

        base.OnRender(context);
    }
}
