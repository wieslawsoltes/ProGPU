using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Numerics;
using System.Reflection;
using ProGPU.Layout;
using ProGPU.Scene;
using ProGPU.Vector;
using ProGPU.Text;

namespace ProGPU.WinUI;

public class PropertyItem
{
    private readonly PropertyInfo _propInfo;
    private readonly FrameworkElement _element;

    public string Name => _propInfo.Name;
    public string Type => _propInfo.PropertyType.Name;

    public string Value
    {
        get
        {
            try
            {
                var v = _propInfo.GetValue(_element);
                if (v is SolidColorBrush scb)
                {
                    return $"#{(scb.Color.W * 255):X2}{(scb.Color.X * 255):X2}{(scb.Color.Y * 255):X2}{(scb.Color.Z * 255):X2}";
                }
                if (v is Thickness th)
                {
                    return $"{th.Left:N0},{th.Top:N0},{th.Right:N0},{th.Bottom:N0}";
                }
                return v?.ToString() ?? string.Empty;
            }
            catch { return string.Empty; }
        }
        set
        {
            try
            {
                object converted = value;
                var t = _propInfo.PropertyType;
                if (t == typeof(float)) converted = float.Parse(value, System.Globalization.CultureInfo.InvariantCulture);
                else if (t == typeof(int)) converted = int.Parse(value);
                else if (t == typeof(double)) converted = double.Parse(value, System.Globalization.CultureInfo.InvariantCulture);
                else if (t == typeof(bool)) converted = bool.Parse(value);
                else if (t == typeof(Thickness))
                {
                    string[] parts = value.Split(new[] { ',', ' ' }, StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length == 1)
                    {
                        float f = float.Parse(parts[0], System.Globalization.CultureInfo.InvariantCulture);
                        converted = new Thickness(f);
                    }
                    else if (parts.Length == 4)
                    {
                        float l = float.Parse(parts[0], System.Globalization.CultureInfo.InvariantCulture);
                        float t1 = float.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture);
                        float r = float.Parse(parts[2], System.Globalization.CultureInfo.InvariantCulture);
                        float b = float.Parse(parts[3], System.Globalization.CultureInfo.InvariantCulture);
                        converted = new Thickness(l, t1, r, b);
                    }
                }
                else if (t == typeof(Brush) || t == typeof(SolidColorBrush))
                {
                    string hex = value.Trim().Replace("#", "").Replace("0x", "");
                    uint argb = uint.Parse(hex, System.Globalization.NumberStyles.HexNumber);
                    converted = new SolidColorBrush(argb);
                }

                _propInfo.SetValue(_element, converted);
                _element.Invalidate();
                InputSystem.Root?.Invalidate();
            }
            catch { }
        }
    }

    public PropertyItem(PropertyInfo propInfo, FrameworkElement element)
    {
        _propInfo = propInfo;
        _element = element;
    }
}

public class DevTools : Border
{
    private readonly Grid _mainGrid;
    private readonly TreeView _treeView;
    private readonly DataGrid _propertyGrid;
    private readonly RichTextBlock _perfTextBlock;

    private readonly Grid _visualTreeTabContent;
    private readonly Grid _propertyTabContent;
    private readonly Grid _perfTabContent;

    private readonly Button _inspectBtn;
    private readonly Button _treeTabBtn;
    private readonly Button _propTabBtn;
    private readonly Button _perfTabBtn;

    private static DevTools? _instance;
    public static DevTools? Instance => _instance;

