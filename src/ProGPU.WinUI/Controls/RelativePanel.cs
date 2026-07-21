using System;
using System.Collections.Generic;
using System.Numerics;
using ProGPU.Layout;
using ProGPU.Scene;

namespace Microsoft.UI.Xaml.Controls;

/// <summary>
/// Constraint panel implementing WinUI's sibling and panel-edge relationships.
/// The dependency graph is solved once per layout pass in O(N + E) graph work,
/// where N is the child count and E is the number of active relationships.
/// </summary>
public class RelativePanel : Panel
{
    public static readonly DependencyProperty LeftOfProperty = RegisterObject(nameof(LeftOfProperty));
    public static readonly DependencyProperty AboveProperty = RegisterObject(nameof(AboveProperty));
    public static readonly DependencyProperty RightOfProperty = RegisterObject(nameof(RightOfProperty));
    public static readonly DependencyProperty BelowProperty = RegisterObject(nameof(BelowProperty));
    public static readonly DependencyProperty AlignHorizontalCenterWithProperty = RegisterObject(nameof(AlignHorizontalCenterWithProperty));
    public static readonly DependencyProperty AlignVerticalCenterWithProperty = RegisterObject(nameof(AlignVerticalCenterWithProperty));
    public static readonly DependencyProperty AlignLeftWithProperty = RegisterObject(nameof(AlignLeftWithProperty));
    public static readonly DependencyProperty AlignTopWithProperty = RegisterObject(nameof(AlignTopWithProperty));
    public static readonly DependencyProperty AlignRightWithProperty = RegisterObject(nameof(AlignRightWithProperty));
    public static readonly DependencyProperty AlignBottomWithProperty = RegisterObject(nameof(AlignBottomWithProperty));
    public static readonly DependencyProperty AlignLeftWithPanelProperty = RegisterBoolean(nameof(AlignLeftWithPanelProperty));
    public static readonly DependencyProperty AlignTopWithPanelProperty = RegisterBoolean(nameof(AlignTopWithPanelProperty));
    public static readonly DependencyProperty AlignRightWithPanelProperty = RegisterBoolean(nameof(AlignRightWithPanelProperty));
    public static readonly DependencyProperty AlignBottomWithPanelProperty = RegisterBoolean(nameof(AlignBottomWithPanelProperty));
    public static readonly DependencyProperty AlignHorizontalCenterWithPanelProperty = RegisterBoolean(nameof(AlignHorizontalCenterWithPanelProperty));
    public static readonly DependencyProperty AlignVerticalCenterWithPanelProperty = RegisterBoolean(nameof(AlignVerticalCenterWithPanelProperty));

    private static DependencyProperty RegisterObject(string identifierName) =>
        DependencyProperty.RegisterAttached(
            identifierName[..^"Property".Length],
            typeof(object),
            typeof(RelativePanel),
            new PropertyMetadata(null, OnConstraintChanged));

    private static DependencyProperty RegisterBoolean(string identifierName) =>
        DependencyProperty.RegisterAttached(
            identifierName[..^"Property".Length],
            typeof(bool),
            typeof(RelativePanel),
            new PropertyMetadata(false, OnConstraintChanged));

