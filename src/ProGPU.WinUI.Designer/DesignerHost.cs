using System;
using System.Collections.Generic;
using System.Numerics;
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

namespace ProGPU.WinUI.Designer;

public class DesignerHost : Grid
{
    private readonly Border _sidebarLeftBorder;
    private readonly Grid _sidebarLeft;
    private readonly Grid _workspaceCenter;
    private readonly Border _sidebarRightBorder;
    private readonly StylePanel _stylePanel;
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
    private RichTextBlock? _webflowLabelText;
    private Button? _interactionModeButton;
    private RichTextBlock? _interactionModeText;
    private RichTextBlock? _desktopText;
    private RichTextBlock? _tabletText;
    private RichTextBlock? _mobileText;
    
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
    public bool IsInteractionMode => _designerCanvas.IsInteractionMode;

    public void SetInteractionMode(bool enabled)
    {
        _designerCanvas.IsInteractionMode = enabled;
        if (_interactionModeButton == null || _interactionModeText == null)
        {
            return;
        }

        _interactionModeText.Inlines.Clear();
        _interactionModeText.Inlines.Add(new Run(enabled ? "Design" : "Interact"));
        _interactionModeButton.Background = new ThemeResourceBrush(
            enabled ? "SystemAccentColor" : "ControlBackground");
        _interactionModeText.Foreground = new ThemeResourceBrush(
            enabled ? "TextOnAccent" : "TextPrimary");
        _interactionModeButton.BorderThickness = enabled ? new Thickness(0) : new Thickness(1f);
        _interactionModeButton.BorderBrush = new ThemeResourceBrush(
            enabled ? "SystemAccentColor" : "ControlBorder");
        _interactionModeButton.Invalidate();
    }

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
        
        var leftSplitView = new ResponsiveSplitView
        {
            OpenPaneLength = 260f,
            CompactModeThreshold = 900f,
            PanePlacement = PanePlacement.Left,
            IsPaneScrollEnabled = false
        };
        var rightSplitView = new ResponsiveSplitView
        {
            OpenPaneLength = 280f,
            CompactModeThreshold = 700f,
            PanePlacement = PanePlacement.Right,
            IsPaneScrollEnabled = false
        };
        leftSplitView.MainContent = rightSplitView;
        Grid.SetRow(leftSplitView, 0);
        AddChild(leftSplitView);

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
        leftSplitView.PaneContent = _sidebarLeftBorder;

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
        _visualTreeOutline.ModeChanged += (isLogical) => {
            _designerCanvas.IsLogicalMode = isLogical;
            _designerCanvas.SelectElement(null);
            _designerCanvas.Invalidate();
        };

        Grid.SetRow(_visualTreeOutline, 1);
        _sidebarLeft.AddChild(_visualTreeOutline);

        // 2. Center Workspace
        _workspaceCenter = new Grid();
        _workspaceCenter.RowDefinitions.Add(GridLength.Auto);
        _workspaceCenter.RowDefinitions.Add(GridLength.Star(1f));
        rightSplitView.MainContent = _workspaceCenter;

        // Reorganized elegant top visual designer settings bar
        var toolbarBorder = new Border
        {
            Background = new ThemeResourceBrush("CardBackground"),
            BorderThickness = new Thickness(0, 0, 0, 1),
            BorderBrush = new ThemeResourceBrush("ControlBorder"),
            Padding = new Thickness(16, 6, 16, 6),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch
        };

        var toolbarGrid = new Grid { HorizontalAlignment = HorizontalAlignment.Stretch };
        toolbarGrid.ColumnDefinitions.Add(GridLength.Star(1f)); // Left section: canvas settings
        toolbarGrid.ColumnDefinitions.Add(GridLength.Auto);      // Center section: viewport breakpoints
        toolbarGrid.ColumnDefinitions.Add(GridLength.Star(1f)); // Right section: zoom & clear
        toolbarBorder.Child = toolbarGrid;

