using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Documents;
#pragma warning disable CS0169 // The field is never used
#pragma warning disable CS0414 // The field is assigned but its value is never used

using System;
using System.Collections.Generic;
using System.Numerics;
using Silk.NET.Input;
using ProGPU.Layout;
using ProGPU.Vector;
using ProGPU.Scene;
using ProGPU.Text;

namespace Microsoft.UI.Xaml.Controls;

public class DataGridColumn
{
    public string Header { get; set; } = string.Empty;
    public DataGridLength Width { get; set; }
    public float ActualWidth { get; internal set; }
    public string PropertyName { get; set; } = string.Empty;
    public bool IsAscending { get; set; } = true;

    public DataGridColumn(string header, DataGridLength width, string propName)
    {
        Header = header;
        Width = width;
        ActualWidth = width.IsPixel ? width.Value : 120f;
        PropertyName = propName;
    }
}

public class DataGrid : Control
{
    private float _fontSize = 13f;
    private float _rowHeight = 28f;
    private float _headerHeight = 32f;
    private int _selectedIndex = -1;
    private float _scrollOffset;
    private bool _isDraggingScroll;
    private float _dragStartOffset;
    private float _dragStartMouseY;
    private int _hoveredRowIndex = -1;
    private bool _isPointerOverScrollbar;
    private int _resizingColumnIndex = -1;
    private float _resizeStartMouseX;
    private float _resizeStartWidth;
    private int _hoveredSeparatorIndex = -1;

    private DataGridColumn? _sortingColumn;
    private readonly List<object> _itemsSource = new();

    private int _editingRow = -1;
    private int _editingCol = -1;
    private FrameworkElement? _cellEditor;
    private DateTime _lastClickTime = DateTime.MinValue;
    private int _lastClickRow = -1;
    private int _lastClickCol = -1;

    public List<DataGridColumn> Columns { get; } = new();

    public List<object> ItemsSource => _itemsSource;

    protected override void OnPropertyChanged(Microsoft.UI.Xaml.DependencyProperty dp, object? oldValue, object? newValue)
    {
        base.OnPropertyChanged(dp, oldValue, newValue);
        if (dp == FontProperty)
        {
            Invalidate();
        }
    }

    public TtfFont? GetActiveFont()
    {
        return Font ?? PopupService.DefaultFont;
    }

    public float FontSize
    {
        get => _fontSize;
        set { _fontSize = value; Invalidate(); }
    }

    public float RowHeight
    {
        get => _rowHeight;
        set { _rowHeight = value; Invalidate(); }
    }