    private static void OnConstraintChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        if (sender is Visual { Parent: RelativePanel panel })
        {
            panel.InvalidateMeasure();
            panel.InvalidateArrange();
            panel.Invalidate();
        }
    }

    public static object? GetLeftOf(UIElement element) => GetObject(element, LeftOfProperty);
    public static void SetLeftOf(UIElement element, object? value) => SetObject(element, LeftOfProperty, value);
    public static object? GetAbove(UIElement element) => GetObject(element, AboveProperty);
    public static void SetAbove(UIElement element, object? value) => SetObject(element, AboveProperty, value);
    public static object? GetRightOf(UIElement element) => GetObject(element, RightOfProperty);
    public static void SetRightOf(UIElement element, object? value) => SetObject(element, RightOfProperty, value);
    public static object? GetBelow(UIElement element) => GetObject(element, BelowProperty);
    public static void SetBelow(UIElement element, object? value) => SetObject(element, BelowProperty, value);
    public static object? GetAlignHorizontalCenterWith(UIElement element) => GetObject(element, AlignHorizontalCenterWithProperty);
    public static void SetAlignHorizontalCenterWith(UIElement element, object? value) => SetObject(element, AlignHorizontalCenterWithProperty, value);
    public static object? GetAlignVerticalCenterWith(UIElement element) => GetObject(element, AlignVerticalCenterWithProperty);
    public static void SetAlignVerticalCenterWith(UIElement element, object? value) => SetObject(element, AlignVerticalCenterWithProperty, value);
    public static object? GetAlignLeftWith(UIElement element) => GetObject(element, AlignLeftWithProperty);
    public static void SetAlignLeftWith(UIElement element, object? value) => SetObject(element, AlignLeftWithProperty, value);
    public static object? GetAlignTopWith(UIElement element) => GetObject(element, AlignTopWithProperty);
    public static void SetAlignTopWith(UIElement element, object? value) => SetObject(element, AlignTopWithProperty, value);
    public static object? GetAlignRightWith(UIElement element) => GetObject(element, AlignRightWithProperty);
    public static void SetAlignRightWith(UIElement element, object? value) => SetObject(element, AlignRightWithProperty, value);
    public static object? GetAlignBottomWith(UIElement element) => GetObject(element, AlignBottomWithProperty);
    public static void SetAlignBottomWith(UIElement element, object? value) => SetObject(element, AlignBottomWithProperty, value);
    public static bool GetAlignLeftWithPanel(UIElement element) => GetBoolean(element, AlignLeftWithPanelProperty);
    public static void SetAlignLeftWithPanel(UIElement element, bool value) => SetBoolean(element, AlignLeftWithPanelProperty, value);
    public static bool GetAlignTopWithPanel(UIElement element) => GetBoolean(element, AlignTopWithPanelProperty);
    public static void SetAlignTopWithPanel(UIElement element, bool value) => SetBoolean(element, AlignTopWithPanelProperty, value);
    public static bool GetAlignRightWithPanel(UIElement element) => GetBoolean(element, AlignRightWithPanelProperty);
    public static void SetAlignRightWithPanel(UIElement element, bool value) => SetBoolean(element, AlignRightWithPanelProperty, value);
    public static bool GetAlignBottomWithPanel(UIElement element) => GetBoolean(element, AlignBottomWithPanelProperty);
    public static void SetAlignBottomWithPanel(UIElement element, bool value) => SetBoolean(element, AlignBottomWithPanelProperty, value);
    public static bool GetAlignHorizontalCenterWithPanel(UIElement element) => GetBoolean(element, AlignHorizontalCenterWithPanelProperty);
    public static void SetAlignHorizontalCenterWithPanel(UIElement element, bool value) => SetBoolean(element, AlignHorizontalCenterWithPanelProperty, value);
    public static bool GetAlignVerticalCenterWithPanel(UIElement element) => GetBoolean(element, AlignVerticalCenterWithPanelProperty);
    public static void SetAlignVerticalCenterWithPanel(UIElement element, bool value) => SetBoolean(element, AlignVerticalCenterWithPanelProperty, value);

    protected override Vector2 MeasureOverride(Vector2 availableSize)
    {
        foreach (Visual child in VisualChildren)
        {
            if (child is LayoutNode node)
                node.Measure(availableSize);
        }

        float width = float.IsFinite(availableSize.X) ? availableSize.X : 0f;
        float height = float.IsFinite(availableSize.Y) ? availableSize.Y : 0f;
        Dictionary<LayoutNode, Rect> rects = Solve(new Rect(0f, 0f, width, height));
        if (rects.Count == 0) return Vector2.Zero;

        float minX = 0f;
        float minY = 0f;
        float maxX = 0f;
        float maxY = 0f;
        foreach (Rect rect in rects.Values)
        {
            minX = Math.Min(minX, rect.X);
            minY = Math.Min(minY, rect.Y);
            maxX = Math.Max(maxX, rect.X + rect.Width);
            maxY = Math.Max(maxY, rect.Y + rect.Height);
        }

        return new Vector2(maxX - minX, maxY - minY);
    }

    protected override void ArrangeOverride(Rect arrangeRect)
    {
        Dictionary<LayoutNode, Rect> rects = Solve(arrangeRect);
        foreach ((LayoutNode child, Rect rect) in rects)
            child.Arrange(rect);
    }

    private Dictionary<LayoutNode, Rect> Solve(Rect panelRect)
    {
        var nodes = new List<LayoutNode>(VisualChildren.Count);
        var elements = new Dictionary<LayoutNode, UIElement>();
        var names = new Dictionary<string, LayoutNode>(StringComparer.Ordinal);
        foreach (Visual child in VisualChildren)
        {
            if (child is not LayoutNode node) continue;
            nodes.Add(node);
            if (child is UIElement element)
            {
                elements[node] = element;
                if (element is FrameworkElement { Name.Length: > 0 } named)
                    names[named.Name] = node;
            }
        }

        var nodeSet = new HashSet<LayoutNode>(nodes);
        var result = new Dictionary<LayoutNode, Rect>(nodes.Count);
        var visiting = new HashSet<LayoutNode>();

        Rect Resolve(LayoutNode node)
        {
            if (result.TryGetValue(node, out Rect resolved)) return resolved;
            if (!visiting.Add(node))
                throw new InvalidOperationException("RelativePanel constraints contain a dependency cycle.");

            float width = Math.Max(0f, node.DesiredSize.X);
            float height = Math.Max(0f, node.DesiredSize.Y);
            float? left = null;
            float? right = null;
            float? horizontalCenter = null;
            float? top = null;
            float? bottom = null;
            float? verticalCenter = null;

            if (elements.TryGetValue(node, out UIElement? element))
            {
                if (GetAlignLeftWithPanel(element)) left = panelRect.X;
                if (GetAlignRightWithPanel(element)) right = panelRect.X + panelRect.Width;
                if (GetAlignHorizontalCenterWithPanel(element)) horizontalCenter = panelRect.X + panelRect.Width / 2f;
                if (GetAlignTopWithPanel(element)) top = panelRect.Y;
                if (GetAlignBottomWithPanel(element)) bottom = panelRect.Y + panelRect.Height;
                if (GetAlignVerticalCenterWithPanel(element)) verticalCenter = panelRect.Y + panelRect.Height / 2f;

                if (TryResolveTarget(element, LeftOfProperty, out LayoutNode? target))
                    right = Resolve(target).X;
                if (TryResolveTarget(element, RightOfProperty, out target))
                {
                    Rect targetRect = Resolve(target);
                    left = targetRect.X + targetRect.Width;
                }
                if (TryResolveTarget(element, AlignLeftWithProperty, out target))
                    left = Resolve(target).X;
                if (TryResolveTarget(element, AlignRightWithProperty, out target))
                {
                    Rect targetRect = Resolve(target);
                    right = targetRect.X + targetRect.Width;
                }
                if (TryResolveTarget(element, AlignHorizontalCenterWithProperty, out target))
                {
                    Rect targetRect = Resolve(target);
                    horizontalCenter = targetRect.X + targetRect.Width / 2f;
                }

                if (TryResolveTarget(element, AboveProperty, out target))
                    bottom = Resolve(target).Y;
                if (TryResolveTarget(element, BelowProperty, out target))
                {
                    Rect targetRect = Resolve(target);
                    top = targetRect.Y + targetRect.Height;
                }
                if (TryResolveTarget(element, AlignTopWithProperty, out target))
                    top = Resolve(target).Y;
                if (TryResolveTarget(element, AlignBottomWithProperty, out target))
                {
                    Rect targetRect = Resolve(target);
                    bottom = targetRect.Y + targetRect.Height;
                }
                if (TryResolveTarget(element, AlignVerticalCenterWithProperty, out target))
                {
                    Rect targetRect = Resolve(target);
                    verticalCenter = targetRect.Y + targetRect.Height / 2f;
                }
            }

            float x;
            if (left.HasValue && right.HasValue)
            {
                x = left.Value;
                width = Math.Max(0f, right.Value - left.Value);
            }
            else if (horizontalCenter.HasValue)
                x = horizontalCenter.Value - width / 2f;
            else if (left.HasValue)
                x = left.Value;
            else if (right.HasValue)
                x = right.Value - width;
            else
                x = panelRect.X;

            float y;
            if (top.HasValue && bottom.HasValue)
            {
                y = top.Value;
                height = Math.Max(0f, bottom.Value - top.Value);
            }
            else if (verticalCenter.HasValue)
                y = verticalCenter.Value - height / 2f;
            else if (top.HasValue)
                y = top.Value;
            else if (bottom.HasValue)
                y = bottom.Value - height;
            else
                y = panelRect.Y;

            visiting.Remove(node);
            resolved = new Rect(x, y, width, height);
            result[node] = resolved;
            return resolved;
        }

        bool TryResolveTarget(UIElement element, DependencyProperty property, out LayoutNode target)
        {
            object? value = element.GetValue(property);
            if (value is LayoutNode node && nodeSet.Contains(node))
            {
                target = node;
                return true;
            }
            if (value is string name && names.TryGetValue(name, out LayoutNode? namedNode))
            {
                target = namedNode;
                return true;
            }
            if (value is not null)
                throw new InvalidOperationException("RelativePanel relationship targets must be sibling elements or sibling names.");
            target = null!;
            return false;
        }

        foreach (LayoutNode node in nodes)
            Resolve(node);
        return result;
    }

    private static object? GetObject(UIElement element, DependencyProperty property)
    {
        ArgumentNullException.ThrowIfNull(element);
        return element.GetValue(property);
    }

    private static void SetObject(UIElement element, DependencyProperty property, object? value)
    {
        ArgumentNullException.ThrowIfNull(element);
        element.SetValue(property, value);
    }

    private static bool GetBoolean(UIElement element, DependencyProperty property)
    {
        ArgumentNullException.ThrowIfNull(element);
        return (bool)(element.GetValue(property) ?? false);
    }

    private static void SetBoolean(UIElement element, DependencyProperty property, bool value)
    {
        ArgumentNullException.ThrowIfNull(element);
        element.SetValue(property, value);
    }
}