        // Custom Helper to dynamically style active vs inactive toggle buttons
        Action<Button, bool> updateToggleStyle = (btn, isActive) => {
            if (isActive)
            {
                btn.Background = new ThemeResourceBrush("SystemAccentColor");
                if (btn.Content is RichTextBlock rtb)
                {
                    rtb.Foreground = new SolidColorBrush(Vector4.One); // white text
                }
                btn.BorderThickness = new Thickness(0);
            }
            else
            {
                btn.Background = new ThemeResourceBrush("ControlBackground");
                if (btn.Content is RichTextBlock rtb)
                {
                    rtb.Foreground = new ThemeResourceBrush("TextPrimary");
                }
                btn.BorderThickness = new Thickness(1f);
                btn.BorderBrush = new ThemeResourceBrush("ControlBorder");
            }
        };

        // 1. Left Section: Compact modern pill toggles
        var leftPanel = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };

        // Grid Lines Toggle
        var btnGridLines = new Button { Height = 28f, Margin = new Thickness(0, 0, 6, 0), CornerRadius = 4f, Padding = new Thickness(8, 0, 8, 0) };
        var rtbGrid = new RichTextBlock { Font = DesignerFont, FontSize = 10.5f, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        rtbGrid.Inlines.Add(new Run("Grid Lines"));
        btnGridLines.Content = rtbGrid;
        updateToggleStyle(btnGridLines, _designerCanvas.ShowGridLines);
        btnGridLines.Click += (s, e) => {
            _designerCanvas.ShowGridLines = !_designerCanvas.ShowGridLines;
            updateToggleStyle(btnGridLines, _designerCanvas.ShowGridLines);
            _designerCanvas.Invalidate();
        };
        leftPanel.AddChild(btnGridLines);

        // Grid Snapping Toggle
        var btnSnapping = new Button { Height = 28f, Margin = new Thickness(0, 0, 6, 0), CornerRadius = 4f, Padding = new Thickness(8, 0, 8, 0) };
        var rtbSnap = new RichTextBlock { Font = DesignerFont, FontSize = 10.5f, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        rtbSnap.Inlines.Add(new Run("Snap"));
        btnSnapping.Content = rtbSnap;
        updateToggleStyle(btnSnapping, _designerCanvas.GridSnappingEnabled);
        btnSnapping.Click += (s, e) => {
            _designerCanvas.GridSnappingEnabled = !_designerCanvas.GridSnappingEnabled;
            updateToggleStyle(btnSnapping, _designerCanvas.GridSnappingEnabled);
        };
        leftPanel.AddChild(btnSnapping);

        // Outlines Toggle
        var btnOutlines = new Button { Height = 28f, Margin = new Thickness(0, 0, 6, 0), CornerRadius = 4f, Padding = new Thickness(8, 0, 8, 0) };
        _outlinesLabelText = new RichTextBlock { Font = DesignerFont, FontSize = 10.5f, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        _outlinesLabelText.Inlines.Add(new Run("Outlines"));
        btnOutlines.Content = _outlinesLabelText;
        updateToggleStyle(btnOutlines, _designerCanvas.AlwaysShowPanelOutlines);
        btnOutlines.Click += (s, e) => {
            _designerCanvas.AlwaysShowPanelOutlines = !_designerCanvas.AlwaysShowPanelOutlines;
            updateToggleStyle(btnOutlines, _designerCanvas.AlwaysShowPanelOutlines);
            _designerCanvas.Invalidate();
        };
        leftPanel.AddChild(btnOutlines);

        // Responsive Mode Toggle
        var btnResponsive = new Button { Height = 28f, Margin = new Thickness(0, 0, 6, 0), CornerRadius = 4f, Padding = new Thickness(8, 0, 8, 0) };
        _webflowLabelText = new RichTextBlock { Font = DesignerFont, FontSize = 10.5f, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        _webflowLabelText.Inlines.Add(new Run("Responsive"));
        btnResponsive.Content = _webflowLabelText;
        updateToggleStyle(btnResponsive, _designerCanvas.IsResponsiveMode);
        btnResponsive.Click += (s, e) => {
            _designerCanvas.IsResponsiveMode = !_designerCanvas.IsResponsiveMode;
            updateToggleStyle(btnResponsive, _designerCanvas.IsResponsiveMode);
            _designerCanvas.SelectElement(null);
            _designerCanvas.Invalidate();
            OnCanvasModified();
            UpdateOutline();
        };
        leftPanel.AddChild(btnResponsive);

        _interactionModeButton = new Button
        {
            Name = "DesignerInteractionModeButton",
            Height = 28f,
            Margin = new Thickness(0, 0, 6, 0),
            CornerRadius = 4f,
            Padding = new Thickness(8, 0, 8, 0)
        };
        _interactionModeText = new RichTextBlock
        {
            Font = DesignerFont,
            FontSize = 10.5f,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        _interactionModeText.Inlines.Add(new Run("Interact"));
        _interactionModeButton.Content = _interactionModeText;
        updateToggleStyle(_interactionModeButton, _designerCanvas.IsInteractionMode);
        _interactionModeButton.Click += (s, e) =>
        {
            SetInteractionMode(!_designerCanvas.IsInteractionMode);
        };
        leftPanel.AddChild(_interactionModeButton);

        Grid.SetColumn(leftPanel, 0);
        toolbarGrid.AddChild(leftPanel);

        // 2. Center Section: Segmented Breakpoints Control Capsule
        var centerPanel = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
        
        var desktopBtn = new Button { Width = 80f, Height = 28f, CornerRadius = 4f, Margin = new Thickness(0), Padding = new Thickness(0) };
        _desktopText = new RichTextBlock { Font = DesignerFont, FontSize = 10.5f, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        _desktopText.Inlines.Add(new Run("Desktop"));
        desktopBtn.Content = _desktopText;

        var tabletBtn = new Button { Width = 75f, Height = 28f, CornerRadius = 0f, Margin = new Thickness(0), Padding = new Thickness(0) };
        _tabletText = new RichTextBlock { Font = DesignerFont, FontSize = 10.5f, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        _tabletText.Inlines.Add(new Run("Tablet"));
        tabletBtn.Content = _tabletText;

        var mobileBtn = new Button { Width = 75f, Height = 28f, CornerRadius = 4f, Margin = new Thickness(0), Padding = new Thickness(0) };
        _mobileText = new RichTextBlock { Font = DesignerFont, FontSize = 10.5f, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        _mobileText.Inlines.Add(new Run("Mobile"));
        mobileBtn.Content = _mobileText;

        Action updateBreakpointSegmentStyle = () => {
            float? vw = _designerCanvas.ViewportWidth;
            if (vw == null)
            {
                desktopBtn.Background = new ThemeResourceBrush("SystemAccentColor");
                _desktopText.Foreground = new SolidColorBrush(Vector4.One);
                desktopBtn.BorderThickness = new Thickness(0);

                tabletBtn.Background = new ThemeResourceBrush("ControlBackground");
                _tabletText.Foreground = new ThemeResourceBrush("TextPrimary");
                tabletBtn.BorderThickness = new Thickness(1, 1, 1, 1);
                tabletBtn.BorderBrush = new ThemeResourceBrush("ControlBorder");

                mobileBtn.Background = new ThemeResourceBrush("ControlBackground");
                _mobileText.Foreground = new ThemeResourceBrush("TextPrimary");
                mobileBtn.BorderThickness = new Thickness(0, 1, 1, 1);
                mobileBtn.BorderBrush = new ThemeResourceBrush("ControlBorder");
            }
            else if (vw == 768f)
            {
                desktopBtn.Background = new ThemeResourceBrush("ControlBackground");
                _desktopText.Foreground = new ThemeResourceBrush("TextPrimary");
                desktopBtn.BorderThickness = new Thickness(1, 1, 0, 1);
                desktopBtn.BorderBrush = new ThemeResourceBrush("ControlBorder");

                tabletBtn.Background = new ThemeResourceBrush("SystemAccentColor");
                _tabletText.Foreground = new SolidColorBrush(Vector4.One);
                tabletBtn.BorderThickness = new Thickness(0);

                mobileBtn.Background = new ThemeResourceBrush("ControlBackground");
                _mobileText.Foreground = new ThemeResourceBrush("TextPrimary");
                mobileBtn.BorderThickness = new Thickness(0, 1, 1, 1);
                mobileBtn.BorderBrush = new ThemeResourceBrush("ControlBorder");
            }
            else // 375f
            {
                desktopBtn.Background = new ThemeResourceBrush("ControlBackground");
                _desktopText.Foreground = new ThemeResourceBrush("TextPrimary");
                desktopBtn.BorderThickness = new Thickness(1, 1, 0, 1);
                desktopBtn.BorderBrush = new ThemeResourceBrush("ControlBorder");

                tabletBtn.Background = new ThemeResourceBrush("ControlBackground");
                _tabletText.Foreground = new ThemeResourceBrush("TextPrimary");
                tabletBtn.BorderThickness = new Thickness(1, 1, 1, 1);
                tabletBtn.BorderBrush = new ThemeResourceBrush("ControlBorder");

                mobileBtn.Background = new ThemeResourceBrush("SystemAccentColor");
                _mobileText.Foreground = new SolidColorBrush(Vector4.One);
                mobileBtn.BorderThickness = new Thickness(0);
            }
        };

        desktopBtn.Click += (s, e) => {
            _designerCanvas.ViewportWidth = null;
            updateBreakpointSegmentStyle();
            _designerCanvas.InvalidateArrange();
            _designerCanvas.Invalidate();
            _designerCanvas.UpdateSelectionAdorner();
        };

        tabletBtn.Click += (s, e) => {
            _designerCanvas.ViewportWidth = 768f;
            updateBreakpointSegmentStyle();
            _designerCanvas.InvalidateArrange();
            _designerCanvas.Invalidate();
            _designerCanvas.UpdateSelectionAdorner();
        };

        mobileBtn.Click += (s, e) => {
            _designerCanvas.ViewportWidth = 375f;
            updateBreakpointSegmentStyle();
            _designerCanvas.InvalidateArrange();
            _designerCanvas.Invalidate();
            _designerCanvas.UpdateSelectionAdorner();
        };

        updateBreakpointSegmentStyle(); // Initial breakpoint state

        centerPanel.AddChild(desktopBtn);
        centerPanel.AddChild(tabletBtn);
        centerPanel.AddChild(mobileBtn);

        Grid.SetColumn(centerPanel, 1);
        toolbarGrid.AddChild(centerPanel);

        // 3. Right Section: History, Workspace Management, Zoom Controls
        var rightPanel = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Right };

        // Undo & Redo Capsule Group
        var historyPanel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 12, 0) };
        
        var undoBtn = new Button { Width = 65f, Height = 28f, CornerRadius = 4f, Margin = new Thickness(0), Background = new ThemeResourceBrush("ControlBackground"), BorderThickness = new Thickness(1), BorderBrush = new ThemeResourceBrush("ControlBorder"), Padding = new Thickness(0) };
        var undoText = new RichTextBlock { Font = DesignerFont, FontSize = 10.5f, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        undoText.Inlines.Add(new Run("Undo"));
        undoBtn.Content = undoText;
        undoBtn.Click += (s, e) => {
            TriggerUndo();
        };
        historyPanel.AddChild(undoBtn);

        var redoBtn = new Button { Width = 65f, Height = 28f, CornerRadius = 4f, Margin = new Thickness(0), Background = new ThemeResourceBrush("ControlBackground"), BorderThickness = new Thickness(0, 1, 1, 1), BorderBrush = new ThemeResourceBrush("ControlBorder"), Padding = new Thickness(0) };
        var redoText = new RichTextBlock { Font = DesignerFont, FontSize = 10.5f, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        redoText.Inlines.Add(new Run("Redo"));
        redoBtn.Content = redoText;
        redoBtn.Click += (s, e) => {
            TriggerRedo();
        };
        historyPanel.AddChild(redoBtn);
        rightPanel.AddChild(historyPanel);

        // Clear Workspace button
        var clearBtn = new Button { Height = 28f, Margin = new Thickness(0, 0, 12, 0), CornerRadius = 4f, Background = new ThemeResourceBrush("ControlBackground"), BorderThickness = new Thickness(1), BorderBrush = new ThemeResourceBrush("ControlBorder"), Padding = new Thickness(10, 0, 10, 0) };
        var clearBtnText = new RichTextBlock { Font = DesignerFont, FontSize = 10.5f, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Foreground = new ThemeResourceBrush("TextPrimary") };
        clearBtnText.Inlines.Add(new Run("Clear"));
        clearBtn.Content = clearBtnText;
        clearBtn.Click += (s, e) => {
            SaveUndoState();
            _designerCanvas.DesignSurface.Children.Clear();
            _designerCanvas.SelectElement(null);
            _designerCanvas.Invalidate();
            OnCanvasModified();
            UpdateOutline();
        };
        rightPanel.AddChild(clearBtn);

        // Zoom capsule
        var zoomPanel = new StackPanel { Orientation = Orientation.Horizontal };
        
        var zoomOutBtn = new Button { Width = 28f, Height = 28f, CornerRadius = 4f, Margin = new Thickness(0), Background = new ThemeResourceBrush("ControlBackground"), BorderThickness = new Thickness(1), BorderBrush = new ThemeResourceBrush("ControlBorder") };
        _zoomOutText = new RichTextBlock { Font = DesignerFont, FontSize = 12f, Foreground = new ThemeResourceBrush("TextPrimary"), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        _zoomOutText.Inlines.Add(new Run("-"));
        zoomOutBtn.Content = _zoomOutText;

        var zoomValBorder = new Border { Height = 28f, Background = new ThemeResourceBrush("ControlBackground"), BorderThickness = new Thickness(0, 1, 0, 1), BorderBrush = new ThemeResourceBrush("ControlBorder"), Padding = new Thickness(8, 0, 8, 0), VerticalAlignment = VerticalAlignment.Stretch };
        _zoomValText = new RichTextBlock { Font = DesignerFont, FontSize = 10.5f, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
        _zoomValText.Inlines.Add(new Run("100%"));
        zoomValBorder.Child = _zoomValText;

        var zoomInBtn = new Button { Width = 28f, Height = 28f, CornerRadius = 4f, Margin = new Thickness(0), Background = new ThemeResourceBrush("ControlBackground"), BorderThickness = new Thickness(1, 1, 1, 1), BorderBrush = new ThemeResourceBrush("ControlBorder") };
        _zoomInText = new RichTextBlock { Font = DesignerFont, FontSize = 12f, Foreground = new ThemeResourceBrush("TextPrimary"), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
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

        zoomPanel.AddChild(zoomOutBtn);
        zoomPanel.AddChild(zoomValBorder);
        zoomPanel.AddChild(zoomInBtn);
        rightPanel.AddChild(zoomPanel);

        Grid.SetColumn(rightPanel, 2);
        toolbarGrid.AddChild(rightPanel);

        _workspaceCenter.AddChild(toolbarBorder);
        Grid.SetRow(toolbarBorder, 0);

        _designerCanvas.CanvasModified += OnCanvasModified;
        
        var canvasScrollViewer = new ScrollViewer
        {
            Content = _designerCanvas,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch
        };

        _workspaceCenter.AddChild(canvasScrollViewer);
        Grid.SetRow(canvasScrollViewer, 1);

        // 3. Right Sidebar - StylePanel & PropertyGrid in Pivot
        _stylePanel = new StylePanel(DesignerFont);
        _stylePanel.PropertyChanged += () => {
            _designerCanvas.UpdateSelectionAdorner();
            _designerCanvas.Invalidate();
            OnCanvasModified();
            UpdateOutline();
        };

        _propertyGrid = new PropertyGrid(DesignerFont);
        _propertyGrid.PropertyChanged += () => {
            _designerCanvas.UpdateSelectionAdorner();
            _designerCanvas.Invalidate();
            OnCanvasModified();
            UpdateOutline();
        };

        _designerCanvas.SelectionChanged += () => {
            _stylePanel.SelectedElement = _designerCanvas.SelectedElement;
            _propertyGrid.SelectedElement = _designerCanvas.SelectedElement;
            UpdateOutline();
        };

        var sidebarPivot = new Pivot
        {
            Font = DesignerFont,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            Padding = new Thickness(0)
        };

        var tabStyle = new PivotItem("Style", _stylePanel);
        var tabSettings = new PivotItem("Property Grid", _propertyGrid);

        var interactionsStack = new StackPanel { Orientation = Orientation.Vertical, Padding = new Thickness(16) };
        var interactionsTitle = new RichTextBlock { Font = DesignerFont, FontSize = 12f, Margin = new Thickness(0, 0, 0, 8) };
        interactionsTitle.Inlines.Add(new Bold(new Run("Element Interactions")));
        interactionsTitle.Foreground = new ThemeResourceBrush("TextPrimary");

        var interactionsDesc = new RichTextBlock { Font = DesignerFont, FontSize = 10f };
        interactionsDesc.Foreground = new ThemeResourceBrush("TextSecondary");
        interactionsDesc.Inlines.Add(new Run("Add dynamic triggers, hover transitions, and click behaviors to make your elements feel responsive and alive."));

        interactionsStack.AddChild(interactionsTitle);
        interactionsStack.AddChild(interactionsDesc);

        var tabInteractions = new PivotItem("Interactions", interactionsStack);

        sidebarPivot.Items.Add(tabStyle);
        sidebarPivot.Items.Add(tabSettings);
        sidebarPivot.Items.Add(tabInteractions);

        _sidebarRightBorder = new Border
        {
            Background = new ThemeResourceBrush("CardBackground"),
            BorderThickness = new Thickness(1, 0, 0, 0),
            BorderBrush = new ThemeResourceBrush("ControlBorder"),
            Child = sidebarPivot
        };
        rightSplitView.PaneContent = _sidebarRightBorder;

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

        TtfFont? primaryFont = DesignerFont ?? Microsoft.UI.Xaml.Controls.PopupService.DefaultFont;
        TtfFont? courierFont = DesignerFontCourier ?? DesignerFont ?? Microsoft.UI.Xaml.Controls.PopupService.DefaultFont;

        if (_outlinesLabelText != null) _outlinesLabelText.Font = primaryFont;
        if (_webflowLabelText != null) _webflowLabelText.Font = primaryFont;
        if (_desktopText != null) _desktopText.Font = primaryFont;
        if (_tabletText != null) _tabletText.Font = primaryFont;
        if (_mobileText != null) _mobileText.Font = primaryFont;
        if (_zoomOutText != null) _zoomOutText.Font = primaryFont;
        if (_zoomValText != null) _zoomValText.Font = primaryFont;
        if (_zoomInText != null) _zoomInText.Font = primaryFont;
        bottomTitle.Font = primaryFont;
        toggleText.Font = primaryFont;
        _csharpCodeBlock.Font = courierFont;

        OnCanvasModified();
        UpdateOutline();
    }

    public void InitializeFonts(TtfFont? mainFont, TtfFont? codeFont)
    {
        DesignerFont = mainFont;
        DesignerFontCourier = codeFont;

        TtfFont? primaryFont = DesignerFont ?? Microsoft.UI.Xaml.Controls.PopupService.DefaultFont;
        TtfFont? courierFont = DesignerFontCourier ?? DesignerFont ?? Microsoft.UI.Xaml.Controls.PopupService.DefaultFont;
        
        foreach (var child in Children)
        {
            ApplyFontToVisualTree(child, primaryFont, courierFont);
        }
        
        if (_zoomOutText != null) _zoomOutText.Font = primaryFont;
        if (_zoomValText != null) _zoomValText.Font = primaryFont;
        if (_zoomInText != null) _zoomInText.Font = primaryFont;
        if (_outlinesLabelText != null) _outlinesLabelText.Font = primaryFont;
        if (_webflowLabelText != null) _webflowLabelText.Font = primaryFont;
        if (_desktopText != null) _desktopText.Font = primaryFont;
        if (_tabletText != null) _tabletText.Font = primaryFont;
        if (_mobileText != null) _mobileText.Font = primaryFont;
        _csharpCodeBlock.Font = courierFont;
        _visualTreeOutline.Font = primaryFont;
        
        UpdateOutline();
        OnCanvasModified();
    }

    private void ApplyFontToVisualTree(Visual element, TtfFont? mainFont, TtfFont? codeFont)
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
        if (DesignerElementRegistry.TryCreate(controlType, DesignerFont ?? PopupService.DefaultFont, out var newInstance))
        {
            try
            {
                Canvas.SetLeft(newInstance, defaultX);
                Canvas.SetTop(newInstance, defaultY);
                newInstance.Name = $"{controlType}_{_designerCanvas.DesignSurface.Children.Count + 1}";

                _designerCanvas.DesignSurface.Children.Add(newInstance);
                _designerCanvas.SelectElement(newInstance);

                OnCanvasModified();
                UpdateOutline();
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
        var isTextInput = false;
        while (current != null)
        {
            if (current is TextBox || current is PasswordBox || current is VirtualizedCodeEditor)
            {
                isTextInput = true;
            }
            if (ReferenceEquals(current, this)) return isTextInput;
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

        if (_designerCanvas.IsResponsiveMode)
        {
            var currentMargin = sel.Margin;
            float newLeft = currentMargin.Left + dx * factor;
            float newTop = currentMargin.Top + dy * factor;

            sel.Margin = new Thickness(
                MathF.Max(0f, newLeft),
                MathF.Max(0f, newTop),
                MathF.Max(0f, currentMargin.Right),
                MathF.Max(0f, currentMargin.Bottom)
            );
        }
        else
        {
            float currentLeft = Canvas.GetLeft(sel);
            float currentTop = Canvas.GetTop(sel);

            float newLeft = currentLeft + dx * factor;
            float newTop = currentTop + dy * factor;

            Canvas.SetLeft(sel, newLeft);
            Canvas.SetTop(sel, newTop);
        }

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

        try
        {
            if (!DesignerElementRegistry.TryCreateLike(original, out var clone))
            {
                return null;
            }

            clone.Width = original.Width;
            clone.Height = original.Height;
            clone.Visibility = original.Visibility;
            clone.Margin = original.Margin;
            clone.Padding = original.Padding;
            clone.HorizontalAlignment = original.HorizontalAlignment;
            clone.VerticalAlignment = original.VerticalAlignment;
            clone.Opacity = original.Opacity;
            clone.IsHitTestVisible = original.IsHitTestVisible;

            if (!_designerCanvas.IsResponsiveMode)
            {
                float left = Canvas.GetLeft(original);
                float top = Canvas.GetTop(original);
                Canvas.SetLeft(clone, left + 20f);
                Canvas.SetTop(clone, top + 20f);
            }

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
                        var childClone = CloneElement(childFe);
                        if (childClone != null)
                        {
                            clonePanel.Children.Add(childClone);
                        }
                    }
                }
            }

            int suffix = 1;
            string baseName = original.GetType().Name;
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

        try
        {
            if (!DesignerElementRegistry.TryCreateLike(original, out var clone))
            {
                return null;
            }

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
                        var childClone = CloneElementForUndo(childFe);
                        if (childClone != null)
                        {
                            clonePanel.Children.Add(childClone);
                        }
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
