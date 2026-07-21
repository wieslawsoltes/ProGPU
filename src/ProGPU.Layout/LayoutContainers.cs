using System;
using System.Collections.Generic;
using System.Numerics;
using ProGPU.Scene;

namespace Microsoft.UI.Xaml.Controls
{
    public enum Orientation
    {
        Vertical = 0,
        Horizontal = 1
    }
}

namespace Microsoft.UI.Xaml
{
    public enum GridUnitType
    {
        Absolute,
        Auto,
        Star
    }

    public struct GridLength
    {
        public float Value;
        public GridUnitType UnitType;

        public GridLength(float value, GridUnitType type = GridUnitType.Absolute)
        {
            Value = value;
            UnitType = type;
        }

        public static GridLength Auto => new(1f, GridUnitType.Auto);
        public static GridLength Star(float weight = 1f) => new(weight, GridUnitType.Star);
    }
}

namespace ProGPU.Layout
{
    using Microsoft.UI.Xaml;
    using Microsoft.UI.Xaml.Controls;

    public class StackPanel : LayoutNode
    {
        private Orientation _orientation = Orientation.Vertical;
        public Orientation Orientation
        {
            get => _orientation;
            set
            {
                if (_orientation != value)
                {
                    _orientation = value;
                    InvalidateMeasure();
                }
            }
        }

        protected override Vector2 MeasureOverride(Vector2 availableSize)
        {
            float totalWidth = 0f;
            float totalHeight = 0f;

            foreach (var child in Children)
            {
                if (child is LayoutNode node)
                {
                    if (Orientation == Orientation.Vertical)
                    {
                        node.Measure(new Vector2(availableSize.X, float.PositiveInfinity));
                        var desired = node.DesiredSize;
                        totalWidth = Math.Max(totalWidth, desired.X);
                        totalHeight += desired.Y;
                    }
                    else
                    {
                        node.Measure(new Vector2(float.PositiveInfinity, availableSize.Y));
                        var desired = node.DesiredSize;
                        totalWidth += desired.X;
                        totalHeight = Math.Max(totalHeight, desired.Y);
                    }
                }
            }

            return new Vector2(totalWidth, totalHeight);
        }

        protected override void ArrangeOverride(Rect arrangeRect)
        {
            float offset = 0f;

            foreach (var child in Children)
            {
                if (child is LayoutNode node)
                {
                    var desired = node.DesiredSize;
                    
                    if (Orientation == Orientation.Vertical)
                    {
                        float childHeight = desired.Y;
                        node.Arrange(new Rect(arrangeRect.X, arrangeRect.Y + offset, arrangeRect.Width, childHeight));
                        offset += childHeight;
                    }
                    else
                    {
                        float childWidth = desired.X;
                        node.Arrange(new Rect(arrangeRect.X + offset, arrangeRect.Y, childWidth, arrangeRect.Height));
                        offset += childWidth;
                    }
                }
            }
        }
    }

    public class GridPanel : LayoutNode
    {
        public List<GridLength> ColumnDefinitions { get; } = new();
        public List<GridLength> RowDefinitions { get; } = new();

        private readonly Dictionary<Visual, (int row, int col)> _cellMappings = new();

        public void SetRow(Visual child, int row)
        {
            var (currentRow, currentCol) = _cellMappings.TryGetValue(child, out var mapping) ? mapping : (0, 0);
            _cellMappings[child] = (row, currentCol);
            Invalidate();
            InvalidateMeasure();
        }

        public void SetColumn(Visual child, int col)
        {
            var (currentRow, currentCol) = _cellMappings.TryGetValue(child, out var mapping) ? mapping : (0, 0);
            _cellMappings[child] = (currentRow, col);
            Invalidate();
            InvalidateMeasure();
        }

        private (int row, int col) GetCell(Visual child)
        {
            return _cellMappings.TryGetValue(child, out var cell) ? cell : (0, 0);
        }

