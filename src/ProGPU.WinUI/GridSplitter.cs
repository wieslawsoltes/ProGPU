using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Documents;
using System;
using System.Numerics;
using ProGPU.Layout;
using ProGPU.Scene;
using ProGPU.Vector;

namespace Microsoft.UI.Xaml.Controls;

public enum GridSplitterResizeDirection
{
    Auto,
    Columns,
    Rows
}

public enum GridSplitterResizeBehavior
{
    BasedOnAlignment,
    CurrentAndNext,
    PreviousAndNext,
    PreviousAndCurrent
}

public class GridSplitter : Thumb
{
    public static readonly DependencyProperty ResizeDirectionProperty =
        DependencyProperty.Register(
            "ResizeDirection",
            typeof(GridSplitterResizeDirection),
            typeof(GridSplitter),
            new PropertyMetadata(GridSplitterResizeDirection.Auto, (d, e) => ((GridSplitter)d).Invalidate()));

    public GridSplitterResizeDirection ResizeDirection
    {
        get => (GridSplitterResizeDirection)(GetValue(ResizeDirectionProperty) ?? GridSplitterResizeDirection.Auto);
        set => SetValue(ResizeDirectionProperty, value);
    }

    public static readonly DependencyProperty ResizeBehaviorProperty =
        DependencyProperty.Register(
            "ResizeBehavior",
            typeof(GridSplitterResizeBehavior),
            typeof(GridSplitter),
            new PropertyMetadata(GridSplitterResizeBehavior.BasedOnAlignment, null));

    public GridSplitterResizeBehavior ResizeBehavior
    {
        get => (GridSplitterResizeBehavior)(GetValue(ResizeBehaviorProperty) ?? GridSplitterResizeBehavior.BasedOnAlignment);
        set => SetValue(ResizeBehaviorProperty, value);
    }

    public GridSplitter()
    {
        DragDelta += OnGridSplitterDragDelta;
        Background = new ThemeResourceBrush("ControlBorder");
        CornerRadius = 2f;
    }

