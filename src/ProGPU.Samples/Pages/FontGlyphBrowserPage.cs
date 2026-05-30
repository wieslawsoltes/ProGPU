using Thickness = Microsoft.UI.Xaml.Thickness;
using System;
using System.IO;
using System.Collections.Generic;
using System.Numerics;
using ProGPU.Backend;
using ProGPU.Scene;
using ProGPU.Layout;
using ProGPU.Vector;
using ProGPU.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using StackPanel = Microsoft.UI.Xaml.Controls.StackPanel;
using Grid = Microsoft.UI.Xaml.Controls.Grid;

namespace ProGPU.Samples;

public static class FontGlyphBrowserPage
{
    private static TtfFont? _selectedFont;
    private static List<FontInfo> _systemFonts = new();
    private static ushort _selectedGlyphIndex = 0;

    // UI references for live metrics updates
    private static RichTextBlock? _unitsPerEmText;
    private static RichTextBlock? _totalGlyphsText;
    private static RichTextBlock? _ascenderText;
    private static RichTextBlock? _descenderText;
    private static RichTextBlock? _lineGapText;

    // UI references for glyph details inspector
    private static RichTextBlock? _detailIndexText;
    private static RichTextBlock? _detailHexText;
    private static Border? _detailColorsBorder;
    private static RichTextBlock? _detailColorsText;
    private static RichTextBlock? _detailWidthText;

    private static FontIcon? _largeGlyphPreview;
    private static ItemsControl? _itemsControl;
    private static UniformVirtualizingGridPanel? _virtualGrid;
    private static TextBox? _pathInput;
    private static RichTextBlock? _pathStatus;

    public static FrameworkElement Create()
    {
        // 1. Initial State Font Load
        _selectedFont = AppState._font ?? PopupService.DefaultFont;
        try
        {
            _systemFonts = FontApi.GetSystemFonts();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[FontGlyphBrowserPage] System font scan error: {ex.Message}");
            _systemFonts = new List<FontInfo>();
        }

        // 2. Main Page Layout Root
        var mainGrid = new Grid
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch
        };
        mainGrid.ColumnDefinitions.Add(new GridLength(1.4f, GridUnitType.Star)); // Left Pane: Controls + Grid
        mainGrid.ColumnDefinitions.Add(new GridLength(20f, GridUnitType.Absolute)); // Spacer
        mainGrid.ColumnDefinitions.Add(new GridLength(0.8f, GridUnitType.Star)); // Right Pane: Large Preview

        // Left Container
        var leftStack = new Grid
        {
            Margin = new Thickness(20),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch
        };
        leftStack.RowDefinitions.Add(new GridLength(1f, GridUnitType.Auto)); // Row 0: Title
        leftStack.RowDefinitions.Add(new GridLength(1f, GridUnitType.Auto)); // Row 1: Description
        leftStack.RowDefinitions.Add(new GridLength(1f, GridUnitType.Auto)); // Row 2: Controls
        leftStack.RowDefinitions.Add(new GridLength(1f, GridUnitType.Auto)); // Row 3: Path status
        leftStack.RowDefinitions.Add(new GridLength(1f, GridUnitType.Auto)); // Row 4: Metadata (Metrics Dashboard)
        leftStack.RowDefinitions.Add(new GridLength(1f, GridUnitType.Star)); // Row 5: Glyph grid!
        mainGrid.AddChild(leftStack);
        Grid.SetColumn(leftStack, 0);

        // Right Container Card
        var previewCard = new Border
        {
            Background = new ThemeResourceBrush("CardBackground"),
            BorderBrush = new ThemeResourceBrush("ControlBorder"),
            BorderThickness = new Thickness(1f),
            CornerRadius = 12f,
            Padding = new Thickness(24),
            Margin = new Thickness(20),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch
        };
        mainGrid.AddChild(previewCard);
        Grid.SetColumn(previewCard, 2);

        var previewStack = new StackPanel
        {
            Orientation = Orientation.Vertical,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch
        };
        previewCard.Child = previewStack;

