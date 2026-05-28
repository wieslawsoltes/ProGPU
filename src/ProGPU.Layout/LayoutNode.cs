using System;
using System.Numerics;
using ProGPU.Scene;

namespace ProGPU.Layout;

public enum HorizontalAlignment
{
    Left,
    Center,
    Right,
    Stretch
}

public enum VerticalAlignment
{
    Top,
    Center,
    Bottom,
    Stretch
}

public struct Thickness
{
    public float Left;
    public float Top;
    public float Right;
    public float Bottom;

    public float Horizontal => Left + Right;
    public float Vertical => Top + Bottom;

    public Thickness(float uniform)
    {
        Left = Top = Right = Bottom = uniform;
    }

    public Thickness(float horizontal, float vertical)
    {
        Left = Right = horizontal;
        Top = Bottom = vertical;
    }

    public Thickness(float left, float top, float right, float bottom)
    {
        Left = left;
        Top = top;
        Right = right;
        Bottom = bottom;
    }

    public bool Equals(Thickness other)
    {
        return Left == other.Left && Top == other.Top && Right == other.Right && Bottom == other.Bottom;
    }

    public override bool Equals(object? obj)
    {
        return obj is Thickness other && Equals(other);
    }

    public override int GetHashCode()
    {
        return HashCode.Combine(Left, Top, Right, Bottom);
    }

    public static bool operator ==(Thickness left, Thickness right)
    {
        return left.Equals(right);
    }

    public static bool operator !=(Thickness left, Thickness right)
    {
        return !left.Equals(right);
    }

    public static Thickness Parse(string s)
    {
        if (string.IsNullOrEmpty(s)) return default;
        var parts = s.Split(new[] { ',', ' ' }, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 1)
        {
            float val = float.Parse(parts[0], System.Globalization.CultureInfo.InvariantCulture);
            return new Thickness(val);
        }
        if (parts.Length == 2)
        {
            float h = float.Parse(parts[0], System.Globalization.CultureInfo.InvariantCulture);
            float v = float.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture);
            return new Thickness(h, v);
        }
        if (parts.Length == 4)
        {
            float l = float.Parse(parts[0], System.Globalization.CultureInfo.InvariantCulture);
            float t = float.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture);
            float r = float.Parse(parts[2], System.Globalization.CultureInfo.InvariantCulture);
            float b = float.Parse(parts[3], System.Globalization.CultureInfo.InvariantCulture);
            return new Thickness(l, t, r, b);
        }
        throw new FormatException($"Invalid Thickness format: '{s}'");
    }
}

public class LayoutNode : ContainerVisual, ILayoutNode
{
    private Thickness _margin;
    private Thickness _padding;
    private HorizontalAlignment _horizontalAlignment = HorizontalAlignment.Stretch;
    private VerticalAlignment _verticalAlignment = VerticalAlignment.Stretch;
    private float? _widthConstraint;
    private float? _heightConstraint;
    private bool _isCollapsed = false;

    private bool _isMeasureValid;
    private bool _isArrangeValid;
    private Vector2 _previousAvailableSize = new Vector2(-1f, -1f);
    private Rect _previousFinalRect = new Rect(-1f, -1f, -1f, -1f);

    public virtual Thickness Margin
    {
        get => _margin;
        set
        {
            if (!_margin.Equals(value))
            {
                _margin = value;
                InvalidateMeasure();
            }
        }
    }

    public virtual Thickness Padding
    {
        get => _padding;
        set
        {
            if (!_padding.Equals(value))
            {
                _padding = value;
                InvalidateMeasure();
            }
        }
    }

    public bool IsCollapsed
    {
        get => _isCollapsed;
        set
        {
            if (_isCollapsed != value)
            {
                _isCollapsed = value;
                InvalidateMeasure();
                InvalidateArrange();
            }
        }
    }

    public virtual HorizontalAlignment HorizontalAlignment
    {
        get => _horizontalAlignment;
        set
        {
            if (_horizontalAlignment != value)
            {
                _horizontalAlignment = value;
                InvalidateArrange();
            }
        }
    }

    public virtual VerticalAlignment VerticalAlignment
    {
        get => _verticalAlignment;
        set
        {
            if (_verticalAlignment != value)
            {
                _verticalAlignment = value;
                InvalidateArrange();
            }
        }
    }

    public virtual float? WidthConstraint
    {
        get => _widthConstraint;
        set
        {
            if (_widthConstraint != value)
            {
                _widthConstraint = value;
                InvalidateMeasure();
            }
        }
    }

    public virtual float? HeightConstraint
    {
        get => _heightConstraint;
        set
        {
            if (_heightConstraint != value)
            {
                _heightConstraint = value;
                InvalidateMeasure();
            }
        }
    }

    public Vector2 DesiredSize { get; protected set; }

    public void InvalidateMeasure()
    {
        if (_isMeasureValid)
        {
            _isMeasureValid = false;
            _isArrangeValid = false;
            if (Parent is LayoutNode parentNode)
            {
                parentNode.InvalidateMeasure();
            }
        }
    }

    public void InvalidateArrange()
    {
        if (_isArrangeValid)
        {
            _isArrangeValid = false;
            if (Parent is LayoutNode parentNode)
            {
                parentNode.InvalidateArrange();
            }
        }
    }

