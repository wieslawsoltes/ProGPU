using System;
using System.Collections.Generic;
using System.Numerics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using ProGPU.Layout;
using ProGPU.Scene;
using StackPanel = Microsoft.UI.Xaml.Controls.StackPanel;

namespace ProGPU.Designer;

public interface IPanelDragEditor
{
    bool CanHandle(FrameworkElement panel);
    void HandleDrag(FrameworkElement child, FrameworkElement panel, Vector2 localMousePos, Vector2 dragDelta);
}

public static class PanelDragEditorRegistry
{
    private static readonly List<IPanelDragEditor> Editors = new()
    {
        new CanvasDragEditor(),
        new StackPanelDragEditor(),
        new GridDragEditor(),
        new DefaultPanelDragEditor() // Fallback
    };

    public static void HandleDrag(FrameworkElement child, FrameworkElement panel, Vector2 localMousePos, Vector2 dragDelta)
    {
        if (child == null || panel == null) return;

        foreach (var editor in Editors)
        {
            if (editor.CanHandle(panel))
            {
                editor.HandleDrag(child, panel, localMousePos, dragDelta);
                break;
            }
        }
    }
}

public class CanvasDragEditor : IPanelDragEditor
{
    public bool CanHandle(FrameworkElement panel) => panel is Canvas;

    public void HandleDrag(FrameworkElement child, FrameworkElement panel, Vector2 localMousePos, Vector2 dragDelta)
    {
        // For nested canvas, drag standard local coordinates using simple incremental translation.
        float currentLeft = Canvas.GetLeft(child);
        float currentTop = Canvas.GetTop(child);

        Canvas.SetLeft(child, currentLeft + dragDelta.X);
        Canvas.SetTop(child, currentTop + dragDelta.Y);
    }
}

public class StackPanelDragEditor : IPanelDragEditor
{
    public bool CanHandle(FrameworkElement panel) => panel is StackPanel;

    public void HandleDrag(FrameworkElement child, FrameworkElement panel, Vector2 localMousePos, Vector2 dragDelta)
    {
        var stackPanel = (StackPanel)panel;
        var children = new List<Visual>();
        int currentIdx = -1;

        for (int i = 0; i < stackPanel.Children.Count; i++)
        {
            var sibling = stackPanel.Children[i];
            if (sibling == child)
            {
                currentIdx = i;
            }
            else
            {
                children.Add(sibling);
            }
        }

        if (currentIdx == -1) return;

        int targetIdx = 0;
        if (stackPanel.Orientation == Orientation.Vertical)
        {
            // Find index by comparing localMousePos.Y with sibling center Y bounds
            for (int i = 0; i < children.Count; i++)
            {
                var sibling = children[i] as FrameworkElement;
                if (sibling == null) continue;

                float height = float.IsNaN(sibling.Height) ? sibling.Size.Y : sibling.Height;
                if (height <= 0) height = 36f; // Standard default

                float siblingCenterY = Canvas.GetTop(sibling) + height / 2f;
                if (localMousePos.Y > siblingCenterY)
                {
                    targetIdx = i + 1;
                }
            }
        }
        else
        {
            // Horizontal orientation
            for (int i = 0; i < children.Count; i++)
            {
                var sibling = children[i] as FrameworkElement;
                if (sibling == null) continue;

                float width = float.IsNaN(sibling.Width) ? sibling.Size.X : sibling.Width;
                if (width <= 0) width = 120f; // Standard default

                float siblingCenterX = Canvas.GetLeft(sibling) + width / 2f;
                if (localMousePos.X > siblingCenterX)
                {
                    targetIdx = i + 1;
                }
            }
        }

        if (targetIdx != currentIdx)
        {
            children.Insert(targetIdx, child);
            stackPanel.ClearChildren();
            foreach (var item in children)
            {
                stackPanel.AddChild(item);
            }
        }
    }
}

public class GridDragEditor : IPanelDragEditor
{
    public bool CanHandle(FrameworkElement panel) => panel is Grid;

