using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Documents;
using System;
using System.Collections.Generic;
using System.Numerics;
using ProGPU.Layout;
using ProGPU.Vector;
using ProGPU.Scene;

namespace Microsoft.UI.Xaml.Controls;

public class RadioButton : ContentControl
{
    public static readonly DependencyProperty IsCheckedProperty =
        DependencyProperty.Register(
            "IsChecked",
            typeof(bool),
            typeof(RadioButton),
            new PropertyMetadata(false, (d, e) => ((RadioButton)d).OnCheckedChanged()));

    public static readonly DependencyProperty GroupNameProperty =
        DependencyProperty.Register(
            "GroupName",
            typeof(string),
            typeof(RadioButton),
            new PropertyMetadata(string.Empty, (d, e) => ((RadioButton)d).OnGroupNameChanged()));

    public bool IsChecked
    {
        get => (bool)(GetValue(IsCheckedProperty) ?? false);
        set => SetValue(IsCheckedProperty, value);
    }

    public string GroupName
    {
        get => (string)(GetValue(GroupNameProperty) ?? string.Empty);
        set => SetValue(GroupNameProperty, value);
    }

    public event EventHandler? Checked;
    public event EventHandler? Unchecked;
    public event EventHandler? CheckedChanged;

    public RadioButton()
    {
        CornerRadius = 9f; // Perfect circle for 18x18 size
        Padding = new Thickness(8, 4, 8, 4);
    }

    private void OnCheckedChanged()
    {
        Invalidate();
        if (IsChecked)
        {
            UpdateSiblingRadioButtons();
            Checked?.Invoke(this, EventArgs.Empty);
        }
        else
        {
            Unchecked?.Invoke(this, EventArgs.Empty);
        }
        CheckedChanged?.Invoke(this, EventArgs.Empty);
        UpdateGroupTabStops();
    }

    private void OnGroupNameChanged()
    {
        UpdateGroupTabStops();
    }

    private void UpdateSiblingRadioButtons()
    {
        if (!IsChecked) return;

        var parent = Parent;
        if (parent == null) return;

        var groupName = GroupName;

        if (!string.IsNullOrEmpty(groupName))
        {
            // Explicit Grouping: find all radio buttons with the same GroupName in the entire visual tree
            var root = FindRoot(this);
            if (root != null)
            {
                var list = new List<RadioButton>();
                FindRadioButtonsInTree(root, list);
                foreach (var rb in list)
                {
                    if (rb != this && rb.GroupName == groupName && rb.IsChecked)
                    {
                        rb.IsChecked = false;
                    }
                }
            }
        }
        else
        {
            // Implicit Grouping: find sibling RadioButtons (same parent) with empty GroupName
            if (parent is ContainerVisual container)
            {
                foreach (var child in container.Children)
                {
                    if (child is RadioButton rb && rb != this && string.IsNullOrEmpty(rb.GroupName) && rb.IsChecked)
                    {
                        rb.IsChecked = false;
                    }
                }
            }
        }
    }

    private List<RadioButton> GetGroupRadioButtons()
    {
        var list = new List<RadioButton>();
        var groupName = GroupName;

        if (!string.IsNullOrEmpty(groupName))
        {
            var root = FindRoot(this);
            if (root != null)
            {
                var all = new List<RadioButton>();
                FindRadioButtonsInTree(root, all);
                foreach (var rb in all)
                {
                    if (rb.GroupName == groupName && rb.IsEnabled)
                    {
                        list.Add(rb);
                    }
                }
            }
        }
        else
        {
            var parent = Parent;
            if (parent is ContainerVisual container)
            {
                foreach (var child in container.Children)
                {
                    if (child is RadioButton rb && rb.IsEnabled && string.IsNullOrEmpty(rb.GroupName))
                    {
                        list.Add(rb);
                    }
                }
            }
        }

        return list;
    }

    private void UpdateGroupTabStops()
    {
        var group = GetGroupRadioButtons();
        if (group.Count == 0) return;

        RadioButton? targetTab = null;
        var focused = InputSystem.FocusedElement as RadioButton;
        
        if (focused != null && group.Contains(focused))
        {
            targetTab = focused;
        }

        if (targetTab == null)
        {
            foreach (var rb in group)
            {
                if (rb.IsChecked)
                {
                    targetTab = rb;
                    break;
                }
            }
        }

        if (targetTab == null)
        {
            targetTab = group[0];
        }

        foreach (var rb in group)
        {
            rb.IsTabStop = (rb == targetTab);
        }
    }

    private static Visual? FindRoot(Visual visual)
    {
        var current = visual;
        while (current.Parent != null)
        {
            current = current.Parent;
        }
        return current;
    }

    private static void FindRadioButtonsInTree(Visual visual, List<RadioButton> list)
    {
        if (visual is RadioButton rb)
        {
            list.Add(rb);
        }

        if (visual is ContainerVisual container)
        {
            foreach (var child in container.Children)
            {
                FindRadioButtonsInTree(child, list);
            }
        }
    }

    public override void OnPointerReleased(PointerRoutedEventArgs e)
    {
        if (IsEnabled && IsPointerPressed && IsPointerOver)
        {
            if (!IsChecked)
            {
                IsChecked = true;
            }
        }
        base.OnPointerReleased(e);
    }

