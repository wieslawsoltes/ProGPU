namespace ProGPU.WinUI.Designer;

using System;
using System.Collections.Generic;
using System.Reflection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Markup;
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
        Padding = new Thickness(depth * 12 + 6, 6, 6, 6);
        CornerRadius = 4f;
        Background = isSelected 
            ? new ThemeResourceBrush("SelectionHighlight") 
            : new ThemeResourceBrush("Transparent");

        AllowDrop = true;

        var grid = new Grid { HorizontalAlignment = HorizontalAlignment.Stretch };
        grid.ColumnDefinitions.Add(new GridLength(1, GridUnitType.Star));
        grid.ColumnDefinitions.Add(new GridLength(24, GridUnitType.Absolute));
        grid.ColumnDefinitions.Add(new GridLength(24, GridUnitType.Absolute));

        var indentPanel = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        
        // Expansion arrow prefix if element has visual children
        if (element is ContainerVisual container && container.Children.Count > 0)
        {
            bool isCollapsed = parentOutline.IsCollapsed(element);
            var toggleText = new RichTextBlock { FontSize = 8f, Foreground = new ThemeResourceBrush("TextSecondary") };
            toggleText.Inlines.Add(new Run(isCollapsed ? "▶" : "▼"));
            var toggleBtn = new Button
            {
                Content = toggleText,
                WidthConstraint = 14f,
                HeightConstraint = 14f,
                Padding = new Thickness(0),
                BorderThickness = new Thickness(0),
                Background = new ThemeResourceBrush("Transparent"),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 4, 0)
            };
            toggleBtn.Click += (s, e) => {
                parentOutline.ToggleExpanded(element);
            };
            indentPanel.AddChild(toggleBtn);
        }
        else
        {
            var spacer = new Border { Width = 14f, Height = 1f };
            indentPanel.AddChild(spacer);
        }

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
        indentPanel.AddChild(textBlock);

        grid.AddChild(indentPanel);
        Grid.SetColumn(indentPanel, 0);

        var visCheckBox = new CheckBox
        {
            IsChecked = element.Visibility == Visibility.Visible,
            WidthConstraint = 16f,
            HeightConstraint = 16f,
            Padding = new Thickness(0),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        visCheckBox.Checked += (s, e) => {
            element.Visibility = Visibility.Visible;
            _parentOutline.NotifyCanvasModified();
        };
        visCheckBox.Unchecked += (s, e) => {
            element.Visibility = Visibility.Collapsed;
            _parentOutline.NotifyCanvasModified();
        };
        grid.AddChild(visCheckBox);
        Grid.SetColumn(visCheckBox, 1);

        if (element != parentOutline.RootElement)
        {
            var delText = new RichTextBlock
            {
                Font = font,
                FontSize = 12f,
                Foreground = new ThemeResourceBrush("TextSecondary"),
                WidthConstraint = 16f,
                HeightConstraint = 16f,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                IsHitTestVisible = true
            };
            delText.Inlines.Add(new Run("×"));
            delText.PointerPressed += (s, e) => {
                _parentOutline.DeleteElement(_element);
                e.Handled = true;
            };
            delText.PointerEntered += (s, e) => {
                delText.Foreground = new ThemeResourceBrush("SystemAccentColor");
            };
            delText.PointerExited += (s, e) => {
                delText.Foreground = new ThemeResourceBrush("TextSecondary");
            };
            grid.AddChild(delText);
            Grid.SetColumn(delText, 2);
        }

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

            // Start drag-and-drop move operation for re-arranging!
            var dp = new DataPackage();
            dp.SetData("OutlineItem", _element);

            var dragVisual = new Border
            {
                Width = 140f,
                Height = 30f,
                Background = new ThemeResourceBrush("SystemAccentColor"),
                BorderBrush = new ThemeResourceBrush("ControlBorder"),
                BorderThickness = new Thickness(1f),
                CornerRadius = 4f,
                Opacity = 0.75f,
                Padding = new Thickness(6, 4, 6, 4)
            };
            var visualText = new RichTextBlock
            {
                Font = _font,
                FontSize = 10f,
                Foreground = new ThemeResourceBrush("TextPrimary"),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            string name = string.IsNullOrEmpty(_element.Name) ? "" : $" \"{_element.Name}\"";
            visualText.Inlines.Add(new Run($"{_element.GetType().Name}{name}"));
            dragVisual.Child = visualText;

            DragDropManager.StartDrag(this, dp, DragDropEffects.Move, dragVisual);
        }
        base.OnPointerPressed(e);
    }

    public override void OnDragOver(Microsoft.UI.Xaml.DragEventArgs e)
    {
        if (e.Data.Contains("OutlineItem") || e.Data.Contains(StandardDataFormats.Tool))
        {
            e.AcceptedOperation = DragDropEffects.Move;
            e.Handled = true;
        }
        base.OnDragOver(e);
    }

    public override void OnDrop(Microsoft.UI.Xaml.DragEventArgs e)
    {
        if (e.Data.Contains("OutlineItem"))
        {
            var sourceElement = e.Data.GetData("OutlineItem") as FrameworkElement;
            if (sourceElement != null && sourceElement != _element && !IsAncestorOf(sourceElement, _element))
            {
                MoveElement(sourceElement, _element);
                e.Handled = true;
            }
        }
        else if (e.Data.Contains(StandardDataFormats.Tool))
        {
            var toolData = e.Data.GetData(StandardDataFormats.Tool);
            string? toolName = toolData as string;
            if (!string.IsNullOrEmpty(toolName))
            {
                CreateAndAddTool(toolName, _element);
                e.Handled = true;
            }
        }
        base.OnDrop(e);
    }

    private static bool IsAncestorOf(FrameworkElement possibleAncestor, FrameworkElement child)
    {
        var current = child.Parent as FrameworkElement;
        while (current != null)
        {
            if (current == possibleAncestor) return true;
            current = current.Parent as FrameworkElement;
        }
        return false;
    }

    private bool IsValidDropContainer(FrameworkElement fe)
    {
        if (fe is Panel) return true;
        
        var type = fe.GetType();
        var contentPropertyAttr = type.GetCustomAttribute<ContentPropertyAttribute>(true);
        if (contentPropertyAttr != null && !string.IsNullOrEmpty(contentPropertyAttr.Name))
        {
            return true;
        }

        if (VisualTreeOutline.GetPropertySafe(type, "Child") != null || VisualTreeOutline.GetPropertySafe(type, "Content") != null)
        {
            return true;
        }

        return false;
    }

    public static void AddChildToTarget(FrameworkElement target, FrameworkElement newChild)
    {
        if (target == null || newChild == null) return;

        if (target is Panel panel)
        {
            panel.Children.Add(newChild);
            return;
        }

        var type = target.GetType();
        var contentPropertyAttr = type.GetCustomAttribute<ContentPropertyAttribute>(true);
        if (contentPropertyAttr != null && !string.IsNullOrEmpty(contentPropertyAttr.Name))
        {
            var prop = VisualTreeOutline.GetPropertySafe(type, contentPropertyAttr.Name);
            if (prop != null && prop.CanWrite)
            {
                prop.SetValue(target, newChild);
                return;
            }
        }

        var childProp = VisualTreeOutline.GetPropertySafe(type, "Child");
        if (childProp != null && childProp.CanWrite && typeof(FrameworkElement).IsAssignableFrom(childProp.PropertyType))
        {
            childProp.SetValue(target, newChild);
            return;
        }

        var contentProp = VisualTreeOutline.GetPropertySafe(type, "Content");
        if (contentProp != null && contentProp.CanWrite)
        {
            contentProp.SetValue(target, newChild);
            return;
        }

        if (target is ContainerVisual container)
        {
            container.AddChild(newChild);
        }
    }

    private void MoveElement(FrameworkElement source, FrameworkElement target)
    {
        _parentOutline.NotifyCanvasModifying();
        VisualTreeOutline.RemoveChildFromParent(source);

        if (IsValidDropContainer(target))
        {
            if (target is Canvas canvasTarget)
            {
                Canvas.SetLeft(source, 20f);
                Canvas.SetTop(source, 20f);
                canvasTarget.Children.Add(source);
            }
            else
            {
                AddChildToTarget(target, source);
            }
        }
        else
        {
            if (target.Parent is ContainerVisual targetParent)
            {
                if (targetParent is Canvas canvasParent)
                {
                    Canvas.SetLeft(source, Canvas.GetLeft(target) + 20f);
                    Canvas.SetTop(source, Canvas.GetTop(target) + 20f);
                    canvasParent.Children.Add(source);
                }
                else
                {
                    AddChildToTarget(targetParent as FrameworkElement, source);
                }
            }
        }

        _parentOutline.SelectElement(source);
        _parentOutline.NotifyCanvasModified();
    }

    private void CreateAndAddTool(string toolName, FrameworkElement target)
    {
        _parentOutline.NotifyCanvasModifying();
        Type? controlType = null;
        string[] searchNamespaces = {
            "Microsoft.UI.Xaml.Controls",
            "Microsoft.UI.Xaml",
            "ProGPU.WinUI.Designer"
        };

        foreach (var ns in searchNamespaces)
        {
            var typeName = $"{ns}.{toolName}";
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                controlType = assembly.GetType(typeName);
                if (controlType != null) break;
            }
            if (controlType != null) break;
        }

        if (controlType == null)
        {
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                foreach (var type in assembly.GetTypes())
                {
                    if (type.Name.Equals(toolName, StringComparison.OrdinalIgnoreCase))
                    {
                        controlType = type;
                        break;
                    }
                }
                if (controlType != null) break;
            }
        }

        if (controlType != null && typeof(FrameworkElement).IsAssignableFrom(controlType))
        {
            try
            {
                var newInstance = Activator.CreateInstance(controlType) as FrameworkElement;
                if (newInstance != null)
                {
                    newInstance.IsHitTestVisible = false;

                    if (float.IsNaN(newInstance.Width) || newInstance.Width <= 0) newInstance.Width = 120f;
                    if (float.IsNaN(newInstance.Height) || newInstance.Height <= 0) newInstance.Height = 36f;

                    if (newInstance is Button button)
                    {
                        var richText = new RichTextBlock { Font = _font ?? PopupService.DefaultFont };
                        richText.Inlines.Add(new Run(toolName));
                        button.Content = richText;
                    }
                    else if (newInstance is TextBlock textBlock)
                    {
                        textBlock.Text = toolName;
                    }
                    else if (newInstance is CheckBox checkBox)
                    {
                        var richText = new RichTextBlock { Font = _font ?? PopupService.DefaultFont };
                        richText.Inlines.Add(new Run(toolName));
                        checkBox.Content = richText;
                    }
                    else if (newInstance is RadioButton radioButton)
                    {
                        var richText = new RichTextBlock { Font = _font ?? PopupService.DefaultFont };
                        richText.Inlines.Add(new Run(toolName));
                        radioButton.Content = richText;
                    }
                    else if (newInstance is ToggleSwitch toggleSwitch)
                    {
                        var richText = new RichTextBlock { Font = _font ?? PopupService.DefaultFont };
                        richText.Inlines.Add(new Run(toolName));
                        toggleSwitch.Content = richText;
                    }
                    else if (newInstance is ComboBox comboBox)
                    {
                        comboBox.PlaceholderText = toolName;
                    }

                    int suffix = 1;
                    string baseName = $"{toolName}";
                    string candidateName = $"{baseName}_{suffix}";
                    
                    var existingNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    FindNamesInVisualTree(_parentOutline.RootElement, existingNames);
                    while (existingNames.Contains(candidateName))
                    {
                        candidateName = $"{baseName}_{++suffix}";
                    }
                    newInstance.Name = candidateName;

                    if (IsValidDropContainer(target))
                    {
                        if (target is Canvas canvasTarget)
                        {
                            Canvas.SetLeft(newInstance, 50f);
                            Canvas.SetTop(newInstance, 50f);
                            canvasTarget.Children.Add(newInstance);
                        }
                        else
                        {
                            AddChildToTarget(target, newInstance);
                        }
                    }
                    else if (target.Parent is ContainerVisual targetParent)
                    {
                        if (targetParent is Canvas canvasParent)
                        {
                            Canvas.SetLeft(newInstance, Canvas.GetLeft(target) + 20f);
                            Canvas.SetTop(newInstance, Canvas.GetTop(target) + 20f);
                            canvasParent.Children.Add(newInstance);
                        }
                        else
                        {
                            AddChildToTarget(targetParent as FrameworkElement, newInstance);
                        }
                    }

                    _parentOutline.SelectElement(newInstance);
                    _parentOutline.NotifyCanvasModified();
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[VisualTreeOutlineItem] Error instantiating {toolName}: {ex.Message}");
            }
        }
    }

    private void FindNamesInVisualTree(Visual? root, HashSet<string> names)
    {
        if (root is FrameworkElement fe)
        {
            if (!string.IsNullOrEmpty(fe.Name)) names.Add(fe.Name);
            if (fe is ContainerVisual container)
            {
                foreach (var child in container.Children)
                {
                    FindNamesInVisualTree(child, names);
                }
            }
        }
    }
}

