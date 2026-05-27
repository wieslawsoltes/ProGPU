using System;
using System.Collections.Generic;
using System.Numerics;
using System.Reflection;
using System.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Documents;
using ProGPU.Layout;
using ProGPU.Vector;
using ProGPU.Text;
using ProGPU.Scene;

using StackPanel = Microsoft.UI.Xaml.Controls.StackPanel;
using Grid = Microsoft.UI.Xaml.Controls.Grid;
using Thickness = Microsoft.UI.Xaml.Thickness;

namespace ProGPU.Designer;

public class DesignerHost : Grid
{
    private readonly Border _sidebarLeftBorder;
    private readonly Grid _sidebarLeft;
    private readonly Grid _workspaceCenter;
    private readonly Border _sidebarRightBorder;
    private readonly PropertyGrid _propertyGrid;
    private readonly Border _bottomPanel;
    
    private readonly Toolbox _toolbox;
    private readonly StackPanel _outlinePanel;
    private readonly RichTextBlock _csharpCodeBlock;
    
    private readonly DesignerCanvas _designerCanvas;
    
    private bool _isBottomExpanded = false;

    public DesignerCanvas WorkspaceCanvas => _designerCanvas;

    public TtfFont? DesignerFont { get; set; }
    public TtfFont? DesignerFontCourier { get; set; }
    public Func<float>? GetDpiScale { get; set; }

    public DesignerHost()
    {
        RowDefinitions.Add(GridLength.Star(1f));
        RowDefinitions.Add(GridLength.Auto);
        
        var contentGrid = new Grid();
        contentGrid.ColumnDefinitions.Add(new GridLength(260f, GridUnitType.Absolute));
        contentGrid.ColumnDefinitions.Add(GridLength.Star(1f));
        contentGrid.ColumnDefinitions.Add(new GridLength(280f, GridUnitType.Absolute));
        
        Grid.SetRow(contentGrid, 0);
        AddChild(contentGrid);

        // 1. Left Sidebar
        _sidebarLeftBorder = new Border
        {
            Background = new ThemeResourceBrush("CardBackground"),
            BorderThickness = new Thickness(0, 0, 1, 0),
            BorderBrush = new ThemeResourceBrush("ControlBorder")
        };
        _sidebarLeft = new Grid();
        _sidebarLeftBorder.Child = _sidebarLeft;

        _sidebarLeft.RowDefinitions.Add(GridLength.Star(1.0f));
        _sidebarLeft.RowDefinitions.Add(GridLength.Star(1.0f));
        Grid.SetColumn(_sidebarLeftBorder, 0);
        contentGrid.AddChild(_sidebarLeftBorder);

        // Top half: Toolbox
        _toolbox = new Toolbox(DesignerFont);
        Grid.SetRow(_toolbox, 0);
        _sidebarLeft.AddChild(_toolbox);

        // Bottom half: Visual Tree Outline
        var outlineContainer = new Border
        {
            Background = new ThemeResourceBrush("CardBackground"),
            BorderThickness = new Thickness(0, 1, 0, 0),
            BorderBrush = new ThemeResourceBrush("ControlBorder"),
            Padding = new Thickness(8)
        };
        var outlineStack = new StackPanel { Orientation = Orientation.Vertical, HorizontalAlignment = HorizontalAlignment.Stretch };
        var outlineTitle = new RichTextBlock { FontSize = 14f, Margin = new Thickness(4, 4, 4, 8) };
        outlineTitle.Inlines.Add(new Bold(new Run("Visual Tree Outline")));
        outlineStack.AddChild(outlineTitle);

        _outlinePanel = new StackPanel { Orientation = Orientation.Vertical, HorizontalAlignment = HorizontalAlignment.Stretch };
        outlineStack.AddChild(_outlinePanel);

        var outlineScroll = new ScrollViewer
        {
            Content = outlineStack,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch
        };
        outlineContainer.Child = outlineScroll;

        Grid.SetRow(outlineContainer, 1);
        _sidebarLeft.AddChild(outlineContainer);

        // 2. Center Workspace
        _workspaceCenter = new Grid();
        _workspaceCenter.RowDefinitions.Add(GridLength.Auto);
        _workspaceCenter.RowDefinitions.Add(GridLength.Star(1f));
        Grid.SetColumn(_workspaceCenter, 1);
        contentGrid.AddChild(_workspaceCenter);

        var actionBar = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(12, 8, 12, 8) };
        var gridLinesCheck = new CheckBox { IsChecked = true, Margin = new Thickness(0, 0, 16, 0) };
        var gridLinesLabel = new RichTextBlock { FontSize = 11f };
        gridLinesLabel.Inlines.Add(new Run("Show Snap Grid Lines"));
        gridLinesCheck.Content = gridLinesLabel;
        gridLinesCheck.CheckedChanged += (s, e) => {
            _designerCanvas.ShowGridLines = gridLinesCheck.IsChecked;
            _designerCanvas.Invalidate();
        };
        actionBar.AddChild(gridLinesCheck);