    public void HandleDrag(FrameworkElement child, FrameworkElement panel, Vector2 localMousePos, Vector2 dragDelta)
    {
        var grid = (Grid)panel;
        int colCount = Math.Max(1, grid.ColumnDefinitions.Count);
        int rowCount = Math.Max(1, grid.RowDefinitions.Count);

        float gridW = float.IsNaN(grid.Width) ? grid.Size.X : grid.Width;
        float gridH = float.IsNaN(grid.Height) ? grid.Size.Y : grid.Height;
        if (gridW <= 0) gridW = 300f; // Reasonable default
        if (gridH <= 0) gridH = 200f;

        // Compute column widths
        float[] colWidths = new float[colCount];
        float totalAbsoluteCol = 0f;
        float totalStarCol = 0f;
        for (int i = 0; i < grid.ColumnDefinitions.Count; i++)
        {
            var c = grid.ColumnDefinitions[i];
            if (c.UnitType == GridUnitType.Absolute)
                totalAbsoluteCol += c.Value;
            else if (c.UnitType == GridUnitType.Star)
                totalStarCol += c.Value;
        }
        float remainingCol = Math.Max(0f, gridW - totalAbsoluteCol);
        for (int i = 0; i < colCount; i++)
        {
            if (grid.ColumnDefinitions.Count == 0)
            {
                colWidths[i] = gridW;
            }
            else
            {
                var c = grid.ColumnDefinitions[i];
                if (c.UnitType == GridUnitType.Absolute)
                    colWidths[i] = c.Value;
                else if (c.UnitType == GridUnitType.Star && totalStarCol > 0f)
                    colWidths[i] = (c.Value / totalStarCol) * remainingCol;
                else
                    colWidths[i] = gridW / colCount;
            }
        }

        // Compute row heights
        float[] rowHeights = new float[rowCount];
        float totalAbsoluteRow = 0f;
        float totalStarRow = 0f;
        for (int i = 0; i < grid.RowDefinitions.Count; i++)
        {
            var r = grid.RowDefinitions[i];
            if (r.UnitType == GridUnitType.Absolute)
                totalAbsoluteRow += r.Value;
            else if (r.UnitType == GridUnitType.Star)
                totalStarRow += r.Value;
        }
        float remainingRow = Math.Max(0f, gridH - totalAbsoluteRow);
        for (int i = 0; i < rowCount; i++)
        {
            if (grid.RowDefinitions.Count == 0)
            {
                rowHeights[i] = gridH;
            }
            else
            {
                var r = grid.RowDefinitions[i];
                if (r.UnitType == GridUnitType.Absolute)
                    rowHeights[i] = r.Value;
                else if (r.UnitType == GridUnitType.Star && totalStarRow > 0f)
                    rowHeights[i] = (r.Value / totalStarRow) * remainingRow;
                else
                    rowHeights[i] = gridH / rowCount;
            }
        }

        // Find target column
        int targetCol = 0;
        float accX = 0f;
        for (int i = 0; i < colCount; i++)
        {
            if (localMousePos.X >= accX && localMousePos.X < accX + colWidths[i])
            {
                targetCol = i;
                break;
            }
            accX += colWidths[i];
            if (i == colCount - 1) targetCol = i;
        }

        // Find target row
        int targetRow = 0;
        float accY = 0f;
        for (int i = 0; i < rowCount; i++)
        {
            if (localMousePos.Y >= accY && localMousePos.Y < accY + rowHeights[i])
            {
                targetRow = i;
                break;
            }
            accY += rowHeights[i];
            if (i == rowCount - 1) targetRow = i;
        }

        // Update child properties
        int currentRow = Grid.GetRow(child);
        int currentCol = Grid.GetColumn(child);

        if (currentRow != targetRow || currentCol != targetCol)
        {
            Grid.SetRow(child, targetRow);
            Grid.SetColumn(child, targetCol);
        }
    }
}

public class DefaultPanelDragEditor : IPanelDragEditor
{
    public bool CanHandle(FrameworkElement panel) => true;

    public void HandleDrag(FrameworkElement child, FrameworkElement panel, Vector2 localMousePos, Vector2 dragDelta)
    {
        // Default container fallback (e.g. Border, ScrollViewer): no specific reordering/attached positioning.
    }
}
