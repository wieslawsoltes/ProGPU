using Microsoft.UI.Xaml;
using System.Numerics;

namespace Microsoft.UI.Xaml.Media;

public sealed class CompositeTransform : Transform
{
    public static readonly DependencyProperty CenterXProperty = Register(nameof(CenterX), 0d);
    public static readonly DependencyProperty CenterYProperty = Register(nameof(CenterY), 0d);
    public static readonly DependencyProperty RotationProperty = Register(nameof(Rotation), 0d);
    public static readonly DependencyProperty ScaleXProperty = Register(nameof(ScaleX), 1d);
    public static readonly DependencyProperty ScaleYProperty = Register(nameof(ScaleY), 1d);
    public static readonly DependencyProperty SkewXProperty = Register(nameof(SkewX), 0d);
    public static readonly DependencyProperty SkewYProperty = Register(nameof(SkewY), 0d);
    public static readonly DependencyProperty TranslateXProperty = Register(nameof(TranslateX), 0d);
    public static readonly DependencyProperty TranslateYProperty = Register(nameof(TranslateY), 0d);

    public double CenterX { get => GetDouble(CenterXProperty); set => SetValue(CenterXProperty, value); }
    public double CenterY { get => GetDouble(CenterYProperty); set => SetValue(CenterYProperty, value); }
    public new double Rotation { get => GetDouble(RotationProperty); set => SetValue(RotationProperty, value); }
    public double ScaleX { get => GetDouble(ScaleXProperty, 1d); set => SetValue(ScaleXProperty, value); }
    public double ScaleY { get => GetDouble(ScaleYProperty, 1d); set => SetValue(ScaleYProperty, value); }
    public double SkewX { get => GetDouble(SkewXProperty); set => SetValue(SkewXProperty, value); }
    public double SkewY { get => GetDouble(SkewYProperty); set => SetValue(SkewYProperty, value); }
    public double TranslateX { get => GetDouble(TranslateXProperty); set => SetValue(TranslateXProperty, value); }
    public double TranslateY { get => GetDouble(TranslateYProperty); set => SetValue(TranslateYProperty, value); }

    private double GetDouble(DependencyProperty property, double fallback = 0d) =>
        (double)(GetValue(property) ?? fallback);

    private static DependencyProperty Register(string name, double defaultValue) =>
        DependencyProperty.Register(
            name,
            typeof(double),
            typeof(CompositeTransform),
            new PropertyMetadata(defaultValue)
            {
                AffectsMeasure = true,
                AffectsArrange = true,
                AffectsRender = true
            });

    public override Matrix4x4 Value
    {
        get
        {
            var center = Matrix4x4.CreateTranslation((float)-CenterX, (float)-CenterY, 0f);
            var restore = Matrix4x4.CreateTranslation((float)(CenterX + TranslateX), (float)(CenterY + TranslateY), 0f);
            var scale = Matrix4x4.CreateScale((float)ScaleX, (float)ScaleY, 1f);
            var skew = Matrix4x4.Identity;
            skew.M12 = MathF.Tan((float)(SkewY * Math.PI / 180d));
            skew.M21 = MathF.Tan((float)(SkewX * Math.PI / 180d));
            var rotation = Matrix4x4.CreateRotationZ((float)(Rotation * Math.PI / 180d));
            return center * scale * skew * rotation * restore;
        }
    }
}
