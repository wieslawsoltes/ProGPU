using System;
using System.Numerics;
using ProGPU.Layout;
using ProGPU.Vector;
using ProGPU.Scene;

namespace ProGPU.WinUI;

public class Control : FrameworkElement
{
    private Brush? _background;
    private Brush? _foreground;
    private Brush? _borderBrush;
    private Thickness _borderThickness;
    private float _cornerRadius;
    private HorizontalAlignment _horizontalContentAlignment = HorizontalAlignment.Center;
    private VerticalAlignment _verticalContentAlignment = VerticalAlignment.Center;

    private bool _isPointerOver;
    private bool _isPointerPressed;
    private bool _isFocused;

    public Brush? Background
    {
        get => _background;
        set { if (_background != value) { _background = value; Invalidate(); } }
    }

    public Brush? Foreground
    {
        get => _foreground;
        set { if (_foreground != value) { _foreground = value; Invalidate(); } }
    }

    public Brush? BorderBrush
    {
        get => _borderBrush;
        set { if (_borderBrush != value) { _borderBrush = value; Invalidate(); } }
    }

    public Thickness BorderThickness
    {
        get => _borderThickness;
        set { if (!_borderThickness.Equals(value)) { _borderThickness = value; Invalidate(); } }
    }

    public float CornerRadius
    {
        get => _cornerRadius;
        set { if (_cornerRadius != value) { _cornerRadius = value; Invalidate(); } }
    }

    public HorizontalAlignment HorizontalContentAlignment
    {
        get => _horizontalContentAlignment;
        set { if (_horizontalContentAlignment != value) { _horizontalContentAlignment = value; Invalidate(); } }
    }

    public VerticalAlignment VerticalContentAlignment
    {
        get => _verticalContentAlignment;
        set { if (_verticalContentAlignment != value) { _verticalContentAlignment = value; Invalidate(); } }
    }

    public bool IsPointerOver
    {
        get => _isPointerOver;
        protected set { if (_isPointerOver != value) { _isPointerOver = value; OnVisualStateChanged(); } }
    }

    public bool IsPointerPressed
    {
        get => _isPointerPressed;
        protected set { if (_isPointerPressed != value) { _isPointerPressed = value; OnVisualStateChanged(); } }
    }

    public bool IsFocused
    {
        get => _isFocused;
        internal set { if (_isFocused != value) { _isFocused = value; OnVisualStateChanged(); } }
    }

    private bool _isTabStop = true;
    public bool IsTabStop
    {
        get => _isTabStop;
        set { _isTabStop = value; }
    }

    public virtual void OnVisualStateChanged()
    {
        Invalidate();
    }

    public override void OnPointerEntered(PointerRoutedEventArgs e)
    {
        if (IsEnabled)
        {
            IsPointerOver = true;
        }
        base.OnPointerEntered(e);
    }

    public override void OnPointerExited(PointerRoutedEventArgs e)
    {
        if (IsEnabled)
        {
            IsPointerOver = false;
            IsPointerPressed = false;
        }
        base.OnPointerExited(e);
    }

    public override void OnPointerPressed(PointerRoutedEventArgs e)
    {
        if (IsEnabled)
        {
            IsPointerPressed = true;
            // Also acquire focus if we are hit-test visible and enabled
            if (IsHitTestVisible)
            {
                InputSystem.SetFocus(this);
            }
        }
        base.OnPointerPressed(e);
    }

    public override void OnPointerReleased(PointerRoutedEventArgs e)
    {
        if (IsEnabled)
        {
            IsPointerPressed = false;
        }
        base.OnPointerReleased(e);
    }

    public override void OnRender(DrawingContext context)
    {
        base.OnRender(context);

        // Draw dynamic high-contrast keyboard focus visual borders 2px outside the control
        if (IsFocused && InputSystem.IsKeyboardFocusActive)
        {
            var accentColor = ThemeManager.GetBrush("SystemAccentColor");
            context.DrawRectangle(null, new Pen(accentColor, 1.5f), new Rect(-2f, -2f, Size.X + 4f, Size.Y + 4f));
        }
    }
}
