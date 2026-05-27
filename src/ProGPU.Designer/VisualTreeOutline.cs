namespace ProGPU.Designer;

using System;
using System.Collections.Generic;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Documents;
using ProGPU.Vector;
using ProGPU.Layout;
using Thickness = Microsoft.UI.Xaml.Thickness;
using HorizontalAlignment = ProGPU.Layout.HorizontalAlignment;
using VerticalAlignment = ProGPU.Layout.VerticalAlignment;
using StackPanel = Microsoft.UI.Xaml.Controls.StackPanel;
using Grid = Microsoft.UI.Xaml.Controls.Grid;
using ProGPU.Scene;

public class VisualTreeOutlineItem : Border
{
    private readonly FrameworkElement _element;
    private readonly ProGPU.Text.TtfFont? _font;
    private readonly VisualTreeOutline _parentOutline;
    private readonly bool _isSelected;

    public FrameworkElement Element => _element;

    public VisualTreeOutlineItem(FrameworkElement element, int depth, bool isSelected, ProGPU.Text.TtfFont? font, VisualTreeOutline parentOutline)
    {
        _element = element;
        _font = font;
        _parentOutline = parentOutline;
        _isSelected = isSelected;

        Margin = new Thickness(2, 1, 2, 1);
        Padding = new Thickness(depth * 14 + 6, 6, 6, 6);
        CornerRadius = 4f;
        Background = isSelected 
            ? new ThemeResourceBrush("SelectionHighlight") 
            : new ThemeResourceBrush("Transparent");

        var grid = new Grid { HorizontalAlignment = HorizontalAlignment.Stretch };
        grid.ColumnDefinitions.Add(new GridLength(1, GridUnitType.Star));
        grid.ColumnDefinitions.Add(new GridLength(24, GridUnitType.Absolute));

        var textBlock = new RichTextBlock
        {
            Font = font,
            FontSize = 11f,
            Foreground = isSelected 
                ? new ThemeResourceBrush("SystemAccentColor") 
                : new ThemeResourceBrush("TextPrimary"),
            VerticalAlignment = VerticalAlignment.Center
        };
        
        string name = string.IsNullOrEmpty(element.Name) ? "" : $" \"{element.Name}\"";
        textBlock.Inlines.Add(new Run($"{element.GetType().Name}{name}"));
        grid.AddChild(textBlock);
        Grid.SetColumn(textBlock, 0);

        var delBtn = new Button
        {
            Content = "❌",
            WidthConstraint = 20f,
            HeightConstraint = 20f,
            Padding = new Thickness(0),
            BorderThickness = new Thickness(0),
            Background = new ThemeResourceBrush("Transparent"),
            CornerRadius = 3f,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        delBtn.PointerPressed += (s, e) => {
            _parentOutline.DeleteElement(_element);
            e.Handled = true;
        };
        grid.AddChild(delBtn);
        Grid.SetColumn(delBtn, 1);

        Child = grid;
    }

    public override void OnPointerEntered(PointerRoutedEventArgs e)
    {
        if (!_isSelected)
        {
            Background = new ThemeResourceBrush("ControlBackgroundHover");
        }
        base.OnPointerEntered(e);
    }

    public override void OnPointerExited(PointerRoutedEventArgs e)
    {
        if (!_isSelected)
        {
            Background = new ThemeResourceBrush("Transparent");
        }
        base.OnPointerExited(e);
    }

    public override void OnPointerPressed(PointerRoutedEventArgs e)
    {
        if (e.IsLeftButtonPressed)
        {
            _parentOutline.SelectElement(_element);
            e.Handled = true;
        }
        base.OnPointerPressed(e);
    }
}

public class VisualTreeOutline : Border
{
    private FrameworkElement? _rootElement;
    private FrameworkElement? _selectedElement;
    private readonly StackPanel _treeStack;
    private readonly ScrollViewer _scrollViewer;
    private readonly ProGPU.Text.TtfFont? _font;

    public event Action<FrameworkElement?>? SelectionChanged;

    public FrameworkElement? RootElement
    {
        get => _rootElement;
        set
        {
            if (_rootElement != value)
            {
                _rootElement = value;
                RefreshTree();
            }
        }
    }

    public FrameworkElement? SelectedElement
    {
        get => _selectedElement;
        set
        {
            if (_selectedElement != value)
            {
                _selectedElement = value;
                RefreshTree();
            }
        }
    }

    public VisualTreeOutline(ProGPU.Text.TtfFont? font)
    {
        _font = font;
        Background = new ThemeResourceBrush("CardBackground");
        BorderBrush = new ThemeResourceBrush("ControlBorder");
        BorderThickness = new Thickness(1f);
        CornerRadius = 8f;
        Padding = new Thickness(8);
        HorizontalAlignment = HorizontalAlignment.Stretch;
        VerticalAlignment = VerticalAlignment.Stretch;

        _treeStack = new StackPanel
        {
            Orientation = Orientation.Vertical,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };

        var titleText = new RichTextBlock
        {
            Font = font,
            FontSize = 14f,
            Margin = new Thickness(4, 4, 4, 12),
            HorizontalAlignment = HorizontalAlignment.Left
        };
        titleText.Inlines.Add(new Bold(new Run("Visual Tree")));
        _treeStack.AddChild(titleText);

        _scrollViewer = new ScrollViewer
        {
            Content = _treeStack,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch
        };

        Child = _scrollViewer;
        
        KeyDown += OnOutlineKeyDown;
        
        RefreshTree();
    }

    private void OnOutlineKeyDown(object? sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Silk.NET.Input.Key.Delete || e.Key == Silk.NET.Input.Key.Backspace)
        {
            if (_selectedElement != null && _selectedElement != _rootElement)
            {
                DeleteElement(_selectedElement);
                e.Handled = true;
            }
        }
    }

    public void RefreshTree()
    {
        while (_treeStack.Children.Count > 1)
        {
            _treeStack.RemoveChild(_treeStack.Children[1]);
        }

        if (_rootElement == null)
        {
            var noTreeText = new RichTextBlock
            {
                Font = _font,
                FontSize = 12f,
                Foreground = new ThemeResourceBrush("TextSecondary"),
                Margin = new Thickness(4, 10, 4, 4)
            };
            noTreeText.Inlines.Add(new Run("No tree root"));
            _treeStack.AddChild(noTreeText);
            return;
        }

        BuildTreeRecursively(_rootElement, 0);
    }

    private void BuildTreeRecursively(FrameworkElement element, int depth)
    {
        if (element.Name == "DevToolsPanel" || element.Name == "DesignerSidebar")
            return;

        bool isSelected = element == _selectedElement;
        var row = new VisualTreeOutlineItem(element, depth, isSelected, _font, this);
        _treeStack.AddChild(row);

        if (element is ContainerVisual container)
        {
            foreach (var child in container.Children)
            {
                if (child is FrameworkElement fe)
                {
                    BuildTreeRecursively(fe, depth + 1);
                }
            }
        }
    }

    public void SelectElement(FrameworkElement element)
    {
        _selectedElement = element;
        SelectionChanged?.Invoke(element);
        RefreshTree();
    }

    public void DeleteElement(FrameworkElement element)
    {
        if (element == _rootElement)
            return;

        var parent = element.Parent as ContainerVisual;
        if (parent != null)
        {
            parent.RemoveChild(element);
            
            if (_selectedElement == element)
            {
                _selectedElement = null;
                SelectionChanged?.Invoke(null);
            }
            
            RefreshTree();
            
            _rootElement?.InvalidateMeasure();
            _rootElement?.Invalidate();
        }
    }
}