        var snapCheck = new CheckBox { IsChecked = true, Margin = new Thickness(0, 0, 16, 0) };
        var snapLabel = new RichTextBlock { FontSize = 11f };
        snapLabel.Inlines.Add(new Run("Snap to Grid (10px)"));
        snapCheck.Content = snapLabel;
        snapCheck.CheckedChanged += (s, e) => {
            _designerCanvas.GridSnappingEnabled = snapCheck.IsChecked;
        };
        actionBar.AddChild(snapCheck);

        var clearBtn = new Button { Width = 100f, Height = 28f, Margin = new Thickness(16, 0, 0, 0) };
        var clearBtnText = new RichTextBlock { FontSize = 11f, Foreground = new ThemeResourceBrush("TextPrimary") };
        clearBtnText.Inlines.Add(new Run("Clear Workspace"));
        clearBtn.Content = clearBtnText;
        clearBtn.Click += (s, e) => {
            _designerCanvas.DesignSurface.Children.Clear();
            _designerCanvas.SelectElement(null);
            _designerCanvas.Invalidate();
            OnCanvasModified();
            UpdateOutline();
        };
        actionBar.AddChild(clearBtn);

        Grid.SetRow(actionBar, 0);
        _workspaceCenter.AddChild(actionBar);

        _designerCanvas = new DesignerCanvas();
        _designerCanvas.SelectionChanged += () => {
            _propertyGrid.SelectedElement = _designerCanvas.SelectedElement;
            UpdateOutline();
        };
        _designerCanvas.CanvasModified += OnCanvasModified;
        
        var canvasScrollViewer = new ScrollViewer
        {
            Content = _designerCanvas,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch
        };

        Grid.SetRow(canvasScrollViewer, 1);
        _workspaceCenter.AddChild(canvasScrollViewer);

        // 3. Right Sidebar - PropertyGrid
        _propertyGrid = new PropertyGrid(DesignerFont);
        _propertyGrid.PropertyChanged += () => {
            _designerCanvas.UpdateSelectionAdorner();
            _designerCanvas.Invalidate();
            OnCanvasModified();
            UpdateOutline();
        };

        _sidebarRightBorder = new Border
        {
            Background = new ThemeResourceBrush("CardBackground"),
            BorderThickness = new Thickness(1, 0, 0, 0),
            BorderBrush = new ThemeResourceBrush("ControlBorder"),
            Child = _propertyGrid
        };
        Grid.SetColumn(_sidebarRightBorder, 2);
        contentGrid.AddChild(_sidebarRightBorder);

        // 4. Bottom Collapsible Panel - C# Script Preview
        _bottomPanel = new Border { Background = new ThemeResourceBrush("CardBackground") };
        _bottomPanel.BorderThickness = new Thickness(0, 1, 0, 0);
        _bottomPanel.BorderBrush = new ThemeResourceBrush("ControlBorder");
        Grid.SetRow(_bottomPanel, 1);
        AddChild(_bottomPanel);

        var bottomContainer = new StackPanel { Orientation = Orientation.Vertical };
        _bottomPanel.Child = bottomContainer;