    public DevTools()
    {
        _instance = this;
        Name = "DevToolsPanel";

        Background = Background ?? ThemeManager.GetBrush("CardBackground");
        BorderBrush = BorderBrush ?? ThemeManager.GetBrush("ControlBorder");
        BorderThickness = new Thickness(1f, 0, 0, 0);

        _mainGrid = new Grid { HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Stretch };
        Child = _mainGrid;

        _mainGrid.RowDefinitions.Add(new GridLength(40, GridUnitType.Absolute));  // Row 0: Toolbar
        _mainGrid.RowDefinitions.Add(new GridLength(1, GridUnitType.Star));      // Row 1: Workspace

        // 1. TOOLBAR
        var toolbarGrid = new Grid { VerticalAlignment = VerticalAlignment.Stretch, Margin = new Thickness(4) };
        toolbarGrid.ColumnDefinitions.Add(new GridLength(64, GridUnitType.Absolute));  // Inspect button
        toolbarGrid.ColumnDefinitions.Add(new GridLength(1, GridUnitType.Star));       // Tabs spacer
        toolbarGrid.ColumnDefinitions.Add(new GridLength(32, GridUnitType.Absolute));  // Close button

        var inspectStack = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
        inspectStack.AddChild(new MagnifyingGlassVisual());
        inspectStack.AddChild(new TextVisual
        {
            Text = "Inspect",
            FontSize = 10f,
            VerticalAlignment = VerticalAlignment.Center
        });

        _inspectBtn = new Button
        {
            Content = inspectStack,
            Background = ThemeManager.GetBrush("ControlBackground")
        };
        _inspectBtn.Click += (s, e) =>
        {
            DevToolsService.IsInspectModeActive = !DevToolsService.IsInspectModeActive;
            UpdateInspectButtonState();
        };
        toolbarGrid.AddChild(_inspectBtn);
        Grid.SetColumn(_inspectBtn, 0);

        // Tab selection stack
        var tabsStack = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) };
        
        _treeTabBtn = new Button
        {
            Content = new TextVisual
            {
                Text = "Visual Tree",
                FontSize = 10f,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            },
            Margin = new Thickness(2),
            Background = ThemeManager.GetBrush("SystemAccentColor")
        };

        _propTabBtn = new Button
        {
            Content = new TextVisual
            {
                Text = "Properties",
                FontSize = 10f,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            },
            Margin = new Thickness(2),
            Background = ThemeManager.GetBrush("ControlBackground")
        };

        _perfTabBtn = new Button
        {
            Content = new TextVisual
            {
                Text = "Performance",
                FontSize = 10f,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            },
            Margin = new Thickness(2),
            Background = ThemeManager.GetBrush("ControlBackground")
        };

        _treeTabBtn.Click += (s, e) => SwitchTab("tree");
        _propTabBtn.Click += (s, e) => SwitchTab("prop");
        _perfTabBtn.Click += (s, e) => SwitchTab("perf");

        tabsStack.AddChild(_treeTabBtn);
        tabsStack.AddChild(_propTabBtn);
        tabsStack.AddChild(_perfTabBtn);
        toolbarGrid.AddChild(tabsStack);
        Grid.SetColumn(tabsStack, 1);

        var closeBtn = new Button
        {
            Content = new CloseIconVisual(),
            Background = new SolidColorBrush(0xFFFFFF08)
        };
        closeBtn.Click += (s, e) => DevToolsService.IsDevToolsActive = false;
        toolbarGrid.AddChild(closeBtn);
        Grid.SetColumn(closeBtn, 2);

        _mainGrid.AddChild(toolbarGrid);
        Grid.SetRow(toolbarGrid, 0);

        // 2. WORKSPACE TABS CONTENT
        // Tab A: Visual Tree Content
        _visualTreeTabContent = new Grid { Margin = new Thickness(4) };
        _treeView = new TreeView { HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Stretch };
        _treeView.SelectionChanged += OnTreeSelectionChanged;
        _visualTreeTabContent.AddChild(_treeView);

        // Tab B: Properties Content
        _propertyTabContent = new Grid { Margin = new Thickness(4) };
        _propertyGrid = new DataGrid
        {
            RowHeight = 24f,
            FontSize = 10f,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch
        };
        _propertyGrid.Columns.Add(new DataGridColumn("Property", 150f, "Name"));
        _propertyGrid.Columns.Add(new DataGridColumn("Type", 90f, "Type"));
        _propertyGrid.Columns.Add(new DataGridColumn("Value", 180f, "Value"));
        _propertyTabContent.AddChild(_propertyGrid);

        // Tab C: Performance Content
        _perfTabContent = new Grid { Margin = new Thickness(8) };
        _perfTextBlock = new RichTextBlock { FontSize = 12f, VerticalAlignment = VerticalAlignment.Stretch };
        _perfTabContent.AddChild(_perfTextBlock);

        // We will dynamically add/remove tab content inside SwitchTab to prevent overlapping.

        // Default layout selection: Visual Tree
        SwitchTab("tree");

        // Hooks
        DevToolsService.StateChanged += (s, e) =>
        {
            if (DevToolsService.IsDevToolsActive)
            {
                RefreshVisualTree();
                UpdateInspectButtonState();
            }
        };

        DevToolsService.InspectedElementChanged += (s, e) =>
        {
            if (DevToolsService.IsDevToolsActive && DevToolsService.InspectedElement != null)
            {
                SelectInspectedElementInTree(DevToolsService.InspectedElement);
                LoadProperties(DevToolsService.InspectedElement);
            }
        };
    }

    private void UpdateInspectButtonState()
    {
        _inspectBtn.Background = DevToolsService.IsInspectModeActive 
            ? ThemeManager.GetBrush("SystemAccentColor")  // Fluent Accent
            : ThemeManager.GetBrush("ControlBackground"); // Standard background
    }

    private void SwitchTab(string tab)
    {
        _mainGrid.RemoveChild(_visualTreeTabContent);
        _mainGrid.RemoveChild(_propertyTabContent);
        _mainGrid.RemoveChild(_perfTabContent);

        if (tab == "tree")
        {
            _mainGrid.AddChild(_visualTreeTabContent);
            Grid.SetRow(_visualTreeTabContent, 1);
        }
        else if (tab == "prop")
        {
            _mainGrid.AddChild(_propertyTabContent);
            Grid.SetRow(_propertyTabContent, 1);
        }
        else if (tab == "perf")
        {
            _mainGrid.AddChild(_perfTabContent);
            Grid.SetRow(_perfTabContent, 1);
        }

        _treeTabBtn.Background = tab == "tree" ? ThemeManager.GetBrush("SystemAccentColor") : ThemeManager.GetBrush("ControlBackground");
        _propTabBtn.Background = tab == "prop" ? ThemeManager.GetBrush("SystemAccentColor") : ThemeManager.GetBrush("ControlBackground");
        _perfTabBtn.Background = tab == "perf" ? ThemeManager.GetBrush("SystemAccentColor") : ThemeManager.GetBrush("ControlBackground");

        _mainGrid.Invalidate();
        Invalidate();
    }

    public void RefreshVisualTree()
    {
        _treeView.Items.Clear();
        if (InputSystem.Root != null)
        {
            string rootName = string.IsNullOrEmpty(InputSystem.Root.Name) ? InputSystem.Root.GetType().Name : $"{InputSystem.Root.GetType().Name} ({InputSystem.Root.Name})";
            var rootItem = new TreeViewItem(rootName)
            {
                TagValue = InputSystem.Root,
                IsExpanded = true
            };
            _treeView.Items.Add(rootItem);
            PopulateVisualTree(InputSystem.Root, rootItem);
            _treeView.RefreshTree();
        }
    }

    private void PopulateVisualTree(Visual node, TreeViewItem parentItem)
    {
        if (node is ContainerVisual container)
        {
            foreach (var child in container.Children)
            {
                if (child is FrameworkElement fe && fe != this && fe.Name != "DevToolsPanel")
                {
                    string name = string.IsNullOrEmpty(fe.Name) ? fe.GetType().Name : $"{fe.GetType().Name} ({fe.Name})";
                    var childItem = new TreeViewItem(name)
                    {
                        TagValue = fe
                    };
                    parentItem.Items.Add(childItem);
                    PopulateVisualTree(child, childItem);
                }
            }
        }
    }

    private void OnTreeSelectionChanged(object? sender, EventArgs e)
    {
        if (_treeView.SelectedItem != null && _treeView.SelectedItem.TagValue is FrameworkElement fe)
        {
            DevToolsService.InspectedElement = fe;
            LoadProperties(fe);
        }
    }

    private void SelectInspectedElementInTree(FrameworkElement fe)
    {
        TreeViewItem? found = FindTreeViewItemByElement(_treeView.Items, fe);
        if (found != null)
        {
            // Expand parents
            var current = found;
            while (current != null)
            {
                current.IsExpanded = true;
                current = GetTreeViewItemParent(current);
            }
            _treeView.SelectedItem = found;
            _treeView.RefreshTree();
        }
    }

    private TreeViewItem? FindTreeViewItemByElement(IList<TreeViewItem> items, FrameworkElement fe)
    {
        foreach (var item in items)
        {
            if (item.TagValue == fe) return item;
            var found = FindTreeViewItemByElement(item.Items, fe);
            if (found != null) return found;
        }
        return null;
    }

    private TreeViewItem? GetTreeViewItemParent(TreeViewItem item)
    {
        return null;
    }

    private void LoadProperties(FrameworkElement fe)
    {
        _propertyGrid.ItemsSource.Clear();
        var props = fe.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance);
        
        var list = new List<PropertyInfo>(props);
        list.Sort((a, b) => a.Name.CompareTo(b.Name));

        foreach (var prop in list)
        {
            if (prop.CanRead && prop.CanWrite && 
                (prop.PropertyType == typeof(string) || 
                 prop.PropertyType == typeof(float) || 
                 prop.PropertyType == typeof(double) || 
                 prop.PropertyType == typeof(int) || 
                 prop.PropertyType == typeof(bool) || 
                 prop.PropertyType == typeof(Thickness) || 
                 prop.PropertyType == typeof(Brush) || 
                 prop.PropertyType == typeof(SolidColorBrush)))
            {
                _propertyGrid.ItemsSource.Add(new PropertyItem(prop, fe));
            }
        }
        _propertyGrid.Invalidate();
    }

    public void UpdatePerfPanel(float fps, float cpuMs, uint vertices, uint drawCalls)
    {
        if (DevToolsService.IsDevToolsActive)
        {
            _perfTextBlock.Inlines.Clear();
            _perfTextBlock.Inlines.Add(new Bold(new Run("Runtime Performance Diagnostics\n\n")) { Foreground = ThemeManager.GetBrush("SystemAccentColor") });
            
            _perfTextBlock.Inlines.Add(new Run("Real-time Frame Rate: "));
            _perfTextBlock.Inlines.Add(new Bold(new Run($"{fps:F1} FPS\n")) { Foreground = fps >= 55f ? new SolidColorBrush(0x4CAF50FF) : new SolidColorBrush(0xF44336FF) });
            
            _perfTextBlock.Inlines.Add(new Run("CPU Frame Render Time: "));
            _perfTextBlock.Inlines.Add(new Bold(new Run($"{cpuMs:F2} ms\n")));
            
            _perfTextBlock.Inlines.Add(new Run("Composed Vertex Count: "));
            _perfTextBlock.Inlines.Add(new Bold(new Run($"{vertices:N0} vertices\n")));

            _perfTextBlock.Inlines.Add(new Run("Batched Draw Calls: "));
            _perfTextBlock.Inlines.Add(new Bold(new Run($"{drawCalls:N0} calls\n\n")));

            _perfTextBlock.Inlines.Add(new Bold(new Run("Adorner Layer System:\n")) { Foreground = ThemeManager.GetBrush("TextSecondary") });
            _perfTextBlock.Inlines.Add(new Run("• Inspect Mode: Enables colored layouts bounds visual annotations on hover.\n"));
            _perfTextBlock.Inlines.Add(new Run("• Blue highlight represents Selected Element boundaries.\n"));
            _perfTextBlock.Inlines.Add(new Run("• Green highlight represents Hovered Element inspection.\n"));
            
            _perfTextBlock.Invalidate();
        }
    }
}

