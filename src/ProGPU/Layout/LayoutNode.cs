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
}

public class LayoutNode : ContainerVisual
{
    public Thickness Margin { get; set; }
    public Thickness Padding { get; set; }
    
    public HorizontalAlignment HorizontalAlignment { get; set; } = HorizontalAlignment.Stretch;
    public VerticalAlignment VerticalAlignment { get; set; } = VerticalAlignment.Stretch;

    public float? WidthConstraint { get; set; }
    public float? HeightConstraint { get; set; }

    public Vector2 DesiredSize { get; protected set; }

    public void Measure(Vector2 availableSize)
    {
        // 1. Account for Margin
        float marginH = Margin.Horizontal;
        float marginV = Margin.Vertical;

        float width = Math.Max(0f, availableSize.X - marginH);
        float height = Math.Max(0f, availableSize.Y - marginV);

        // 2. Apply explicit user-defined constraints
        if (WidthConstraint.HasValue) width = Math.Min(width, WidthConstraint.Value);
        if (HeightConstraint.HasValue) height = Math.Min(height, HeightConstraint.Value);

        // 3. Delegate to core measure override
        Vector2 childrenAvailableSize = new Vector2(width, height);
        Vector2 desired = MeasureOverride(childrenAvailableSize);

        // 4. Re-apply explicit dimensions and Padding
        if (WidthConstraint.HasValue) desired.X = WidthConstraint.Value;
        if (HeightConstraint.HasValue) desired.Y = HeightConstraint.Value;

        // Add padding
        desired.X += Padding.Horizontal;
        desired.Y += Padding.Vertical;

        // Add margin back to desired size
        desired.X += marginH;
        desired.Y += marginV;

        DesiredSize = desired;
    }

    public void Arrange(Rect finalRect)
    {
        // 1. Subtract Margin to get visual space
        float marginL = Margin.Left;
        float marginT = Margin.Top;

        float visualWidth = Math.Max(0f, finalRect.Width - Margin.Horizontal);
        float visualHeight = Math.Max(0f, finalRect.Height - Margin.Vertical);

        Vector2 size = new Vector2(visualWidth, visualHeight);

        // 2. Calculate alignment dimensions
        Vector2 arrangeSize = DesiredSize - new Vector2(Margin.Horizontal, Margin.Vertical);

        if (HorizontalAlignment != HorizontalAlignment.Stretch)
        {
            size.X = Math.Min(size.X, arrangeSize.X);
        }
        if (VerticalAlignment != VerticalAlignment.Stretch)
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

        Rect arrangeRect = new Rect(Padding.Left, Padding.Top, Math.Max(0f, size.X - Padding.Horizontal), Math.Max(0f, size.Y - Padding.Vertical));
        ArrangeOverride(arrangeRect);
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
