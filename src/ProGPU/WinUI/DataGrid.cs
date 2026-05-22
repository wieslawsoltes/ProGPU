#pragma warning disable CS0169 // The field is never used
#pragma warning disable CS0414 // The field is assigned but its value is never used

using System;
using System.Collections.Generic;
using System.Numerics;
using ProGPU.Layout;
using ProGPU.Vector;
using ProGPU.Scene;
using ProGPU.Text;

namespace ProGPU.WinUI;

public class DataGridColumn
{
    public string Header { get; set; } = string.Empty;
    public float Width { get; set; } = 120f;
    public string PropertyName { get; set; } = string.Empty;
    public bool IsAscending { get; set; } = true;

    public DataGridColumn(string header, float width, string propName)
    {
        Header = header;
        Width = width;
        PropertyName = propName;
    }
}

public class DataGrid : Control
{
    private TtfFont? _font;
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

    private DataGridColumn? _sortingColumn;
    private readonly List<object> _itemsSource = new();

    public List<DataGridColumn> Columns { get; } = new();

    public List<object> ItemsSource => _itemsSource;

    public TtfFont? Font
    {
        get => _font;
        set { _font = value; Invalidate(); }
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
                Invalidate();
            }
        }
    }

    public float TotalBodyHeight => _itemsSource.Count * _rowHeight;
    public float ViewportHeight => Size.Y - _headerHeight;

    public Func<object, string, string>? CellValueBinding { get; set; }

    public event EventHandler? SelectionChanged;

    public DataGrid()
    {
        Background = new SolidColorBrush(0x0C0C12FF);
        Padding = new Thickness(0);
        WidthConstraint = 600f;
        HeightConstraint = 350f;
    }

    public void AddItem(object item)
    {
        _itemsSource.Add(item);
        Invalidate();
    }

    public void ClearItems()
    {
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
            ScrollOffset -= e.WheelDelta * _rowHeight;
            e.Handled = true;
        }
        base.OnPointerWheelChanged(e);
    }

    public override void OnPointerPressed(PointerRoutedEventArgs e)
    {
        if (IsEnabled)
        {
            // Click inside Header: Column Sorting
            if (e.Position.Y <= _headerHeight)
            {
                float runningX = Padding.Left;
                foreach (var col in Columns)
                {
                    if (e.Position.X >= runningX && e.Position.X <= runningX + col.Width)
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
                    runningX += col.Width;
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
        base.OnPointerReleased(e);
    }

    public override void OnPointerExited(PointerRoutedEventArgs e)
    {
        _hoveredRowIndex = -1;
        _isPointerOverScrollbar = false;
        base.OnPointerExited(e);
    }

    public override void OnPointerMoved(PointerRoutedEventArgs e)
    {
        if (IsEnabled)
        {
            _isPointerOverScrollbar = e.Position.X >= Size.X - 12f;

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

    protected override Vector2 MeasureOverride(Vector2 availableSize)
    {
        float w = WidthConstraint ?? availableSize.X;
        float h = HeightConstraint ?? availableSize.Y;
        if (float.IsInfinity(w)) w = 500f;
        if (float.IsInfinity(h)) h = 300f;
        return new Vector2(w, h);
    }

    protected override void ArrangeOverride(Rect arrangeRect)
    {
        Size = new Vector2(arrangeRect.Width, arrangeRect.Height);
    }

    public override void OnRender(DrawingContext context)
    {
        if (Font == null) return;

        // 1. Draw DataGrid outer card background & border
        Pen outerPen = IsFocused 
            ? new Pen(new SolidColorBrush(0x0078D4FF), 2f) // Glowing Segoe Blue active focus ring
            : new Pen(new SolidColorBrush(0xFFFFFF15), 1f); // Thin translucent outline

        if (Background != null)
        {
            context.DrawRectangle(Background, outerPen, new Rect(Vector2.Zero, Size));
        }

        // 2. Draw Column Headers
        float runningX = Padding.Left;
        Brush headerBg = new SolidColorBrush(0x1A1A26FF); // Fluent Header dark plate
        Pen colBorder = new Pen(new SolidColorBrush(0xFFFFFF15), 1f);

        context.DrawRectangle(headerBg, null, new Rect(0, 0, Size.X, _headerHeight));

        foreach (var col in Columns)
        {
            Rect colRect = new Rect(runningX, 0, col.Width, _headerHeight);
            context.DrawRectangle(null, colBorder, colRect);

            // Draw Header Text
            float textY = (_headerHeight - FontSize) / 2f;
            context.DrawText(col.Header, Font, FontSize, new SolidColorBrush(0xFFFFFFC0), new Vector2(runningX + 8f, textY));

            // Draw Sorting indicator if active sorting
            if (SortingColumn == col)
            {
                string sortIndicator = col.IsAscending ? " ▲" : " ▼";
                float headerTextW = col.Header.Length * (FontSize * 0.6f); // approximate width
                context.DrawText(sortIndicator, Font, FontSize - 2f, new SolidColorBrush(0x0078D4FF), new Vector2(runningX + 8f + headerTextW, textY));
            }

            runningX += col.Width;
        }

        // 3. Draw Body Row Cells (Virtualized recycling viewport loop)
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
                rowBg = new SolidColorBrush(0x0078D420); // Premium translucent Segoe Blue selection
            }
            else if (r == _hoveredRowIndex)
            {
                rowBg = new SolidColorBrush(0xFFFFFF10); // Hover state row highlight
            }
            else if (r % 2 == 1)
            {
                rowBg = new SolidColorBrush(0xFFFFFF05); // Alternate row background
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
                context.DrawRectangle(new SolidColorBrush(0x0078D4FF), null, selectionStripe);
            }

            // Draw cell text grid columns
            float colX = Padding.Left;
            foreach (var col in Columns)
            {
                string val = GetCellValue(item, col.PropertyName);
                float cellTextY = rowY + (_rowHeight - FontSize) / 2f;
                
                context.DrawText(val, Font, FontSize, new SolidColorBrush(0xE0E0E0FF), new Vector2(colX + 8f, cellTextY));
                colX += col.Width;
            }

            // Draw thin grid lines
            context.DrawRectangle(null, new Pen(new SolidColorBrush(0xFFFFFF0A), 0.5f), new Rect(0, rowY, Size.X, _rowHeight));
        }

        context.PopClip();

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
                ? new SolidColorBrush(0xFFFFFF0D) 
                : new SolidColorBrush(0xFFFFFF05);
            context.DrawRectangle(trackBg, null, trackRect);

            // Draw thumb (glassmorphic capsule)
            Brush thumbBg = _isDraggingScroll 
                ? new SolidColorBrush(0xFFFFFF60) 
                : (_isPointerOverScrollbar ? new SolidColorBrush(0xFFFFFF40) : new SolidColorBrush(0xFFFFFF20));
            
            var roundedThumb = CreateRoundedRectPath(thumbRect, scrollbarWidth / 2f);
            context.DrawPath(thumbBg, null, roundedThumb);
        }
    }

    private static PathGeometry CreateRoundedRectPath(Rect rect, float r)
    {
        var geo = new PathGeometry();
        var fig = new PathFigure(new Vector2(rect.X + r, rect.Y), isClosed: true);
        fig.Segments.Add(new LineSegment(new Vector2(rect.X + rect.Width - r, rect.Y)));
        fig.Segments.Add(new QuadraticBezierSegment(new Vector2(rect.X + rect.Width, rect.Y), new Vector2(rect.X + rect.Width, rect.Y + r)));
        fig.Segments.Add(new LineSegment(new Vector2(rect.X + rect.Width, rect.Y + rect.Height - r)));
        fig.Segments.Add(new QuadraticBezierSegment(new Vector2(rect.X + rect.Width, rect.Y + rect.Height), new Vector2(rect.X + rect.Width - r, rect.Y + rect.Height)));
        fig.Segments.Add(new LineSegment(new Vector2(rect.X + r, rect.Y + rect.Height)));
        fig.Segments.Add(new QuadraticBezierSegment(new Vector2(rect.X, rect.Y + rect.Height), new Vector2(rect.X, rect.Y + rect.Height - r)));
        fig.Segments.Add(new LineSegment(new Vector2(rect.X, rect.Y + r)));
        fig.Segments.Add(new QuadraticBezierSegment(new Vector2(rect.X, rect.Y), new Vector2(rect.X + r, rect.Y)));
        geo.Figures.Add(fig);
        return geo;
    }
}