        // Build Right Preview Details
        var previewTitle = new RichTextBlock { Margin = new Thickness(0, 0, 0, 16) };
        previewTitle.Inlines.Add(new Bold(new Run("Glyph High-DPI Outline Preview") { FontSize = 16f, Foreground = new ThemeResourceBrush("SystemAccentColor") }));
        previewStack.AddChild(previewTitle);

        // Vector Designer/Typographic grid backdrop workspace
        var previewBox = new TypographicPreviewBox
        {
            Background = new ThemeResourceBrush("PageBackground"),
            BorderBrush = new ThemeResourceBrush("ControlBorder"),
            BorderThickness = new Thickness(1f),
            CornerRadius = 8f,
            Height = 240f,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 0, 20)
        };
        
        _largeGlyphPreview = new FontIcon
        {
            Font = _selectedFont,
            GlyphIndex = _selectedGlyphIndex,
            FontSize = 160f,
            WidthConstraint = 200f,
            HeightConstraint = 200f,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        previewBox.Child = _largeGlyphPreview;
        previewStack.AddChild(previewBox);

        // structured details card
        var detailsCard = new Border
        {
            Background = new ThemeResourceBrush("PageBackground"),
            BorderBrush = new ThemeResourceBrush("ControlBorder"),
            BorderThickness = new Thickness(1f),
            CornerRadius = 8f,
            Padding = new Thickness(16),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Margin = new Thickness(0, 0, 0, 0)
        };

        var detailsStack = new StackPanel
        {
            Orientation = Orientation.Vertical,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        detailsCard.Child = detailsStack;
        previewStack.AddChild(detailsCard);

        Grid createDetailRow(string labelText, FrameworkElement valueElement)
        {
            var row = new Grid { HorizontalAlignment = HorizontalAlignment.Stretch, HeightConstraint = 28f, Margin = new Thickness(0, 2, 0, 2) };
            row.ColumnDefinitions.Add(new GridLength(1f, GridUnitType.Star));
            row.ColumnDefinitions.Add(new GridLength(1f, GridUnitType.Star));

            var lbl = new RichTextBlock { VerticalAlignment = VerticalAlignment.Center };
            lbl.Inlines.Add(new Bold(new Run(labelText) { FontSize = 11.5f, Foreground = new ThemeResourceBrush("TextSecondary") }));
            row.AddChild(lbl);
            Grid.SetColumn(lbl, 0);

            valueElement.HorizontalAlignment = HorizontalAlignment.Right;
            valueElement.VerticalAlignment = VerticalAlignment.Center;
            row.AddChild(valueElement);
            Grid.SetColumn(valueElement, 1);

            return row;
        }

        _detailIndexText = new RichTextBlock();
        detailsStack.AddChild(createDetailRow("Glyph Index", _detailIndexText));

        _detailHexText = new RichTextBlock();
        detailsStack.AddChild(createDetailRow("Hex Unicode", _detailHexText));

        _detailColorsText = new RichTextBlock { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
        _detailColorsBorder = new Border
        {
            CornerRadius = 4f,
            Padding = new Thickness(8, 2, 8, 2),
            Child = _detailColorsText
        };
        detailsStack.AddChild(createDetailRow("Has Color Layers", _detailColorsBorder));

        _detailWidthText = new RichTextBlock();
        detailsStack.AddChild(createDetailRow("Advance Width (em 100)", _detailWidthText));

        // Build Left Pane: Page title
        var title = new RichTextBlock { Margin = new Thickness(0, 0, 0, 6) };
        title.Inlines.Add(new Bold(new Run("TrueType Font Glyph Inspector") { FontSize = 24f, Foreground = new ThemeResourceBrush("TextPrimary") }));
        leftStack.AddChild(title);
        Grid.SetRow(title, 0);

        var desc = new RichTextBlock { Margin = new Thickness(0, 0, 0, 20) };
        desc.Inlines.Add(new Run("High-performance vector typography inspector. Browse millions of raw glyph contours smoothly via multi-column viewport virtualization backed by our zero-allocation GPGPU compute renderer.") { FontSize = 13f, Foreground = new ThemeResourceBrush("TextSecondary") });
        leftStack.AddChild(desc);
        Grid.SetRow(desc, 1);

        // Font Selectors (Combobox + Load file)
        var controlsRow = new Grid { HorizontalAlignment = HorizontalAlignment.Stretch, Margin = new Thickness(0, 0, 0, 16) };
        controlsRow.ColumnDefinitions.Add(new GridLength(1f, GridUnitType.Star));       // ComboBox
        controlsRow.ColumnDefinitions.Add(new GridLength(12f, GridUnitType.Absolute));  // Spacer
        controlsRow.ColumnDefinitions.Add(new GridLength(1.2f, GridUnitType.Star));     // Path Selector + Button

        // ComboBox Selector
        var fontSelectorStack = new StackPanel { Orientation = Orientation.Vertical };
        var selectorLabel = new RichTextBlock { Margin = new Thickness(0, 0, 0, 4) };
        selectorLabel.Inlines.Add(new Bold(new Run("SYSTEM FONTS") { FontSize = 11f, Foreground = new ThemeResourceBrush("TextSecondary") }));
        fontSelectorStack.AddChild(selectorLabel);

        var fontCombo = new ComboBox
        {
            PlaceholderText = "Select system font...",
            WidthConstraint = 260f,
            HeightConstraint = 32f
        };
        
        foreach (var fontInfo in _systemFonts)
        {
            fontCombo.Items.Add(new ComboBoxItem { Text = fontInfo.Name, Tag = fontInfo });
        }

        fontCombo.SelectionChanged += (s, e) =>
        {
            if (fontCombo.SelectedItem?.Tag is FontInfo info)
            {
                LoadFontFile(info.FilePath);
            }
        };
        fontSelectorStack.AddChild(fontCombo);
        controlsRow.AddChild(fontSelectorStack);
        Grid.SetColumn(fontSelectorStack, 0);

        // Path Selector
        var pathStack = new StackPanel { Orientation = Orientation.Vertical };
        var pathLabel = new RichTextBlock { Margin = new Thickness(0, 0, 0, 4) };
        pathLabel.Inlines.Add(new Bold(new Run("LOAD CUSTOM TTF / TTC FILE PATH") { FontSize = 11f, Foreground = new ThemeResourceBrush("TextSecondary") }));
        pathStack.AddChild(pathLabel);

        var pathGrid = new Grid { HorizontalAlignment = HorizontalAlignment.Stretch };
        pathGrid.ColumnDefinitions.Add(new GridLength(1f, GridUnitType.Star));
        pathGrid.ColumnDefinitions.Add(new GridLength(8f, GridUnitType.Absolute));
        pathGrid.ColumnDefinitions.Add(new GridLength(80f, GridUnitType.Absolute));

        _pathInput = new TextBox
        {
            PlaceholderText = "Enter absolute TTF path...",
            HeightConstraint = 32f,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        pathGrid.AddChild(_pathInput);
        Grid.SetColumn(_pathInput, 0);

        var loadBtn = new Button
        {
            HeightConstraint = 32f,
            CornerRadius = 4f,
            Background = new ThemeResourceBrush("SystemAccentColor")
        };
        var btnRun = new Run("Load") { FontSize = 12f, Foreground = new ThemeResourceBrush("TextOnAccent") };
        loadBtn.Content = new RichTextBlock { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center, Inlines = { new Bold(btnRun) } };
        loadBtn.Click += (s, e) =>
        {
            string p = _pathInput.Text ?? string.Empty;
            if (File.Exists(p))
            {
                LoadFontFile(p);
            }
            else
            {
                _pathStatus!.Inlines.Clear();
                _pathStatus.Inlines.Add(new Run("File not found.") { Foreground = new SolidColorBrush(new Vector4(1f, 0.3f, 0.3f, 1f)) });
                _pathStatus.Invalidate();
            }
        };
        pathGrid.AddChild(loadBtn);
        Grid.SetColumn(loadBtn, 2);
        pathStack.AddChild(pathGrid);
        controlsRow.AddChild(pathStack);
        Grid.SetColumn(pathStack, 2);
        leftStack.AddChild(controlsRow);
        Grid.SetRow(controlsRow, 2);

        // Path status readout
        _pathStatus = new RichTextBlock { Margin = new Thickness(0, 0, 0, 12), FontSize = 11f };
        leftStack.AddChild(_pathStatus);
        Grid.SetRow(_pathStatus, 3);

        // Font metadata readout card (Dashboard row of metric tiles)
        var metaCard = new Border
        {
            Background = new ThemeResourceBrush("CardBackground"),
            BorderBrush = new ThemeResourceBrush("ControlBorder"),
            BorderThickness = new Thickness(1f),
            CornerRadius = 8f,
            Padding = new Thickness(12),
            Margin = new Thickness(0, 0, 0, 16),
            HorizontalAlignment = HorizontalAlignment.Stretch
        };

        var metaGrid = new Grid { HorizontalAlignment = HorizontalAlignment.Stretch };
        metaGrid.ColumnDefinitions.Add(new GridLength(1f, GridUnitType.Star));
        metaGrid.ColumnDefinitions.Add(new GridLength(8f, GridUnitType.Absolute));
        metaGrid.ColumnDefinitions.Add(new GridLength(1f, GridUnitType.Star));
        metaGrid.ColumnDefinitions.Add(new GridLength(8f, GridUnitType.Absolute));
        metaGrid.ColumnDefinitions.Add(new GridLength(1f, GridUnitType.Star));
        metaGrid.ColumnDefinitions.Add(new GridLength(8f, GridUnitType.Absolute));
        metaGrid.ColumnDefinitions.Add(new GridLength(1f, GridUnitType.Star));
        metaGrid.ColumnDefinitions.Add(new GridLength(8f, GridUnitType.Absolute));
        metaGrid.ColumnDefinitions.Add(new GridLength(1f, GridUnitType.Star));

        Border createMetricCard(string labelText, out RichTextBlock valueBlock, bool isAccent = false)
        {
            var card = new Border
            {
                Background = new ThemeResourceBrush("ControlBackground"),
                BorderBrush = new ThemeResourceBrush("ControlBorder"),
                BorderThickness = new Thickness(1f),
                CornerRadius = 6f,
                Padding = new Thickness(10, 8, 10, 8),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Stretch
            };
            var stack = new StackPanel { Orientation = Orientation.Vertical, HorizontalAlignment = HorizontalAlignment.Center };
            var labelBlock = new RichTextBlock { Margin = new Thickness(0, 0, 0, 4), HorizontalAlignment = HorizontalAlignment.Center };
            labelBlock.Inlines.Add(new Bold(new Run(labelText.ToUpper()) { FontSize = 8.5f, Foreground = new ThemeResourceBrush("TextSecondary") }));
            valueBlock = new RichTextBlock { HorizontalAlignment = HorizontalAlignment.Center };
            var valueRun = new Run("0") { FontSize = 16f };
            if (isAccent)
            {
                valueRun.Foreground = new ThemeResourceBrush("SystemAccentColor");
                valueBlock.Inlines.Add(new Bold(valueRun));
            }
            else
            {
                valueRun.Foreground = new ThemeResourceBrush("TextPrimary");
                valueBlock.Inlines.Add(new Bold(valueRun));
            }
            stack.AddChild(labelBlock);
            stack.AddChild(valueBlock);
            card.Child = stack;
            return card;
        }

        var unitsCard = createMetricCard("Units Per Em", out _unitsPerEmText);
        metaGrid.AddChild(unitsCard);
        Grid.SetColumn(unitsCard, 0);

        var glyphsCard = createMetricCard("Total Glyphs", out _totalGlyphsText, isAccent: true);
        metaGrid.AddChild(glyphsCard);
        Grid.SetColumn(glyphsCard, 2);

        var ascenderCard = createMetricCard("Ascender", out _ascenderText);
        metaGrid.AddChild(ascenderCard);
        Grid.SetColumn(ascenderCard, 4);

        var descenderCard = createMetricCard("Descender", out _descenderText);
        metaGrid.AddChild(descenderCard);
        Grid.SetColumn(descenderCard, 6);

        var lineGapCard = createMetricCard("Line Gap", out _lineGapText);
        metaGrid.AddChild(lineGapCard);
        Grid.SetColumn(lineGapCard, 8);

        metaCard.Child = metaGrid;
        leftStack.AddChild(metaCard);
        Grid.SetRow(metaCard, 4);

        // 3. Setup the ItemsControl with UniformVirtualizingGridPanel ItemsPanel
        var gridBorder = new Border
        {
            Background = new ThemeResourceBrush("CardBackground"),
            BorderBrush = new ThemeResourceBrush("ControlBorder"),
            BorderThickness = new Thickness(1f),
            CornerRadius = 8f,
            Padding = new Thickness(8),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch
        };

        _itemsControl = new ItemsControl
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch
        };

        _virtualGrid = new UniformVirtualizingGridPanel
        {
            ItemWidth = 92f,
            ItemHeight = 100f,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch
        };

        _itemsControl.ItemsPanel = _virtualGrid;

        // Wire virtualized recycling delegates on ItemsControl
        _itemsControl.ItemTemplate = () =>
        {
            var itemBorder = new Border
            {
                CornerRadius = 6f,
                Padding = new Thickness(6),
                Background = new ThemeResourceBrush("PageBackground"),
                BorderBrush = new ThemeResourceBrush("ControlBorder"),
                BorderThickness = new Thickness(1f),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                WidthConstraint = 84f,
                HeightConstraint = 92f
            };

            var itemStack = new StackPanel
            {
                Orientation = Orientation.Vertical,
                HorizontalAlignment = HorizontalAlignment.Center
            };
            itemBorder.Child = itemStack;

            var itemIcon = new FontIcon
            {
                Name = "GlyphItemIcon",
                FontSize = 36f,
                WidthConstraint = 40f,
                HeightConstraint = 40f,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 0, 0, 6)
            };
            itemStack.AddChild(itemIcon);

            var itemLabel = new RichTextBlock
            {
                Name = "GlyphItemLabel",
                FontSize = 9f,
                HorizontalAlignment = HorizontalAlignment.Center
            };
            itemStack.AddChild(itemLabel);

            return itemBorder;
        };

        _itemsControl.BindVisualCallback = (vis, itemObj, idx) =>
        {
            var border = (Border)vis;
            border.Tag = idx;

            // Wire selection pointer click
            border.PointerPressed -= OnItemClick;
            border.PointerPressed += OnItemClick;

            // Highlight border if active selection
            if (idx == _selectedGlyphIndex)
            {
                border.BorderBrush = new ThemeResourceBrush("SystemAccentColor");
                border.BorderThickness = new Thickness(1.5f);
                border.Background = new ThemeResourceBrush("ControlBackgroundHover");
            }
            else
            {
                border.BorderBrush = new ThemeResourceBrush("ControlBorder");
                border.BorderThickness = new Thickness(1f);
                border.Background = new ThemeResourceBrush("PageBackground");
            }

            var itemStack = (StackPanel)border.Child!;
            var icon = (FontIcon)itemStack.Children[0];
            var label = (RichTextBlock)itemStack.Children[1];

            icon.Font = _selectedFont;
            icon.GlyphIndex = (ushort)idx;
            icon.Invalidate();

            label.Inlines.Clear();
            label.Inlines.Add(new Run($"Idx: {idx}\n"));
            label.Inlines.Add(new Bold(new Run($"0x{idx:X3}")) { Foreground = new ThemeResourceBrush("SystemAccentColor") });
            label.Invalidate();
        };

        gridBorder.Child = _itemsControl;
        leftStack.AddChild(gridBorder);
        Grid.SetRow(gridBorder, 5);

        // Update labels
        UpdateSelectedFontDetails();
        UpdateSelectedGlyph(_selectedGlyphIndex);

        return mainGrid;
    }

    private static void OnItemClick(object? sender, PointerRoutedEventArgs e)
    {
        if (sender is Border border && border.Tag is int idx)
        {
            UpdateSelectedGlyph((ushort)idx);
            
            // Re-render only active border items to refresh selected outline highlights
            _itemsControl?.RefreshItems();
        }
    }

    private static void LoadFontFile(string path)
    {
        if (!File.Exists(path)) return;

        try
        {
            var loaded = new TtfFont(path);
            _selectedFont = loaded;
            _selectedGlyphIndex = 0;

            if (_pathInput != null)
            {
                _pathInput.Text = path;
                _pathInput.Invalidate();
            }

            if (_pathStatus != null)
            {
                _pathStatus.Inlines.Clear();
                _pathStatus.Inlines.Add(new Run("Successfully loaded font: ") { Foreground = new ThemeResourceBrush("SystemGreenAccent") });
                _pathStatus.Inlines.Add(new Bold(new Run(Path.GetFileName(path))));
                _pathStatus.Invalidate();
            }

            UpdateSelectedFontDetails();
            UpdateSelectedGlyph(0);

            if (_itemsControl != null && _virtualGrid != null)
            {
                _virtualGrid.ScrollOffset = 0f;
                var indices = new ushort[_selectedFont.NumGlyphs];
                for (ushort i = 0; i < _selectedFont.NumGlyphs; i++)
                {
                    indices[i] = i;
                }
                _itemsControl.ItemsSource = indices;
            }
        }
        catch (Exception ex)
        {
            if (_pathStatus != null)
            {
                _pathStatus.Inlines.Clear();
                _pathStatus.Inlines.Add(new Run($"Error parsing TTF: {ex.Message}") { Foreground = new SolidColorBrush(new Vector4(1f, 0.3f, 0.3f, 1f)) });
                _pathStatus.Invalidate();
            }
        }
    }

    private static void UpdateSelectedFontDetails()
    {
        if (_selectedFont == null) return;

        if (_unitsPerEmText != null)
        {
            _unitsPerEmText.Inlines.Clear();
            _unitsPerEmText.Inlines.Add(new Bold(new Run(_selectedFont.UnitsPerEm.ToString()) { FontSize = 16f, Foreground = new ThemeResourceBrush("TextPrimary") }));
            _unitsPerEmText.Invalidate();
        }

        if (_totalGlyphsText != null)
        {
            _totalGlyphsText.Inlines.Clear();
            _totalGlyphsText.Inlines.Add(new Bold(new Run(_selectedFont.NumGlyphs.ToString()) { FontSize = 16f, Foreground = new ThemeResourceBrush("SystemAccentColor") }));
            _totalGlyphsText.Invalidate();
        }

        if (_ascenderText != null)
        {
            _ascenderText.Inlines.Clear();
            _ascenderText.Inlines.Add(new Bold(new Run(_selectedFont.Ascender.ToString()) { FontSize = 16f, Foreground = new ThemeResourceBrush("TextPrimary") }));
            _ascenderText.Invalidate();
        }

        if (_descenderText != null)
        {
            _descenderText.Inlines.Clear();
            _descenderText.Inlines.Add(new Bold(new Run(_selectedFont.Descender.ToString()) { FontSize = 16f, Foreground = new ThemeResourceBrush("TextPrimary") }));
            _descenderText.Invalidate();
        }

        if (_lineGapText != null)
        {
            _lineGapText.Inlines.Clear();
            _lineGapText.Inlines.Add(new Bold(new Run(_selectedFont.LineGap.ToString()) { FontSize = 16f, Foreground = new ThemeResourceBrush("TextPrimary") }));
            _lineGapText.Invalidate();
        }

        if (_itemsControl != null)
        {
            var indices = new ushort[_selectedFont.NumGlyphs];
            for (ushort i = 0; i < _selectedFont.NumGlyphs; i++)
            {
                indices[i] = i;
            }
            _itemsControl.ItemsSource = indices;
        }
    }

    private static void UpdateSelectedGlyph(ushort index)
    {
        _selectedGlyphIndex = index;

        if (_largeGlyphPreview != null)
        {
            _largeGlyphPreview.Font = _selectedFont;
            _largeGlyphPreview.GlyphIndex = _selectedGlyphIndex;
            _largeGlyphPreview.Invalidate();
        }

        if (_selectedFont != null)
        {
            if (_detailIndexText != null)
            {
                _detailIndexText.Inlines.Clear();
                _detailIndexText.Inlines.Add(new Bold(new Run($"#{index}") { FontSize = 12f, Foreground = new ThemeResourceBrush("TextPrimary") }));
                _detailIndexText.Invalidate();
            }

            if (_detailHexText != null)
            {
                _detailHexText.Inlines.Clear();
                _detailHexText.Inlines.Add(new Bold(new Run($"0x{index:X3}") { FontSize = 12f, Foreground = new ThemeResourceBrush("SystemAccentColor") }));
                _detailHexText.Invalidate();
            }

            if (_detailColorsBorder != null && _detailColorsText != null)
            {
                bool hasColors = _selectedFont.HasColorLayers(index);
                _detailColorsText.Inlines.Clear();
                if (hasColors)
                {
                    _detailColorsBorder.Background = new SolidColorBrush(new Vector4(0.188f, 0.82f, 0.345f, 0.15f));
                    _detailColorsBorder.BorderBrush = new SolidColorBrush(new Vector4(0.188f, 0.82f, 0.345f, 0.3f));
                    _detailColorsBorder.BorderThickness = new Thickness(0.5f);
                    _detailColorsText.Inlines.Add(new Bold(new Run("YES") { FontSize = 10f, Foreground = new ThemeResourceBrush("SystemGreenAccent") }));
                }
                else
                {
                    _detailColorsBorder.Background = new ThemeResourceBrush("ControlBackground");
                    _detailColorsBorder.BorderBrush = new ThemeResourceBrush("ControlBorder");
                    _detailColorsBorder.BorderThickness = new Thickness(0.5f);
                    _detailColorsText.Inlines.Add(new Bold(new Run("NO") { FontSize = 10f, Foreground = new ThemeResourceBrush("TextSecondary") }));
                }
                _detailColorsBorder.Invalidate();
                _detailColorsText.Invalidate();
            }

            if (_detailWidthText != null)
            {
                float advance = _selectedFont.GetAdvanceWidth(index, 100f);
                _detailWidthText.Inlines.Clear();
                _detailWidthText.Inlines.Add(new Bold(new Run($"{advance:F2}px") { FontSize = 12f, Foreground = new ThemeResourceBrush("TextPrimary") }));
                _detailWidthText.Invalidate();
            }
        }
    }
}

