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

public class DesignerCanvas : Canvas
{
    public FrameworkElement? SelectedElement { get; set; }
    public bool ShowGridLines { get; set; } = true;
    public bool GridSnappingEnabled { get; set; } = true;
    public float GridSize { get; set; } = 8f;

    public Func<float>? GetDpiScale { get; set; }
    public Brush? CanvasBackground { get; set; }

    private bool _isDragging = false;
    private Vector2 _dragStartMousePos;
    private Vector2 _dragStartElementPos;

    public event Action? SelectionChanged;
    public event Action? CanvasModified;

    public DesignerCanvas()
    {
        CanvasBackground = new ThemeResourceBrush("ControlBackground");
    }

    public override void OnPointerPressed(PointerRoutedEventArgs e)
    {
        base.OnPointerPressed(e);

        FrameworkElement? hitElement = null;
        for (int i = Children.Count - 1; i >= 0; i--)
        {
            if (Children[i] is FrameworkElement child)
            {
                float left = Microsoft.UI.Xaml.Controls.Canvas.GetLeft(child);
                float top = Microsoft.UI.Xaml.Controls.Canvas.GetTop(child);
                Vector2 size = child.Size;
                if (size.X <= 0) size.X = child.Width;
                if (size.Y <= 0) size.Y = child.Height;
                if (float.IsNaN(size.X) || size.X <= 0) size.X = 100f;
                if (float.IsNaN(size.Y) || size.Y <= 0) size.Y = 40f;

                Rect bounds = new Rect(left, top, size.X, size.Y);
                if (bounds.Contains(e.Position))
                {
                    hitElement = child;
                    break;
                }
            }
        }

        if (hitElement != null)
        {
            SelectedElement = hitElement;
            _isDragging = true;
            _dragStartMousePos = e.Position;
            _dragStartElementPos = new Vector2(Microsoft.UI.Xaml.Controls.Canvas.GetLeft(SelectedElement), Microsoft.UI.Xaml.Controls.Canvas.GetTop(SelectedElement));
            SelectionChanged?.Invoke();
            InputSystem.CapturePointer(this);
            e.Handled = true;
        }
        else
        {
            SelectedElement = null;
            SelectionChanged?.Invoke();
        }
        Invalidate();
    }

    public override void OnPointerMoved(PointerRoutedEventArgs e)
    {
        base.OnPointerMoved(e);
        if (_isDragging && SelectedElement != null)
        {
            Vector2 delta = e.Position - _dragStartMousePos;
            float newX = _dragStartElementPos.X + delta.X;
            float newY = _dragStartElementPos.Y + delta.Y;

            if (GridSnappingEnabled)
            {
                newX = MathF.Round(newX / GridSize) * GridSize;
                newY = MathF.Round(newY / GridSize) * GridSize;
            }

            // Snap to 1/4th of physical pixel
            float dpiScale = GetDpiScale?.Invoke() ?? 1.0f;
            newX = MathF.Round(newX * dpiScale * 4f) / 4f / dpiScale;
            newY = MathF.Round(newY * dpiScale * 4f) / 4f / dpiScale;

            Microsoft.UI.Xaml.Controls.Canvas.SetLeft(SelectedElement, newX);
            Microsoft.UI.Xaml.Controls.Canvas.SetTop(SelectedElement, newY);

            SelectedElement.InvalidateArrange();
            SelectedElement.Invalidate();
            CanvasModified?.Invoke();
            Invalidate();
            e.Handled = true;
        }
    }

