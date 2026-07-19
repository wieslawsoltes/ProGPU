using Thickness = Microsoft.UI.Xaml.Thickness;
using System;
using System.Collections;
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
    private sealed class GlyphBrowserItem : Border
    {
        private readonly FontIcon _icon;
        private readonly RichTextBlock _label;
        private readonly Run _indexRun;
        private readonly Run _hexRun;
        private bool _isSelected;
        private bool _isHovered;

        public GlyphBrowserItem()
        {
            CornerRadius = 6f;
            Padding = new Thickness(6);
            Background = new ThemeResourceBrush("PageBackground");
            BorderBrush = new ThemeResourceBrush("ControlBorder");
            BorderThickness = new Thickness(1f);
            HorizontalAlignment = HorizontalAlignment.Center;
            VerticalAlignment = VerticalAlignment.Center;
            WidthConstraint = 84f;
            HeightConstraint = 92f;

            var itemStack = new StackPanel
            {
                Orientation = Orientation.Vertical,
                HorizontalAlignment = HorizontalAlignment.Center
            };
            Child = itemStack;

            _icon = new FontIcon
            {
                Name = "GlyphItemIcon",
                FontSize = 36f,
                WidthConstraint = 40f,
                HeightConstraint = 40f,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 0, 0, 6)
            };
            itemStack.AddChild(_icon);

            _label = new RichTextBlock
            {
                Name = "GlyphItemLabel",
                FontSize = 9f,
                HorizontalAlignment = HorizontalAlignment.Center
            };
            _indexRun = new Run();
            _hexRun = new Run();
            _label.Inlines.Add(_indexRun);
            _label.Inlines.Add(new Bold(_hexRun)
            {
                Foreground = new ThemeResourceBrush("SystemAccentColor")
            });
            itemStack.AddChild(_label);

            PointerPressed += OnItemClick;
            PointerEntered += OnItemHover;
            PointerExited += OnItemLeave;
        }

        public void Bind(TtfFont? font, int index, bool isSelected)
        {
            Tag = index;

            if (!ReferenceEquals(_icon.Font, font))
            {
                _icon.Font = font;
            }

            var glyphIndex = (ushort)index;
            if (_icon.GlyphIndex != glyphIndex)
            {
                _icon.GlyphIndex = glyphIndex;
            }

            _indexRun.Text = $"Idx: {index}\n";
            _hexRun.Text = $"0x{index:X3}";
            SetSelected(isSelected);
        }

        public void SetHovered(bool isHovered)
        {
            if (_isHovered == isHovered)
            {
                return;
            }

            _isHovered = isHovered;
            UpdateChrome();
        }

        public int BoundGlyphIndex => Tag is int index ? index : -1;

        public bool EmitsGlyphCommand()
        {
            var context = new DrawingContext();
            _icon.OnRender(context);
            return context.Commands.Exists(static command =>
                command.Type == RenderCommandType.DrawGlyphRun &&
                command.GlyphIndices is { Length: > 0 });
        }

        private void SetSelected(bool isSelected)
        {
            if (_isSelected == isSelected)
            {
                return;
            }

            _isSelected = isSelected;
            UpdateChrome();
        }

        private void UpdateChrome()
        {
            if (_isSelected)
            {
                BorderBrush = new ThemeResourceBrush("SystemAccentColor");
                BorderThickness = new Thickness(1.5f);
                Background = new ThemeResourceBrush("ControlBackgroundHover");
            }
            else if (_isHovered)
            {
                BorderBrush = new ThemeResourceBrush("ControlBorderHover");
                BorderThickness = new Thickness(1f);
                Background = new ThemeResourceBrush("ControlBackgroundHover");
            }
            else
            {
                BorderBrush = new ThemeResourceBrush("ControlBorder");
                BorderThickness = new Thickness(1f);
                Background = new ThemeResourceBrush("PageBackground");
            }
        }
    }

    private sealed class GlyphIndexList : IList
    {
        public GlyphIndexList(int count)
        {
            Count = count;
        }

        public int Count { get; }
        public bool IsFixedSize => true;
        public bool IsReadOnly => true;
        public bool IsSynchronized => false;
        public object SyncRoot => this;
        public object? this[int index]
        {
            get => index >= 0 && index < Count ? (ushort)index : throw new ArgumentOutOfRangeException(nameof(index));
            set => throw new NotSupportedException();
        }

        public bool Contains(object? value) => value is ushort glyph && glyph < Count;
        public int IndexOf(object? value) => value is ushort glyph && glyph < Count ? glyph : -1;
        public void CopyTo(Array array, int index)
        {
            for (var glyph = 0; glyph < Count; glyph++)
            {
                array.SetValue((ushort)glyph, index + glyph);
            }
        }

        public IEnumerator GetEnumerator()
        {
            for (var glyph = 0; glyph < Count; glyph++)
            {
                yield return (ushort)glyph;
            }
        }

        public int Add(object? value) => throw new NotSupportedException();
        public void Clear() => throw new NotSupportedException();
        public void Insert(int index, object? value) => throw new NotSupportedException();
        public void Remove(object? value) => throw new NotSupportedException();
        public void RemoveAt(int index) => throw new NotSupportedException();
    }

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
    private static float _benchmarkScrollDirection = 1f;

    internal static void AdvanceBenchmarkScroll(float step)
    {
        if (_virtualGrid == null)
        {
            return;
        }

        float maxOffset = Math.Max(0f, _virtualGrid.TotalVirtualHeight - _virtualGrid.ViewportHeight);
        if (maxOffset <= 0f)
        {
            return;
        }

        float nextOffset = _virtualGrid.ScrollOffset + _benchmarkScrollDirection * step;
        if (nextOffset >= maxOffset)
        {
            nextOffset = maxOffset;
            _benchmarkScrollDirection = -1f;
        }
        else if (nextOffset <= 0f)
        {
            nextOffset = 0f;
            _benchmarkScrollDirection = 1f;
        }

        _virtualGrid.ScrollOffset = nextOffset;
    }

    internal static bool TryGetBenchmarkGlyphState(
        out int realizedItems,
        out int glyphCommandItems,
        out int minimumGlyphIndex,
        out int maximumGlyphIndex)
    {
        realizedItems = 0;
        glyphCommandItems = 0;
        minimumGlyphIndex = int.MaxValue;
        maximumGlyphIndex = -1;
        if (_virtualGrid == null)
        {
            return false;
        }

        foreach (var child in _virtualGrid.Children)
        {
            if (child is not GlyphBrowserItem item || item.BoundGlyphIndex < 0)
            {
                continue;
            }

            realizedItems++;
            minimumGlyphIndex = Math.Min(minimumGlyphIndex, item.BoundGlyphIndex);
            maximumGlyphIndex = Math.Max(maximumGlyphIndex, item.BoundGlyphIndex);
            if (item.EmitsGlyphCommand())
            {
                glyphCommandItems++;
            }
        }

        return realizedItems > 0;
    }

    public static FrameworkElement Create()
    {
        _benchmarkScrollDirection = 1f;
        // 1. Initial State Font Load
        _selectedFont = AppState._font ?? PopupService.DefaultFont;
        _systemFonts = new List<FontInfo>();

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
            UseVectorGlyphRendering = true,
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
        detailsStack.AddChild(createDetailRow("Glyph ID (hex)", _detailHexText));

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
        
        var fontItemsLoaded = false;
        fontCombo.DropDownOpening += (s, e) =>
        {
            if (fontItemsLoaded)
            {
                return;
            }

            fontItemsLoaded = true;
            try
            {
                _systemFonts = FontApi.GetSystemFonts();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[FontGlyphBrowserPage] System font scan error: {ex.Message}");
                _systemFonts = new List<FontInfo>();
            }
            foreach (var fontInfo in _systemFonts)
            {
                fontCombo.Items.Add(new ComboBoxItem { Text = fontInfo.Name, Tag = fontInfo });
            }
        };

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
        _itemsControl.ItemTemplate = static () => new GlyphBrowserItem();

        _itemsControl.BindVisualCallback = (vis, itemObj, idx) =>
        {
            ((GlyphBrowserItem)vis).Bind(_selectedFont, idx, idx == _selectedGlyphIndex);
        };

        gridBorder.Child = _itemsControl;
        leftStack.AddChild(gridBorder);
        Grid.SetRow(gridBorder, 5);

        // Update labels
        UpdateSelectedFontDetails();
        UpdateSelectedGlyph(_selectedGlyphIndex);

        return mainGrid;
    }

    private static void OnItemHover(object? sender, PointerRoutedEventArgs e)
    {
        (sender as GlyphBrowserItem)?.SetHovered(true);
    }

    private static void OnItemLeave(object? sender, PointerRoutedEventArgs e)
    {
        (sender as GlyphBrowserItem)?.SetHovered(false);
    }

    private static void OnItemClick(object? sender, PointerRoutedEventArgs e)
    {
        if (sender is Border border && border.Tag is int idx)
        {
            UpdateSelectedGlyph((ushort)idx);
            
            // Re-render only active border items to refresh selected outline highlights
            _itemsControl?.RefreshVisibleItems();
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

            if (_virtualGrid != null)
            {
                _virtualGrid.ScrollOffset = 0f;
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
            _itemsControl.ItemsSource = new GlyphIndexList(_selectedFont.NumGlyphs);
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