        protected override Vector2 MeasureOverride(Vector2 availableSize)
        {
            int colCount = Math.Max(1, ColumnDefinitions.Count);
            int rowCount = Math.Max(1, RowDefinitions.Count);

            float[] colWidths = new float[colCount];
            float[] rowHeights = new float[rowCount];

            var cols = ColumnDefinitions.Count > 0 ? ColumnDefinitions : new List<GridLength> { GridLength.Star() };
            var rows = RowDefinitions.Count > 0 ? RowDefinitions : new List<GridLength> { GridLength.Star() };

            // 1. Measure absolute and auto cells first
            float remainingWidth = availableSize.X;
            float remainingHeight = availableSize.Y;

            float starColsWeight = 0f;
            float starRowsWeight = 0f;

            for (int i = 0; i < colCount; i++)
            {
                if (cols[i].UnitType == GridUnitType.Absolute)
                {
                    colWidths[i] = cols[i].Value;
                    remainingWidth -= colWidths[i];
                }
                else if (cols[i].UnitType == GridUnitType.Star)
                {
                    starColsWeight += cols[i].Value;
                }
            }

            for (int i = 0; i < rowCount; i++)
            {
                if (rows[i].UnitType == GridUnitType.Absolute)
                {
                    rowHeights[i] = rows[i].Value;
                    remainingHeight -= rowHeights[i];
                }
                else if (rows[i].UnitType == GridUnitType.Star)
                {
                    starRowsWeight += rows[i].Value;
                }
            }

            remainingWidth = Math.Max(0f, remainingWidth);
            remainingHeight = Math.Max(0f, remainingHeight);

            // 2. Measure children that belong to Auto or Star cells
            foreach (var child in Children)
            {
                if (child is LayoutNode node)
                {
                    var (r, c) = GetCell(node);
                    r = Math.Clamp(r, 0, rowCount - 1);
                    c = Math.Clamp(c, 0, colCount - 1);

                    // Approximate cell size for measuring
                    float childAvailW = cols[c].UnitType switch
                    {
                        GridUnitType.Absolute => colWidths[c],
                        GridUnitType.Star => (cols[c].Value / starColsWeight) * remainingWidth,
                        _ => float.PositiveInfinity
                    };

                    float childAvailH = rows[r].UnitType switch
                    {
                        GridUnitType.Absolute => rowHeights[r],
                        GridUnitType.Star => (rows[r].Value / starRowsWeight) * remainingHeight,
                        _ => float.PositiveInfinity
                    };

                    node.Measure(new Vector2(childAvailW, childAvailH));
                    var desired = node.DesiredSize;

                    if (cols[c].UnitType == GridUnitType.Auto)
                    {
                        colWidths[c] = Math.Max(colWidths[c], desired.X);
                    }
                    if (rows[r].UnitType == GridUnitType.Auto)
                    {
                        rowHeights[r] = Math.Max(rowHeights[r], desired.Y);
                    }
                }
            }

            // Subtract measured auto cells from remaining star space
            for (int i = 0; i < colCount; i++)
            {
                if (cols[i].UnitType == GridUnitType.Auto)
                {
                    remainingWidth = Math.Max(0f, remainingWidth - colWidths[i]);
                }
            }
            for (int i = 0; i < rowCount; i++)
            {
                if (rows[i].UnitType == GridUnitType.Auto)
                {
                    remainingHeight = Math.Max(0f, remainingHeight - rowHeights[i]);
                }
            }

            // 3. Distribute remaining space to Star cells
            if (starColsWeight > 0f)
            {
                for (int i = 0; i < colCount; i++)
                {
                    if (cols[i].UnitType == GridUnitType.Star)
                    {
                        colWidths[i] = (cols[i].Value / starColsWeight) * remainingWidth;
                    }
                }
            }

            if (starRowsWeight > 0f)
            {
                for (int i = 0; i < rowCount; i++)
                {
                    if (rows[i].UnitType == GridUnitType.Star)
                    {
                        rowHeights[i] = (rows[i].Value / starRowsWeight) * remainingHeight;
                    }
                }
            }

            // Return total dimensions calculated
            float gridW = 0f;
            foreach (float w in colWidths) gridW += w;
            float gridH = 0f;
            foreach (float h in rowHeights) gridH += h;

            return new Vector2(gridW, gridH);
        }