    public int SelectedIndex
    {
        get => _selectedIndex;
        set
        {
            if (_selectedIndex != value)
            {
                _selectedIndex = Math.Clamp(value, -1, _itemsSource.Count - 1);
                Invalidate();
                SelectionChanged?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    public DataGridColumn? SortingColumn
    {
        get => _sortingColumn;
        set { _sortingColumn = value; Invalidate(); }
    }

    public float ScrollOffset
    {
        get => _scrollOffset;
        set
        {
            float maxScroll = Math.Max(0f, TotalBodyHeight - ViewportHeight);
            float clamped = Math.Clamp(value, 0f, maxScroll);
            if (_scrollOffset != clamped)
            {
                _scrollOffset = clamped;
                if (_editingRow != -1)
                {
                    UpdateCellEditorLayout();
                }
                PopupService.DismissNonDialogPopups();
                Invalidate();
            }
        }
    }

    public float TotalBodyHeight => _itemsSource.Count * _rowHeight;
    public float ViewportHeight => Size.Y - _headerHeight;
    public int EditingRow => _editingRow;
    public int EditingCol => _editingCol;

    public Func<object, string, string>? CellValueBinding { get; set; }

    public event EventHandler? SelectionChanged;

    public DataGrid()
    {
        Padding = new Thickness(0);

        var defaultStyle = ThemeManager.GetDefaultStyle(GetType());
        if (defaultStyle != null)
        {
            Style = defaultStyle;
        }
    }

    public void AddItem(object item)
    {
        _itemsSource.Add(item);
        Invalidate();
    }

    public void ClearItems()
    {
        CancelEdit();
        _itemsSource.Clear();
        _selectedIndex = -1;
        _scrollOffset = 0f;
        Invalidate();
    }

    private string GetCellValue(object item, string propName)
    {
        if (CellValueBinding != null) return CellValueBinding(item, propName);
        var prop = item.GetType().GetProperty(propName);
        return prop?.GetValue(item)?.ToString() ?? string.Empty;
    }

    public void SortItems(DataGridColumn column)
    {
        string prop = column.PropertyName;
        bool asc = column.IsAscending;

        _itemsSource.Sort((x, y) =>
        {
            string valX = GetCellValue(x, prop);
            string valY = GetCellValue(y, prop);

            if (double.TryParse(valX, out double dX) && double.TryParse(valY, out double dY))
            {
                return asc ? dX.CompareTo(dY) : dY.CompareTo(dX);
            }
            return asc ? string.Compare(valX, valY, StringComparison.Ordinal) : string.Compare(valY, valX, StringComparison.Ordinal);
        });

        SortingColumn = column;
        Invalidate();
    }

    public override void OnPointerWheelChanged(PointerRoutedEventArgs e)
    {
        if (IsEnabled)
        {
            float maxScroll = Math.Max(0f, TotalBodyHeight - ViewportHeight);
            if (maxScroll > 0f)
            {
                float delta = -e.WheelDelta * _rowHeight;
                float targetOffset = Math.Clamp(_scrollOffset + delta, 0f, maxScroll);
                if (targetOffset != _scrollOffset)
                {
                    ScrollOffset = targetOffset;
                    e.Handled = true;
                    return;
                }
            }
        }
        base.OnPointerWheelChanged(e);
    }

    public override void OnPointerPressed(PointerRoutedEventArgs e)
    {
        if (IsEnabled)
        {
            // Click inside Header: Column Sorting / Resizing
            if (e.Position.Y <= _headerHeight)
            {
                // First check if within 4px of any column separator for resizing
                float runningX = Padding.Left;
                for (int i = 0; i < Columns.Count; i++)
                {
                    float separatorX = runningX + Columns[i].ActualWidth;
                    if (Math.Abs(e.Position.X - separatorX) <= 4f)
                    {
                        _resizingColumnIndex = i;
                        _resizeStartMouseX = e.Position.X;
                        _resizeStartWidth = Columns[i].ActualWidth;
                        InputSystem.CapturePointer(this);
                        e.Handled = true;
                        return;
                    }
                    runningX = separatorX;
                }

                // Otherwise, sorting
                runningX = Padding.Left;
                foreach (var col in Columns)
                {
                    if (e.Position.X >= runningX && e.Position.X <= runningX + col.ActualWidth)
                    {
                        if (SortingColumn == col)
                        {
                            col.IsAscending = !col.IsAscending;
                        }
                        else
                        {
                            col.IsAscending = true;
                        }
                        SortItems(col);
                        e.Handled = true;
                        return;
                    }
                    runningX += col.ActualWidth;
                }
            }
            // Click in Scrollbar area
            else if (TotalBodyHeight > ViewportHeight && e.Position.X >= Size.X - 10f)
            {
                float viewportH = ViewportHeight;
                float thumbHeight = Math.Max(24f, (viewportH / TotalBodyHeight) * viewportH);
                float scrollableHeight = TotalBodyHeight - viewportH;
                float thumbY = _headerHeight + (ScrollOffset / scrollableHeight) * (viewportH - thumbHeight);

                if (e.Position.Y >= thumbY && e.Position.Y <= thumbY + thumbHeight)
                {
                    _isDraggingScroll = true;
                    _dragStartOffset = ScrollOffset;
                    _dragStartMouseY = e.Position.Y;
                    e.Handled = true;
                    return;
                }
            }
            // Click in Body rows
            else
            {
                int r = (int)((e.Position.Y - _headerHeight + ScrollOffset) / _rowHeight);
                if (r >= 0 && r < _itemsSource.Count)
                {
                    SelectedIndex = r;

                    float runningX = Padding.Left;
                    int colIndex = -1;
                    foreach (var col in Columns)
                    {
                        if (e.Position.X >= runningX && e.Position.X <= runningX + col.ActualWidth)
                        {
                            colIndex = Columns.IndexOf(col);
                            break;
                        }
                        runningX += col.ActualWidth;
                    }

                    if (colIndex != -1)
                    {
                        DateTime now = DateTime.UtcNow;
                        if ((now - _lastClickTime).TotalMilliseconds < 300 && r == _lastClickRow && colIndex == _lastClickCol)
                        {
                            BeginEdit(r, colIndex);
                        }
                        else
                        {
                            _lastClickTime = now;
                            _lastClickRow = r;
                            _lastClickCol = colIndex;
                        }
                    }

                    e.Handled = true;
                    return;
                }
            }
        }
        base.OnPointerPressed(e);
    }

    public override void OnPointerReleased(PointerRoutedEventArgs e)
    {
        _isDraggingScroll = false;
        if (_resizingColumnIndex != -1)
        {
            _resizingColumnIndex = -1;
            InputSystem.ReleasePointerCapture();
        }
        base.OnPointerReleased(e);
    }

    public override void OnPointerExited(PointerRoutedEventArgs e)
    {
        _hoveredRowIndex = -1;
        _hoveredSeparatorIndex = -1;
        _isPointerOverScrollbar = false;
        base.OnPointerExited(e);
    }

    public override void OnPointerMoved(PointerRoutedEventArgs e)
    {
        if (IsEnabled)
        {
            _isPointerOverScrollbar = e.Position.X >= Size.X - 12f;

            if (_resizingColumnIndex != -1)
            {
                float deltaX = e.Position.X - _resizeStartMouseX;
                float newWidth = Math.Max(20f, _resizeStartWidth + deltaX);
                Columns[_resizingColumnIndex].Width = newWidth;

                if (_editingRow != -1)
                {
                    UpdateCellEditorLayout();
                }

                InvalidateMeasure();
                Invalidate();
                e.Handled = true;
                return;
            }

            // Detect mouse movement in column headers for resize zones
            int hoveredSep = -1;
            if (e.Position.Y <= _headerHeight)
            {
                float runningX = Padding.Left;
                for (int i = 0; i < Columns.Count; i++)
                {
                    float separatorX = runningX + Columns[i].ActualWidth;
                    if (Math.Abs(e.Position.X - separatorX) <= 4f)
                    {
                        hoveredSep = i;
                        break;
                    }
                    runningX = separatorX;
                }
            }

            if (_hoveredSeparatorIndex != hoveredSep)
            {
                _hoveredSeparatorIndex = hoveredSep;
                Invalidate();
            }

            if (e.Position.Y > _headerHeight && e.Position.X < Size.X - 12f)
            {
                int r = (int)((e.Position.Y - _headerHeight + ScrollOffset) / _rowHeight);
                if (r >= 0 && r < _itemsSource.Count)
                {
                    if (_hoveredRowIndex != r)
                    {
                        _hoveredRowIndex = r;
                        Invalidate();
                    }
                }
                else
                {
                    if (_hoveredRowIndex != -1)
                    {
                        _hoveredRowIndex = -1;
                        Invalidate();
                    }
                }
            }
            else
            {
                if (_hoveredRowIndex != -1)
                {
                    _hoveredRowIndex = -1;
                    Invalidate();
                }
            }
        }

        if (_isDraggingScroll && IsEnabled)
        {
            float viewportH = ViewportHeight;
            float thumbHeight = Math.Max(24f, (viewportH / TotalBodyHeight) * viewportH);
            float scrollableHeight = TotalBodyHeight - viewportH;
            float trackLength = viewportH - thumbHeight;

            if (trackLength > 0f)
            {
                float deltaY = e.Position.Y - _dragStartMouseY;
                ScrollOffset = _dragStartOffset + (deltaY / trackLength) * scrollableHeight;
            }
            e.Handled = true;
            return;
        }
        base.OnPointerMoved(e);
    }

    private float MeasureTextWidth(string text, TtfFont font, float fontSize)
    {
        if (string.IsNullOrEmpty(text)) return 0f;
        float width = 0f;
        foreach (char c in text)
        {
            ushort gIdx = font.GetGlyphIndex(c);
            width += font.GetAdvanceWidth(gIdx, fontSize);
        }
        return width;
    }

    private void ResolveColumnWidths(float availableWidth)
    {
        if (Columns.Count == 0) return;

        var activeFont = GetActiveFont();
        if (activeFont == null) return;

        float totalStars = 0f;
        float allocatedWidth = 0f;
        var starColumns = new List<DataGridColumn>();

        // 1. First pass: Resolve Pixel and Auto columns
        for (int i = 0; i < Columns.Count; i++)
        {
            var col = Columns[i];
            if (col.Width.IsPixel)
            {
                col.ActualWidth = Math.Max(20f, col.Width.Value);
                allocatedWidth += col.ActualWidth;
            }
            else if (col.Width.IsAuto)
            {
                // Measure header text
                float minWidth = MeasureTextWidth(col.Header, activeFont, FontSize) + 24f; // padding + indicator space

                // High-performance Auto sizing: measure up to 100 sample items
                float maxCellW = 0f;
                int sampleCount = Math.Min(100, _itemsSource.Count);
                for (int j = 0; j < sampleCount; j++)
                {
                    var item = _itemsSource[j];
                    string val = GetCellValue(item, col.PropertyName);
                    if (!string.IsNullOrEmpty(val))
                    {
                        float cellW = MeasureTextWidth(val, activeFont, FontSize) + 20f; // 10px padding on each side
                        maxCellW = Math.Max(maxCellW, cellW);
                    }
                }

                col.ActualWidth = Math.Max(minWidth, maxCellW);
                allocatedWidth += col.ActualWidth;
            }
            else if (col.Width.IsStar)
            {
                totalStars += col.Width.Value;
                starColumns.Add(col);
            }
        }

        // 2. Second pass: Distribute remaining space to Star columns
        if (starColumns.Count > 0)
        {
            float remainingWidth = Math.Max(0f, availableWidth - allocatedWidth);
            if (remainingWidth > 0f && totalStars > 0f)
            {
                float extraPerStar = remainingWidth / totalStars;
                foreach (var col in starColumns)
                {
                    col.ActualWidth = Math.Max(30f, col.Width.Value * extraPerStar);
                }
            }
            else
            {
                foreach (var col in starColumns)
                {
                    col.ActualWidth = 50f;
                }
            }
        }
    }

    protected override Vector2 MeasureOverride(Vector2 availableSize)
    {
        if (_cellEditor != null)
        {
            _cellEditor.Measure(availableSize);
        }
        float w = WidthConstraint ?? availableSize.X;
        float h = HeightConstraint ?? availableSize.Y;
        if (float.IsInfinity(w)) w = 500f;
        if (float.IsInfinity(h)) h = 300f;

        ResolveColumnWidths(w);

        return new Vector2(w, h);
    }

    protected override void ArrangeOverride(Rect arrangeRect)
    {
        Size = new Vector2(arrangeRect.Width, arrangeRect.Height);
        ResolveColumnWidths(arrangeRect.Width);
        UpdateCellEditorLayout();
    }

    public override void OnRender(DrawingContext context)
    {
        var activeFont = GetActiveFont();
        if (activeFont == null) return;

        // 1. Draw DataGrid outer card background & border
        Pen outerPen = IsFocused 
            ? new Pen(BorderBrush ?? ThemeManager.GetBrush("SystemAccentColor"), 2f) // Glowing Segoe Blue active focus ring
            : new Pen(BorderBrush ?? ThemeManager.GetBrush("ControlBorder"), 1f); // Thin outline

        var bg = Background ?? ThemeManager.GetBrush("CardBackground");
        context.DrawRectangle(bg, outerPen, new Rect(Vector2.Zero, Size));

        // 2. Draw Column Headers
        float runningX = Padding.Left;
        Brush headerBg = ThemeManager.GetBrush("HeaderBackground"); // Fluent Header plate
        Pen colBorder = new Pen(ThemeManager.GetBrush("ControlBorder"), 1f);

        context.DrawRectangle(headerBg, null, new Rect(0, 0, Size.X, _headerHeight));

        for (int i = 0; i < Columns.Count; i++)
        {
            var col = Columns[i];
            Rect colRect = new Rect(runningX, 0, col.ActualWidth, _headerHeight);
            context.DrawRectangle(null, colBorder, colRect);

            // Draw Header Text
            float textY = (_headerHeight - FontSize) / 2f;
            context.DrawText(col.Header, activeFont, FontSize, ThemeManager.GetBrush("TextPrimary"), new Vector2(runningX + 8f, textY));

            // Draw Sorting indicator if active sorting
            if (SortingColumn == col)
            {
                string sortIndicator = col.IsAscending ? " ▲" : " ▼";
                float headerTextW = col.Header.Length * (FontSize * 0.6f); // approximate width
                context.DrawText(sortIndicator, activeFont, FontSize - 2f, ThemeManager.GetBrush("SystemAccentColor"), new Vector2(runningX + 8f + headerTextW, textY));
            }

            // Draw highlight if this separator is hovered or being resized
            if (i == _hoveredSeparatorIndex || i == _resizingColumnIndex)
            {
                float separatorX = runningX + col.ActualWidth;
                context.DrawRectangle(ThemeManager.GetBrush("SystemAccentColor"), null, new Rect(separatorX - 1f, 0f, 2f, _headerHeight));
            }

            runningX += col.ActualWidth;
        }

        // 3. Draw Body Row Cells (Virtualized recycling viewport loop)
        if (_itemsSource.Count > 0)
        {
            int startRow = (int)Math.Floor(ScrollOffset / _rowHeight);
            int endRow = (int)Math.Ceiling((ScrollOffset + ViewportHeight) / _rowHeight);

            startRow = Math.Clamp(startRow, 0, _itemsSource.Count - 1);
            endRow = Math.Clamp(endRow, 0, _itemsSource.Count - 1);

            context.PushClip(new Rect(0, _headerHeight, Size.X, ViewportHeight));

            for (int r = startRow; r <= endRow; r++)
            {
                float rowY = _headerHeight + r * _rowHeight - ScrollOffset;
                var item = _itemsSource[r];

                // Alternate, Hover & Selection backgrounds
                Brush? rowBg = null;
                if (r == SelectedIndex)
                {
                    rowBg = ThemeManager.GetBrush("SelectionHighlight"); // Premium selection
                }
                else if (r == _hoveredRowIndex)
                {
                    rowBg = ThemeManager.GetBrush("ControlBackgroundHover"); // Hover state row highlight
                }
                else if (r % 2 == 1)
                {
                    rowBg = ThemeManager.GetBrush("ControlBackground"); // Subtle alternate rows
                }

                Rect rowRect = new Rect(0, rowY, Size.X, _rowHeight);
                if (rowBg != null)
                {
                    context.DrawRectangle(rowBg, null, rowRect);
                }

                // Draw active selection vertical indicator stripe on far-left
                if (r == SelectedIndex)
                {
                    Rect selectionStripe = new Rect(0f, rowY + 2f, 3f, _rowHeight - 4f);
                    context.DrawRectangle(ThemeManager.GetBrush("SystemAccentColor"), null, selectionStripe);
                }

                // Draw cell text grid columns
                float colX = Padding.Left;
                for (int c = 0; c < Columns.Count; c++)
                {
                    var col = Columns[c];
                    float colWidth = col.ActualWidth;

                    if (r == _editingRow && c == _editingCol)
                    {
                        // Do not draw text under editor
                    }
                    else
                    {
                        string val = GetCellValue(item, col.PropertyName);
                        float cellTextY = rowY + (_rowHeight - FontSize) / 2f;
                        context.DrawText(val, activeFont, FontSize, ThemeManager.GetBrush("TextPrimary"), new Vector2(colX + 8f, cellTextY));
                    }
                    colX += colWidth;
                }

                // Draw thin grid lines
                context.DrawRectangle(null, new Pen(ThemeManager.GetBrush("ControlBorder"), 0.5f), new Rect(0, rowY, Size.X, _rowHeight));
            }

            context.PopClip();
        }

        // 4. Draw Scrollbar track
        if (TotalBodyHeight > ViewportHeight)
        {
            float scrollbarWidth = (_isPointerOverScrollbar || _isDraggingScroll) ? 8f : 3f;
            float padding = (_isPointerOverScrollbar || _isDraggingScroll) ? 2f : 4f;
            float viewportH = ViewportHeight;
            float thumbHeight = Math.Max(24f, (viewportH / TotalBodyHeight) * viewportH);
            float scrollableHeight = TotalBodyHeight - viewportH;
            float thumbY = _headerHeight + (ScrollOffset / scrollableHeight) * (viewportH - thumbHeight);

            Rect trackRect = new Rect(Size.X - scrollbarWidth - padding, _headerHeight, scrollbarWidth, viewportH);
            Rect thumbRect = new Rect(Size.X - scrollbarWidth - padding, thumbY, scrollbarWidth, thumbHeight);

            // Draw track (subtle translucent backdrop line)
            Brush trackBg = (_isPointerOverScrollbar || _isDraggingScroll) 
                ? ThemeManager.GetBrush("ControlBackgroundHover") 
                : ThemeManager.GetBrush("ControlBackground");
            context.DrawRectangle(trackBg, null, trackRect);

            // Draw thumb (glassmorphic capsule)
            Brush thumbBg = (_isPointerOverScrollbar || _isDraggingScroll)
                ? ThemeManager.GetBrush("ScrollbarThumbHover")
                : ThemeManager.GetBrush("ScrollbarThumb");
            
            context.DrawRoundedRectangle(thumbBg, null, thumbRect, scrollbarWidth / 2f);
        }
    }

    public override void OnKeyDown(KeyRoutedEventArgs e)
    {
        if (IsEnabled)
        {
            if (e.Key == Key.Enter)
            {
                if (SelectedIndex >= 0 && SelectedIndex < _itemsSource.Count && _editingRow == -1)
                {
                    BeginEdit(SelectedIndex, 0);
                    e.Handled = true;
                    return;
                }
            }
        }
        base.OnKeyDown(e);
    }

    public void BeginEdit(int row, int col)
    {
        if (row < 0 || row >= _itemsSource.Count || col < 0 || col >= Columns.Count)
            return;

        _editingRow = row;
        _editingCol = col;

        var item = _itemsSource[row];
        var column = Columns[col];
        string val = GetCellValue(item, column.PropertyName);

        if (_cellEditor != null)
        {
            RemoveChild(_cellEditor);
            _cellEditor = null;
        }

        // Reflectively retrieve cell property type to avoid circular dependency
        Type cellType = typeof(string);
        var typeProp = item.GetType().GetProperty("PropertyType");
        if (typeProp != null)
        {
            cellType = typeProp.GetValue(item) as Type ?? typeof(string);
        }
        else
        {
            var prop = item.GetType().GetProperty(column.PropertyName);
            if (prop != null) cellType = prop.PropertyType;
        }

        if (cellType == typeof(bool))
        {
            var cb = new CheckBox
            {
                IsChecked = val.Equals("True", StringComparison.OrdinalIgnoreCase),
                Font = GetActiveFont()
            };
            cb.Checked += (s, ev) => { CommitValue("True"); };
            cb.Unchecked += (s, ev) => { CommitValue("False"); };
            _cellEditor = cb;
        }
        else if (cellType.IsEnum)
        {
            var combo = new ComboBox
            {
                Font = GetActiveFont(),
                FontSize = FontSize,
                CornerRadius = 0f
            };
            foreach (var name in Enum.GetNames(cellType))
            {
                combo.Items.Add(new ComboBoxItem { Text = name });
            }
            foreach (var itemNode in combo.Items)
            {
                if (itemNode.Text.Equals(val, StringComparison.OrdinalIgnoreCase))
                {
                    combo.SelectedItem = itemNode;
                    break;
                }
            }
            combo.SelectionChanged += (s, ev) =>
            {
                if (combo.SelectedItem != null)
                {
                    CommitValue(combo.SelectedItem.Text);
                }
            };
            _cellEditor = combo;
        }
        else if (cellType == typeof(Brush) || cellType == typeof(Vector4))
        {
            _cellEditor = new BrushCellEditor(this, val);
        }
        else
        {
            var tb = new CellEditorTextBox(this)
            {
                Text = val,
                Font = GetActiveFont(),
                FontSize = FontSize,
                Padding = new Thickness(8f, 0f, 8f, 0f),
                CornerRadius = 0f
            };
            _cellEditor = tb;
        }

        if (_cellEditor.Parent == null)
        {
            AddChild(_cellEditor);
        }

        UpdateCellEditorLayout();
        Invalidate();

        InputSystem.SetFocus(_cellEditor);
        if (_cellEditor is CellEditorTextBox cet)
        {
            cet.CaretIndex = val.Length;
        }
    }

    public void CommitValue(string val)
    {
        if (_editingRow != -1 && _cellEditor != null)
        {
            if (_cellEditor is TextBox tb) tb.Text = val;
            else if (_cellEditor is CheckBox cb) cb.IsChecked = val.Equals("True", StringComparison.OrdinalIgnoreCase);
            else if (_cellEditor is ComboBox combo)
            {
                foreach (var item in combo.Items)
                {
                    if (item.Text.Equals(val, StringComparison.OrdinalIgnoreCase))
                    {
                        combo.SelectedItem = item;
                        break;
                    }
                }
            }
            CommitEdit();
        }
    }

    public void CommitEdit()
    {
        if (_editingRow != -1 && _cellEditor != null)
        {
            int row = _editingRow;
            int col = _editingCol;
            var item = _itemsSource[row];
            var column = Columns[col];

            string newValueText = "";
            if (_cellEditor is TextBox tb)
            {
                newValueText = tb.Text;
            }
            else if (_cellEditor is CheckBox cb)
            {
                newValueText = cb.IsChecked.ToString();
            }
            else if (_cellEditor is ComboBox combo)
            {
                newValueText = combo.SelectedItem?.Text ?? "";
            }
            else if (_cellEditor is BrushCellEditor bce)
            {
                newValueText = bce.Value;
            }

            _editingRow = -1;
            _editingCol = -1;

            _cellEditor.WidthConstraint = 0f;
            _cellEditor.HeightConstraint = 0f;
            _cellEditor.Measure(new Vector2(0, 0));
            _cellEditor.Arrange(new Rect(0, 0, 0, 0));
            RemoveChild(_cellEditor);

            if (InputSystem.FocusedElement == _cellEditor)
            {
                InputSystem.SetFocus(this);
            }

            try
            {
                System.Reflection.PropertyInfo? prop = item.GetType().GetProperty(column.PropertyName)
                                                     ?? item.GetType().GetProperty("Value");
                if (prop != null && prop.CanWrite)
                {
                    System.Type propType = prop.PropertyType;
                    if (propType == typeof(string))
                    {
                        prop.SetValue(item, newValueText);
                    }
                    else if (propType == typeof(double))
                    {
                        if (double.TryParse(newValueText, out double dVal))
                        {
                            prop.SetValue(item, dVal);
                        }
                    }
                    else if (propType == typeof(int))
                    {
                        if (int.TryParse(newValueText, out int iVal))
                        {
                            prop.SetValue(item, iVal);
                        }
                    }
                    else if (propType == typeof(float))
                    {
                        if (float.TryParse(newValueText, out float fVal))
                        {
                            prop.SetValue(item, fVal);
                        }
                    }
                    else if (propType.IsEnum)
                    {
                        try
                        {
                            var eval = Enum.Parse(propType, newValueText, true);
                            prop.SetValue(item, eval);
                        }
                        catch {}
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error committing edit: {ex.Message}");
            }

            Invalidate();
        }
    }

    public void CancelEdit()
    {
        if (_editingRow != -1)
        {
            _editingRow = -1;
            _editingCol = -1;

            if (_cellEditor != null)
            {
                _cellEditor.WidthConstraint = 0f;
                _cellEditor.HeightConstraint = 0f;
                _cellEditor.Measure(new Vector2(0, 0));
                _cellEditor.Arrange(new Rect(0, 0, 0, 0));
                RemoveChild(_cellEditor);

                if (InputSystem.FocusedElement == _cellEditor)
                {
                    InputSystem.SetFocus(this);
                }
            }

            Invalidate();
        }
    }

    private void UpdateCellEditorLayout()
    {
        if (_cellEditor == null) return;

        if (_editingRow != -1)
        {
            float rowY = _headerHeight + _editingRow * _rowHeight - _scrollOffset;
            float colX = Padding.Left;
            for (int i = 0; i < _editingCol; i++)
            {
                colX += Columns[i].ActualWidth;
            }
            float colWidth = Columns[_editingCol].ActualWidth;

            if (rowY + _rowHeight <= _headerHeight || rowY >= Size.Y)
            {
                _cellEditor.WidthConstraint = 0f;
                _cellEditor.HeightConstraint = 0f;
                _cellEditor.Measure(new Vector2(0, 0));
                _cellEditor.Arrange(new Rect(0, 0, 0, 0));
                _cellEditor.ClipBounds = null;
            }
            else
            {
                _cellEditor.WidthConstraint = colWidth;
                _cellEditor.HeightConstraint = _rowHeight;
                _cellEditor.Measure(new Vector2(colWidth, _rowHeight));
                _cellEditor.Arrange(new Rect(colX, rowY, colWidth, _rowHeight));

                if (rowY < _headerHeight)
                {
                    float clipY = _headerHeight - rowY;
                    _cellEditor.ClipBounds = new Rect(0f, clipY, colWidth, _rowHeight - clipY);
                }
                else if (rowY + _rowHeight > Size.Y)
                {
                    float clipH = Size.Y - rowY;
                    _cellEditor.ClipBounds = new Rect(0f, 0f, colWidth, clipH);
                }
                else
                {
                    _cellEditor.ClipBounds = null;
                }
            }
        }
        else
        {
            _cellEditor.WidthConstraint = 0f;
            _cellEditor.HeightConstraint = 0f;
            _cellEditor.Measure(new Vector2(0, 0));
            _cellEditor.Arrange(new Rect(0, 0, 0, 0));
            _cellEditor.ClipBounds = null;
        }
    }

    private class CellEditorTextBox : TextBox
    {
        private readonly DataGrid _owner;

        public CellEditorTextBox(DataGrid owner)
        {
            _owner = owner;
        }

        public override void OnKeyDown(KeyRoutedEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                _owner.CommitEdit();
                e.Handled = true;
                return;
            }
            else if (e.Key == Key.Escape)
            {
                _owner.CancelEdit();
                e.Handled = true;
                return;
            }
            base.OnKeyDown(e);
        }

        public override void OnVisualStateChanged()
        {
            base.OnVisualStateChanged();
            if (!IsFocused && _owner._editingRow != -1)
            {
                _owner.CancelEdit();
            }
        }
    }

    private class BrushCellEditor : Grid
    {
        private readonly DataGrid _owner;
        private readonly TextBox _textBox;
        private readonly Button _colorBtn;
        private Border? _pickerPopup;

        public BrushCellEditor(DataGrid owner, string initialVal)
        {
            _owner = owner;
            ColumnDefinitions.Add(GridLength.Star(1f));
            ColumnDefinitions.Add(new GridLength(24f, GridUnitType.Absolute));

            _textBox = new TextBox
            {
                Text = initialVal,
                Font = owner.GetActiveFont(),
                FontSize = owner.FontSize,
                Padding = new Thickness(4f, 0f, 4f, 0f),
                CornerRadius = 0f
            };
            _textBox.KeyDown += (s, e) =>
            {
                if (e.Key == Key.Enter)
                {
                    _owner.CommitEdit();
                    e.Handled = true;
                }
                else if (e.Key == Key.Escape)
                {
                    _owner.CancelEdit();
                    e.Handled = true;
                }
            };
            _textBox.TextChanged += (s, e) =>
            {
                var val = _textBox.Text;
                _colorBtn.Background = GetBrushFromText(val);
                UpdateLiveValue(val);
            };
            Grid.SetColumn(_textBox, 0);
            AddChild(_textBox);

            _colorBtn = new Button
            {
                WidthConstraint = 20f,
                HeightConstraint = 20f,
                CornerRadius = 2f,
                Margin = new Thickness(2),
                Background = GetBrushFromText(initialVal)
            };
            _colorBtn.Click += (s, e) => { ShowColorPickerPopup(); };
            Grid.SetColumn(_colorBtn, 1);
            AddChild(_colorBtn);
        }

        public string Value => _textBox.Text;

        private Brush GetBrushFromText(string txt)
        {
            if (string.IsNullOrEmpty(txt)) return new SolidColorBrush(new Vector4(0f, 0f, 0f, 0f));
            if (txt.Equals("Transparent", StringComparison.OrdinalIgnoreCase))
            {
                return new SolidColorBrush(new Vector4(0f, 0f, 0f, 0f));
            }
            if (txt.StartsWith("#"))
            {
                try
                {
                    var hex = txt.Substring(1);
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
            return new ThemeResourceBrush(txt);
        }

        private void UpdateLiveValue(string val)
        {
            if (_owner._editingRow != -1)
            {
                var item = _owner._itemsSource[_owner._editingRow];
                var column = _owner.Columns[_owner._editingCol];
                var prop = item.GetType().GetProperty(column.PropertyName) ?? item.GetType().GetProperty("Value");
                if (prop != null && prop.CanWrite)
                {
                    if (prop.PropertyType == typeof(Brush) || typeof(Brush).IsAssignableFrom(prop.PropertyType))
                    {
                        prop.SetValue(item, GetBrushFromText(val));
                    }
                    else if (prop.PropertyType == typeof(Vector4))
                    {
                        var brush = GetBrushFromText(val);
                        if (brush is SolidColorBrush scb)
                        {
                            prop.SetValue(item, scb.Color);
                        }
                    }
                    else if (prop.PropertyType == typeof(string))
                    {
                        prop.SetValue(item, val);
                    }
                    else
                    {
                        try
                        {
                            prop.SetValue(item, Convert.ChangeType(val, prop.PropertyType));
                        }
                        catch
                        {
                            prop.SetValue(item, val);
                        }
                    }
                }
                // Force redraw of the designer workspace
                _owner.Invalidate();
            }
        }

        private void ShowColorPickerPopup()
        {
            if (_pickerPopup == null)
            {
                var cp = new ColorPicker
                {
                    WidthConstraint = 280f,
                    HeightConstraint = 330f
                };

                // Extract vector4 color from TextBox value
                var currentBrush = GetBrushFromText(_textBox.Text);
                if (currentBrush is SolidColorBrush scb)
                {
                    cp.Color = scb.Color;
                }
                else
                {
                    cp.Color = new Vector4(1f, 1f, 1f, 1f); // default white
                }

                cp.ColorChanged += (s, e) =>
                {
                    var newCol = e.NewColor;
                    byte r = (byte)Math.Clamp(Math.Round(newCol.X * 255f), 0, 255);
                    byte g = (byte)Math.Clamp(Math.Round(newCol.Y * 255f), 0, 255);
                    byte b = (byte)Math.Clamp(Math.Round(newCol.Z * 255f), 0, 255);
                    byte a = (byte)Math.Clamp(Math.Round(newCol.W * 255f), 0, 255);
                    
                    string hex = $"#{a:X2}{r:X2}{g:X2}{b:X2}";
                    if (a == 0 && r == 0 && g == 0 && b == 0) hex = "Transparent";

                    _textBox.Text = hex;
                    _colorBtn.Background = new SolidColorBrush(newCol);

                    // Update the value on the selected designer element in real-time!
                    UpdateLiveValue(hex);
                };

                _pickerPopup = new Border
                {
                    Background = new ThemeResourceBrush("CardBackground"),
                    BorderBrush = new ThemeResourceBrush("ControlBorder"),
                    BorderThickness = new Thickness(1f),
                    CornerRadius = 8f,
                    Padding = new Thickness(10),
                    Child = cp
                };
            }

            Vector2 absPos = Offset;
            Visual? current = Parent;
            while (current != null)
            {
                absPos += current.Offset;
                current = current.Parent;
            }

            _pickerPopup.Width = 300f;
            _pickerPopup.Height = 350f;
            // Force theme synchronization right before showing the popup
            _pickerPopup.NotifyThemeChanged();
            // Place it nicely (shifted to the left so it doesn't clip off the right edge of property grid)
            PopupService.ShowPopup(_pickerPopup, new Vector2(absPos.X - 260f, absPos.Y + Size.Y + 2f), _colorBtn);
        }
    }
}