/// <summary>
/// A premium typographic designer workspace box that draws a dynamic vector blueprint grid backdrop and baseline coordinate crosshair axes.
/// </summary>
public class TypographicPreviewBox : Border
{
    public override void OnRender(DrawingContext context)
    {
        // 1. Draw page background
        var bg = Background ?? ThemeManager.GetBrush("PageBackground");
        context.DrawRectangle(bg, null, new Rect(Vector2.Zero, Size));

        // 2. Draw blueprint grid lines (thin, translucent lines)
        var gridPen = new Pen(ThemeManager.GetBrush("ControlBorder") ?? new SolidColorBrush(new Vector4(0.2f, 0.2f, 0.2f, 0.3f)), 0.5f);
        
        float step = 20f;
        for (float y = step; y < Size.Y; y += step)
        {
            context.DrawLine(gridPen, new Vector2(0, y), new Vector2(Size.X, y));
        }
        for (float x = step; x < Size.X; x += step)
        {
            context.DrawLine(gridPen, new Vector2(x, 0), new Vector2(x, Size.Y));
        }

        // Draw bold center baseline/midline axes (using theme accent color)
        var axisPen = new Pen(ThemeManager.GetBrush("SystemAccentColor") ?? new SolidColorBrush(new Vector4(0f, 0.478f, 1f, 1f)), 1f);
        float centerX = Size.X / 2f;
        float centerY = Size.Y / 2f;
        context.DrawLine(axisPen, new Vector2(0, centerY), new Vector2(Size.X, centerY));
        context.DrawLine(axisPen, new Vector2(centerX, 0), new Vector2(centerX, Size.Y));

        // 3. Draw child visual elements (large FontIcon glyph)
        base.OnRender(context);
    }
}
