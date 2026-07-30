using System.Numerics;
using ProGPU.Scene;
using ProGPU.Vector;

namespace Microsoft.UI.Xaml.Controls;

/// <summary>
/// Renders one or two bounded, append-only value series without allocating on
/// the steady-state render path.
/// </summary>
public class Sparkline : Control
{
    private float[] _primaryValues = new float[120];
    private float[] _secondaryValues = new float[120];
    private Vector2[] _primaryPoints = new Vector2[120];
    private Vector2[] _secondaryPoints = new Vector2[120];
    private int _count;

    public static readonly DependencyProperty CapacityProperty =
        DependencyProperty.Register(
            nameof(Capacity),
            typeof(int),
            typeof(Sparkline),
            new PropertyMetadata(120, OnCapacityChanged)
            {
                AffectsMeasure = true,
                AffectsRender = true
            });

    public static readonly DependencyProperty PrimaryStrokeProperty =
        DependencyProperty.Register(
            nameof(PrimaryStroke),
            typeof(Brush),
            typeof(Sparkline),
            new PropertyMetadata(null) { AffectsRender = true });

    public static readonly DependencyProperty SecondaryStrokeProperty =
        DependencyProperty.Register(
            nameof(SecondaryStroke),
            typeof(Brush),
            typeof(Sparkline),
            new PropertyMetadata(null) { AffectsRender = true });

    public static readonly DependencyProperty StrokeThicknessProperty =
        DependencyProperty.Register(
            nameof(StrokeThickness),
            typeof(float),
            typeof(Sparkline),
            new PropertyMetadata(1.5f) { AffectsRender = true });

    public static readonly DependencyProperty ShowHorizontalGridLineProperty =
        DependencyProperty.Register(
            nameof(ShowHorizontalGridLine),
            typeof(bool),
            typeof(Sparkline),
            new PropertyMetadata(true) { AffectsRender = true });

    public Sparkline()
    {
        MinHeight = 72;
        HorizontalAlignment = HorizontalAlignment.Stretch;
        VerticalAlignment = VerticalAlignment.Stretch;
        Background = new ThemeResourceBrush("CardBackground");
        BorderBrush = new ThemeResourceBrush("ControlBorder");
        BorderThickness = new Thickness(1);
        PrimaryStroke = new ThemeResourceBrush("SystemAccentColor");
        SecondaryStroke = new ThemeResourceBrush("TabViewItemCloseHover");
    }

    public int Capacity
    {
        get => (int)(GetValue(CapacityProperty) ?? 120);
        set => SetValue(CapacityProperty, Math.Max(2, value));
    }

    public Brush? PrimaryStroke
    {
        get => GetValue(PrimaryStrokeProperty) as Brush;
        set => SetValue(PrimaryStrokeProperty, value);
    }

    public Brush? SecondaryStroke
    {
        get => GetValue(SecondaryStrokeProperty) as Brush;
        set => SetValue(SecondaryStrokeProperty, value);
    }

    public float StrokeThickness
    {
        get => (float)(GetValue(StrokeThicknessProperty) ?? 1.5f);
        set => SetValue(StrokeThicknessProperty, Math.Max(0, value));
    }

    public bool ShowHorizontalGridLine
    {
        get => (bool)(GetValue(ShowHorizontalGridLineProperty) ?? true);
        set => SetValue(ShowHorizontalGridLineProperty, value);
    }

    public int ValueCount => _count;

    public void Append(double primaryValue, double secondaryValue = 0)
    {
        int capacity = _primaryValues.Length;
        if (_count < capacity)
        {
            _primaryValues[_count] = NormalizeValue(primaryValue);
            _secondaryValues[_count] = NormalizeValue(secondaryValue);
            _count++;
        }
        else
        {
            Array.Copy(_primaryValues, 1, _primaryValues, 0, capacity - 1);
            Array.Copy(_secondaryValues, 1, _secondaryValues, 0, capacity - 1);
            _primaryValues[^1] = NormalizeValue(primaryValue);
            _secondaryValues[^1] = NormalizeValue(secondaryValue);
        }
        Invalidate();
    }

    public void Clear()
    {
        _count = 0;
        Array.Clear(_primaryValues);
        Array.Clear(_secondaryValues);
        Invalidate();
    }

    public override void OnRender(DrawingContext context)
    {
        Rect bounds = new(Vector2.Zero, Size);
        context.DrawRectangle(
            Background,
            BorderBrush is null || BorderThickness.Left <= 0
                ? null
                : new Pen(BorderBrush, BorderThickness.Left),
            bounds);
        if (_count < 2 || Size.X <= 2 || Size.Y <= 2)
        {
            return;
        }

        if (ShowHorizontalGridLine && BorderBrush is not null)
        {
            context.DrawLine(
                new Pen(BorderBrush, 0.5f),
                new Vector2(0, Size.Y * 0.5f),
                new Vector2(Size.X, Size.Y * 0.5f));
        }

        float maximum = 1;
        for (int index = 0; index < _count; index++)
        {
            maximum = Math.Max(
                maximum,
                Math.Max(_primaryValues[index], _secondaryValues[index]));
        }

        float xStep = Size.X / Math.Max(1, Capacity - 1);
        float startX = Size.X - (_count - 1) * xStep;
        float height = Math.Max(1, Size.Y - 4);
        for (int index = 0; index < _count; index++)
        {
            float x = startX + index * xStep;
            _primaryPoints[index] = new Vector2(
                x,
                Size.Y - 2 - _primaryValues[index] / maximum * height);
            _secondaryPoints[index] = new Vector2(
                x,
                Size.Y - 2 - _secondaryValues[index] / maximum * height);
        }

        context.PushClip(bounds);
        if (PrimaryStroke is not null && StrokeThickness > 0)
        {
            context.DrawPolyline(
                new Pen(PrimaryStroke, StrokeThickness),
                _primaryPoints.AsSpan(0, _count));
        }
        if (SecondaryStroke is not null && StrokeThickness > 0)
        {
            context.DrawPolyline(
                new Pen(SecondaryStroke, StrokeThickness),
                _secondaryPoints.AsSpan(0, _count));
        }
        context.PopClip();
    }

    private void ResizeBuffers(int capacity)
    {
        capacity = Math.Max(2, capacity);
        if (_primaryValues.Length == capacity)
        {
            return;
        }

        int copyCount = Math.Min(_count, capacity);
        int sourceOffset = Math.Max(0, _count - copyCount);
        var primary = new float[capacity];
        var secondary = new float[capacity];
        Array.Copy(_primaryValues, sourceOffset, primary, 0, copyCount);
        Array.Copy(_secondaryValues, sourceOffset, secondary, 0, copyCount);
        _primaryValues = primary;
        _secondaryValues = secondary;
        _primaryPoints = new Vector2[capacity];
        _secondaryPoints = new Vector2[capacity];
        _count = copyCount;
        Invalidate();
    }

    private static float NormalizeValue(double value) =>
        double.IsFinite(value)
            ? (float)Math.Clamp(value, 0, float.MaxValue)
            : 0;

    private static void OnCapacityChanged(
        DependencyObject dependencyObject,
        DependencyPropertyChangedEventArgs args)
    {
        ((Sparkline)dependencyObject).ResizeBuffers((int)(args.NewValue ?? 120));
    }
}
