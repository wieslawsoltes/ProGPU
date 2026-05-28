using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Documents;
using System;
using System.Numerics;
using ProGPU.Layout;
using ProGPU.Scene;

namespace Microsoft.UI.Xaml.Controls;

public enum Dock
{
    Left,
    Top,
    Right,
    Bottom
}

public class DockPanel : Panel
{
    public static readonly DependencyProperty LastChildFillProperty =
        DependencyProperty.Register(
            "LastChildFill",
            typeof(bool),
            typeof(DockPanel),
            new PropertyMetadata(true, (d, e) => {
                var dp = (DockPanel)d;
                dp.Invalidate();
                dp.InvalidateMeasure();
            }));

    public bool LastChildFill
    {
        get => (bool)(GetValue(LastChildFillProperty) ?? true);
        set => SetValue(LastChildFillProperty, value);
    }

    public static readonly DependencyProperty DockProperty =
        DependencyProperty.RegisterAttached(
            "Dock",
            typeof(Dock),
            typeof(DockPanel),
            new PropertyMetadata(Dock.Left, OnDockChanged));

    private static void OnDockChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is Visual child && child.Parent is DockPanel dockPanel)
        {
            dockPanel.Invalidate();
            dockPanel.InvalidateMeasure();
        }
    }

    public static void SetDock(Visual child, Dock dock)
    {
        if (child is DependencyObject d)
        {
            d.SetValue(DockProperty, dock);
        }
    }

    public static Dock GetDock(Visual child)
    {
        if (child is DependencyObject d)
        {
            return (Dock)(d.GetValue(DockProperty) ?? Dock.Left);
        }
        return Dock.Left;
    }

    protected override Vector2 MeasureOverride(Vector2 availableSize)
    {
        float remainingWidth = availableSize.X;
        float remainingHeight = availableSize.Y;

        float maxAccumulatedWidth = 0f;
        float maxAccumulatedHeight = 0f;

        var children = Children;
        int count = children.Count;

        for (int i = 0; i < count; i++)
        {
            if (children[i] is LayoutNode node)
            {
                bool isLast = i == count - 1;
                if (isLast && LastChildFill)
                {
                    node.Measure(new Vector2(remainingWidth, remainingHeight));
                    maxAccumulatedWidth = Math.Max(maxAccumulatedWidth, availableSize.X - remainingWidth + node.DesiredSize.X);
                    maxAccumulatedHeight = Math.Max(maxAccumulatedHeight, availableSize.Y - remainingHeight + node.DesiredSize.Y);
                }
                else
                {
                    node.Measure(new Vector2(remainingWidth, remainingHeight));
                    var desired = node.DesiredSize;
                    var dock = GetDock(node);

                    switch (dock)
                    {
                        case Dock.Left:
                        case Dock.Right:
                            maxAccumulatedHeight = Math.Max(maxAccumulatedHeight, availableSize.Y - remainingHeight + desired.Y);
                            remainingWidth = Math.Max(0f, remainingWidth - desired.X);
                            break;

                        case Dock.Top:
                        case Dock.Bottom:
                            maxAccumulatedWidth = Math.Max(maxAccumulatedWidth, availableSize.X - remainingWidth + desired.X);
                            remainingHeight = Math.Max(0f, remainingHeight - desired.Y);
                            break;
                    }
                }
            }
        }

        maxAccumulatedWidth = Math.Max(maxAccumulatedWidth, availableSize.X - remainingWidth);
        maxAccumulatedHeight = Math.Max(maxAccumulatedHeight, availableSize.Y - remainingHeight);

        return new Vector2(maxAccumulatedWidth, maxAccumulatedHeight);
    }

    protected override void ArrangeOverride(Rect arrangeRect)
    {
        float left = arrangeRect.X;
        float top = arrangeRect.Y;
        float right = arrangeRect.X + arrangeRect.Width;
        float bottom = arrangeRect.Y + arrangeRect.Height;

        var children = Children;
        int count = children.Count;

        for (int i = 0; i < count; i++)
        {
            if (children[i] is LayoutNode node)
            {
                bool isLast = i == count - 1;
                float width = Math.Max(0f, right - left);
                float height = Math.Max(0f, bottom - top);

                if (isLast && LastChildFill)
                {
                    node.Arrange(new Rect(left, top, width, height));
                }
                else
                {
                    var desired = node.DesiredSize;
                    var dock = GetDock(node);

                    switch (dock)
                    {
                        case Dock.Left:
                            node.Arrange(new Rect(left, top, Math.Min(width, desired.X), height));
                            left += Math.Min(width, desired.X);
                            break;

                        case Dock.Right:
                            float rWidth = Math.Min(width, desired.X);
                            node.Arrange(new Rect(right - rWidth, top, rWidth, height));
                            right -= rWidth;
                            break;

                        case Dock.Top:
                            node.Arrange(new Rect(left, top, width, Math.Min(height, desired.Y)));
                            top += Math.Min(height, desired.Y);
                            break;

                        case Dock.Bottom:
                            float bHeight = Math.Min(height, desired.Y);
                            node.Arrange(new Rect(left, bottom - bHeight, width, bHeight));
                            bottom -= bHeight;
                            break;
                    }
                }
            }
        }
    }
}
