using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Documents;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Numerics;
using System.Reflection;
using ProGPU.Layout;
using ProGPU.Scene;
using ProGPU.Vector;
using ProGPU.Text;

namespace Microsoft.UI.Xaml.Controls;

public class PropertyItem
{
    private readonly PropertyInfo _propInfo;
    private readonly Visual _element;

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
                DevToolsService.InvalidateAllMainWindows();
            }
            catch { }
        }
    }

    public PropertyItem(PropertyInfo propInfo, Visual element)
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
    private readonly Pivot _pivot;

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
        _mainGrid.RowDefinitions.Add(new GridLength(1, GridUnitType.Star));      // Row 1: Pivot Workspace

        // 1. TOOLBAR
        var toolbarGrid = new Grid { VerticalAlignment = VerticalAlignment.Stretch, Margin = new Thickness(4) };
        toolbarGrid.ColumnDefinitions.Add(new GridLength(64, GridUnitType.Absolute));  // Inspect button
        toolbarGrid.ColumnDefinitions.Add(new GridLength(1, GridUnitType.Star));       // Spacer
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
        _propertyTabContent.RowDefinitions.Add(new GridLength(20, GridUnitType.Absolute));
        _propertyTabContent.RowDefinitions.Add(new GridLength(1, GridUnitType.Star));

        var propInstructions = new RichTextBlock 
        { 
            FontSize = 9f, 
            Margin = new Thickness(4, 0, 0, 4) 
        };
        propInstructions.Inlines.Add(new Run("Double-click any cell in the ") { Foreground = new SolidColorBrush(0xFFFFFF90) });
        propInstructions.Inlines.Add(new Bold(new Run("Value")) { Foreground = ThemeManager.GetBrush("SystemAccentColor") });
        propInstructions.Inlines.Add(new Run(" column to edit property values in real-time.") { Foreground = new SolidColorBrush(0xFFFFFF90) });
        _propertyTabContent.AddChild(propInstructions);
        Grid.SetRow(propInstructions, 0);

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
        Grid.SetRow(_propertyGrid, 1);

        // Tab C: Performance/Diagnostics Content
        _perfTabContent = new Grid { Margin = new Thickness(8) };
        _perfTabContent.RowDefinitions.Add(new GridLength(1, GridUnitType.Star));
        _perfTabContent.RowDefinitions.Add(new GridLength(45, GridUnitType.Absolute));

        _perfTextBlock = new RichTextBlock { FontSize = 12f, VerticalAlignment = VerticalAlignment.Stretch };
        _perfTabContent.AddChild(_perfTextBlock);
        Grid.SetRow(_perfTextBlock, 0);

        var vsyncStack = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        
        var currentVsync = ProGPU.Backend.WgpuContext.Current?.VSync ?? false;
        var vsyncText = new TextVisual
        {
            Text = currentVsync ? "VSync: ON" : "VSync: OFF",
            FontSize = 11f,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 12, 0)
        };
        
        var vsyncBtn = new Button
        {
            Content = new TextVisual
            {
                Text = "Toggle VSync",
                FontSize = 10f,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            },
            Background = ThemeManager.GetBrush("ControlBackground"),
            WidthConstraint = 100f,
            HeightConstraint = 28f
        };
        
        vsyncBtn.Click += (s, e) =>
        {
            var context = ProGPU.Backend.WgpuContext.Current;
            if (context != null)
            {
                bool nextVal = !context.VSync;
                
                // Update VSync for all active WebGPU contexts and their GLFW windows globally
                foreach (var ctx in ProGPU.Backend.WgpuContext.ActiveContexts)
                {
                    ctx.VSync = nextVal;
                    if (ctx.Window != null)
                    {
                        ctx.Window.VSync = nextVal;
                    }
                }
                
                vsyncText.Text = nextVal ? "VSync: ON" : "VSync: OFF";
            }
        };

        vsyncStack.AddChild(vsyncText);
        vsyncStack.AddChild(vsyncBtn);
        
        _perfTabContent.AddChild(vsyncStack);
        Grid.SetRow(vsyncStack, 1);

        // 3. PIVOT TAB CONTROL
        _pivot = new Pivot { HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Stretch };
        
        var visualTreeItem = new PivotItem("Logical & Visual Tree", _visualTreeTabContent);
        var propertiesItem = new PivotItem("Property Editor", _propertyTabContent);
        var diagnosticsItem = new PivotItem("Diagnostics", _perfTabContent);

        _pivot.Items.Add(visualTreeItem);
        _pivot.Items.Add(propertiesItem);
        _pivot.Items.Add(diagnosticsItem);

        _mainGrid.AddChild(_pivot);
        Grid.SetRow(_pivot, 1);

        _pivot.SelectionChanged += (s, e) =>
        {
            if (_pivot.SelectedIndex == 0)
            {
                RefreshVisualTree();
            }
            else if (_pivot.SelectedIndex == 1)
            {
                if (DevToolsService.InspectedElement != null)
                {
                    LoadProperties(DevToolsService.InspectedElement);
                }
            }
        };

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

        // Explicitly resolve and set Font for Pivot & DataGrid
        var resolvedFont = PopupService.DefaultFont;
        if (resolvedFont == null)
        {
            try
            {
                var asm = AppDomain.CurrentDomain.GetAssemblies();
                foreach (var assembly in asm)
                {
                    var type = assembly.GetType("ProGPU.Samples.AppState") ?? assembly.GetType("ProGPU.Samples.Program");
                    if (type != null)
                    {
                        var method = type.GetMethod("GetFont");
                        if (method != null && method.Invoke(null, null) is TtfFont staticFont)
                        {
                            resolvedFont = staticFont;
                            break;
                        }
                    }
                }
            }
            catch { }
        }

        if (resolvedFont != null)
        {
            _pivot.Font = resolvedFont;
            _propertyGrid.Font = resolvedFont;
        }
    }

    private void UpdateInspectButtonState()
    {
        _inspectBtn.Background = DevToolsService.IsInspectModeActive 
            ? ThemeManager.GetBrush("SystemAccentColor")  // Fluent Accent
            : ThemeManager.GetBrush("ControlBackground"); // Standard background
    }

    public void RefreshVisualTree()
    {
        _treeView.Items.Clear();
        var addedRoots = new HashSet<Visual>();

        // 1. Gather all active application windows (excluding DevTools itself)
        var activeWindows = WindowManager.ActiveWindows;
        for (int i = 0; i < activeWindows.Count; i++)
        {
            var window = activeWindows[i];
            if (window.Content == null || window.Content == this || window.Title.Contains("Developer Tools"))
            {
                continue;
            }

            var root = window.Content;
            addedRoots.Add(root);

            string rootName = string.IsNullOrEmpty(root.Name) 
                ? $"{window.Title} [{root.GetType().Name}]" 
                : $"{window.Title} - {root.GetType().Name} ({root.Name})";

            var rootItem = new TreeViewItem(rootName)
            {
                TagValue = root,
                IsExpanded = true
            };
            _treeView.Items.Add(rootItem);
            PopulateVisualTree(root, rootItem);
        }

        // Fallback: If the main window wasn't registered in WindowManager (e.g. raw Silk.NET window),
        // we retrieve it directly from the thread-static InputSystem.Root!
        if (InputSystem.Root != null && !addedRoots.Contains(InputSystem.Root))
        {
            var root = InputSystem.Root;
            addedRoots.Add(root);

            string rootName = string.IsNullOrEmpty(root.Name) 
                ? $"Main Window [{root.GetType().Name}]" 
                : $"Main Window - {root.GetType().Name} ({root.Name})";

            var rootItem = new TreeViewItem(rootName)
            {
                TagValue = root,
                IsExpanded = true
            };
            _treeView.Items.Add(rootItem);
            PopulateVisualTree(root, rootItem);
        }

        // 2. Gather all active floating popups and dialogs from PopupService
        var activePopups = PopupService.ActivePopups;
        if (activePopups.Count > 0)
        {
            var popupsRootItem = new TreeViewItem("Active Popups & Dialogs")
            {
                IsExpanded = true
            };
            _treeView.Items.Add(popupsRootItem);

            for (int i = 0; i < activePopups.Count; i++)
            {
                var popup = activePopups[i];
                if (popup == this || addedRoots.Contains(popup)) continue;

                string popupName = string.IsNullOrEmpty(popup.Name) 
                    ? popup.GetType().Name 
                    : $"{popup.GetType().Name} ({popup.Name})";

                var popupItem = new TreeViewItem(popupName)
                {
                    TagValue = popup,
                    IsExpanded = true
                };
                popupsRootItem.Items.Add(popupItem);
                PopulateVisualTree(popup, popupItem);
            }
        }

        _treeView.RefreshTree();
    }

    private void PopulateVisualTree(Visual node, TreeViewItem parentItem)
    {
        if (node is ContainerVisual container)
        {
            foreach (var child in container.Children)
            {
                if (child == this || (child is FrameworkElement devToolsFe && devToolsFe.Name == "DevToolsPanel"))
                {
                    continue;
                }

                string typeName = child.GetType().Name;
                string details = "";
                
                if (child is FrameworkElement fe)
                {
                    if (!string.IsNullOrEmpty(fe.Name))
                    {
                        details = $" ({fe.Name})";
                    }
                }
                else
                {
                    details = " [Visual]";
                }
                
                string label = $"{typeName}{details}";
                var childItem = new TreeViewItem(label)
                {
                    TagValue = child
                };
                
                parentItem.Items.Add(childItem);
                PopulateVisualTree(child, childItem);
            }
        }
    }

    private void OnTreeSelectionChanged(object? sender, EventArgs e)
    {
        if (_treeView.SelectedItem != null && _treeView.SelectedItem.TagValue is Visual visual)
        {
            DevToolsService.InspectedElement = visual;
            LoadProperties(visual);
        }
    }

    private void SelectInspectedElementInTree(Visual element)
    {
        TreeViewItem? found = FindTreeViewItemByElement(_treeView.Items, element);
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

    private TreeViewItem? FindTreeViewItemByElement(IList<TreeViewItem> items, Visual element)
    {
        foreach (var item in items)
        {
            if (item.TagValue == element) return item;
            var found = FindTreeViewItemByElement(item.Items, element);
            if (found != null) return found;
        }
        return null;
    }

    private TreeViewItem? GetTreeViewItemParent(TreeViewItem item)
    {
        return FindParentInList(_treeView.Items, item);
    }

    private TreeViewItem? FindParentInList(IList<TreeViewItem> items, TreeViewItem targetItem)
    {
        foreach (var item in items)
        {
            if (item.Items.Contains(targetItem)) return item;
            var found = FindParentInList(item.Items, targetItem);
            if (found != null) return found;
        }
        return null;
    }

    private void LoadProperties(Visual fe)
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
            _perfTextBlock.Inlines.Add(new Bold(new Run("Runtime Performance & Rendering Diagnostics\n\n")) { Foreground = ThemeManager.GetBrush("SystemAccentColor") });
            
            _perfTextBlock.Inlines.Add(new Run("Real-time Frame Rate: "));
            _perfTextBlock.Inlines.Add(new Bold(new Run($"{fps:F1} FPS\n")) { Foreground = fps >= 55f ? new SolidColorBrush(0x4CAF50FF) : new SolidColorBrush(0xF44336FF) });
            
            _perfTextBlock.Inlines.Add(new Run("CPU Frame Render Time: "));
            _perfTextBlock.Inlines.Add(new Bold(new Run($"{cpuMs:F2} ms\n")));
            
            _perfTextBlock.Inlines.Add(new Run("Composed Vertex Count: "));
            _perfTextBlock.Inlines.Add(new Bold(new Run($"{vertices:N0} vertices\n")));

            _perfTextBlock.Inlines.Add(new Run("Batched Draw Calls: "));
            _perfTextBlock.Inlines.Add(new Bold(new Run($"{drawCalls:N0} calls\n")));

            // Count total scene graph nodes dynamically!
            int totalNodes = 0;
            Vector2 mainWinSize = Vector2.Zero;
            var activeWindows = WindowManager.ActiveWindows;
            for (int i = 0; i < activeWindows.Count; i++)
            {
                var window = activeWindows[i];
                if (window.Content != null && window.Content != this && !window.Title.Contains("Developer Tools"))
                {
                    totalNodes += CountSceneNodes(window.Content);
                    mainWinSize = window.Content.Size;
                }
            }
            _perfTextBlock.Inlines.Add(new Run("Total Scene Graph Nodes: "));
            _perfTextBlock.Inlines.Add(new Bold(new Run($"{totalNodes:N0} nodes\n")));

            // Active theme and window info
            string activeTheme = ThemeManager.CurrentTheme.ToString();
            _perfTextBlock.Inlines.Add(new Run("Active Application Theme: "));
            _perfTextBlock.Inlines.Add(new Bold(new Run($"{activeTheme}\n")));

            if (mainWinSize != Vector2.Zero)
            {
                _perfTextBlock.Inlines.Add(new Run("Main Window Dimensions: "));
                _perfTextBlock.Inlines.Add(new Bold(new Run($"{mainWinSize.X:N0} × {mainWinSize.Y:N0}\n")));
            }

            // GPU Backend context details
            var wgpuCtx = ProGPU.Backend.WgpuContext.Current;
            if (wgpuCtx != null)
            {
                _perfTextBlock.Inlines.Add(new Run("WebGPU Context Target: "));
                _perfTextBlock.Inlines.Add(new Bold(new Run("Metal/Vulkan (macOS GPU Engine)\n")));
            }

            _perfTextBlock.Inlines.Add(new Bold(new Run("\nDeveloper Adorner Layer:\n")) { Foreground = ThemeManager.GetBrush("TextSecondary") });
            _perfTextBlock.Inlines.Add(new Run("• Ctrl+Shift + Hover: High-performance select highlights.\n"));
            _perfTextBlock.Inlines.Add(new Run("• Ctrl+Shift + Click: Inspect element boundaries and auto-open tree.\n"));
            _perfTextBlock.Inlines.Add(new Run("• Green outline: Hovered element inspection bounds.\n"));
            _perfTextBlock.Inlines.Add(new Run("• Blue outline: Currently selected element focus.\n"));
            _perfTextBlock.Inlines.Add(new Run("• Properties Tab: Double-click cells in 'Value' column to edit in real-time.\n"));
            
            _perfTextBlock.Invalidate();
        }
    }

    private int CountSceneNodes(Visual node)
    {
        int count = 1;
        if (node is ContainerVisual container)
        {
            foreach (var child in container.Children)
            {
                count += CountSceneNodes(child);
            }
        }
        return count;
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
        
        context.DrawCircle(null, pen, new Vector2(4.5f, 4.5f), 3.5f);
        context.DrawLine(pen, new Vector2(7f, 7f), new Vector2(11f, 11f));
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
        
        context.DrawLine(pen, new Vector2(1f, 1f), new Vector2(9f, 9f));
        context.DrawLine(pen, new Vector2(9f, 1f), new Vector2(1f, 9f));
        base.OnRender(context);
    }
}
