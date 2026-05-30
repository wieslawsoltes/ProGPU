namespace ProGPU.WinUI.Designer;

using System;
using System.Reflection;
using System.Text;
using System.Collections.Generic;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Documents;
using Thickness = Microsoft.UI.Xaml.Thickness;
using HorizontalAlignment = ProGPU.Layout.HorizontalAlignment;
using VerticalAlignment = ProGPU.Layout.VerticalAlignment;
using System.Numerics;
using ProGPU.Vector;
using ProGPU.Layout;
using ProGPU.Scene;

using StackPanel = Microsoft.UI.Xaml.Controls.StackPanel;
using Grid = Microsoft.UI.Xaml.Controls.Grid;

public class StylePanel : Border
{
    private FrameworkElement? _selectedElement;
    private readonly ProGPU.Text.TtfFont? _font;
    private bool _isUpdatingFields = false;
    
    // Value Scrubbing Fields (Webflow drag-to-set style interaction)
    private bool _isScrubbing = false;
    private bool _hasScrubbed = false;
    private TextBox? _scrubbedTextBox = null;
    private Vector2 _scrubStartPos;
    private float _scrubStartValue = 0f;

    // Style Selector Section
    private readonly RichTextBlock _selectorTypeLabel;
    private readonly TextBox _selectorNameInput;

    // Layout Section
    private readonly Button _btnLayoutBlock;
    private readonly Button _btnLayoutFlex;

    // Spacing (Box Model) TextBoxes
    private TextBox _txtMarginTop = null!;
    private TextBox _txtMarginLeft = null!;
    private TextBox _txtMarginRight = null!;
    private TextBox _txtMarginBottom = null!;
    private TextBox _txtPaddingTop = null!;
    private TextBox _txtPaddingLeft = null!;
    private TextBox _txtPaddingRight = null!;
    private TextBox _txtPaddingBottom = null!;

    // Sizing & Alignment Section TextBoxes & ComboBoxes
    private TextBox _txtWidth = null!;
    private TextBox _txtHeight = null!;
    private ComboBox _cmbHorizontalAlignment = null!;
    private ComboBox _cmbVerticalAlignment = null!;

    // Appearance Section
    private TextBox _txtBgColor = null!;
    private TextBox _txtBorderRadius = null!;
    private TextBox _txtBorderWidth = null!;
    private TextBox _txtBorderColor = null!;
    private TextBox _txtOpacity = null!;
    private ComboBox _cmbVisibility = null!;

    public event Action? PropertyChanged;

    public FrameworkElement? SelectedElement
    {
        get => _selectedElement;
        set
        {
            if (_selectedElement != value)
            {
                _selectedElement = value;
                RefreshFields();
            }
        }
    }

