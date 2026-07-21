using System;
using System.Collections.Generic;
using System.Numerics;
using ProGPU.Layout;
using ProGPU.Scene;

namespace Microsoft.UI.Xaml.Controls;

/// <summary>
/// Places children into a uniform cell lattice while honoring per-child row and column
/// spans. Placement is deterministic first-fit in orientation order. For N children and
/// C occupied cells, storage is O(C); ordinary dense layout is O(C), while fragmented
/// span patterns can require O(N*C) candidate probes in the worst case.
/// </summary>
public sealed class VariableSizedWrapGrid : Panel
{
    private readonly List<Placement> _placements = new();
    private float _measuredCellWidth = 1f;
    private float _measuredCellHeight = 1f;

    private readonly record struct Placement(LayoutNode Child, int Row, int Column, int RowSpan, int ColumnSpan);

    public static readonly DependencyProperty ItemWidthProperty =
        RegisterLayoutProperty(nameof(ItemWidth), typeof(float), float.NaN);

    public static readonly DependencyProperty ItemHeightProperty =
        RegisterLayoutProperty(nameof(ItemHeight), typeof(float), float.NaN);

    public static readonly DependencyProperty OrientationProperty =
        RegisterLayoutProperty(nameof(Orientation), typeof(Orientation), Orientation.Vertical);

    public static readonly DependencyProperty HorizontalChildrenAlignmentProperty =
        RegisterLayoutProperty(nameof(HorizontalChildrenAlignment), typeof(HorizontalAlignment), HorizontalAlignment.Left);

    public static readonly DependencyProperty VerticalChildrenAlignmentProperty =
        RegisterLayoutProperty(nameof(VerticalChildrenAlignment), typeof(VerticalAlignment), VerticalAlignment.Top);

    public static readonly DependencyProperty MaximumRowsOrColumnsProperty =
        RegisterLayoutProperty(nameof(MaximumRowsOrColumns), typeof(int), -1);

    public static readonly DependencyProperty RowSpanProperty =
        DependencyProperty.RegisterAttached(
            "RowSpan",
            typeof(int),
            typeof(VariableSizedWrapGrid),
            new PropertyMetadata(1, OnSpanChanged));

    public static readonly DependencyProperty ColumnSpanProperty =
        DependencyProperty.RegisterAttached(
            "ColumnSpan",
            typeof(int),
            typeof(VariableSizedWrapGrid),
            new PropertyMetadata(1, OnSpanChanged));

    public float ItemWidth
    {
        get => (float)(GetValue(ItemWidthProperty) ?? float.NaN);
        set
        {
            ValidateCellSize(value, nameof(value));
            SetValue(ItemWidthProperty, value);
        }
    }

    public float ItemHeight
    {
        get => (float)(GetValue(ItemHeightProperty) ?? float.NaN);
        set
        {
            ValidateCellSize(value, nameof(value));
            SetValue(ItemHeightProperty, value);
        }
    }

    public Orientation Orientation
    {
        get => (Orientation)(GetValue(OrientationProperty) ?? Orientation.Vertical);
        set => SetValue(OrientationProperty, value);
    }

    public HorizontalAlignment HorizontalChildrenAlignment
    {
        get => (HorizontalAlignment)(GetValue(HorizontalChildrenAlignmentProperty) ?? HorizontalAlignment.Left);
        set => SetValue(HorizontalChildrenAlignmentProperty, value);
    }

    public VerticalAlignment VerticalChildrenAlignment
    {
        get => (VerticalAlignment)(GetValue(VerticalChildrenAlignmentProperty) ?? VerticalAlignment.Top);
        set => SetValue(VerticalChildrenAlignmentProperty, value);
    }

    public int MaximumRowsOrColumns
    {
        get => (int)(GetValue(MaximumRowsOrColumnsProperty) ?? -1);
        set
        {
            if (value == 0 || value < -1) throw new ArgumentOutOfRangeException(nameof(value));
            SetValue(MaximumRowsOrColumnsProperty, value);
        }
    }

    public static int GetRowSpan(UIElement element)
    {
        ArgumentNullException.ThrowIfNull(element);
        return (int)(element.GetValue(RowSpanProperty) ?? 1);
    }

    public static void SetRowSpan(UIElement element, int value)
    {
        ArgumentNullException.ThrowIfNull(element);
        if (value <= 0) throw new ArgumentOutOfRangeException(nameof(value));
        element.SetValue(RowSpanProperty, value);
    }

    public static int GetColumnSpan(UIElement element)
    {
        ArgumentNullException.ThrowIfNull(element);
        return (int)(element.GetValue(ColumnSpanProperty) ?? 1);
    }

    public static void SetColumnSpan(UIElement element, int value)
    {
        ArgumentNullException.ThrowIfNull(element);
        if (value <= 0) throw new ArgumentOutOfRangeException(nameof(value));
        element.SetValue(ColumnSpanProperty, value);
    }