    public void Measure(Vector2 availableSize)
    {
        if (IsCollapsed)
        {
            DesiredSize = Vector2.Zero;
            _previousAvailableSize = availableSize;
            _isMeasureValid = true;
            return;
        }

        if (_isMeasureValid && availableSize == _previousAvailableSize)
        {
            return;
        }

        _isArrangeValid = false;

        // 1. Account for Margin
        float marginH = Margin.Horizontal;
        float marginV = Margin.Vertical;

        float width = Math.Max(0f, availableSize.X - marginH);
        float height = Math.Max(0f, availableSize.Y - marginV);

        // 2. Apply explicit user-defined constraints
        if (WidthConstraint.HasValue) width = Math.Min(width, WidthConstraint.Value);
        if (HeightConstraint.HasValue) height = Math.Min(height, HeightConstraint.Value);

        // 3. Delegate to core measure override or template
        Vector2 childrenAvailableSize = new Vector2(width, height);
        Vector2 desired;
        bool hasTemplate = false;
        if (this is ITemplatedControl templated && templated.HasTemplate)
        {
            hasTemplate = true;
            desired = templated.MeasureTemplate(childrenAvailableSize);
        }
        else
        {
            desired = MeasureOverride(childrenAvailableSize);
        }

        // Add padding BEFORE applying constraints
        if (!hasTemplate)
        {
            desired.X += Padding.Horizontal;
            desired.Y += Padding.Vertical;
        }

        // 4. Re-apply explicit dimensions constraints
        if (WidthConstraint.HasValue) desired.X = WidthConstraint.Value;
        if (HeightConstraint.HasValue) desired.Y = HeightConstraint.Value;

        // Add margin back to desired size
        desired.X += marginH;
        desired.Y += marginV;

        DesiredSize = desired;
        _previousAvailableSize = availableSize;
        _isMeasureValid = true;
    }

    public void Arrange(Rect finalRect)
    {
        if (IsCollapsed)
        {
            Size = Vector2.Zero;
            _previousFinalRect = finalRect;
            _isArrangeValid = true;
            return;
        }

        if (_isArrangeValid && _isMeasureValid && finalRect == _previousFinalRect)
        {
            return;
        }

        // 1. Subtract Margin to get visual space
        float marginL = Margin.Left;
        float marginT = Margin.Top;

        float visualWidth = Math.Max(0f, finalRect.Width - Margin.Horizontal);
        float visualHeight = Math.Max(0f, finalRect.Height - Margin.Vertical);

        Vector2 size = new Vector2(visualWidth, visualHeight);

        // 2. Calculate alignment dimensions
        Vector2 arrangeSize = DesiredSize - new Vector2(Margin.Horizontal, Margin.Vertical);

        if (HorizontalAlignment != HorizontalAlignment.Stretch || WidthConstraint.HasValue)
        {
            size.X = Math.Min(size.X, arrangeSize.X);
        }
        if (VerticalAlignment != VerticalAlignment.Stretch || HeightConstraint.HasValue)
        {
            size.Y = Math.Min(size.Y, arrangeSize.Y);
        }

        // 3. Determine positioning offset based on alignments
        Vector2 offset = new Vector2(finalRect.X + marginL, finalRect.Y + marginT);

        if (HorizontalAlignment == HorizontalAlignment.Center)
        {
            offset.X += (visualWidth - size.X) / 2f;
        }
        else if (HorizontalAlignment == HorizontalAlignment.Right)
        {
            offset.X += visualWidth - size.X;
        }

        if (VerticalAlignment == VerticalAlignment.Center)
        {
            offset.Y += (visualHeight - size.Y) / 2f;
        }
        else if (VerticalAlignment == VerticalAlignment.Bottom)
        {
            offset.Y += visualHeight - size.Y;
        }

        // Apply placement and delegate to arrange override
        Offset = offset;
        Size = size;

        if (this is ITemplatedControl templated && templated.HasTemplate)
        {
            Rect arrangeRect = new Rect(0f, 0f, size.X, size.Y);
            templated.ArrangeTemplate(arrangeRect);
        }
        else
        {
            Rect arrangeRect = new Rect(Padding.Left, Padding.Top, Math.Max(0f, size.X - Padding.Horizontal), Math.Max(0f, size.Y - Padding.Vertical));
            ArrangeOverride(arrangeRect);
        }
        
        _previousFinalRect = finalRect;
        _isArrangeValid = true;
    }

    protected virtual Vector2 MeasureOverride(Vector2 availableSize)
    {
        // Default simply measures children with available size and returns zero/minimal size
        foreach (var child in Children)
        {
            if (child is LayoutNode node)
            {
                node.Measure(availableSize);
            }
        }
        return Vector2.Zero;
    }

    protected virtual void ArrangeOverride(Rect arrangeRect)
    {
        // Default arranges all children in the top-left area
        foreach (var child in Children)
        {
            if (child is LayoutNode node)
            {
                node.Arrange(new Rect(arrangeRect.X, arrangeRect.Y, arrangeRect.Width, arrangeRect.Height));
            }
        }
    }
}