    public override void OnKeyDown(KeyRoutedEventArgs e)
    {
        if (IsEnabled && IsFocused)
        {
            if (e.Key == Silk.NET.Input.Key.Up || e.Key == Silk.NET.Input.Key.Left)
            {
                var group = GetGroupRadioButtons();
                int idx = group.IndexOf(this);
                if (idx > 0)
                {
                    var prev = group[idx - 1];
                    InputSystem.SetFocus(prev);
                    prev.IsChecked = true;
                    e.Handled = true;
                }
                return;
            }
            else if (e.Key == Silk.NET.Input.Key.Down || e.Key == Silk.NET.Input.Key.Right)
            {
                var group = GetGroupRadioButtons();
                int idx = group.IndexOf(this);
                if (idx >= 0 && idx < group.Count - 1)
                {
                    var next = group[idx + 1];
                    InputSystem.SetFocus(next);
                    next.IsChecked = true;
                    e.Handled = true;
                }
                return;
            }
            else if (e.Key == Silk.NET.Input.Key.Enter || e.Key == Silk.NET.Input.Key.Space)
            {
                if (!IsChecked)
                {
                    IsChecked = true;
                    e.Handled = true;
                }
                return;
            }
        }
        base.OnKeyDown(e);
    }

    protected override void RaisePropertyChanged(string propertyName)
    {
        base.RaisePropertyChanged(propertyName);
        if (propertyName == "IsFocused" || propertyName == "IsEnabled")
        {
            UpdateGroupTabStops();
        }
    }

    protected override Vector2 MeasureOverride(Vector2 availableSize)
    {
        float borderH = BorderThickness.Horizontal;
        float borderV = BorderThickness.Vertical;
        float paddingH = Padding.Horizontal;
        float paddingV = Padding.Vertical;

        float boxSize = 18f;
        float spacing = 8f;

        Vector2 inset = new Vector2(borderH + paddingH + boxSize + spacing, borderV + paddingV);
        Vector2 contentAvail = new Vector2(
            Math.Max(0f, availableSize.X - inset.X),
            Math.Max(0f, availableSize.Y - inset.Y)
        );

        Vector2 contentDesired = Vector2.Zero;
        var contentVisual = ContentVisual;
        if (contentVisual != null)
        {
            contentVisual.Measure(contentAvail);
            contentDesired = contentVisual.DesiredSize;
        }

        return new Vector2(
            contentDesired.X + borderH + boxSize + spacing,
            Math.Max(boxSize, contentDesired.Y) + borderV
        );
    }

    protected override void ArrangeOverride(Rect arrangeRect)
    {
        float leftInset = BorderThickness.Left;
        float topInset = BorderThickness.Top;
        float boxSize = 18f;
        float spacing = 8f;

        float boxY = arrangeRect.Y + topInset + (arrangeRect.Height - (topInset + BorderThickness.Bottom) - boxSize) / 2f;

        var contentVisual = ContentVisual;
        if (contentVisual != null)
        {
            float contentX = arrangeRect.X + leftInset + boxSize + spacing;
            float contentW = arrangeRect.Width - (leftInset + BorderThickness.Right + boxSize + spacing);
            float contentH = contentVisual.DesiredSize.Y;
            float contentY = arrangeRect.Y + topInset + (arrangeRect.Height - (topInset + BorderThickness.Bottom) - contentH) / 2f;

            contentVisual.Arrange(new Rect(contentX, contentY, contentW, contentH));
        }
    }

    public override void OnRender(DrawingContext context)
    {
        float leftInset = BorderThickness.Left + Padding.Left;
        float boxSize = 18f;
        float boxY = (Size.Y - boxSize) / 2f;

        Rect boxRect = new Rect(leftInset, boxY, boxSize, boxSize);

        Brush? boxBg;
        Pen? boxBorder;

        if (!IsEnabled)
        {
            boxBg = Background ?? ThemeManager.GetBrush("ControlBackground");
            boxBorder = BorderBrush != null
                ? new Pen(BorderBrush, 1f)
                : ThemeManager.GetPen("ControlBorder", 1f);
        }
        else if (IsChecked)
        {
            boxBg = IsPointerPressed 
                ? ThemeManager.GetBrush("SystemAccentColorDark1") 
                : (IsPointerOver ? ThemeManager.GetBrush("SystemAccentColorLight1") : ThemeManager.GetBrush("SystemAccentColor"));
            boxBorder = null;
        }
        else
        {
            boxBg = Background ?? ThemeManager.GetBrush(IsPointerPressed ? "ControlBackgroundPressed" : IsPointerOver ? "ControlBackgroundHover" : "ControlBackground");
            boxBorder = BorderBrush != null
                ? new Pen(BorderBrush, 1f)
                : ThemeManager.GetPen(IsPointerOver ? "ControlBorderHover" : "ControlBorder", 1f);
        }

        // Draw outer radio circle
        context.DrawRoundedRectangle(boxBg, boxBorder, boxRect, 9f);

        // Draw inner dot if checked
        if (IsChecked)
        {
            var dotBrush = IsEnabled 
                ? (ThemeManager.CurrentTheme == ElementTheme.Light ? ThemeManager.GetBrush("CardBackground") : ThemeManager.GetBrush("TextPrimary"))
                : ThemeManager.GetBrush("TextSecondary");

            // Draw center dot of diameter 6px
            Rect dotRect = new Rect(boxRect.X + 6f, boxRect.Y + 6f, 6f, 6f);
            context.DrawRoundedRectangle(dotBrush, null, dotRect, 3f);
        }

        // Draw active blue focus ring 2px outside the circle
        if (IsEnabled && IsFocused && InputSystem.IsKeyboardFocusActive)
        {
            var focusPen = ThemeManager.GetPen("SystemAccentColor", 2f);
            Rect focusRect = new Rect(boxRect.X - 2f, boxRect.Y - 2f, boxRect.Width + 4f, boxRect.Height + 4f);
            context.DrawRoundedRectangle(null, focusPen, focusRect, 11f);
        }

        base.OnRender(context);
    }
}