    public override void OnPointerReleased(PointerRoutedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_isDragging)
        {
            InputSystem.ReleasePointerCapture();
            _isDragging = false;
            CanvasModified?.Invoke();
            Invalidate();
            e.Handled = true;
        }
    }

    public override void OnRender(DrawingContext context)
    {
        float dpiScale = GetDpiScale?.Invoke() ?? 1.0f;

        var bgBrush = CanvasBackground ?? new ThemeResourceBrush("ControlBackground");
        context.DrawRectangle(bgBrush, null, new Rect(Vector2.Zero, Size));

        if (ShowGridLines && GridSize > 1f)
        {
            var gridPen = new Pen(new ThemeResourceBrush("ControlBorder"), 0.5f);
            for (float x = GridSize; x < Size.X; x += GridSize)
            {
                float snapX = MathF.Round(x * dpiScale * 4f) / 4f / dpiScale;
                context.DrawLine(gridPen, new Vector2(snapX, 0), new Vector2(snapX, Size.Y));
            }
            for (float y = GridSize; y < Size.Y; y += GridSize)
            {
                float snapY = MathF.Round(y * dpiScale * 4f) / 4f / dpiScale;
                context.DrawLine(gridPen, new Vector2(0, snapY), new Vector2(Size.X, snapY));
            }
        }

        base.OnRender(context);

        if (SelectedElement != null)
        {
            float left = Microsoft.UI.Xaml.Controls.Canvas.GetLeft(SelectedElement);
            float top = Microsoft.UI.Xaml.Controls.Canvas.GetTop(SelectedElement);
            Vector2 size = SelectedElement.Size;
            if (size.X <= 0) size.X = SelectedElement.Width;
            if (size.Y <= 0) size.Y = SelectedElement.Height;
            if (float.IsNaN(size.X) || size.X <= 0) size.X = 100f;
            if (float.IsNaN(size.Y) || size.Y <= 0) size.Y = 40f;

            var selectionPen = new Pen(new ThemeResourceBrush("SystemAccentColor"), 2f);
            var handleBrush = new ThemeResourceBrush("SystemAccentColor");
            var handlePen = new Pen(new ThemeResourceBrush("TextPrimary"), 1f);

            Rect rect = new Rect(left, top, size.X, size.Y);
            context.DrawRectangle(null, selectionPen, rect);

            float handleSize = 6f;
            context.DrawRectangle(handleBrush, handlePen, new Rect(left - handleSize / 2, top - handleSize / 2, handleSize, handleSize));
            context.DrawRectangle(handleBrush, handlePen, new Rect(left + size.X - handleSize / 2, top - handleSize / 2, handleSize, handleSize));
            context.DrawRectangle(handleBrush, handlePen, new Rect(left - handleSize / 2, top + size.Y - handleSize / 2, handleSize, handleSize));
            context.DrawRectangle(handleBrush, handlePen, new Rect(left + size.X - handleSize / 2, top + size.Y - handleSize / 2, handleSize, handleSize));
        }
    }
}

public class DesignerHost : Grid
{
    private readonly Border _sidebarLeftBorder;
    private readonly Grid _sidebarLeft;
    private readonly Grid _workspaceCenter;
    private readonly Border _sidebarRightBorder;
    private readonly Grid _sidebarRight;
    private readonly Border _bottomPanel;
    
    private readonly StackPanel _toolboxPanel;
    private readonly StackPanel _outlinePanel;
    private readonly StackPanel _propertiesPanel;
    private readonly RichTextBlock _xamlCodeBlock;
    
    private readonly DesignerCanvas _designerCanvas;
    
    private bool _isBottomExpanded = true;
    private bool _isUpdatingProperties = false;

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

        _sidebarLeftBorder = new Border
        {
            Background = new ThemeResourceBrush("CardBackground"),
            BorderThickness = new Thickness(0, 0, 1, 0),
            BorderBrush = new ThemeResourceBrush("ControlBorder")
        };
        _sidebarLeft = new Grid();
        _sidebarLeftBorder.Child = _sidebarLeft;

        _sidebarLeft.RowDefinitions.Add(GridLength.Star(1.2f));
        _sidebarLeft.RowDefinitions.Add(GridLength.Star(0.8f));
        Grid.SetColumn(_sidebarLeftBorder, 0);
        contentGrid.AddChild(_sidebarLeftBorder);