public class MagnifyingGlassVisual : FrameworkElement
{
    public MagnifyingGlassVisual()
    {
        WidthConstraint = 12f;
        HeightConstraint = 12f;
        Margin = new Thickness(0, 0, 4, 0);
        VerticalAlignment = VerticalAlignment.Center;
    }

    public override void OnRender(DrawingContext context)
    {
        var strokeBrush = ThemeManager.GetBrush("TextPrimary");
        var pen = new Pen(strokeBrush, 1.5f);
        
        var path = new PathGeometry();
        var fig = new PathFigure(new Vector2(8f, 4.5f), isClosed: true);
        fig.Segments.Add(new QuadraticBezierSegment(new Vector2(8f, 8f), new Vector2(4.5f, 8f)));
        fig.Segments.Add(new QuadraticBezierSegment(new Vector2(1f, 8f), new Vector2(1f, 4.5f)));
        fig.Segments.Add(new QuadraticBezierSegment(new Vector2(1f, 1f), new Vector2(4.5f, 1f)));
        fig.Segments.Add(new QuadraticBezierSegment(new Vector2(8f, 1f), new Vector2(8f, 4.5f)));
        path.Figures.Add(fig);

        var handleFig = new PathFigure(new Vector2(7f, 7f), isClosed: false);
        handleFig.Segments.Add(new LineSegment(new Vector2(11f, 11f)));
        path.Figures.Add(handleFig);

        context.DrawPath(null, pen, path);
        base.OnRender(context);
    }
}

public class CloseIconVisual : FrameworkElement
{
    public CloseIconVisual()
    {
        WidthConstraint = 10f;
        HeightConstraint = 10f;
        HorizontalAlignment = HorizontalAlignment.Center;
        VerticalAlignment = VerticalAlignment.Center;
    }

    public override void OnRender(DrawingContext context)
    {
        var strokeBrush = ThemeManager.GetBrush("TextPrimary");
        var pen = new Pen(strokeBrush, 1.5f);
        
        var path = new PathGeometry();
        var fig1 = new PathFigure(new Vector2(1f, 1f), isClosed: false);
        fig1.Segments.Add(new LineSegment(new Vector2(9f, 9f)));
        path.Figures.Add(fig1);

        var fig2 = new PathFigure(new Vector2(9f, 1f), isClosed: false);
        fig2.Segments.Add(new LineSegment(new Vector2(1f, 9f)));
        path.Figures.Add(fig2);

        context.DrawPath(null, pen, path);
        base.OnRender(context);
    }
}