        protected override void ArrangeOverride(Rect arrangeRect)
        {
            int colCount = Math.Max(1, ColumnDefinitions.Count);
            int rowCount = Math.Max(1, RowDefinitions.Count);

            var cols = ColumnDefinitions.Count > 0 ? ColumnDefinitions : new List<GridLength> { GridLength.Star() };
            var rows = RowDefinitions.Count > 0 ? RowDefinitions : new List<GridLength> { GridLength.Star() };

            float[] colWidths = new float[colCount];
            float[] rowHeights = new float[rowCount];

            float totalW = arrangeRect.Width;
            float totalH = arrangeRect.Height;

            float starColsWeight = 0f;
            float starRowsWeight = 0f;

            // Same allocation logic for arranging
            for (int i = 0; i < colCount; i++)
            {
                if (cols[i].UnitType == GridUnitType.Absolute)
                {
                    colWidths[i] = cols[i].Value;
                    totalW -= colWidths[i];
                }
                else if (cols[i].UnitType == GridUnitType.Star)
                {
                    starColsWeight += cols[i].Value;
                }
            }

            for (int i = 0; i < rowCount; i++)
            {
                if (rows[i].UnitType == GridUnitType.Absolute)
                {
                    rowHeights[i] = rows[i].Value;
                    totalH -= rowHeights[i];
                }
                else if (rows[i].UnitType == GridUnitType.Star)
                {
                    starRowsWeight += rows[i].Value;
                }
            }

            totalW = Math.Max(0f, totalW);
            totalH = Math.Max(0f, totalH);

            // Auto sizing is based on Measured cell dimensions
            foreach (var child in Children)
            {
                if (child is LayoutNode node)
                {
                    var (r, c) = GetCell(node);
                    r = Math.Clamp(r, 0, rowCount - 1);
                    c = Math.Clamp(c, 0, colCount - 1);

                    if (cols[c].UnitType == GridUnitType.Auto)
                    {
                        colWidths[c] = Math.Max(colWidths[c], node.DesiredSize.X);
                    }
                    if (rows[r].UnitType == GridUnitType.Auto)
                    {
                        rowHeights[r] = Math.Max(rowHeights[r], node.DesiredSize.Y);
                    }
                }
            }

            // Subtract auto cell widths
            for (int i = 0; i < colCount; i++)
            {
                if (cols[i].UnitType == GridUnitType.Auto)
                {
                    totalW = Math.Max(0f, totalW - colWidths[i]);
                }
            }
            for (int i = 0; i < rowCount; i++)
            {
                if (rows[i].UnitType == GridUnitType.Auto)
                {
                    totalH = Math.Max(0f, totalH - rowHeights[i]);
                }
            }

            // Distribute star space
            if (starColsWeight > 0f)
            {
                for (int i = 0; i < colCount; i++)
                {
                    if (cols[i].UnitType == GridUnitType.Star)
                    {
                        colWidths[i] = (cols[i].Value / starColsWeight) * totalW;
                    }
                }
            }

            if (starRowsWeight > 0f)
            {
                for (int i = 0; i < rowCount; i++)
                {
                    if (rows[i].UnitType == GridUnitType.Star)
                    {
                        rowHeights[i] = (rows[i].Value / starRowsWeight) * totalH;
                    }
                }
            }

            // Calculate cell offsets
            float[] colOffsets = new float[colCount];
            float curX = arrangeRect.X;
            for (int i = 0; i < colCount; i++)
            {
                colOffsets[i] = curX;
                curX += colWidths[i];
            }

            float[] rowOffsets = new float[rowCount];
            float curY = arrangeRect.Y;
            for (int i = 0; i < rowCount; i++)
            {
                rowOffsets[i] = curY;
                curY += rowHeights[i];
            }

            // Arrange children inside their cells
            foreach (var child in Children)
            {
                if (child is LayoutNode node)
                {
                    var (r, c) = GetCell(node);
                    r = Math.Clamp(r, 0, rowCount - 1);
                    c = Math.Clamp(c, 0, colCount - 1);

                    node.Arrange(new Rect(colOffsets[c], rowOffsets[r], colWidths[c], rowHeights[r]));
                }
            }
        }
    }
}