    public StylePanel(ProGPU.Text.TtfFont? font)
    {
        _font = font;

        Background = new ThemeResourceBrush("PageBackground");
        Padding = new Thickness(0);
        HorizontalAlignment = HorizontalAlignment.Stretch;
        VerticalAlignment = VerticalAlignment.Stretch;

        var mainLayout = new StackPanel { Orientation = Orientation.Vertical };

        // 1. STYLE SELECTOR SECTION
        var selectorPanel = new StackPanel { Orientation = Orientation.Vertical, Margin = new Thickness(0, 0, 0, 4) };
        _selectorTypeLabel = new RichTextBlock { Font = _font, FontSize = 10f, Margin = new Thickness(0, 0, 0, 2) };
        _selectorTypeLabel.Foreground = new ThemeResourceBrush("TextSecondary");
        _selectorTypeLabel.Inlines.Add(new Run("STYLE SELECTOR:"));

        var selectorHeader = new Grid();
        selectorHeader.ColumnDefinitions.Add(GridLength.Auto);
        selectorHeader.ColumnDefinitions.Add(GridLength.Star(1f));

        var pillBorder = new Border
        {
            Background = new ThemeResourceBrush("SystemAccentColor"),
            CornerRadius = 4f,
            Padding = new Thickness(8, 4, 8, 4),
            Margin = new Thickness(0, 4, 0, 4),
            HorizontalAlignment = HorizontalAlignment.Left
        };
        var pillText = new RichTextBlock { Font = _font, FontSize = 11f };
        pillText.Foreground = new ThemeResourceBrush("Transparent"); // White fallback
        pillText.Inlines.Add(new Bold(new Run("Tag")));
        pillBorder.Child = pillText;
        Grid.SetColumn(pillBorder, 0);
        selectorHeader.AddChild(pillBorder);

        selectorPanel.AddChild(_selectorTypeLabel);
        selectorPanel.AddChild(selectorHeader);

        _selectorNameInput = new TextBox
        {
            HeightConstraint = 28f,
            FontSize = 11f,
            Padding = new Thickness(8, 4, 8, 4),
            PlaceholderText = "Control name...",
            Margin = new Thickness(0, 8, 0, 0)
        };
        _selectorNameInput.TextChanged += (s, e) => {
            if (_isUpdatingFields || _selectedElement == null) return;
            _selectedElement.Name = _selectorNameInput.Text.Trim();
            PropertyChanged?.Invoke();
        };
        selectorPanel.AddChild(_selectorNameInput);

        var selectorCard = new Border
        {
            Background = new ThemeResourceBrush("CardBackground"),
            BorderBrush = new ThemeResourceBrush("ControlBorder"),
            BorderThickness = new Thickness(0, 0, 0, 1),
            Padding = new Thickness(12, 10, 12, 12),
            Child = selectorPanel
        };
        mainLayout.AddChild(selectorCard);

        // 2. LAYOUT & ORIENTATION SECTION
        var layoutGrid = new Grid();
        layoutGrid.ColumnDefinitions.Add(GridLength.Star(1f));
        layoutGrid.ColumnDefinitions.Add(GridLength.Star(1f));

        _btnLayoutBlock = new Button { HeightConstraint = 26f };
        var txtBlock = new RichTextBlock { Font = _font, FontSize = 9.5f, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        txtBlock.Inlines.Add(new Run("Vertical Stack"));
        _btnLayoutBlock.Content = txtBlock;

        _btnLayoutFlex = new Button { HeightConstraint = 26f };
        var txtFlex = new RichTextBlock { Font = _font, FontSize = 9.5f, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        txtFlex.Inlines.Add(new Run("Horizontal Stack"));
        _btnLayoutFlex.Content = txtFlex;

        Grid.SetColumn(_btnLayoutBlock, 0);
        Grid.SetColumn(_btnLayoutFlex, 1);

        layoutGrid.AddChild(_btnLayoutBlock);
        layoutGrid.AddChild(_btnLayoutFlex);

        _btnLayoutBlock.Click += (s, e) => ApplyLayoutMode("Block");
        _btnLayoutFlex.Click += (s, e) => ApplyLayoutMode("Flex");

        mainLayout.AddChild(CreateSection("Layout & Direction", layoutGrid));

        // 3. SPACING (BOX MODEL) SECTION
        var spacingWidget = CreateSpacingWidget();
        mainLayout.AddChild(CreateSection("Spacing (Margin & Padding)", spacingWidget));

        // 4. SIZING & ALIGNMENT SECTION
        var sizeGrid = new Grid();
        sizeGrid.ColumnDefinitions.Add(GridLength.Star(1f));
        sizeGrid.ColumnDefinitions.Add(GridLength.Star(1f));
        sizeGrid.RowDefinitions.Add(GridLength.Auto);
        sizeGrid.RowDefinitions.Add(GridLength.Auto);

        _txtWidth = CreateTextBoxInput("Width", "Auto");
        _txtHeight = CreateTextBoxInput("Height", "Auto");

        RegisterDragScrubber(_txtWidth, minValue: 0f, isDimension: true);
        RegisterDragScrubber(_txtHeight, minValue: 0f, isDimension: true);

        _cmbHorizontalAlignment = new ComboBox { HeightConstraint = 26f, FontSize = 10f, WidthConstraint = 105f };
        _cmbHorizontalAlignment.Items.Add(new ComboBoxItem("Stretch"));
        _cmbHorizontalAlignment.Items.Add(new ComboBoxItem("Left"));
        _cmbHorizontalAlignment.Items.Add(new ComboBoxItem("Center"));
        _cmbHorizontalAlignment.Items.Add(new ComboBoxItem("Right"));
        _cmbHorizontalAlignment.SelectedItem = _cmbHorizontalAlignment.Items[0];

        _cmbVerticalAlignment = new ComboBox { HeightConstraint = 26f, FontSize = 10f, WidthConstraint = 105f };
        _cmbVerticalAlignment.Items.Add(new ComboBoxItem("Stretch"));
        _cmbVerticalAlignment.Items.Add(new ComboBoxItem("Top"));
        _cmbVerticalAlignment.Items.Add(new ComboBoxItem("Center"));
        _cmbVerticalAlignment.Items.Add(new ComboBoxItem("Bottom"));
        _cmbVerticalAlignment.SelectedItem = _cmbVerticalAlignment.Items[0];

        _txtWidth.TextChanged += (s, e) => UpdateWidthFromUi();
        _txtHeight.TextChanged += (s, e) => UpdateHeightFromUi();

        _cmbHorizontalAlignment.SelectionChanged += (s, e) => {
            if (_isUpdatingFields || _selectedElement == null) return;
            if (Enum.TryParse<HorizontalAlignment>(_cmbHorizontalAlignment.SelectedItem?.Text, out var hAlign))
            {
                _selectedElement.HorizontalAlignment = hAlign;
                NotifyChanged();
            }
        };

        _cmbVerticalAlignment.SelectionChanged += (s, e) => {
            if (_isUpdatingFields || _selectedElement == null) return;
            if (Enum.TryParse<VerticalAlignment>(_cmbVerticalAlignment.SelectedItem?.Text, out var vAlign))
            {
                _selectedElement.VerticalAlignment = vAlign;
                NotifyChanged();
            }
        };

        sizeGrid.AddChild(CreateFieldWrapper("Width", _txtWidth, 0, 0));
        sizeGrid.AddChild(CreateFieldWrapper("Height", _txtHeight, 0, 1));
        sizeGrid.AddChild(CreateFieldWrapper("Align Horiz", _cmbHorizontalAlignment, 1, 0));
        sizeGrid.AddChild(CreateFieldWrapper("Align Vert", _cmbVerticalAlignment, 1, 1));

        mainLayout.AddChild(CreateSection("Sizing & Alignment", sizeGrid));

        // 5. APPEARANCE SECTION
        var bgBorderGrid = new Grid();
        bgBorderGrid.ColumnDefinitions.Add(GridLength.Star(1f));
        bgBorderGrid.ColumnDefinitions.Add(GridLength.Star(1f));
        bgBorderGrid.RowDefinitions.Add(GridLength.Auto);
        bgBorderGrid.RowDefinitions.Add(GridLength.Auto);
        bgBorderGrid.RowDefinitions.Add(GridLength.Auto);

        _txtBgColor = CreateTextBoxInput("Color", "#FFFFFF");
        _txtBorderRadius = CreateTextBoxInput("Radius", "0");
        _txtBorderWidth = CreateTextBoxInput("Width", "0");
        _txtBorderColor = CreateTextBoxInput("Color", "#000000");
        _txtOpacity = CreateTextBoxInput("Opacity", "100");

        RegisterDragScrubber(_txtBorderRadius, minValue: 0f);
        RegisterDragScrubber(_txtBorderWidth, minValue: 0f);
        RegisterDragScrubber(_txtOpacity, minValue: 0f, maxValue: 100f);

        _cmbVisibility = new ComboBox { HeightConstraint = 26f, FontSize = 10f, WidthConstraint = 105f };
        _cmbVisibility.Items.Add(new ComboBoxItem("Visible"));
        _cmbVisibility.Items.Add(new ComboBoxItem("Collapsed"));
        _cmbVisibility.SelectedItem = _cmbVisibility.Items[0];

        _txtBgColor.TextChanged += (s, e) => {
            if (_isUpdatingFields || _selectedElement == null) return;
            var prop = _selectedElement.GetType().GetProperty("Background");
            if (prop != null)
            {
                var brush = ConvertValueToBrush(_txtBgColor.Text);
                prop.SetValue(_selectedElement, brush);
                NotifyChanged();
            }
        };

        _txtBorderRadius.TextChanged += (s, e) => {
            if (_isUpdatingFields || _selectedElement == null) return;
            var prop = _selectedElement.GetType().GetProperty("CornerRadius");
            if (prop != null && float.TryParse(_txtBorderRadius.Text, out float r))
            {
                prop.SetValue(_selectedElement, r);
                NotifyChanged();
            }
        };

        _txtBorderWidth.TextChanged += (s, e) => {
            if (_isUpdatingFields || _selectedElement == null) return;
            var prop = _selectedElement.GetType().GetProperty("BorderThickness");
            if (prop != null && float.TryParse(_txtBorderWidth.Text, out float w))
            {
                prop.SetValue(_selectedElement, new Thickness(w));
                NotifyChanged();
            }
        };

        _txtBorderColor.TextChanged += (s, e) => {
            if (_isUpdatingFields || _selectedElement == null) return;
            var prop = _selectedElement.GetType().GetProperty("BorderBrush");
            if (prop != null)
            {
                var brush = ConvertValueToBrush(_txtBorderColor.Text);
                prop.SetValue(_selectedElement, brush);
                NotifyChanged();
            }
        };

        _txtOpacity.TextChanged += (s, e) => {
            if (_isUpdatingFields || _selectedElement == null) return;
            if (float.TryParse(_txtOpacity.Text, out float op))
            {
                _selectedElement.Opacity = Math.Clamp(op / 100f, 0f, 1f);
                NotifyChanged();
            }
        };

        _cmbVisibility.SelectionChanged += (s, e) => {
            if (_isUpdatingFields || _selectedElement == null) return;
            if (Enum.TryParse<Visibility>(_cmbVisibility.SelectedItem?.Text, out var vis))
            {
                _selectedElement.Visibility = vis;
                NotifyChanged();
            }
        };

        bgBorderGrid.AddChild(CreateFieldWrapper("Bg Color", _txtBgColor, 0, 0));
        bgBorderGrid.AddChild(CreateFieldWrapper("Radius", _txtBorderRadius, 0, 1));
        bgBorderGrid.AddChild(CreateFieldWrapper("Border Width", _txtBorderWidth, 1, 0));
        bgBorderGrid.AddChild(CreateFieldWrapper("Border Color", _txtBorderColor, 1, 1));
        bgBorderGrid.AddChild(CreateFieldWrapper("Opacity %", _txtOpacity, 2, 0));
        bgBorderGrid.AddChild(CreateFieldWrapper("Visibility", _cmbVisibility, 2, 1));

        mainLayout.AddChild(CreateSection("Appearance", bgBorderGrid));

        // Setup scroll viewer wrapping all panels
        var scroll = new ScrollViewer
        {
            Content = mainLayout,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch
        };

        Child = scroll;
        RefreshFields();
    }

    private FrameworkElement CreateSpacingWidget()
    {
        // Spacing Widget Presenter using custom bevel backgrounds
        var marginGrid = new SpacingWidgetPresenter(_font);
        marginGrid.ColumnDefinitions.Add(new GridLength(40f, GridUnitType.Absolute));
        marginGrid.ColumnDefinitions.Add(GridLength.Star(1f));
        marginGrid.ColumnDefinitions.Add(new GridLength(40f, GridUnitType.Absolute));
        marginGrid.RowDefinitions.Add(new GridLength(28f, GridUnitType.Absolute));
        marginGrid.RowDefinitions.Add(GridLength.Star(1f));
        marginGrid.RowDefinitions.Add(new GridLength(28f, GridUnitType.Absolute));

        var marginBorder = new Border
        {
            BorderBrush = new ThemeResourceBrush("ControlBorder"),
            BorderThickness = new Thickness(1f),
            CornerRadius = 6f,
            Padding = new Thickness(0),
            Child = marginGrid
        };

        // Margin indicator label
        var marginLabel = new RichTextBlock { Font = _font, FontSize = 5.5f, Margin = new Thickness(2, 2, 0, 0) };
        marginLabel.Foreground = new ThemeResourceBrush("TextSecondary");
        marginLabel.Inlines.Add(new Run("MARGIN"));
        Grid.SetRow(marginLabel, 0);
        Grid.SetColumn(marginLabel, 0);
        marginGrid.AddChild(marginLabel);

        _txtMarginTop = CreateSpacingTextBox();
        _txtMarginLeft = CreateSpacingTextBox();
        _txtMarginRight = CreateSpacingTextBox();
        _txtMarginBottom = CreateSpacingTextBox();

        RegisterDragScrubber(_txtMarginTop, minValue: null, snapToInteger: true);
        RegisterDragScrubber(_txtMarginLeft, minValue: null, snapToInteger: true);
        RegisterDragScrubber(_txtMarginRight, minValue: null, snapToInteger: true);
        RegisterDragScrubber(_txtMarginBottom, minValue: null, snapToInteger: true);

        _txtMarginTop.TextChanged += (s, e) => { UpdateMarginFromUi(); UpdateScrubbedTextBoxStyle(_txtMarginTop); };
        _txtMarginLeft.TextChanged += (s, e) => { UpdateMarginFromUi(); UpdateScrubbedTextBoxStyle(_txtMarginLeft); };
        _txtMarginRight.TextChanged += (s, e) => { UpdateMarginFromUi(); UpdateScrubbedTextBoxStyle(_txtMarginRight); };
        _txtMarginBottom.TextChanged += (s, e) => { UpdateMarginFromUi(); UpdateScrubbedTextBoxStyle(_txtMarginBottom); };

        Grid.SetRow(_txtMarginTop, 0); Grid.SetColumn(_txtMarginTop, 1);
        Grid.SetRow(_txtMarginLeft, 1); Grid.SetColumn(_txtMarginLeft, 0);
        Grid.SetRow(_txtMarginRight, 1); Grid.SetColumn(_txtMarginRight, 2);
        Grid.SetRow(_txtMarginBottom, 2); Grid.SetColumn(_txtMarginBottom, 1);

        marginGrid.AddChild(_txtMarginTop);
        marginGrid.AddChild(_txtMarginLeft);
        marginGrid.AddChild(_txtMarginRight);
        marginGrid.AddChild(_txtMarginBottom);

        // 2. Padding Grid (Inner Layer)
        var paddingGrid = new Grid();
        paddingGrid.ColumnDefinitions.Add(new GridLength(40f, GridUnitType.Absolute));
        paddingGrid.ColumnDefinitions.Add(GridLength.Star(1f));
        paddingGrid.ColumnDefinitions.Add(new GridLength(40f, GridUnitType.Absolute));
        paddingGrid.RowDefinitions.Add(new GridLength(28f, GridUnitType.Absolute));
        paddingGrid.RowDefinitions.Add(GridLength.Star(1f));
        paddingGrid.RowDefinitions.Add(new GridLength(28f, GridUnitType.Absolute));

        var paddingBorder = new Border
        {
            Margin = new Thickness(4, 4, 4, 4),
            Child = paddingGrid
        };

        // Padding indicator label
        var paddingLabel = new RichTextBlock { Font = _font, FontSize = 5.5f, Margin = new Thickness(2, 2, 0, 0) };
        paddingLabel.Foreground = new ThemeResourceBrush("TextSecondary");
        paddingLabel.Inlines.Add(new Run("PADDING"));
        Grid.SetRow(paddingLabel, 0);
        Grid.SetColumn(paddingLabel, 0);
        paddingGrid.AddChild(paddingLabel);

        _txtPaddingTop = CreateSpacingTextBox();
        _txtPaddingLeft = CreateSpacingTextBox();
        _txtPaddingRight = CreateSpacingTextBox();
        _txtPaddingBottom = CreateSpacingTextBox();

        RegisterDragScrubber(_txtPaddingTop, minValue: 0f, snapToInteger: true);
        RegisterDragScrubber(_txtPaddingLeft, minValue: 0f, snapToInteger: true);
        RegisterDragScrubber(_txtPaddingRight, minValue: 0f, snapToInteger: true);
        RegisterDragScrubber(_txtPaddingBottom, minValue: 0f, snapToInteger: true);

        _txtPaddingTop.TextChanged += (s, e) => { UpdatePaddingFromUi(); UpdateScrubbedTextBoxStyle(_txtPaddingTop); };
        _txtPaddingLeft.TextChanged += (s, e) => { UpdatePaddingFromUi(); UpdateScrubbedTextBoxStyle(_txtPaddingLeft); };
        _txtPaddingRight.TextChanged += (s, e) => { UpdatePaddingFromUi(); UpdateScrubbedTextBoxStyle(_txtPaddingRight); };
        _txtPaddingBottom.TextChanged += (s, e) => { UpdatePaddingFromUi(); UpdateScrubbedTextBoxStyle(_txtPaddingBottom); };

        Grid.SetRow(_txtPaddingTop, 0); Grid.SetColumn(_txtPaddingTop, 1);
        Grid.SetRow(_txtPaddingLeft, 1); Grid.SetColumn(_txtPaddingLeft, 0);
        Grid.SetRow(_txtPaddingRight, 1); Grid.SetColumn(_txtPaddingRight, 2);
        Grid.SetRow(_txtPaddingBottom, 2); Grid.SetColumn(_txtPaddingBottom, 1);

        paddingGrid.AddChild(_txtPaddingTop);
        paddingGrid.AddChild(_txtPaddingLeft);
        paddingGrid.AddChild(_txtPaddingRight);
        paddingGrid.AddChild(_txtPaddingBottom);

        // Center Content Block representing the selected element box
        var elementCenter = new Border
        {
            Margin = new Thickness(4, 4, 4, 4)
        };
        var centerText = new RichTextBlock { Font = _font, FontSize = 8f, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        centerText.Foreground = new ThemeResourceBrush("TextSecondary");
        centerText.Inlines.Add(new Run("Element"));
        elementCenter.Child = centerText;

        Grid.SetRow(elementCenter, 1); Grid.SetColumn(elementCenter, 1);
        paddingGrid.AddChild(elementCenter);

        // Nested link
        Grid.SetRow(paddingBorder, 1); Grid.SetColumn(paddingBorder, 1);
        marginGrid.AddChild(paddingBorder);

        return marginBorder;
    }

    private TextBox CreateSpacingTextBox()
    {
        return new TextBox
        {
            WidthConstraint = 36f,
            HeightConstraint = 20f,
            FontSize = 9f,
            Padding = new Thickness(2, 2, 2, 2),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Text = "0"
        };
    }

    private TextBox CreateTextBoxInput(string placeholder, string defaultValue)
    {
        return new TextBox
        {
            HeightConstraint = 26f,
            WidthConstraint = 105f,
            FontSize = 10f,
            Padding = new Thickness(6, 4, 6, 4),
            PlaceholderText = placeholder,
            Text = defaultValue
        };
    }

    private FrameworkElement CreateFieldWrapper(string label, FrameworkElement input, int row, int col)
    {
        var wrapper = new StackPanel { Orientation = Orientation.Vertical, Margin = new Thickness(4, 4, 4, 4) };
        var textLabel = new RichTextBlock { Font = _font, FontSize = 8.5f, Margin = new Thickness(2, 0, 0, 2) };
        textLabel.Foreground = new ThemeResourceBrush("TextSecondary");
        textLabel.Inlines.Add(new Run(label));

        wrapper.AddChild(textLabel);
        wrapper.AddChild(input);

        Grid.SetRow(wrapper, row);
        Grid.SetColumn(wrapper, col);
        return wrapper;
    }

    private Border CreateSection(string title, FrameworkElement content)
    {
        var panel = new StackPanel { Orientation = Orientation.Vertical, Margin = new Thickness(0, 0, 0, 4) };

        var titleBlock = new RichTextBlock
        {
            Font = _font,
            FontSize = 9.5f,
            Margin = new Thickness(4, 2, 4, 8)
        };
        titleBlock.Inlines.Add(new Bold(new Run(title.ToUpper())));
        titleBlock.Foreground = new ThemeResourceBrush("TextSecondary");

        panel.AddChild(titleBlock);
        panel.AddChild(content);

        var border = new Border
        {
            Background = new ThemeResourceBrush("CardBackground"),
            BorderBrush = new ThemeResourceBrush("ControlBorder"),
            BorderThickness = new Thickness(0, 0, 0, 1),
            Padding = new Thickness(6, 10, 6, 12),
            Child = panel
        };
        return border;
    }

    private void RefreshFields()
    {
        if (_selectedElement == null)
        {
            _selectorTypeLabel.Inlines.Clear();
            _selectorTypeLabel.Inlines.Add(new Run("STYLE SELECTOR (NO SELECTION)"));
            _selectorNameInput.Text = "";
            return;
        }

        _isUpdatingFields = true;
        try
        {
            string typeName = _selectedElement.GetType().Name;
            _selectorTypeLabel.Inlines.Clear();
            _selectorTypeLabel.Inlines.Add(new Run($"STYLE SELECTOR (Tag: {typeName})"));

            _selectorNameInput.Text = _selectedElement.Name ?? "";

            // Margins
            var margin = _selectedElement.Margin;
            _txtMarginTop.Text = margin.Top.ToString("F0");
            _txtMarginLeft.Text = margin.Left.ToString("F0");
            _txtMarginRight.Text = margin.Right.ToString("F0");
            _txtMarginBottom.Text = margin.Bottom.ToString("F0");

            UpdateScrubbedTextBoxStyle(_txtMarginTop);
            UpdateScrubbedTextBoxStyle(_txtMarginLeft);
            UpdateScrubbedTextBoxStyle(_txtMarginRight);
            UpdateScrubbedTextBoxStyle(_txtMarginBottom);

            // Paddings
            var padding = _selectedElement.Padding;
            _txtPaddingTop.Text = padding.Top.ToString("F0");
            _txtPaddingLeft.Text = padding.Left.ToString("F0");
            _txtPaddingRight.Text = padding.Right.ToString("F0");
            _txtPaddingBottom.Text = padding.Bottom.ToString("F0");

            UpdateScrubbedTextBoxStyle(_txtPaddingTop);
            UpdateScrubbedTextBoxStyle(_txtPaddingLeft);
            UpdateScrubbedTextBoxStyle(_txtPaddingRight);
            UpdateScrubbedTextBoxStyle(_txtPaddingBottom);

            // Sizes
            float w = _selectedElement.Width;
            _txtWidth.Text = float.IsNaN(w) ? "Auto" : w.ToString("F0");

            float h = _selectedElement.Height;
            _txtHeight.Text = float.IsNaN(h) ? "Auto" : h.ToString("F0");

            // Alignments
            _cmbHorizontalAlignment.SelectedItem = FindComboBoxItem(_cmbHorizontalAlignment, _selectedElement.HorizontalAlignment.ToString());
            _cmbVerticalAlignment.SelectedItem = FindComboBoxItem(_cmbVerticalAlignment, _selectedElement.VerticalAlignment.ToString());

            // Opacity & Visibility
            _txtOpacity.Text = ((int)(_selectedElement.Opacity * 100f)).ToString();
            _cmbVisibility.SelectedItem = FindComboBoxItem(_cmbVisibility, _selectedElement.Visibility.ToString());

            // Background Brush reflectively
            var bgProp = _selectedElement.GetType().GetProperty("Background");
            if (bgProp != null)
            {
                var bgBrush = bgProp.GetValue(_selectedElement) as Brush;
                _txtBgColor.Text = GetBrushString(bgBrush);
            }

            // Radius reflectively
            var crProp = _selectedElement.GetType().GetProperty("CornerRadius");
            if (crProp != null)
            {
                float crVal = crProp.GetValue(_selectedElement) is float f ? f : 0f;
                _txtBorderRadius.Text = crVal.ToString("F0");
            }

            // Border W & Col reflectively
            var borderThickProp = _selectedElement.GetType().GetProperty("BorderThickness");
            if (borderThickProp != null)
            {
                var thick = (Thickness)(borderThickProp.GetValue(_selectedElement) ?? default(Thickness));
                _txtBorderWidth.Text = thick.Left.ToString("F0");
            }

            var borderBrushProp = _selectedElement.GetType().GetProperty("BorderBrush");
            if (borderBrushProp != null)
            {
                var brush = borderBrushProp.GetValue(_selectedElement) as Brush;
                _txtBorderColor.Text = GetBrushString(brush);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[StylePanel] Error refreshing fields: {ex.Message}");
        }
        finally
        {
            _isUpdatingFields = false;
        }
    }

    private ComboBoxItem? FindComboBoxItem(ComboBox combo, string text)
    {
        foreach (var item in combo.Items)
        {
            if (item.Text.Equals(text, StringComparison.OrdinalIgnoreCase))
            {
                return item;
            }
        }
        return null;
    }

    private void UpdateMarginFromUi()
    {
        if (_isUpdatingFields || _selectedElement == null) return;
        float.TryParse(_txtMarginLeft.Text, out float l);
        float.TryParse(_txtMarginTop.Text, out float t);
        float.TryParse(_txtMarginRight.Text, out float r);
        float.TryParse(_txtMarginBottom.Text, out float b);

        _selectedElement.Margin = new Thickness(l, t, r, b);
        NotifyChanged();
    }

    private void UpdatePaddingFromUi()
    {
        if (_isUpdatingFields || _selectedElement == null) return;
        float.TryParse(_txtPaddingLeft.Text, out float l);
        float.TryParse(_txtPaddingTop.Text, out float t);
        float.TryParse(_txtPaddingRight.Text, out float r);
        float.TryParse(_txtPaddingBottom.Text, out float b);

        _selectedElement.Padding = new Thickness(l, t, r, b);
        NotifyChanged();
    }

    private void UpdateWidthFromUi()
    {
        if (_isUpdatingFields || _selectedElement == null) return;
        string text = _txtWidth.Text.Trim();
        if (text.Equals("Auto", StringComparison.OrdinalIgnoreCase) || text.Equals("None", StringComparison.OrdinalIgnoreCase) || string.IsNullOrEmpty(text))
        {
            _selectedElement.Width = float.NaN;
        }
        else if (float.TryParse(text.Replace("px", "").Trim(), out float wval))
        {
            _selectedElement.Width = wval;
        }
        NotifyChanged();
    }

    private void UpdateHeightFromUi()
    {
        if (_isUpdatingFields || _selectedElement == null) return;
        string text = _txtHeight.Text.Trim();
        if (text.Equals("Auto", StringComparison.OrdinalIgnoreCase) || text.Equals("None", StringComparison.OrdinalIgnoreCase) || string.IsNullOrEmpty(text))
        {
            _selectedElement.Height = float.NaN;
        }
        else if (float.TryParse(text.Replace("px", "").Trim(), out float hval))
        {
            _selectedElement.Height = hval;
        }
        NotifyChanged();
    }

    private void ApplyLayoutMode(string mode)
    {
        if (_isUpdatingFields || _selectedElement == null) return;

        // Apply display block / flex reflectively if element is a StackPanel
        if (_selectedElement is StackPanel stack)
        {
            if (mode == "Block")
            {
                stack.Orientation = Orientation.Vertical;
            }
            else if (mode == "Flex")
            {
                stack.Orientation = Orientation.Horizontal;
            }
        }
        NotifyChanged();
    }

    private void NotifyChanged()
    {
        _selectedElement?.InvalidateMeasure();
        _selectedElement?.InvalidateArrange();
        _selectedElement?.Invalidate();
        PropertyChanged?.Invoke();
    }

    private string GetBrushString(Brush? brush)
    {
        if (brush == null) return "";
        if (brush is ThemeResourceBrush tr) return tr.ResourceKey;
        if (brush is SolidColorBrush scb)
        {
            var col = scb.Color;
            byte r = (byte)Math.Clamp(Math.Round(col.X * 255f), 0, 255);
            byte g = (byte)Math.Clamp(Math.Round(col.Y * 255f), 0, 255);
            byte b = (byte)Math.Clamp(Math.Round(col.Z * 255f), 0, 255);
            byte a = (byte)Math.Clamp(Math.Round(col.W * 255f), 0, 255);
            if (a == 0 && r == 0 && g == 0 && b == 0) return "Transparent";
            return $"#{a:X2}{r:X2}{g:X2}{b:X2}";
        }
        return brush.ToString() ?? "";
    }

    private Brush? ConvertValueToBrush(string val)
    {
        if (string.IsNullOrEmpty(val)) return null;
        if (val.Equals("Transparent", StringComparison.OrdinalIgnoreCase))
        {
            return new SolidColorBrush(new Vector4(0f, 0f, 0f, 0f));
        }
        if (val.StartsWith("#"))
        {
            try
            {
                var hex = val.Substring(1);
                if (hex.Length == 6) hex = "FF" + hex;
                if (hex.Length == 8)
                {
                    uint rgba = Convert.ToUInt32(hex, 16);
                    float a = ((rgba >> 24) & 0xFF) / 255.0f;
                    float r = ((rgba >> 16) & 0xFF) / 255.0f;
                    float g = ((rgba >> 8) & 0xFF) / 255.0f;
                    float b = (rgba & 0xFF) / 255.0f;
                    return new SolidColorBrush(new Vector4(r, g, b, a));
                }
            }
            catch {}
        }
        return new ThemeResourceBrush(val);
    }

    private void RegisterDragScrubber(TextBox txt, float? minValue = 0f, float? maxValue = null, bool isDimension = false, bool snapToInteger = false)
    {
        txt.PointerEntered += (s, e) => {
            if (!_isScrubbing)
            {
                InputSystem.SetMouseCursor(Silk.NET.Input.StandardCursor.HResize);
            }
        };

        txt.PointerExited += (s, e) => {
            if (!_isScrubbing)
            {
                InputSystem.SetMouseCursor(Silk.NET.Input.StandardCursor.Default);
            }
        };

        txt.PointerPressed += (s, e) => {
            if (_selectedElement != null)
            {
                _isScrubbing = true;
                _hasScrubbed = false;
                _scrubbedTextBox = txt;
                _scrubStartPos = e.Position;

                string text = txt.Text.Trim();
                float parsedVal = 0f;

                if (isDimension && (text.Equals("Auto", StringComparison.OrdinalIgnoreCase) || string.IsNullOrEmpty(text)))
                {
                    // Use actual element size as starting point if "Auto"
                    if (txt == _txtWidth) parsedVal = _selectedElement.Size.X > 0 ? _selectedElement.Size.X : 100f;
                    else if (txt == _txtHeight) parsedVal = _selectedElement.Size.Y > 0 ? _selectedElement.Size.Y : 100f;
                }
                else
                {
                    // Strip non-numeric suffixes like "px", "%" for parsing
                    string clean = text.Replace("px", "").Replace("%", "").Trim();
                    float.TryParse(clean, out parsedVal);
                }

                _scrubStartValue = parsedVal;

                InputSystem.CapturePointer(txt);
                InputSystem.SetMouseCursor(Silk.NET.Input.StandardCursor.HResize);
                e.Handled = true;
            }
        };

        txt.PointerMoved += (s, e) => {
            if (_isScrubbing && _scrubbedTextBox == txt)
            {
                float dx = e.Position.X - _scrubStartPos.X;
                if (!_hasScrubbed && Math.Abs(dx) > 3f)
                {
                    _hasScrubbed = true;
                }

                if (_hasScrubbed)
                {
                    // Multipliers: Shift = 10x speed, Alt = 0.1x speed (Webflow-parity)
                    float multiplier = 1f;
                    if (InputSystem.Current.IsShiftPressed) multiplier = 10f;
                    else if (InputSystem.Current.IsAltPressed) multiplier = 0.1f;

                    float deltaValue = dx * multiplier;
                    float newVal = _scrubStartValue + deltaValue;

                    if (snapToInteger)
                    {
                        newVal = MathF.Round(newVal);
                    }

                    if (minValue.HasValue) newVal = Math.Max(minValue.Value, newVal);
                    if (maxValue.HasValue) newVal = Math.Min(maxValue.Value, newVal);

                    txt.Text = newVal.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);
                }
                e.Handled = true;
            }
        };

        txt.PointerReleased += (s, e) => {
            if (_isScrubbing && _scrubbedTextBox == txt)
            {
                InputSystem.ReleasePointerCapture();
                InputSystem.SetMouseCursor(Silk.NET.Input.StandardCursor.Default);

                if (!_hasScrubbed)
                {
                    InputSystem.SetFocus(txt);
                }

                _isScrubbing = false;
                _hasScrubbed = false;
                _scrubbedTextBox = null;
                e.Handled = true;
            }
        };
    }

    private void UpdateScrubbedTextBoxStyle(TextBox txt)
    {
        float.TryParse(txt.Text.Trim(), out float val);
        if (val != 0)
        {
            txt.Background = ThemeManager.GetBrush("SystemAccentColor");
            txt.Foreground = new SolidColorBrush(Vector4.One); // white text
            txt.CornerRadius = 3f;
            txt.BorderThickness = new Thickness(0);
        }
        else
        {
            txt.Background = ThemeManager.GetBrush("ControlBackground");
            txt.Foreground = ThemeManager.GetBrush("TextSecondary");
            txt.CornerRadius = 3f;
            txt.BorderThickness = new Thickness(1f);
            txt.BorderBrush = ThemeManager.GetBrush("ControlBorder");
        }
    }
}

public class SpacingWidgetPresenter : Grid
{
    private readonly ProGPU.Text.TtfFont? _font;

    public SpacingWidgetPresenter(ProGPU.Text.TtfFont? font)
    {
        _font = font;
    }

    public override void OnRender(DrawingContext context)
    {
        var size = Size;
        float w = size.X;
        float h = size.Y;

        // Custom HSL dark grays matching Webflow's spaced box 3D layout bevels
        float marginL = 40f;
        float marginR = w - 40f;
        float marginT = 28f;
        float marginB = h - 28f;

        float padL = 80f;
        float padR = w - 80f;
        float padT = 56f;
        float padB = h - 56f;

        var borderPen = new Pen(ThemeManager.GetBrush("ControlBorder"), 1f);

        // Curated grays for Margin 3D bevel sectors
        var marginTopBrush = new SolidColorBrush(new Vector4(0.2f, 0.2f, 0.22f, 1f));     // Lighter top
        var marginBottomBrush = new SolidColorBrush(new Vector4(0.12f, 0.12f, 0.14f, 1f)); // Darker bottom
        var marginSideBrush = new SolidColorBrush(new Vector4(0.16f, 0.16f, 0.18f, 1f));   // Medium sides

        // Top Margin
        var pathTop = PathGeometry.Parse(System.FormattableString.Invariant($"M 0 0 L {w} 0 L {marginR} {marginT} L {marginL} {marginT} Z"));
        context.DrawPath(marginTopBrush, borderPen, pathTop);

        // Bottom Margin
        var pathBottom = PathGeometry.Parse(System.FormattableString.Invariant($"M 0 {h} L {w} {h} L {marginR} {marginB} L {marginL} {marginB} Z"));
        context.DrawPath(marginBottomBrush, borderPen, pathBottom);

        // Left Margin
        var pathLeft = PathGeometry.Parse(System.FormattableString.Invariant($"M 0 0 L 0 {h} L {marginL} {marginB} L {marginL} {marginT} Z"));
        context.DrawPath(marginSideBrush, borderPen, pathLeft);

        // Right Margin
        var pathRight = PathGeometry.Parse(System.FormattableString.Invariant($"M {w} 0 L {w} {h} L {marginR} {marginB} L {marginR} {marginT} Z"));
        context.DrawPath(marginSideBrush, borderPen, pathRight);

        // Curated grays for Padding 3D bevel sectors (slightly lighter hierarchy)
        var padTopBrush = new SolidColorBrush(new Vector4(0.24f, 0.24f, 0.26f, 1f));
        var padBottomBrush = new SolidColorBrush(new Vector4(0.14f, 0.14f, 0.16f, 1f));
        var padSideBrush = new SolidColorBrush(new Vector4(0.18f, 0.18f, 0.2f, 1f));

        // Top Padding
        var pathPadTop = PathGeometry.Parse(System.FormattableString.Invariant($"M {marginL} {marginT} L {marginR} {marginT} L {padR} {padT} L {padL} {padT} Z"));
        context.DrawPath(padTopBrush, borderPen, pathPadTop);

        // Bottom Padding
        var pathPadBottom = PathGeometry.Parse(System.FormattableString.Invariant($"M {marginL} {marginB} L {marginR} {marginB} L {padR} {padB} L {padL} {padB} Z"));
        context.DrawPath(padBottomBrush, borderPen, pathPadBottom);

        // Left Padding
        var pathPadLeft = PathGeometry.Parse(System.FormattableString.Invariant($"M {marginL} {marginT} L {marginL} {marginB} L {padL} {padB} L {padL} {padT} Z"));
        context.DrawPath(padSideBrush, borderPen, pathPadLeft);

        // Right Padding
        var pathPadRight = PathGeometry.Parse(System.FormattableString.Invariant($"M {marginR} {marginT} L {marginR} {marginB} L {padR} {padB} L {padR} {padT} Z"));
        context.DrawPath(padSideBrush, borderPen, pathPadRight);

        // Center Element Box
        var elementBg = new SolidColorBrush(new Vector4(0.1f, 0.1f, 0.12f, 1f));
        context.DrawRoundedRectangle(elementBg, borderPen, new Rect(padL, padT, padR - padL, padB - padT), 2f);

        base.OnRender(context);
    }
}
