using System.Numerics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using ProGPU.Vector;

namespace ProGPU.Samples.Suntrail.Presentation;

public enum TouchLayout { FloatingStick, FixedStick, Buttons }

/// <summary>One captured thumb controls horizontal movement and optional outer-ring sprint.</summary>
public sealed class TouchStick : Grid
{
    private readonly Border _base, _thumb;
    private uint? _pointer;
    private Vector2 _origin;
    public bool Floating { get; set; } = true;
    public bool AutoSprint { get; set; } = true;
    public float Axis { get; private set; }
    public bool Sprint { get; private set; }
    public event Action? InputChanged;

    public TouchStick()
    {
        Background = new ThemeResourceBrush("SuntrailTransparent");
        _base = new Border { Width = 112, Height = 112, CornerRadius = new(56), BorderThickness = new(2),
            BorderBrush = new ThemeResourceBrush("SuntrailCream"), Background = new ThemeResourceBrush("SuntrailButton"),
            HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, IsHitTestVisible = false };
        _thumb = new Border { Width = 48, Height = 48, CornerRadius = new(24), Background = new ThemeResourceBrush("SuntrailCream"),
            HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, IsHitTestVisible = false };
        AddChild(_base); AddChild(_thumb); UpdateVisual();
    }

    public override void OnPointerPressed(PointerRoutedEventArgs e)
    {
        if (_pointer.HasValue) return;
        _pointer = e.Pointer.PointerId; CapturePointer(e.Pointer);
        var p = e.GetCurrentPoint(this).Position;
        _origin = Floating ? new((float)Math.Clamp(p.X, 56, Math.Max(56, Size.X - 56)), (float)Math.Clamp(p.Y, 56, Math.Max(56, Size.Y - 56))) : new(76, Size.Y / 2);
        Update(p); e.Handled = true;
    }

    public override void OnPointerMoved(PointerRoutedEventArgs e)
    {
        if (_pointer != e.Pointer.PointerId) return;
        Update(e.GetCurrentPoint(this).Position); e.Handled = true;
    }

    private void Update(Vector2 p)
    {
        float raw = Math.Clamp((p.X - _origin.X) / 48, -1, 1);
        Axis = MathF.Abs(raw) < .15f ? 0 : MathF.CopySign((MathF.Abs(raw) - .15f) / .85f, raw);
        Sprint = AutoSprint && MathF.Abs(raw) > .85f;
        UpdateVisual(); InputChanged?.Invoke();
    }

    private void UpdateVisual()
    {
        var origin = _pointer.HasValue ? _origin : new Vector2(76, Math.Max(75, Size.Y / 2));
        _base.Margin = new(origin.X - 56, origin.Y - 56, 0, 0);
        _thumb.Margin = new(origin.X - 24 + Axis * 44, origin.Y - 24, 0, 0);
        _base.Opacity = _pointer.HasValue ? .75f : .28f;
        _thumb.Opacity = _pointer.HasValue ? .95f : .42f;
    }

    protected override void ArrangeOverride(ProGPU.Scene.Rect arrangeRect)
    {
        if (!_pointer.HasValue) UpdateVisual();
        base.ArrangeOverride(arrangeRect);
    }

    public override void OnPointerReleased(PointerRoutedEventArgs e) => Release(e);
    public override void OnPointerCanceled(PointerRoutedEventArgs e) => Release(e);
    public override void OnPointerCaptureLost(PointerRoutedEventArgs e) => Release(e);
    private void Release(PointerRoutedEventArgs e)
    {
        if (_pointer != e.Pointer.PointerId) return;
        Reset(); e.Handled = true;
    }
    public void Reset()
    {
        _pointer = null; ReleasePointerCaptures(); Axis = 0; Sprint = false; UpdateVisual(); InputChanged?.Invoke();
    }
}