        var toolboxContainer = new StackPanel { Orientation = Orientation.Vertical, Margin = new Thickness(12) };
        var toolboxTitle = new RichTextBlock { FontSize = 14f, Margin = new Thickness(0, 0, 0, 8) };
        toolboxTitle.Inlines.Add(new Bold(new Run("Toolbox & Palette")));
        toolboxContainer.AddChild(toolboxTitle);
        
        _toolboxPanel = new StackPanel { Orientation = Orientation.Vertical };
        toolboxContainer.AddChild(_toolboxPanel);
        Grid.SetRow(toolboxContainer, 0);
        _sidebarLeft.AddChild(toolboxContainer);

        var outlineContainer = new StackPanel { Orientation = Orientation.Vertical, Margin = new Thickness(12) };
        var outlineTitle = new RichTextBlock { FontSize = 14f, Margin = new Thickness(0, 8, 0, 8) };
        outlineTitle.Inlines.Add(new Bold(new Run("Visual Tree Outline")));
        outlineContainer.AddChild(outlineTitle);

        _outlinePanel = new StackPanel { Orientation = Orientation.Vertical };
        outlineContainer.AddChild(_outlinePanel);
        Grid.SetRow(outlineContainer, 1);
        _sidebarLeft.AddChild(outlineContainer);

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
        snapLabel.Inlines.Add(new Run("Snap to Grid (8px)"));
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
            _designerCanvas.Children.Clear();
            _designerCanvas.SelectedElement = null;
            _designerCanvas.Invalidate();
            OnCanvasModified();
            UpdatePropertyGrid();
            UpdateOutline();
        };
        actionBar.AddChild(clearBtn);

        Grid.SetRow(actionBar, 0);
        _workspaceCenter.AddChild(actionBar);

        _designerCanvas = new DesignerCanvas();
        _designerCanvas.GetDpiScale = () => GetDpiScale?.Invoke() ?? 1.0f;
        _designerCanvas.SelectionChanged += () => {
            UpdatePropertyGrid();
            UpdateOutline();
        };
        _designerCanvas.CanvasModified += OnCanvasModified;
        
        Grid.SetRow(_designerCanvas, 1);
        _workspaceCenter.AddChild(_designerCanvas);

        _sidebarRightBorder = new Border
        {
            Background = new ThemeResourceBrush("CardBackground"),
            BorderThickness = new Thickness(1, 0, 0, 0),
            BorderBrush = new ThemeResourceBrush("ControlBorder")
        };
        _sidebarRight = new Grid();
        _sidebarRightBorder.Child = _sidebarRight;

        _sidebarRight.RowDefinitions.Add(GridLength.Star(1f));
        Grid.SetColumn(_sidebarRightBorder, 2);
        contentGrid.AddChild(_sidebarRightBorder);

        var propContainer = new StackPanel { Orientation = Orientation.Vertical, Margin = new Thickness(12) };
        var propTitle = new RichTextBlock { FontSize = 14f, Margin = new Thickness(0, 0, 0, 8) };
        propTitle.Inlines.Add(new Bold(new Run("Properties Reflective Editor")));
        propContainer.AddChild(propTitle);

        _propertiesPanel = new StackPanel { Orientation = Orientation.Vertical };
        propContainer.AddChild(_propertiesPanel);
        _sidebarRight.AddChild(propContainer);

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
        bottomTitle.Inlines.Add(new Bold(new Run("LIVE XAML CODE SERIALIZATION DISPLAY")));
        Grid.SetColumn(bottomTitle, 0);
        bottomHeader.AddChild(bottomTitle);

        var toggleBottomBtn = new Button { Width = 140f, Height = 24f, Margin = new Thickness(0, 4, 12, 0) };
        var toggleText = new RichTextBlock { FontSize = 11f, Foreground = new ThemeResourceBrush("TextPrimary") };
        toggleText.Inlines.Add(new Run("Collapse Code Panel"));
        toggleBottomBtn.Content = toggleText;
        
        _xamlCodeBlock = new RichTextBlock { FontSize = 11f, Margin = new Thickness(12) };
        _xamlCodeBlock.Foreground = new ThemeResourceBrush("TextSecondary");
        bottomContainer.AddChild(_xamlCodeBlock);

        toggleBottomBtn.Click += (s, e) => {
            _isBottomExpanded = !_isBottomExpanded;
            if (_isBottomExpanded)
            {
                toggleText.Inlines.Clear();
                toggleText.Inlines.Add(new Run("Collapse Code Panel"));
                bottomContainer.AddChild(_xamlCodeBlock);
            }
            else
            {
                toggleText.Inlines.Clear();
                toggleText.Inlines.Add(new Run("Expand Code Panel"));
                bottomContainer.RemoveChild(_xamlCodeBlock);
            }
            InvalidateMeasure();
            InvalidateArrange();
        };
        Grid.SetColumn(toggleBottomBtn, 1);
        bottomHeader.AddChild(toggleBottomBtn);

        TtfFont primaryFont = DesignerFont ?? Microsoft.UI.Xaml.Controls.PopupService.DefaultFont;
        TtfFont courierFont = DesignerFontCourier ?? DesignerFont ?? Microsoft.UI.Xaml.Controls.PopupService.DefaultFont;
        
        toolboxTitle.Font = primaryFont;
        outlineTitle.Font = primaryFont;
        gridLinesLabel.Font = primaryFont;
        snapLabel.Font = primaryFont;
        clearBtnText.Font = primaryFont;
        propTitle.Font = primaryFont;
        bottomTitle.Font = primaryFont;
        toggleText.Font = primaryFont;
        _xamlCodeBlock.Font = courierFont;

        PopulateToolbox();
        OnCanvasModified();
        UpdatePropertyGrid();
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
        
        _xamlCodeBlock.Font = courierFont;
        
        PopulateToolbox();
        UpdatePropertyGrid();
        UpdateOutline();
        OnCanvasModified();
    }

    private void ApplyFontToVisualTree(Visual element, TtfFont mainFont, TtfFont codeFont)
    {
        if (element is RichTextBlock rtb)
        {
            if (rtb == _xamlCodeBlock) rtb.Font = codeFont;
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

    private void PopulateToolbox()
    {
        _toolboxPanel.Children.Clear();
        var controls = new[] { "Button", "TextBox", "CheckBox", "Slider", "ToggleSwitch" };
        foreach (var cName in controls)
        {
            var btn = new Button { Width = 220f, Height = 32f, Margin = new Thickness(0, 4, 0, 4) };
            var txt = new RichTextBlock { Font = DesignerFont ?? Microsoft.UI.Xaml.Controls.PopupService.DefaultFont, FontSize = 11f, Foreground = new ThemeResourceBrush("TextPrimary") };
            txt.Inlines.Add(new Run($"Add premium {cName}"));
            btn.Content = txt;

            string localName = cName;
            btn.Click += (s, e) => AddControlToCanvas(localName);
            _toolboxPanel.AddChild(btn);
        }
    }

    public void AddControlToCanvas(string controlType, float defaultX = 100f, float defaultY = 100f)
    {
        FrameworkElement newControl = controlType switch
        {
            "Button" => CreateSampleButton(),
            "TextBox" => CreateSampleTextBox(),
            "CheckBox" => CreateSampleCheckBox(),
            "Slider" => CreateSampleSlider(),
            "ToggleSwitch" => CreateSampleToggleSwitch(),
            _ => throw new ArgumentException("Unknown control type")
        };

        newControl.Name = $"{controlType}_{_designerCanvas.Children.Count + 1}";
        Microsoft.UI.Xaml.Controls.Canvas.SetLeft(newControl, defaultX);
        Microsoft.UI.Xaml.Controls.Canvas.SetTop(newControl, defaultY);

        _designerCanvas.AddChild(newControl);
        _designerCanvas.SelectedElement = newControl;

        _designerCanvas.Invalidate();
        OnCanvasModified();
        UpdatePropertyGrid();
        UpdateOutline();
    }

    private Button CreateSampleButton()
    {
        var btn = new Button { Width = 140f, Height = 36f };
        var txt = new RichTextBlock { Font = DesignerFont ?? Microsoft.UI.Xaml.Controls.PopupService.DefaultFont, FontSize = 12f, Foreground = new ThemeResourceBrush("TextPrimary") };
        txt.Inlines.Add(new Run("Sample Button"));
        btn.Content = txt;
        return btn;
    }

    private TextBox CreateSampleTextBox()
    {
        return new TextBox { Width = 150f, Height = 32f, Text = "Sample TextBox", Font = DesignerFont ?? Microsoft.UI.Xaml.Controls.PopupService.DefaultFont };
    }

    private CheckBox CreateSampleCheckBox()
    {
        var chk = new CheckBox { IsChecked = false };
        var txt = new RichTextBlock { Font = DesignerFont ?? Microsoft.UI.Xaml.Controls.PopupService.DefaultFont, FontSize = 12f };
        txt.Inlines.Add(new Run("Sample CheckBox"));
        chk.Content = txt;
        return chk;
    }

    private Slider CreateSampleSlider()
    {
        return new Slider { Width = 180f, Minimum = 0f, Maximum = 100f, Value = 50f };
    }

    private ToggleSwitch CreateSampleToggleSwitch()
    {
        var tgl = new ToggleSwitch { IsOn = false };
        var txt = new RichTextBlock { Font = DesignerFont ?? Microsoft.UI.Xaml.Controls.PopupService.DefaultFont, FontSize = 12f };
        txt.Inlines.Add(new Run("Sample Toggle"));
        tgl.Content = txt;
        return tgl;
    }

    private void OnCanvasModified()
    {
        string xaml = SerializeToXaml(_designerCanvas);
        _xamlCodeBlock.Inlines.Clear();
        
        var lines = xaml.Split('\n');
        foreach (var line in lines)
        {
            var run = new Run(line + "\n");
            if (line.Trim().StartsWith("<") || line.Trim().StartsWith("</"))
            {
                run.Foreground = new ThemeResourceBrush("SystemAccentColor");
            }
            _xamlCodeBlock.Inlines.Add(run);
        }
        _xamlCodeBlock.Invalidate();
    }

    private void UpdateOutline()
    {
        _outlinePanel.Children.Clear();
        foreach (var child in _designerCanvas.Children)
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
                    _designerCanvas.SelectedElement = fe;
                    _designerCanvas.Invalidate();
                    UpdatePropertyGrid();
                    UpdateOutline();
                };
                _outlinePanel.AddChild(selectBtn);
            }
        }
        _outlinePanel.InvalidateMeasure();
    }

    private void UpdatePropertyGrid()
    {
        _propertiesPanel.Children.Clear();
        var selected = _designerCanvas.SelectedElement;
        if (selected == null)
        {
            var noSelection = new RichTextBlock { Font = DesignerFont ?? Microsoft.UI.Xaml.Controls.PopupService.DefaultFont, FontSize = 12f, Margin = new Thickness(0, 16, 0, 0) };
            noSelection.Inlines.Add(new Run("Select an element on the canvas to edit its properties."));
            _propertiesPanel.AddChild(noSelection);
            _propertiesPanel.InvalidateMeasure();
            return;
        }

        _isUpdatingProperties = true;

        AddPropertyTextBox("Name", selected.Name, val => selected.Name = val);

        float left = Microsoft.UI.Xaml.Controls.Canvas.GetLeft(selected);
        AddPropertyTextBox("Canvas.Left", left.ToString("F1"), val => {
            if (float.TryParse(val, out float l)) {
                Microsoft.UI.Xaml.Controls.Canvas.SetLeft(selected, l);
                selected.InvalidateArrange();
                _designerCanvas.Invalidate();
            }
        });

        float top = Microsoft.UI.Xaml.Controls.Canvas.GetTop(selected);
        AddPropertyTextBox("Canvas.Top", top.ToString("F1"), val => {
            if (float.TryParse(val, out float t)) {
                Microsoft.UI.Xaml.Controls.Canvas.SetTop(selected, t);
                selected.InvalidateArrange();
                _designerCanvas.Invalidate();
            }
        });

        float width = selected.Width;
        if (float.IsNaN(width)) width = selected.Size.X;
        AddPropertyTextBox("Width", width.ToString("F1"), val => {
            if (float.TryParse(val, out float w)) {
                selected.Width = w;
                selected.InvalidateMeasure();
                selected.InvalidateArrange();
                _designerCanvas.Invalidate();
            }
        });

        float height = selected.Height;
        if (float.IsNaN(height)) height = selected.Size.Y;
        AddPropertyTextBox("Height", height.ToString("F1"), val => {
            if (float.TryParse(val, out float h)) {
                selected.Height = h;
                selected.InvalidateMeasure();
                selected.InvalidateArrange();
                _designerCanvas.Invalidate();
            }
        });

        var contentProp = selected.GetType().GetProperty("Content");
        if (contentProp != null)
        {
            var val = contentProp.GetValue(selected);
            string contentStr = "";
            if (val is RichTextBlock rtb)
            {
                contentStr = GetTextFromRichText(rtb);
            }
            else if (val != null)
            {
                contentStr = val.ToString() ?? "";
            }

            AddPropertyTextBox("Content", contentStr, newVal => {
                if (val is RichTextBlock richText)
                {
                    richText.Inlines.Clear();
                    richText.Inlines.Add(new Run(newVal));
                    richText.Invalidate();
                }
                else
                {
                    contentProp.SetValue(selected, newVal);
                }
                selected.InvalidateMeasure();
                selected.InvalidateArrange();
                _designerCanvas.Invalidate();
            });
        }

        var textProp = selected.GetType().GetProperty("Text");
        if (textProp != null)
        {
            string val = textProp.GetValue(selected) as string ?? "";
            AddPropertyTextBox("Text", val, newVal => {
                textProp.SetValue(selected, newVal);
                selected.InvalidateMeasure();
                selected.InvalidateArrange();
                _designerCanvas.Invalidate();
            });
        }

        var isCheckedProp = selected.GetType().GetProperty("IsChecked");
        if (isCheckedProp != null)
        {
            bool val = (bool)(isCheckedProp.GetValue(selected) ?? false);
            AddPropertyCheckBox("IsChecked", val, newVal => {
                isCheckedProp.SetValue(selected, newVal);
                _designerCanvas.Invalidate();
            });
        }

        var isOnProp = selected.GetType().GetProperty("IsOn");
        if (isOnProp != null)
        {
            bool val = (bool)(isOnProp.GetValue(selected) ?? false);
            AddPropertyCheckBox("IsOn", val, newVal => {
                isOnProp.SetValue(selected, newVal);
                _designerCanvas.Invalidate();
            });
        }

        _isUpdatingProperties = false;
        _propertiesPanel.InvalidateMeasure();
    }

    private void AddPropertyTextBox(string labelName, string initialValue, Action<string> onValueSubmitted)
    {
        var label = new RichTextBlock { Font = DesignerFont ?? Microsoft.UI.Xaml.Controls.PopupService.DefaultFont, FontSize = 10f, Margin = new Thickness(0, 6, 0, 2) };
        label.Inlines.Add(new Run(labelName));
        _propertiesPanel.AddChild(label);

        var box = new TextBox { Width = 240f, Height = 28f, Text = initialValue, Font = DesignerFont ?? Microsoft.UI.Xaml.Controls.PopupService.DefaultFont };
        box.TextChanged += (s, e) => {
            if (!_isUpdatingProperties)
            {
                onValueSubmitted(box.Text);
                OnCanvasModified();
            }
        };
        _propertiesPanel.AddChild(box);
    }

    private void AddPropertyCheckBox(string labelName, bool initialValue, Action<bool> onValueChanged)
    {
        var check = new CheckBox { IsChecked = initialValue, Margin = new Thickness(0, 6, 0, 6) };
        var label = new RichTextBlock { Font = DesignerFont ?? Microsoft.UI.Xaml.Controls.PopupService.DefaultFont, FontSize = 11f };
        label.Inlines.Add(new Run(labelName));
        check.Content = label;

        check.CheckedChanged += (s, e) => {
            if (!_isUpdatingProperties)
            {
                onValueChanged(check.IsChecked);
                OnCanvasModified();
            }
        };
        _propertiesPanel.AddChild(check);
    }

    public static string SerializeToXaml(Canvas canvas)
    {
        var sb = new StringBuilder();
        sb.AppendLine("<Canvas Width=\"800\" Height=\"600\">");
        foreach (var child in canvas.Children)
        {
            if (child is FrameworkElement fe)
            {
                string typeName = fe.GetType().Name;
                float left = Microsoft.UI.Xaml.Controls.Canvas.GetLeft(fe);
                float top = Microsoft.UI.Xaml.Controls.Canvas.GetTop(fe);
                float width = fe.Width;
                float height = fe.Height;
                if (float.IsNaN(width)) width = fe.Size.X;
                if (float.IsNaN(height)) height = fe.Size.Y;

                sb.Append($"    <{typeName} Name=\"{fe.Name}\" Canvas.Left=\"{left:F1}\" Canvas.Top=\"{top:F1}\" Width=\"{width:F1}\" Height=\"{height:F1}\"");

                var contentProp = fe.GetType().GetProperty("Content");
                if (contentProp != null)
                {
                    var contentVal = contentProp.GetValue(fe);
                    if (contentVal is RichTextBlock rtb)
                    {
                        string runText = GetTextFromRichText(rtb);
                        sb.Append($" Content=\"{runText}\"");
                    }
                    else if (contentVal != null)
                    {
                        sb.Append($" Content=\"{contentVal}\"");
                    }
                }

                var textProp = fe.GetType().GetProperty("Text");
                if (textProp != null)
                {
                    sb.Append($" Text=\"{textProp.GetValue(fe)}\"");
                }

                var valueProp = fe.GetType().GetProperty("Value");
                if (valueProp != null)
                {
                    sb.Append($" Value=\"{valueProp.GetValue(fe):F1}\"");
                }

                var isCheckedProp = fe.GetType().GetProperty("IsChecked");
                if (isCheckedProp != null)
                {
                    sb.Append($" IsChecked=\"{isCheckedProp.GetValue(fe)}\"");
                }

                var isOnProp = fe.GetType().GetProperty("IsOn");
                if (isOnProp != null)
                {
                    sb.Append($" IsOn=\"{isOnProp.GetValue(fe)}\"");
                }

                sb.AppendLine(" />");
            }
        }
        sb.AppendLine("</Canvas>");
        return sb.ToString();
    }

    private static string GetTextFromRichText(RichTextBlock rtb)
    {
        var sb = new StringBuilder();
        foreach (var inline in rtb.Inlines)
        {
            if (inline is Run r)
            {
                sb.Append(r.Text);
            }
            else if (inline is Bold b)
            {
                foreach (var binline in b.Inlines)
                {
                    if (binline is Run br) sb.Append(br.Text);
                }
            }
        }
        return sb.ToString();
    }
}