        var bottomHeaderBorder = new Border
        {
            Background = new ThemeResourceBrush("ControlBackground"),
            Height = 32f
        };
        var bottomHeader = new Grid();
        bottomHeader.ColumnDefinitions.Add(GridLength.Star(1f));
        bottomHeader.ColumnDefinitions.Add(GridLength.Auto);
        bottomHeaderBorder.Child = bottomHeader;
        bottomContainer.AddChild(bottomHeaderBorder);

        var bottomTitle = new RichTextBlock { FontSize = 12f, Margin = new Thickness(12, 6, 0, 0) };
        bottomTitle.Inlines.Add(new Bold(new Run("LIVE C# CREATION SCRIPT PREVIEW")));
        Grid.SetColumn(bottomTitle, 0);
        bottomHeader.AddChild(bottomTitle);

        var toggleBottomBtn = new Button { Width = 140f, Height = 24f, Margin = new Thickness(0, 4, 12, 0) };
        var toggleText = new RichTextBlock { FontSize = 11f, Foreground = new ThemeResourceBrush("TextPrimary") };
        toggleText.Inlines.Add(new Run("Expand Preview Panel"));
        toggleBottomBtn.Content = toggleText;
        
        _csharpCodeBlock = new RichTextBlock { FontSize = 11f, Margin = new Thickness(12) };
        _csharpCodeBlock.Foreground = new ThemeResourceBrush("TextSecondary");

        toggleBottomBtn.Click += (s, e) => {
            _isBottomExpanded = !_isBottomExpanded;
            if (_isBottomExpanded)
            {
                toggleText.Inlines.Clear();
                toggleText.Inlines.Add(new Run("Collapse Preview Panel"));
                bottomContainer.AddChild(_csharpCodeBlock);
                OnCanvasModified();
            }
            else
            {
                toggleText.Inlines.Clear();
                toggleText.Inlines.Add(new Run("Expand Preview Panel"));
                bottomContainer.RemoveChild(_csharpCodeBlock);
                _csharpCodeBlock.Inlines.Clear();
            }
            InvalidateMeasure();
            InvalidateArrange();
        };
        Grid.SetColumn(toggleBottomBtn, 1);
        bottomHeader.AddChild(toggleBottomBtn);

        TtfFont primaryFont = DesignerFont ?? Microsoft.UI.Xaml.Controls.PopupService.DefaultFont;
        TtfFont courierFont = DesignerFontCourier ?? DesignerFont ?? Microsoft.UI.Xaml.Controls.PopupService.DefaultFont;
        
        outlineTitle.Font = primaryFont;
        gridLinesLabel.Font = primaryFont;
        snapLabel.Font = primaryFont;
        clearBtnText.Font = primaryFont;
        bottomTitle.Font = primaryFont;
        toggleText.Font = primaryFont;
        _csharpCodeBlock.Font = courierFont;

