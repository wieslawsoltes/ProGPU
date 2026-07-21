using System;
using System.Numerics;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;
using ProGPU.Layout;
using ProGPU.Scene;

namespace Microsoft.UI.Xaml.Controls;

public enum StretchDirection
{
    UpOnly = 0,
    DownOnly = 1,
    Both = 2
}

/// <summary>
/// Scales one child without overwriting that child's own composition transform.
/// Measure and arrange perform fixed O(1) work in addition to the child layout pass.
/// </summary>
[ContentProperty(Name = nameof(Child))]
public sealed class Viewbox : FrameworkElement
{
    private readonly ViewboxPresenter _presenter = new();
    private Vector2 _naturalSize;

    public Viewbox()
    {
        AddChild(_presenter);
    }

    public static readonly DependencyProperty ChildProperty =
        DependencyProperty.Register(
            nameof(Child),
            typeof(UIElement),
            typeof(Viewbox),
            new PropertyMetadata(null, static (sender, args) =>
            {
                var viewbox = (Viewbox)sender;
                viewbox._presenter.Child = args.NewValue as UIElement;
                viewbox.InvalidateMeasure();
                viewbox.InvalidateArrange();
            }));

    public static readonly DependencyProperty StretchProperty =
        DependencyProperty.Register(
            nameof(Stretch),
            typeof(Stretch),
            typeof(Viewbox),
            new PropertyMetadata(Stretch.Uniform) { AffectsMeasure = true, AffectsArrange = true });

    public static readonly DependencyProperty StretchDirectionProperty =
        DependencyProperty.Register(
            nameof(StretchDirection),
            typeof(StretchDirection),
            typeof(Viewbox),
            new PropertyMetadata(StretchDirection.Both) { AffectsMeasure = true, AffectsArrange = true });

    public UIElement? Child
    {
        get => GetValue(ChildProperty) as UIElement;
        set => SetValue(ChildProperty, value);
    }

    public Stretch Stretch
    {
        get => (Stretch)(GetValue(StretchProperty) ?? Stretch.Uniform);
        set => SetValue(StretchProperty, value);
    }

    public StretchDirection StretchDirection
    {
        get => (StretchDirection)(GetValue(StretchDirectionProperty) ?? StretchDirection.Both);
        set => SetValue(StretchDirectionProperty, value);
    }

    protected override Vector2 MeasureOverride(Vector2 availableSize)
    {
        UIElement? child = Child;
        if (child is null)
        {
            _naturalSize = Vector2.Zero;
            return Vector2.Zero;
        }

        child.Measure(new Vector2(float.PositiveInfinity, float.PositiveInfinity));
        _naturalSize = child.DesiredSize;
        Vector2 scale = CalculateScale(availableSize, _naturalSize);
        Vector2 desired = _naturalSize * scale;
        if (float.IsFinite(availableSize.X)) desired.X = Math.Min(desired.X, availableSize.X);
        if (float.IsFinite(availableSize.Y)) desired.Y = Math.Min(desired.Y, availableSize.Y);
        return desired;
    }

    protected override void ArrangeOverride(Rect arrangeRect)
    {
        UIElement? child = Child;
        if (child is null || _naturalSize.X <= 0f || _naturalSize.Y <= 0f)
        {
            _presenter.Scale = Vector3.One;
            _presenter.Arrange(new Rect(arrangeRect.X, arrangeRect.Y, 0f, 0f));
            ClipBounds = null;
            return;
        }

        Vector2 scale = CalculateScale(new Vector2(arrangeRect.Width, arrangeRect.Height), _naturalSize);
        float renderedWidth = _naturalSize.X * scale.X;
        float renderedHeight = _naturalSize.Y * scale.Y;
        float x = arrangeRect.X + (arrangeRect.Width - renderedWidth) / 2f;
        float y = arrangeRect.Y + (arrangeRect.Height - renderedHeight) / 2f;

        _presenter.Scale = new Vector3(scale, 1f);
        _presenter.RenderTransformOrigin = Vector2.Zero;
        _presenter.Arrange(new Rect(x, y, _naturalSize.X, _naturalSize.Y));
        ClipBounds = new Rect(0f, 0f, Size.X, Size.Y);
    }

    private Vector2 CalculateScale(Vector2 available, Vector2 natural)
    {
        if (natural.X <= 0f || natural.Y <= 0f || Stretch == Stretch.None)
            return Vector2.One;

        float? ratioX = float.IsFinite(available.X) ? Math.Max(0f, available.X) / natural.X : null;
        float? ratioY = float.IsFinite(available.Y) ? Math.Max(0f, available.Y) / natural.Y : null;
        float sx;
        float sy;

        if (Stretch == Stretch.Fill)
        {
            sx = ratioX ?? 1f;
            sy = ratioY ?? 1f;
        }
        else
        {
            float uniform = ratioX.HasValue && ratioY.HasValue
                ? Stretch == Stretch.Uniform ? Math.Min(ratioX.Value, ratioY.Value) : Math.Max(ratioX.Value, ratioY.Value)
                : ratioX ?? ratioY ?? 1f;
            sx = uniform;
            sy = uniform;
        }

        if (StretchDirection == StretchDirection.UpOnly)
        {
            sx = Math.Max(1f, sx);
            sy = Math.Max(1f, sy);
        }
        else if (StretchDirection == StretchDirection.DownOnly)
        {
            sx = Math.Min(1f, sx);
            sy = Math.Min(1f, sy);
        }
        return new Vector2(sx, sy);
    }

    private sealed class ViewboxPresenter : FrameworkElement
    {
        private UIElement? _child;

        public UIElement? Child
        {
            get => _child;
            set
            {
                if (ReferenceEquals(_child, value)) return;
                if (_child is not null) RemoveChild(_child);
                _child = value;
                if (_child is not null) AddChild(_child);
                InvalidateMeasure();
            }
        }

        protected override Vector2 MeasureOverride(Vector2 availableSize)
        {
            if (_child is null) return Vector2.Zero;
            _child.Measure(availableSize);
            return _child.DesiredSize;
        }

        protected override void ArrangeOverride(Rect arrangeRect)
        {
            _child?.Arrange(new Rect(arrangeRect.X, arrangeRect.Y, arrangeRect.Width, arrangeRect.Height));
        }
    }
}