    protected override Vector2 MeasureOverride(Vector2 availableSize)
    {
        _placements.Clear();
        var children = new List<LayoutNode>(VisualChildren.Count);
        foreach (Visual visual in VisualChildren)
        {
            if (visual is LayoutNode child && !child.IsCollapsed)
                children.Add(child);
        }
        if (children.Count == 0) return Vector2.Zero;

        float cellWidth = ItemWidth;
        float cellHeight = ItemHeight;
        if (float.IsNaN(cellWidth) || float.IsNaN(cellHeight))
        {
            children[0].Measure(new Vector2(
                float.IsNaN(cellWidth) ? float.PositiveInfinity : cellWidth,
                float.IsNaN(cellHeight) ? float.PositiveInfinity : cellHeight));
            if (float.IsNaN(cellWidth)) cellWidth = Math.Max(1f, children[0].DesiredSize.X);
            if (float.IsNaN(cellHeight)) cellHeight = Math.Max(1f, children[0].DesiredSize.Y);
        }
        _measuredCellWidth = cellWidth;
        _measuredCellHeight = cellHeight;

        int primaryLimit = GetPrimaryLimit(availableSize, children, cellWidth, cellHeight);
        var occupied = new HashSet<long>();
        int maxRow = 0;
        int maxColumn = 0;

        foreach (LayoutNode child in children)
        {
            int requestedRows = child is UIElement element ? GetRowSpan(element) : 1;
            int requestedColumns = child is UIElement uiElement ? GetColumnSpan(uiElement) : 1;
            int rows = Orientation == Orientation.Vertical ? Math.Min(requestedRows, primaryLimit) : requestedRows;
            int columns = Orientation == Orientation.Horizontal ? Math.Min(requestedColumns, primaryLimit) : requestedColumns;

            (int row, int column) = FindFirstFit(occupied, rows, columns, primaryLimit);
            MarkOccupied(occupied, row, column, rows, columns);
            _placements.Add(new Placement(child, row, column, rows, columns));

            child.Measure(new Vector2(columns * cellWidth, rows * cellHeight));
            maxRow = Math.Max(maxRow, row + rows);
            maxColumn = Math.Max(maxColumn, column + columns);
        }

        return new Vector2(maxColumn * cellWidth, maxRow * cellHeight);
    }

    protected override void ArrangeOverride(Rect arrangeRect)
    {
        foreach (Placement placement in _placements)
        {
            float x = arrangeRect.X + placement.Column * _measuredCellWidth;
            float y = arrangeRect.Y + placement.Row * _measuredCellHeight;
            float width = placement.ColumnSpan * _measuredCellWidth;
            float height = placement.RowSpan * _measuredCellHeight;
            Vector2 desired = placement.Child.DesiredSize;

            if (HorizontalChildrenAlignment != HorizontalAlignment.Stretch)
            {
                float alignedWidth = Math.Min(width, desired.X);
                if (HorizontalChildrenAlignment == HorizontalAlignment.Center) x += (width - alignedWidth) / 2f;
                else if (HorizontalChildrenAlignment == HorizontalAlignment.Right) x += width - alignedWidth;
                width = alignedWidth;
            }
            if (VerticalChildrenAlignment != VerticalAlignment.Stretch)
            {
                float alignedHeight = Math.Min(height, desired.Y);
                if (VerticalChildrenAlignment == VerticalAlignment.Center) y += (height - alignedHeight) / 2f;
                else if (VerticalChildrenAlignment == VerticalAlignment.Bottom) y += height - alignedHeight;
                height = alignedHeight;
            }

            placement.Child.Arrange(new Rect(x, y, width, height));
        }
    }

    private int GetPrimaryLimit(Vector2 availableSize, List<LayoutNode> children, float cellWidth, float cellHeight)
    {
        if (MaximumRowsOrColumns > 0) return MaximumRowsOrColumns;

        float available = Orientation == Orientation.Horizontal ? availableSize.X : availableSize.Y;
        float cell = Orientation == Orientation.Horizontal ? cellWidth : cellHeight;
        if (float.IsFinite(available)) return Math.Max(1, (int)Math.Floor(Math.Max(0f, available) / cell));

        long totalSpans = 0;
        foreach (LayoutNode child in children)
        {
            int span = child is UIElement element
                ? Orientation == Orientation.Horizontal ? GetColumnSpan(element) : GetRowSpan(element)
                : 1;
            totalSpans += span;
        }
        return (int)Math.Clamp(totalSpans, 1L, int.MaxValue);
    }

    private (int Row, int Column) FindFirstFit(HashSet<long> occupied, int rows, int columns, int primaryLimit)
    {
        for (int secondary = 0; ; secondary++)
        {
            for (int primary = 0; primary < primaryLimit; primary++)
            {
                int row = Orientation == Orientation.Horizontal ? secondary : primary;
                int column = Orientation == Orientation.Horizontal ? primary : secondary;
                if (Orientation == Orientation.Horizontal && primary + columns > primaryLimit) break;
                if (Orientation == Orientation.Vertical && primary + rows > primaryLimit) break;
                if (IsFree(occupied, row, column, rows, columns)) return (row, column);
            }
        }
    }

    private static bool IsFree(HashSet<long> occupied, int row, int column, int rows, int columns)
    {
        for (int y = row; y < row + rows; y++)
            for (int x = column; x < column + columns; x++)
                if (occupied.Contains(CellKey(y, x))) return false;
        return true;
    }

    private static void MarkOccupied(HashSet<long> occupied, int row, int column, int rows, int columns)
    {
        for (int y = row; y < row + rows; y++)
            for (int x = column; x < column + columns; x++)
                occupied.Add(CellKey(y, x));
    }

    private static long CellKey(int row, int column) => ((long)row << 32) | (uint)column;

    private static DependencyProperty RegisterLayoutProperty(string name, Type type, object defaultValue) =>
        DependencyProperty.Register(
            name,
            type,
            typeof(VariableSizedWrapGrid),
            new PropertyMetadata(defaultValue) { AffectsMeasure = true, AffectsArrange = true });

    private static void OnSpanChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        if (sender is Visual { Parent: VariableSizedWrapGrid panel })
        {
            panel.InvalidateMeasure();
            panel.InvalidateArrange();
        }
    }

    private static void ValidateCellSize(float value, string parameterName)
    {
        if (!float.IsNaN(value) && (!float.IsFinite(value) || value <= 0f))
            throw new ArgumentOutOfRangeException(parameterName);
    }
}