        OnCanvasModified();
        UpdateOutline();
    }

    public void InitializeFonts(TtfFont mainFont, TtfFont codeFont)
    {
        DesignerFont = mainFont;
        DesignerFontCourier = codeFont;

        TtfFont primaryFont = DesignerFont ?? Microsoft.UI.Xaml.Controls.PopupService.DefaultFont;
        TtfFont courierFont = DesignerFontCourier ?? DesignerFont ?? Microsoft.UI.Xaml.Controls.PopupService.DefaultFont;
        
        foreach (var child in Children)
        {
            ApplyFontToVisualTree(child, primaryFont, courierFont);
        }
        
        _csharpCodeBlock.Font = courierFont;
        
        UpdateOutline();
        OnCanvasModified();
    }

    private void ApplyFontToVisualTree(Visual element, TtfFont mainFont, TtfFont codeFont)
    {
        if (element is RichTextBlock rtb)
        {
            if (rtb == _csharpCodeBlock) rtb.Font = codeFont;
            else rtb.Font = mainFont;
        }
        else if (element is TextBox tb)
        {
            tb.Font = mainFont;
        }
        else if (element is ContainerVisual p)
        {
            foreach (var c in p.Children) ApplyFontToVisualTree(c, mainFont, codeFont);
        }
        else if (element is Border b && b.Child != null)
        {
            ApplyFontToVisualTree(b.Child, mainFont, codeFont);
        }
    }

    public void AddControlToCanvas(string controlType, float defaultX = 100f, float defaultY = 100f)
    {
        Type? type = null;
        string[] searchNamespaces = {
            "Microsoft.UI.Xaml.Controls",
            "Microsoft.UI.Xaml",
            "ProGPU.Designer"
        };

        foreach (var ns in searchNamespaces)
        {
            var typeName = $"{ns}.{controlType}";
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                type = assembly.GetType(typeName);
                if (type != null) break;
            }
            if (type != null) break;
        }

        if (type != null && typeof(FrameworkElement).IsAssignableFrom(type))
        {
            try
            {
                var newInstance = Activator.CreateInstance(type) as FrameworkElement;
                if (newInstance != null)
                {
                    newInstance.IsHitTestVisible = false;
                    Canvas.SetLeft(newInstance, defaultX);
                    Canvas.SetTop(newInstance, defaultY);

                    if (float.IsNaN(newInstance.Width) || newInstance.Width <= 0) newInstance.Width = 120f;
                    if (float.IsNaN(newInstance.Height) || newInstance.Height <= 0) newInstance.Height = 36f;

                    newInstance.Name = $"{controlType}_{_designerCanvas.DesignSurface.Children.Count + 1}";

                    if (newInstance is Button button)
                    {
                        var richText = new RichTextBlock { Font = DesignerFont ?? PopupService.DefaultFont };
                        richText.Inlines.Add(new Run(controlType));
                        button.Content = richText;
                    }
                    else if (newInstance is TextBlock textBlock)
                    {
                        textBlock.Text = controlType;
                    }

                    _designerCanvas.DesignSurface.Children.Add(newInstance);
                    _designerCanvas.SelectElement(newInstance);

                    OnCanvasModified();
                    UpdateOutline();
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[DesignerHost] Error instantiating {controlType}: {ex.Message}");
            }
        }
    }

    private void OnCanvasModified()
    {
        if (!_isBottomExpanded)
        {
            _csharpCodeBlock.Inlines.Clear();
            return;
        }

        string csharpScript = DesignerSerializer.SerializeToCSharp(_designerCanvas.DesignSurface);
        _csharpCodeBlock.Inlines.Clear();
        
        var lines = csharpScript.Split('\n');
        foreach (var line in lines)
        {
            var run = new Run(line + "\n");
            var trimmed = line.Trim();
            if (trimmed.StartsWith("var ") || trimmed.StartsWith("Canvas.Set") || trimmed.StartsWith("Grid.Set"))
            {
                run.Foreground = new ThemeResourceBrush("SystemAccentColor");
            }
            else if (trimmed.StartsWith("//"))
            {
                run.Foreground = new ThemeResourceBrush("TextSecondary");
            }
            _csharpCodeBlock.Inlines.Add(run);
        }
        _csharpCodeBlock.Invalidate();
    }

    private void UpdateOutline()
    {
        _outlinePanel.Children.Clear();
        foreach (var child in _designerCanvas.DesignSurface.Children)
        {
            if (child is FrameworkElement fe)
            {
                var selectBtn = new Button { Width = 220f, Height = 28f, Margin = new Thickness(0, 2, 0, 2) };
                
                if (fe == _designerCanvas.SelectedElement)
                {
                    selectBtn.Background = new ThemeResourceBrush("SystemAccentColor");
                }
                else
                {
                    selectBtn.Background = new ThemeResourceBrush("ButtonBackground");
                }

                var label = new RichTextBlock { Font = DesignerFont ?? Microsoft.UI.Xaml.Controls.PopupService.DefaultFont, FontSize = 11f };
                string displayName = string.IsNullOrEmpty(fe.Name) ? fe.GetType().Name : fe.Name;
                label.Inlines.Add(new Run(displayName));
                
                selectBtn.Content = label;
                selectBtn.Click += (s, e) => {
                    _designerCanvas.SelectElement(fe);
                    _designerCanvas.Invalidate();
                    UpdateOutline();
                };
                _outlinePanel.AddChild(selectBtn);
            }
        }
        _outlinePanel.InvalidateMeasure();
    }
}
