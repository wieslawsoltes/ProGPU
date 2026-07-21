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
        var defaultStyle = ThemeManager.GetDefaultStyle(GetType());
        if (defaultStyle != null)
        {
            SetDefaultStyle(defaultStyle);
        }
    }

    private void OnCheckedChanged()
    {
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
            bool movePrevious = e.Key == Silk.NET.Input.Key.Up ||
                (e.Key == Silk.NET.Input.Key.Left && FlowDirection != FlowDirection.RightToLeft) ||
                (e.Key == Silk.NET.Input.Key.Right && FlowDirection == FlowDirection.RightToLeft);
            bool moveNext = e.Key == Silk.NET.Input.Key.Down ||
                (e.Key == Silk.NET.Input.Key.Right && FlowDirection != FlowDirection.RightToLeft) ||
                (e.Key == Silk.NET.Input.Key.Left && FlowDirection == FlowDirection.RightToLeft);
            if (movePrevious)
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
            else if (moveNext)
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
        return base.MeasureOverride(availableSize);
    }

    protected override void ArrangeOverride(Rect arrangeRect)
    {
        base.ArrangeOverride(arrangeRect);
    }

    public override void OnRender(DrawingContext context)
    {
        base.OnRender(context);
    }
}