public class VisualTreeOutline : Border
{
    private FrameworkElement? _rootElement;
    private FrameworkElement? _selectedElement;
    private readonly StackPanel _treeStack;
    private readonly ScrollViewer _scrollViewer;
    private ProGPU.Text.TtfFont? _font;
    private readonly HashSet<FrameworkElement> _collapsedElements = new();

    public event Action<FrameworkElement?>? SelectionChanged;
    public event Action? CanvasModified;
    public event Action? CanvasModifying;

    public void NotifyCanvasModifying()
    {
        CanvasModifying?.Invoke();
    }

    public static PropertyInfo? GetPropertySafe(Type type, string name)
    {
        Type? currentType = type;
        while (currentType != null)
        {
            var prop = currentType.GetProperty(name, BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly);
            if (prop != null)
            {
                return prop;
            }
            currentType = currentType.BaseType;
        }
        return null;
    }

    public static void RemoveChildFromParent(FrameworkElement child)
    {
        if (child == null) return;
        var parent = child.Parent as FrameworkElement;
        if (parent == null)
        {
            var containerParent = child.Parent as ContainerVisual;
            containerParent?.RemoveChild(child);
            return;
        }

        var type = parent.GetType();
        var contentPropertyAttr = type.GetCustomAttribute<ContentPropertyAttribute>(true);
        if (contentPropertyAttr != null && !string.IsNullOrEmpty(contentPropertyAttr.Name))
        {
            var prop = GetPropertySafe(type, contentPropertyAttr.Name);
            if (prop != null)
            {
                if (prop.CanWrite && prop.GetValue(parent) == child)
                {
                    prop.SetValue(parent, null);
                    return;
                }
                else if (typeof(System.Collections.IList).IsAssignableFrom(prop.PropertyType))
                {
                    var list = prop.GetValue(parent) as System.Collections.IList;
                    if (list != null && list.Contains(child))
                    {
                        list.Remove(child);
                        return;
                    }
                }
            }
        }

        var childProp = GetPropertySafe(type, "Child");
        if (childProp != null && childProp.CanWrite && childProp.GetValue(parent) == child)
        {
            childProp.SetValue(parent, null);
            return;
        }

        var contentProp = GetPropertySafe(type, "Content");
        if (contentProp != null && contentProp.CanWrite && contentProp.GetValue(parent) == child)
        {
            contentProp.SetValue(parent, null);
            return;
        }

        if (parent is Panel panel)
        {
            panel.Children.Remove(child);
            return;
        }

        parent.RemoveChild(child);
    }

    public new ProGPU.Text.TtfFont? Font
    {
        get => _font;
        set
        {
            if (_font != value)
            {
                _font = value;
                RefreshTree();
            }
        }
    }

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
        titleText.Inlines.Add(new Bold(new Run("Visual Tree Outline")));
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

    public new bool IsCollapsed(FrameworkElement element)
    {
        return _collapsedElements.Contains(element);
    }

    public void ToggleExpanded(FrameworkElement element)
    {
        if (_collapsedElements.Contains(element))
        {
            _collapsedElements.Remove(element);
        }
        else
        {
            _collapsedElements.Add(element);
        }
        RefreshTree();
    }

    public void NotifyCanvasModified()
    {
        CanvasModified?.Invoke();
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

        if (element is ContainerVisual container && !IsCollapsed(element))
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

        NotifyCanvasModifying();

        var parent = element.Parent as ContainerVisual;
        if (parent != null)
        {
            RemoveChildFromParent(element);
            
            if (_selectedElement == element)
            {
                _selectedElement = null;
                SelectionChanged?.Invoke(null);
            }
            
            RefreshTree();
            NotifyCanvasModified();
            
            _rootElement?.InvalidateMeasure();
            _rootElement?.Invalidate();
        }
    }
}
