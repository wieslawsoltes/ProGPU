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
    private readonly VisualTreeOutline _visualTreeOutline;
    private readonly VirtualizedCodeEditor _csharpCodeBlock;
    
    private readonly DesignerCanvas _designerCanvas;
    private RichTextBlock? _zoomValText;
    private RichTextBlock? _zoomOutText;
    private RichTextBlock? _zoomInText;
    private RichTextBlock? _outlinesLabelText;
    
    private class DesignState
    {
        public List<FrameworkElement> Elements { get; } = new();
        public string SelectedElementName { get; } = string.Empty;

        public DesignState(List<FrameworkElement> elements, string selectedElementName)
        {
            Elements = elements;
            SelectedElementName = selectedElementName;
        }
    }

    private readonly Stack<DesignState> _undoStack = new();
    private readonly Stack<DesignState> _redoStack = new();
    private bool _isApplyingHistoryState = false;
    private FrameworkElement? _clipboardElement;

    private bool _isBottomExpanded = false;

    public DesignerCanvas WorkspaceCanvas => _designerCanvas;

    public TtfFont? DesignerFont { get; set; }
    public TtfFont? DesignerFontCourier { get; set; }
    public Func<float>? GetDpiScale
    {
        get => _designerCanvas.GetDpiScale;
        set => _designerCanvas.GetDpiScale = value;
    }

    public DesignerHost()
    {
        _designerCanvas = new DesignerCanvas();
        _designerCanvas.CanvasModifying += () => {
            SaveUndoState();
        };
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
        _visualTreeOutline = new VisualTreeOutline(DesignerFont);
        _visualTreeOutline.CanvasModifying += () => {
            SaveUndoState();
        };
        _visualTreeOutline.BorderThickness = new Thickness(0, 1, 0, 0); // border separator
        _visualTreeOutline.CornerRadius = 0f; // clean look
        _visualTreeOutline.SelectionChanged += (fe) => {
            _designerCanvas.SelectElement(fe);
            _designerCanvas.Invalidate();
        };
        _visualTreeOutline.CanvasModified += () => {
            OnCanvasModified();
            _designerCanvas.Invalidate();
        };

        Grid.SetRow(_visualTreeOutline, 1);
        _sidebarLeft.AddChild(_visualTreeOutline);

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

        var outlinesCheck = new CheckBox { IsChecked = false, Margin = new Thickness(0, 0, 16, 0) };
        _outlinesLabelText = new RichTextBlock { FontSize = 11f };
        _outlinesLabelText.Inlines.Add(new Run("Always Show Panel Outlines"));
        outlinesCheck.Content = _outlinesLabelText;
        outlinesCheck.CheckedChanged += (s, e) => {
            _designerCanvas.AlwaysShowPanelOutlines = outlinesCheck.IsChecked;
            _designerCanvas.Invalidate();
        };
        actionBar.AddChild(outlinesCheck);

        var clearBtn = new Button { Width = 130f, Height = 32f, Margin = new Thickness(16, 0, 0, 0) };
        var clearBtnText = new RichTextBlock { FontSize = 11f, Foreground = new ThemeResourceBrush("TextPrimary") };
        clearBtnText.Inlines.Add(new Run("Clear Workspace"));
        clearBtn.Content = clearBtnText;
        clearBtn.Click += (s, e) => {
            SaveUndoState();
            _designerCanvas.DesignSurface.Children.Clear();
            _designerCanvas.SelectElement(null);
            _designerCanvas.Invalidate();
            OnCanvasModified();
            UpdateOutline();
        };
        actionBar.AddChild(clearBtn);

        // Zoom Controls
        var zoomOutBtn = new Button { Width = 32f, Height = 32f, Margin = new Thickness(24, 0, 0, 0) };
        _zoomOutText = new RichTextBlock { FontSize = 12f, Foreground = new ThemeResourceBrush("TextPrimary"), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        _zoomOutText.Inlines.Add(new Run("-"));
        zoomOutBtn.Content = _zoomOutText;

        _zoomValText = new RichTextBlock { FontSize = 11f, Margin = new Thickness(8, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
        _zoomValText.Inlines.Add(new Run("100%"));

        var zoomInBtn = new Button { Width = 32f, Height = 32f, Margin = new Thickness(0, 0, 0, 0) };
        _zoomInText = new RichTextBlock { FontSize = 12f, Foreground = new ThemeResourceBrush("TextPrimary"), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        _zoomInText.Inlines.Add(new Run("+"));
        zoomInBtn.Content = _zoomInText;

        zoomOutBtn.Click += (s, e) => {
            float oldZoom = _designerCanvas.ZoomScale;
            float newZoom = Math.Clamp(oldZoom - 0.1f, 0.15f, 4.0f);
            _designerCanvas.ZoomScale = newZoom;
            _designerCanvas.ApplyTransforms();
            _designerCanvas.Invalidate();
            UpdateZoomLabel();
        };

        zoomInBtn.Click += (s, e) => {
            float oldZoom = _designerCanvas.ZoomScale;
            float newZoom = Math.Clamp(oldZoom + 0.1f, 0.15f, 4.0f);
            _designerCanvas.ZoomScale = newZoom;
            _designerCanvas.ApplyTransforms();
            _designerCanvas.Invalidate();
            UpdateZoomLabel();
        };

        actionBar.AddChild(zoomOutBtn);
        actionBar.AddChild(_zoomValText);
        actionBar.AddChild(zoomInBtn);

        Grid.SetRow(actionBar, 0);
        _workspaceCenter.AddChild(actionBar);

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
        _bottomPanel = new Border { Background = new ThemeResourceBrush("CardBackground"), Height = 20f };
        _bottomPanel.BorderThickness = new Thickness(0, 1, 0, 0);
        _bottomPanel.BorderBrush = new ThemeResourceBrush("ControlBorder");
        Grid.SetRow(_bottomPanel, 1);
        AddChild(_bottomPanel);

        var bottomContainer = new Grid();
        bottomContainer.RowDefinitions.Add(GridLength.Auto);
        bottomContainer.RowDefinitions.Add(GridLength.Star(1f));
        _bottomPanel.Child = bottomContainer;

        var bottomHeaderBorder = new Border
        {
            Background = new ThemeResourceBrush("ControlBackground"),
            Height = 20f
        };
        var bottomHeader = new Grid();
        bottomHeader.ColumnDefinitions.Add(GridLength.Star(1f));
        bottomHeader.ColumnDefinitions.Add(GridLength.Auto);
        bottomHeaderBorder.Child = bottomHeader;
        
        Grid.SetRow(bottomHeaderBorder, 0);
        bottomContainer.AddChild(bottomHeaderBorder);

        var bottomTitle = new RichTextBlock { FontSize = 9.5f, Margin = new Thickness(12, 3, 0, 0) };
        bottomTitle.Inlines.Add(new Bold(new Run("LIVE C# CREATION SCRIPT PREVIEW")));
        Grid.SetColumn(bottomTitle, 0);
        bottomHeader.AddChild(bottomTitle);

        var toggleBottomBtn = new Button { Width = 24f, Height = 14f, Margin = new Thickness(0, 3, 12, 0) };
        var toggleText = new RichTextBlock { FontSize = 8f, Foreground = new ThemeResourceBrush("TextPrimary"), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        toggleText.Inlines.Add(new Run("▲"));
        toggleBottomBtn.Content = toggleText;
        
        _csharpCodeBlock = new VirtualizedCodeEditor();
        Grid.SetRow(_csharpCodeBlock, 1);
        bottomContainer.AddChild(_csharpCodeBlock);

        toggleBottomBtn.Click += (s, e) => {
            _isBottomExpanded = !_isBottomExpanded;
            if (_isBottomExpanded)
            {
                toggleText.Inlines.Clear();
                toggleText.Inlines.Add(new Run("▼"));
                _bottomPanel.Height = 300f;
            }
            else
            {
                toggleText.Inlines.Clear();
                toggleText.Inlines.Add(new Run("▲"));
                _bottomPanel.Height = 20f;
            }
            InvalidateMeasure();
            InvalidateArrange();
        };
        Grid.SetColumn(toggleBottomBtn, 1);
        bottomHeader.AddChild(toggleBottomBtn);

        TtfFont primaryFont = DesignerFont ?? Microsoft.UI.Xaml.Controls.PopupService.DefaultFont;
        TtfFont courierFont = DesignerFontCourier ?? DesignerFont ?? Microsoft.UI.Xaml.Controls.PopupService.DefaultFont;
        
        gridLinesLabel.Font = primaryFont;
        snapLabel.Font = primaryFont;
        if (_outlinesLabelText != null) _outlinesLabelText.Font = primaryFont;
        clearBtnText.Font = primaryFont;
        if (_zoomOutText != null) _zoomOutText.Font = primaryFont;
        if (_zoomValText != null) _zoomValText.Font = primaryFont;
        if (_zoomInText != null) _zoomInText.Font = primaryFont;
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
        
        if (_zoomOutText != null) _zoomOutText.Font = primaryFont;
        if (_zoomValText != null) _zoomValText.Font = primaryFont;
        if (_zoomInText != null) _zoomInText.Font = primaryFont;
        if (_outlinesLabelText != null) _outlinesLabelText.Font = primaryFont;
        _csharpCodeBlock.Font = courierFont;
        _visualTreeOutline.Font = primaryFont;
        
        UpdateOutline();
        OnCanvasModified();
    }

    private void ApplyFontToVisualTree(Visual element, TtfFont mainFont, TtfFont codeFont)
    {
        if (element is VirtualizedCodeEditor vce)
        {
            vce.Font = codeFont;
        }
        else if (element is RichTextBlock rtb)
        {
            rtb.Font = mainFont;
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
        SaveUndoState();
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
                    else if (newInstance is CheckBox checkBox)
                    {
                        var richText = new RichTextBlock { Font = DesignerFont ?? PopupService.DefaultFont };
                        richText.Inlines.Add(new Run(controlType));
                        checkBox.Content = richText;
                    }
                    else if (newInstance is RadioButton radioButton)
                    {
                        var richText = new RichTextBlock { Font = DesignerFont ?? PopupService.DefaultFont };
                        richText.Inlines.Add(new Run(controlType));
                        radioButton.Content = richText;
                    }
                    else if (newInstance is ToggleSwitch toggleSwitch)
                    {
                        var richText = new RichTextBlock { Font = DesignerFont ?? PopupService.DefaultFont };
                        richText.Inlines.Add(new Run(controlType));
                        toggleSwitch.Content = richText;
                    }
                    else if (newInstance is ComboBox comboBox)
                    {
                        comboBox.PlaceholderText = controlType;
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
        UpdateZoomLabel();

        string csharpScript = DesignerSerializer.SerializeToCSharp(_designerCanvas.DesignSurface);
        _csharpCodeBlock.SetCode(csharpScript);
    }

    private void UpdateZoomLabel()
    {
        if (_zoomValText != null && _designerCanvas != null)
        {
            _zoomValText.Inlines.Clear();
            _zoomValText.Inlines.Add(new Run($"{(int)Math.Round(_designerCanvas.ZoomScale * 100f)}%"));
        }
    }

    private void UpdateOutline()
    {
        if (_visualTreeOutline != null)
        {
            _visualTreeOutline.SelectedElement = _designerCanvas.SelectedElement;
            _visualTreeOutline.RootElement = _designerCanvas.DesignSurface;
            _visualTreeOutline.RefreshTree();
        }
    }

    public override void OnKeyDown(KeyRoutedEventArgs e)
    {
        if (IsTextInputFocused())
        {
            base.OnKeyDown(e);
            return;
        }

        bool handled = false;

        // Handle Shortcuts
        if (e.Key == Silk.NET.Input.Key.Left)
        {
            NudgeSelectedElement(-1f, 0f);
            handled = true;
        }
        else if (e.Key == Silk.NET.Input.Key.Right)
        {
            NudgeSelectedElement(1f, 0f);
            handled = true;
        }
        else if (e.Key == Silk.NET.Input.Key.Up)
        {
            NudgeSelectedElement(0f, -1f);
            handled = true;
        }
        else if (e.Key == Silk.NET.Input.Key.Down)
        {
            NudgeSelectedElement(0f, 1f);
            handled = true;
        }
        else if (e.Key == Silk.NET.Input.Key.Delete || e.Key == Silk.NET.Input.Key.Backspace)
        {
            var sel = _designerCanvas.SelectedElement;
            if (sel != null && sel != _designerCanvas.DesignSurface)
            {
                SaveUndoState();
                _visualTreeOutline.DeleteElement(sel);
                handled = true;
            }
        }
        else if (e.Key == Silk.NET.Input.Key.D && InputSystem.Current.IsControlPressed)
        {
            var sel = _designerCanvas.SelectedElement;
            if (sel != null && sel != _designerCanvas.DesignSurface)
            {
                SaveUndoState();
                var dup = CloneElement(sel);
                if (dup != null)
                {
                    var parent = (sel.Parent ?? _designerCanvas.DesignSurface) as ContainerVisual;
                    VisualTreeOutlineItem.AddChildToTarget(parent as FrameworkElement, dup);

                    _designerCanvas.SelectElement(dup);
                    _designerCanvas.Invalidate();
                    OnCanvasModified();
                    UpdateOutline();
                }
                handled = true;
            }
        }
        else if (e.Key == Silk.NET.Input.Key.C && InputSystem.Current.IsControlPressed)
        {
            var sel = _designerCanvas.SelectedElement;
            if (sel != null && sel != _designerCanvas.DesignSurface)
            {
                _clipboardElement = sel;
                handled = true;
            }
        }
        else if (e.Key == Silk.NET.Input.Key.X && InputSystem.Current.IsControlPressed)
        {
            var sel = _designerCanvas.SelectedElement;
            if (sel != null && sel != _designerCanvas.DesignSurface)
            {
                _clipboardElement = sel;
                SaveUndoState();
                _visualTreeOutline.DeleteElement(sel);
                handled = true;
            }
        }
        else if (e.Key == Silk.NET.Input.Key.V && InputSystem.Current.IsControlPressed)
        {
            if (_clipboardElement != null)
            {
                SaveUndoState();
                var pasted = CloneElement(_clipboardElement);
                if (pasted != null)
                {
                    var sel = _designerCanvas.SelectedElement;
                    var parent = (sel?.Parent ?? _designerCanvas.DesignSurface) as ContainerVisual;
                    VisualTreeOutlineItem.AddChildToTarget(parent as FrameworkElement, pasted);

                    _designerCanvas.SelectElement(pasted);
                    _designerCanvas.Invalidate();
                    OnCanvasModified();
                    UpdateOutline();
                }
                handled = true;
            }
        }
        else if (e.Key == Silk.NET.Input.Key.Z && InputSystem.Current.IsControlPressed)
        {
            TriggerUndo();
            handled = true;
        }
        else if (e.Key == Silk.NET.Input.Key.Y && InputSystem.Current.IsControlPressed)
        {
            TriggerRedo();
            handled = true;
        }

        if (handled)
        {
            e.Handled = true;
            return;
        }

        base.OnKeyDown(e);
    }

    private bool IsTextInputFocused()
    {
        var focused = InputSystem.FocusedElement;
        if (focused == null) return false;

        var current = focused;
        while (current != null)
        {
            if (current is TextBox || current is PasswordBox || current is VirtualizedCodeEditor)
            {
                return true;
            }
            current = current.Parent as FrameworkElement;
        }
        return false;
    }

    private void NudgeSelectedElement(float dx, float dy)
    {
        var sel = _designerCanvas.SelectedElement;
        if (sel == null || sel == _designerCanvas.DesignSurface) return;

        SaveUndoState();

        float factor = InputSystem.Current.IsShiftPressed ? 10f : 1f;
        float currentLeft = Canvas.GetLeft(sel);
        float currentTop = Canvas.GetTop(sel);

        float newLeft = currentLeft + dx * factor;
        float newTop = currentTop + dy * factor;

        Canvas.SetLeft(sel, newLeft);
        Canvas.SetTop(sel, newTop);

        _designerCanvas.UpdateSelectionAdorner();
        _designerCanvas.Invalidate();
        OnCanvasModified();
    }

    public void SaveUndoState()
    {
        if (_isApplyingHistoryState) return;

        _redoStack.Clear();

        var savedList = new List<FrameworkElement>();
        foreach (var child in _designerCanvas.DesignSurface.Children)
        {
            if (child is FrameworkElement fe)
            {
                var cloned = CloneElementForUndo(fe);
                if (cloned != null)
                {
                    savedList.Add(cloned);
                }
            }
        }

        string selName = _designerCanvas.SelectedElement?.Name ?? "";
        _undoStack.Push(new DesignState(savedList, selName));

        if (_undoStack.Count > 50)
        {
            var temp = new Stack<DesignState>();
            while (_undoStack.Count > 1) temp.Push(_undoStack.Pop());
            _undoStack.Pop();
            while (temp.Count > 0) _undoStack.Push(temp.Pop());
        }
    }

    private void TriggerUndo()
    {
        if (_undoStack.Count == 0) return;

        _isApplyingHistoryState = true;
        try
        {
            var currentList = new List<FrameworkElement>();
            foreach (var child in _designerCanvas.DesignSurface.Children)
            {
                if (child is FrameworkElement fe)
                {
                    var cloned = CloneElementForUndo(fe);
                    if (cloned != null) currentList.Add(cloned);
                }
            }
            string currentSel = _designerCanvas.SelectedElement?.Name ?? "";
            _redoStack.Push(new DesignState(currentList, currentSel));

            var state = _undoStack.Pop();
            _designerCanvas.DesignSurface.Children.Clear();
            foreach (var fe in state.Elements)
            {
                _designerCanvas.DesignSurface.Children.Add(fe);
            }

            FrameworkElement? newSel = null;
            if (!string.IsNullOrEmpty(state.SelectedElementName))
            {
                newSel = FindElementByName(_designerCanvas.DesignSurface, state.SelectedElementName);
            }
            _designerCanvas.SelectElement(newSel);

            _designerCanvas.Invalidate();
            OnCanvasModified();
            UpdateOutline();
        }
        finally
        {
            _isApplyingHistoryState = false;
        }
    }

    private void TriggerRedo()
    {
        if (_redoStack.Count == 0) return;

        _isApplyingHistoryState = true;
        try
        {
            var currentList = new List<FrameworkElement>();
            foreach (var child in _designerCanvas.DesignSurface.Children)
            {
                if (child is FrameworkElement fe)
                {
                    var cloned = CloneElementForUndo(fe);
                    if (cloned != null) currentList.Add(cloned);
                }
            }
            string currentSel = _designerCanvas.SelectedElement?.Name ?? "";
            _undoStack.Push(new DesignState(currentList, currentSel));

            var state = _redoStack.Pop();
            _designerCanvas.DesignSurface.Children.Clear();
            foreach (var fe in state.Elements)
            {
                _designerCanvas.DesignSurface.Children.Add(fe);
            }

            FrameworkElement? newSel = null;
            if (!string.IsNullOrEmpty(state.SelectedElementName))
            {
                newSel = FindElementByName(_designerCanvas.DesignSurface, state.SelectedElementName);
            }
            _designerCanvas.SelectElement(newSel);

            _designerCanvas.Invalidate();
            OnCanvasModified();
            UpdateOutline();
        }
        finally
        {
            _isApplyingHistoryState = false;
        }
    }

    private FrameworkElement? CloneElement(FrameworkElement original)
    {
        if (original == null) return null;

        Type type = original.GetType();
        try
        {
            var clone = Activator.CreateInstance(type) as FrameworkElement;
            if (clone == null) return null;

            clone.Width = original.Width;
            clone.Height = original.Height;
            clone.Visibility = original.Visibility;
            clone.Margin = original.Margin;
            clone.Padding = original.Padding;
            clone.HorizontalAlignment = original.HorizontalAlignment;
            clone.VerticalAlignment = original.VerticalAlignment;
            clone.Opacity = original.Opacity;
            clone.IsHitTestVisible = original.IsHitTestVisible;

            float left = Canvas.GetLeft(original);
            float top = Canvas.GetTop(original);
            Canvas.SetLeft(clone, left + 20f);
            Canvas.SetTop(clone, top + 20f);

            if (original is Button origButton && clone is Button cloneButton)
            {
                cloneButton.Content = CloneContent(origButton.Content);
            }
            else if (original is CheckBox origCheck && clone is CheckBox cloneCheck)
            {
                cloneCheck.Content = CloneContent(origCheck.Content);
            }
            else if (original is RadioButton origRadio && clone is RadioButton cloneRadio)
            {
                cloneRadio.Content = CloneContent(origRadio.Content);
            }
            else if (original is ToggleSwitch origToggle && clone is ToggleSwitch cloneToggle)
            {
                cloneToggle.Content = CloneContent(origToggle.Content) as FrameworkElement;
            }
            else if (original is TextBlock origTb && clone is TextBlock cloneTb)
            {
                cloneTb.Text = origTb.Text;
            }
            else if (original is TextBox origTextBox && clone is TextBox cloneTextBox)
            {
                cloneTextBox.Text = origTextBox.Text;
                cloneTextBox.PlaceholderText = origTextBox.PlaceholderText;
            }
            else if (original is ComboBox origCombo && clone is ComboBox cloneCombo)
            {
                cloneCombo.PlaceholderText = origCombo.PlaceholderText;
            }
            else if (original is Border origBorder && clone is Border cloneBorder)
            {
                cloneBorder.Background = origBorder.Background;
                cloneBorder.BorderBrush = origBorder.BorderBrush;
                cloneBorder.BorderThickness = origBorder.BorderThickness;
                cloneBorder.CornerRadius = origBorder.CornerRadius;
                if (origBorder.Child is FrameworkElement childFe)
                {
                    cloneBorder.Child = CloneElement(childFe);
                }
            }
            else if (original is Panel origPanel && clone is Panel clonePanel)
            {
                foreach (var child in origPanel.Children)
                {
                    if (child is FrameworkElement childFe)
                    {
                        clonePanel.Children.Add(CloneElement(childFe));
                    }
                }
            }

            int suffix = 1;
            string baseName = type.Name;
            string candidateName = $"{baseName}_{suffix}";

            var existingNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            FindNamesInVisualTree(_designerCanvas.DesignSurface, existingNames);
            while (existingNames.Contains(candidateName))
            {
                candidateName = $"{baseName}_{++suffix}";
            }
            clone.Name = candidateName;

            return clone;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[DesignerHost] Error cloning element: {ex.Message}");
            return null;
        }
    }

    private FrameworkElement? CloneElementForUndo(FrameworkElement original)
    {
        if (original == null) return null;

        Type type = original.GetType();
        try
        {
            var clone = Activator.CreateInstance(type) as FrameworkElement;
            if (clone == null) return null;

            clone.Name = original.Name;
            clone.Width = original.Width;
            clone.Height = original.Height;
            clone.Visibility = original.Visibility;
            clone.Margin = original.Margin;
            clone.Padding = original.Padding;
            clone.HorizontalAlignment = original.HorizontalAlignment;
            clone.VerticalAlignment = original.VerticalAlignment;
            clone.Opacity = original.Opacity;
            clone.IsHitTestVisible = original.IsHitTestVisible;

            float left = Canvas.GetLeft(original);
            float top = Canvas.GetTop(original);
            Canvas.SetLeft(clone, left);
            Canvas.SetTop(clone, top);

            if (original is Button origButton && clone is Button cloneButton)
            {
                cloneButton.Content = CloneContent(origButton.Content);
            }
            else if (original is CheckBox origCheck && clone is CheckBox cloneCheck)
            {
                cloneCheck.Content = CloneContent(origCheck.Content);
            }
            else if (original is RadioButton origRadio && clone is RadioButton cloneRadio)
            {
                cloneRadio.Content = CloneContent(origRadio.Content);
            }
            else if (original is ToggleSwitch origToggle && clone is ToggleSwitch cloneToggle)
            {
                cloneToggle.Content = CloneContent(origToggle.Content) as FrameworkElement;
            }
            else if (original is TextBlock origTb && clone is TextBlock cloneTb)
            {
                cloneTb.Text = origTb.Text;
            }
            else if (original is TextBox origTextBox && clone is TextBox cloneTextBox)
            {
                cloneTextBox.Text = origTextBox.Text;
                cloneTextBox.PlaceholderText = origTextBox.PlaceholderText;
            }
            else if (original is ComboBox origCombo && clone is ComboBox cloneCombo)
            {
                cloneCombo.PlaceholderText = origCombo.PlaceholderText;
            }
            else if (original is Border origBorder && clone is Border cloneBorder)
            {
                cloneBorder.Background = origBorder.Background;
                cloneBorder.BorderBrush = origBorder.BorderBrush;
                cloneBorder.BorderThickness = origBorder.BorderThickness;
                cloneBorder.CornerRadius = origBorder.CornerRadius;
                if (origBorder.Child is FrameworkElement childFe)
                {
                    cloneBorder.Child = CloneElementForUndo(childFe);
                }
            }
            else if (original is Panel origPanel && clone is Panel clonePanel)
            {
                foreach (var child in origPanel.Children)
                {
                    if (child is FrameworkElement childFe)
                    {
                        clonePanel.Children.Add(CloneElementForUndo(childFe));
                    }
                }
            }

            return clone;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[DesignerHost] Error cloning element for undo: {ex.Message}");
            return null;
        }
    }

    private object? CloneContent(object? content)
    {
        if (content == null) return null;
        if (content is string str) return str;
        if (content is RichTextBlock rtb)
        {
            var cloneRtb = new RichTextBlock { Font = rtb.Font, FontSize = rtb.FontSize, Foreground = rtb.Foreground };
            foreach (var inline in rtb.Inlines)
            {
                if (inline is Run run)
                {
                    cloneRtb.Inlines.Add(new Run(run.Text));
                }
                else if (inline is Bold bold)
                {
                    var cloneBold = new Bold();
                    foreach (var inner in bold.Inlines)
                    {
                        if (inner is Run innerRun) cloneBold.Inlines.Add(new Run(innerRun.Text));
                    }
                    cloneRtb.Inlines.Add(cloneBold);
                }
            }
            return cloneRtb;
        }
        return content.ToString();
    }

    private FrameworkElement? FindElementByName(Visual? root, string name)
    {
        if (root is FrameworkElement fe)
        {
            if (fe.Name == name) return fe;
            if (fe is ContainerVisual container)
            {
                foreach (var child in container.Children)
                {
                    var found = FindElementByName(child, name);
                    if (found != null) return found;
                }
            }
            else if (fe is Border b && b.Child != null)
            {
                var found = FindElementByName(b.Child, name);
                if (found != null) return found;
            }
            else if (fe is ContentControl cc && cc.Content is FrameworkElement contentFe)
            {
                var found = FindElementByName(contentFe, name);
                if (found != null) return found;
            }
        }
        return null;
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