    private void OnGridSplitterDragDelta(object sender, DragDeltaEventArgs e)
    {
        var parentGrid = Parent as Grid;
        if (parentGrid == null) return;

        // Determine effective resize direction
        var dir = ResizeDirection;
        if (dir == GridSplitterResizeDirection.Auto)
        {
            if (HorizontalAlignment == HorizontalAlignment.Stretch && VerticalAlignment != VerticalAlignment.Stretch)
            {
                dir = GridSplitterResizeDirection.Rows;
            }
            else if (VerticalAlignment == VerticalAlignment.Stretch && HorizontalAlignment != HorizontalAlignment.Stretch)
            {
                dir = GridSplitterResizeDirection.Columns;
            }
            else
            {
                dir = (Size.Y >= Size.X) ? GridSplitterResizeDirection.Columns : GridSplitterResizeDirection.Rows;
            }
        }

        if (dir == GridSplitterResizeDirection.Columns)
        {
            // Vertical splitter bar: resizes columns left/right
            int colIndex = Grid.GetColumn(this);
            int leftIdx = colIndex - 1;
            int rightIdx = colIndex + 1;

            if (leftIdx < 0 || rightIdx >= parentGrid.ColumnDefinitions.Count) return;

            var leftCol = parentGrid.ColumnDefinitions[leftIdx];
            var rightCol = parentGrid.ColumnDefinitions[rightIdx];

            float leftActual = (parentGrid._actualColWidths != null && leftIdx < parentGrid._actualColWidths.Length) 
                ? parentGrid._actualColWidths[leftIdx] 
                : leftCol.Value;
            float rightActual = (parentGrid._actualColWidths != null && rightIdx < parentGrid._actualColWidths.Length) 
                ? parentGrid._actualColWidths[rightIdx] 
                : rightCol.Value;

            if (leftCol.UnitType == GridUnitType.Star && rightCol.UnitType == GridUnitType.Star)
            {
                float totalWeight = leftCol.Value + rightCol.Value;
                float totalWidth = leftActual + rightActual;

                if (totalWidth > 0f)
                {
                    float newLeft = Math.Max(10f, leftActual + e.HorizontalChange);
                    float newRight = Math.Max(10f, rightActual - e.HorizontalChange);

                    parentGrid.ColumnDefinitions[leftIdx] = new GridLength((newLeft / totalWidth) * totalWeight, GridUnitType.Star);
                    parentGrid.ColumnDefinitions[rightIdx] = new GridLength((newRight / totalWidth) * totalWeight, GridUnitType.Star);
                }
            }
            else if (leftCol.UnitType == GridUnitType.Star && rightCol.UnitType == GridUnitType.Absolute)
            {
                parentGrid.ColumnDefinitions[rightIdx] = new GridLength(Math.Max(10f, rightCol.Value - e.HorizontalChange), GridUnitType.Absolute);
            }
            else if (leftCol.UnitType == GridUnitType.Absolute && rightCol.UnitType == GridUnitType.Star)
            {
                parentGrid.ColumnDefinitions[leftIdx] = new GridLength(Math.Max(10f, leftCol.Value + e.HorizontalChange), GridUnitType.Absolute);
            }
            else // Both are Absolute or others
            {
                parentGrid.ColumnDefinitions[leftIdx] = new GridLength(Math.Max(10f, leftCol.Value + e.HorizontalChange), GridUnitType.Absolute);
                parentGrid.ColumnDefinitions[rightIdx] = new GridLength(Math.Max(10f, rightCol.Value - e.HorizontalChange), GridUnitType.Absolute);
            }

            parentGrid.InvalidateMeasure();
            parentGrid.Invalidate();
        }
        else
        {
            // Horizontal splitter bar: resizes rows top/bottom
            int rowIndex = Grid.GetRow(this);
            int topIdx = rowIndex - 1;
            int bottomIdx = rowIndex + 1;

            if (topIdx < 0 || bottomIdx >= parentGrid.RowDefinitions.Count) return;

            var topRow = parentGrid.RowDefinitions[topIdx];
            var bottomRow = parentGrid.RowDefinitions[bottomIdx];

            float topActual = (parentGrid._actualRowHeights != null && topIdx < parentGrid._actualRowHeights.Length) 
                ? parentGrid._actualRowHeights[topIdx] 
                : topRow.Value;
            float bottomActual = (parentGrid._actualRowHeights != null && bottomIdx < parentGrid._actualRowHeights.Length) 
                ? parentGrid._actualRowHeights[bottomIdx] 
                : bottomRow.Value;

            if (topRow.UnitType == GridUnitType.Star && bottomRow.UnitType == GridUnitType.Star)
            {
                float totalWeight = topRow.Value + bottomRow.Value;
                float totalHeight = topActual + bottomActual;

                if (totalHeight > 0f)
                {
                    float newTop = Math.Max(10f, topActual + e.VerticalChange);
                    float newBottom = Math.Max(10f, bottomActual - e.VerticalChange);

                    parentGrid.RowDefinitions[topIdx] = new GridLength((newTop / totalHeight) * totalWeight, GridUnitType.Star);
                    parentGrid.RowDefinitions[bottomIdx] = new GridLength((newBottom / totalHeight) * totalWeight, GridUnitType.Star);
                }
            }
            else if (topRow.UnitType == GridUnitType.Star && bottomRow.UnitType == GridUnitType.Absolute)
            {
                parentGrid.RowDefinitions[bottomIdx] = new GridLength(Math.Max(10f, bottomRow.Value - e.VerticalChange), GridUnitType.Absolute);
            }
            else if (topRow.UnitType == GridUnitType.Absolute && bottomRow.UnitType == GridUnitType.Star)
            {
                parentGrid.RowDefinitions[topIdx] = new GridLength(Math.Max(10f, topRow.Value + e.VerticalChange), GridUnitType.Absolute);
            }
            else
            {
                parentGrid.RowDefinitions[topIdx] = new GridLength(Math.Max(10f, topRow.Value + e.VerticalChange), GridUnitType.Absolute);
                parentGrid.RowDefinitions[bottomIdx] = new GridLength(Math.Max(10f, bottomRow.Value - e.VerticalChange), GridUnitType.Absolute);
            }

            parentGrid.InvalidateMeasure();
            parentGrid.Invalidate();
        }
    }

    public override void OnRender(DrawingContext context)
    {
        var bg = GetCurrentBackground();
        var border = GetCurrentBorderBrush();
        var thickness = BorderThickness;

        // Render base resizer bar
        if (bg != null || (border != null && thickness.Left > 0f))
        {
            var pen = border != null && thickness.Left > 0f ? new Pen(border, thickness.Left) : null;
            context.DrawRoundedRectangle(bg, pen, new Rect(Vector2.Zero, Size), CornerRadius);
        }

        // Render central grab handle to match WinUI premium aesthetics
        var handleBrush = ThemeManager.GetBrush(IsPointerPressed ? "SystemAccentColor" : (IsPointerOver ? "SystemAccentColorLight1" : "TextSecondary"));
        
        bool isVerticalSplit = Size.Y >= Size.X;
        if (isVerticalSplit)
        {
            float w = Math.Min(Size.X, 3f);
            float h = Math.Min(Size.Y, 24f);
            float x = (Size.X - w) / 2f;
            float y = (Size.Y - h) / 2f;
            context.DrawRoundedRectangle(handleBrush, null, new Rect(x, y, w, h), 1.5f);
        }
        else
        {
            float w = Math.Min(Size.X, 24f);
            float h = Math.Min(Size.Y, 3f);
            float x = (Size.X - w) / 2f;
            float y = (Size.Y - h) / 2f;
            context.DrawRoundedRectangle(handleBrush, null, new Rect(x, y, w, h), 1.5f);
        }

        base.OnRender(context);
    }
}
