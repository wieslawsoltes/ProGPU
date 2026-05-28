using Thickness = Microsoft.UI.Xaml.Thickness;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using System;
using System.Numerics;
using ProGPU.Vector;
using ProGPU.Scene;
using ProGPU.Layout;
using ProGPU.Text;

namespace ProGPU.Designer;

public class SelectionAdorner : Panel
{
    private readonly Thumb _topLeftThumb = new();
    private readonly Thumb _topCenterThumb = new();
    private readonly Thumb _topRightThumb = new();
    private readonly Thumb _middleLeftThumb = new();
    private readonly Thumb _middleRightThumb = new();
    private readonly Thumb _bottomLeftThumb = new();
    private readonly Thumb _bottomCenterThumb = new();
    private readonly Thumb _bottomRightThumb = new();
    private readonly Thumb _rotateThumb = new();

    public FrameworkElement? AssociatedElement { get; }
    public DesignerCanvas? ParentCanvas { get; }

    public float ZoomScale => ParentCanvas?.ZoomScale ?? 1.0f;

    public SelectionAdorner(FrameworkElement associatedElement, DesignerCanvas parentCanvas)
    {
        AssociatedElement = associatedElement;
        ParentCanvas = parentCanvas;

        IsHitTestVisible = true;

        Children.Add(_topLeftThumb);
        Children.Add(_topCenterThumb);
        Children.Add(_topRightThumb);
        Children.Add(_middleLeftThumb);
        Children.Add(_middleRightThumb);
        Children.Add(_bottomLeftThumb);
        Children.Add(_bottomCenterThumb);
        Children.Add(_bottomRightThumb);
        Children.Add(_rotateThumb);

        ApplyThumbStyle(_topLeftThumb);
        ApplyThumbStyle(_topCenterThumb);
        ApplyThumbStyle(_topRightThumb);
        ApplyThumbStyle(_middleLeftThumb);
        ApplyThumbStyle(_middleRightThumb);
        ApplyThumbStyle(_bottomLeftThumb);
        ApplyThumbStyle(_bottomCenterThumb);
        ApplyThumbStyle(_bottomRightThumb);
        ApplyThumbStyle(_rotateThumb);

        _topLeftThumb.DragDelta += (s, e) => HandleDragDelta((Thumb)s, e.HorizontalChange, e.VerticalChange);
        _topCenterThumb.DragDelta += (s, e) => HandleDragDelta((Thumb)s, e.HorizontalChange, e.VerticalChange);
        _topRightThumb.DragDelta += (s, e) => HandleDragDelta((Thumb)s, e.HorizontalChange, e.VerticalChange);
        _middleLeftThumb.DragDelta += (s, e) => HandleDragDelta((Thumb)s, e.HorizontalChange, e.VerticalChange);
        _middleRightThumb.DragDelta += (s, e) => HandleDragDelta((Thumb)s, e.HorizontalChange, e.VerticalChange);
        _bottomLeftThumb.DragDelta += (s, e) => HandleDragDelta((Thumb)s, e.HorizontalChange, e.VerticalChange);
        _bottomCenterThumb.DragDelta += (s, e) => HandleDragDelta((Thumb)s, e.HorizontalChange, e.VerticalChange);
        _bottomRightThumb.DragDelta += (s, e) => HandleDragDelta((Thumb)s, e.HorizontalChange, e.VerticalChange);
        _rotateThumb.DragDelta += (s, e) => HandleRotateDrag();

        void ClearSnapGuidelines(object sender, DragCompletedEventArgs e)
        {
            if (ParentCanvas != null)
            {
                ParentCanvas.ActiveVerticalSnapX = null;
                ParentCanvas.ActiveHorizontalSnapY = null;
                ParentCanvas.Invalidate();
            }
        }
        _topLeftThumb.DragCompleted += ClearSnapGuidelines;
        _topCenterThumb.DragCompleted += ClearSnapGuidelines;
        _topRightThumb.DragCompleted += ClearSnapGuidelines;
        _middleLeftThumb.DragCompleted += ClearSnapGuidelines;
        _middleRightThumb.DragCompleted += ClearSnapGuidelines;
        _bottomLeftThumb.DragCompleted += ClearSnapGuidelines;
        _bottomCenterThumb.DragCompleted += ClearSnapGuidelines;
        _bottomRightThumb.DragCompleted += ClearSnapGuidelines;
        _rotateThumb.DragCompleted += ClearSnapGuidelines;
    }

    private void ApplyThumbStyle(Thumb thumb)
    {
        thumb.Background = new ThemeResourceBrush("SystemAccentColor");
        thumb.BorderBrush = new ThemeResourceBrush("PageBackground");
        thumb.BorderThickness = new Thickness(1f);
        thumb.CornerRadius = 4f;
    }

    public void UpdatePositionAndSize()
    {
        if (AssociatedElement == null) return;
        
        float left = Canvas.GetLeft(AssociatedElement);
        float top = Canvas.GetTop(AssociatedElement);
        float width = float.IsNaN(AssociatedElement.Width) ? AssociatedElement.Size.X : AssociatedElement.Width;
        float height = float.IsNaN(AssociatedElement.Height) ? AssociatedElement.Size.Y : AssociatedElement.Height;
        
        Canvas.SetLeft(this, left);
        Canvas.SetTop(this, top);
        this.Width = width;
        this.Height = height;
        
        this.Rotation = AssociatedElement.Rotation;
        
        InvalidateMeasure();
        InvalidateArrange();
        Invalidate();
    }

    protected override Vector2 MeasureOverride(Vector2 availableSize)
    {
        float handleSize = 8f / ZoomScale;
        Vector2 handleAvailable = new Vector2(handleSize, handleSize);
        foreach (var child in Children)
        {
            if (child is LayoutNode node)
            {
                node.Measure(handleAvailable);
            }
        }
        return availableSize;
    }

    protected override void ArrangeOverride(Rect arrangeRect)
    {
        float handleSize = 8f / ZoomScale;
        float halfSize = handleSize / 2f;
        float w = arrangeRect.Width;
        float h = arrangeRect.Height;

        _topLeftThumb.Arrange(new Rect(-halfSize, -halfSize, handleSize, handleSize));
        _topCenterThumb.Arrange(new Rect(w / 2f - halfSize, -halfSize, handleSize, handleSize));
        _topRightThumb.Arrange(new Rect(w - halfSize, -halfSize, handleSize, handleSize));

        _middleLeftThumb.Arrange(new Rect(-halfSize, h / 2f - halfSize, handleSize, handleSize));
        _middleRightThumb.Arrange(new Rect(w - halfSize, h / 2f - halfSize, handleSize, handleSize));

        _bottomLeftThumb.Arrange(new Rect(-halfSize, h - halfSize, handleSize, handleSize));
        _bottomCenterThumb.Arrange(new Rect(w / 2f - halfSize, h - halfSize, handleSize, handleSize));
        _bottomRightThumb.Arrange(new Rect(w - halfSize, h - halfSize, handleSize, handleSize));

        float rotateOffset = 20f / ZoomScale;
        _rotateThumb.Arrange(new Rect(w / 2f - halfSize, -halfSize - rotateOffset, handleSize, handleSize));
    }

    private void HandleDragDelta(Thumb thumb, float dx, float dy)
    {
        if (AssociatedElement == null || ParentCanvas == null) return;

        float z = ZoomScale;
        float dxScaled = dx / z;
        float dyScaled = dy / z;

        var element = AssociatedElement;
        float minWidth = 20f;
        float minHeight = 20f;

        float currentLeft = Canvas.GetLeft(element);
        float currentTop = Canvas.GetTop(element);
        float currentWidth = float.IsNaN(element.Width) ? element.Size.X : element.Width;
        float currentHeight = float.IsNaN(element.Height) ? element.Size.Y : element.Height;

        float newLeft = currentLeft;
        float newTop = currentTop;
        float newWidth = currentWidth;
        float newHeight = currentHeight;

        if (thumb == _topLeftThumb)
        {
            float targetLeft = currentLeft + dxScaled;
            float targetTop = currentTop + dyScaled;
            float targetWidth = currentWidth - dxScaled;
            float targetHeight = currentHeight - dyScaled;

            Vector2 snappedLeftTop = ParentCanvas.SnapPosition(element, new Vector2(targetLeft, targetTop));
            
            float snapDx = snappedLeftTop.X - currentLeft;
            float snapDy = snappedLeftTop.Y - currentTop;
            
            newWidth = MathF.Max(minWidth, currentWidth - snapDx);
            newLeft = (currentLeft + currentWidth) - newWidth;
            
            newHeight = MathF.Max(minHeight, currentHeight - snapDy);
            newTop = (currentTop + currentHeight) - newHeight;
        }
        else if (thumb == _topCenterThumb)
        {
            float targetTop = currentTop + dyScaled;
            float targetHeight = currentHeight - dyScaled;

            Vector2 snapped = ParentCanvas.SnapPosition(element, new Vector2(currentLeft, targetTop));
            float snapDy = snapped.Y - currentTop;

            newHeight = MathF.Max(minHeight, currentHeight - snapDy);
            newTop = (currentTop + currentHeight) - newHeight;
        }
        else if (thumb == _topRightThumb)
        {
            float targetTop = currentTop + dyScaled;
            float targetWidth = currentWidth + dxScaled;

            Vector2 snapped = ParentCanvas.SnapPosition(element, new Vector2(currentLeft, targetTop));
            float snapDy = snapped.Y - currentTop;
            
            newHeight = MathF.Max(minHeight, currentHeight - snapDy);
            newTop = (currentTop + currentHeight) - newHeight;
            
            float candidateRight = currentLeft + targetWidth;
            float? snapX = ParentCanvas.GetSnapX(element, candidateRight);
            if (snapX != null)
            {
                newWidth = MathF.Max(minWidth, snapX.Value - currentLeft);
            }
            else
            {
                newWidth = MathF.Max(minWidth, targetWidth);
            }
        }
        else if (thumb == _middleLeftThumb)
        {
            float targetLeft = currentLeft + dxScaled;
            Vector2 snapped = ParentCanvas.SnapPosition(element, new Vector2(targetLeft, currentTop));
            float snapDx = snapped.X - currentLeft;

            newWidth = MathF.Max(minWidth, currentWidth - snapDx);
            newLeft = (currentLeft + currentWidth) - newWidth;
        }
        else if (thumb == _middleRightThumb)
        {
            float targetWidth = currentWidth + dxScaled;
            float candidateRight = currentLeft + targetWidth;
            float? snapX = ParentCanvas.GetSnapX(element, candidateRight);
            if (snapX != null)
            {
                newWidth = MathF.Max(minWidth, snapX.Value - currentLeft);
            }
            else
            {
                newWidth = MathF.Max(minWidth, targetWidth);
            }
        }
        else if (thumb == _bottomLeftThumb)
        {
            float targetLeft = currentLeft + dxScaled;
            float targetHeight = currentHeight + dyScaled;

            Vector2 snapped = ParentCanvas.SnapPosition(element, new Vector2(targetLeft, currentTop));
            float snapDx = snapped.X - currentLeft;
            newWidth = MathF.Max(minWidth, currentWidth - snapDx);
            newLeft = (currentLeft + currentWidth) - newWidth;

            float candidateBottom = currentTop + targetHeight;
            float? snapYVal = ParentCanvas.GetSnapY(element, candidateBottom);
            if (snapYVal != null)
            {
                newHeight = MathF.Max(minHeight, snapYVal.Value - currentTop);
            }
            else
            {
                newHeight = MathF.Max(minHeight, targetHeight);
            }
        }
        else if (thumb == _bottomCenterThumb)
        {
            float targetHeight = currentHeight + dyScaled;
            float candidateBottom = currentTop + targetHeight;
            float? snapYVal = ParentCanvas.GetSnapY(element, candidateBottom);
            if (snapYVal != null)
            {
                newHeight = MathF.Max(minHeight, snapYVal.Value - currentTop);
            }
            else
            {
                newHeight = MathF.Max(minHeight, targetHeight);
            }
        }
        else if (thumb == _bottomRightThumb)
        {
            float targetWidth = currentWidth + dxScaled;
            float targetHeight = currentHeight + dyScaled;

            float candidateRight = currentLeft + targetWidth;
            float? snapX = ParentCanvas.GetSnapX(element, candidateRight);
            if (snapX != null)
            {
                newWidth = MathF.Max(minWidth, snapX.Value - currentLeft);
            }
            else
            {
                newWidth = MathF.Max(minWidth, targetWidth);
            }

            float candidateBottom = currentTop + targetHeight;
            float? snapYVal = ParentCanvas.GetSnapY(element, candidateBottom);
            if (snapYVal != null)
            {
                newHeight = MathF.Max(minHeight, snapYVal.Value - currentTop);
            }
            else
            {
                newHeight = MathF.Max(minHeight, targetHeight);
            }
        }

        Canvas.SetLeft(element, newLeft);
        Canvas.SetTop(element, newTop);
        element.Width = newWidth;
        element.Height = newHeight;

        UpdatePositionAndSize();
        element.InvalidateMeasure();
        element.InvalidateArrange();
        element.Invalidate();
        
        ParentCanvas.NotifyCanvasModified();
        ParentCanvas.InvalidateArrange();
        ParentCanvas.Invalidate();
    }

    public override void OnRender(DrawingContext context)
    {
        base.OnRender(context);

        if (AssociatedElement == null || ParentCanvas == null) return;

        float z = ZoomScale;
        var borderBrush = ThemeManager.GetBrush("SystemAccentColor", ActualTheme);
        var borderPen = new Pen(borderBrush, 1.5f / z);
        
        Rect borderRect = new Rect(0, 0, Size.X, Size.Y);
        context.DrawRectangle(null, borderPen, borderRect);

        // Draw connection line to rotate handle
        float w = Size.X;
        float rotateOffset = 20f / z;
        float lineX = w / 2f;
        context.DrawLine(borderPen, new Vector2(lineX, 0f), new Vector2(lineX, -rotateOffset));

        // Render WPF-style margin guidelines and distance markers
        DrawMarginGuidelines(context, z);
    }

    private void HandleRotateDrag()
    {
        if (AssociatedElement == null || ParentCanvas == null) return;

        var element = AssociatedElement;
        float left = Canvas.GetLeft(element);
        float top = Canvas.GetTop(element);
        float width = float.IsNaN(element.Width) ? element.Size.X : element.Width;
        float height = float.IsNaN(element.Height) ? element.Size.Y : element.Height;

        float centerX = left + width / 2f;
        float centerY = top + height / 2f;

        Vector2 screenMouse = InputSystem.LastMousePosition;
        Vector2 localMouse = InputSystem.GetLocalPosition(ParentCanvas, screenMouse);
        Vector2 canvasMouse = (localMouse - ParentCanvas.PanOffset) / ParentCanvas.ZoomScale;

        float angle = MathF.Atan2(canvasMouse.Y - centerY, canvasMouse.X - centerX);
        float rotation = angle + MathF.PI / 2f;

        element.Rotation = rotation;
        this.Rotation = rotation;

        UpdatePositionAndSize();
        element.Invalidate();
        ParentCanvas.NotifyCanvasModified();
        ParentCanvas.Invalidate();
    }

    private void DrawMarginGuidelines(DrawingContext context, float z)
    {
        if (AssociatedElement == null || ParentCanvas == null) return;

        float left = Canvas.GetLeft(AssociatedElement);
        float top = Canvas.GetTop(AssociatedElement);
        float width = Size.X;
        float height = Size.Y;

        float canvasWidth = ParentCanvas.Size.X;
        float canvasHeight = ParentCanvas.Size.Y;

        float rightDistance = canvasWidth - (left + width);
        float bottomDistance = canvasHeight - (top + height);

        float dpiScale = ParentCanvas.GetDpiScale?.Invoke() ?? 1.0f;
        if (dpiScale <= 0f) dpiScale = 1.0f;

        float Snap(float coord) => MathF.Round(coord * dpiScale * 4f) / 4f / dpiScale;

        // Dashed pen for guidelines
        var guidelineBrush = new SolidColorBrush(new Vector4(0.55f, 0.55f, 0.55f, 0.8f));
        var guidelinePen = new Pen(guidelineBrush, 1f / z);

        var font = PopupService.DefaultFont;
        if (font == null) return;

        float fontSize = 9f / z;

        // 1. Left Guideline and Pill
        if (left > 0f)
        {
            float y = height / 2f;
            float snappedY = Snap(y);
            float startX = Snap(-left);
            float endX = Snap(0f);
            DrawDashedHorizontalLine(context, guidelinePen, snappedY, startX, endX, 6f / z, 4f / z);

            // Left Pill
            float cx = -left / 2f;
            float cy = height / 2f;
            DrawDistancePill(context, font, fontSize, $"Left: {Math.Round(left)}", cx, cy, z, dpiScale);
        }

        // 2. Top Guideline and Pill
        if (top > 0f)
        {
            float x = width / 2f;
            float snappedX = Snap(x);
            float startY = Snap(-top);
            float endY = Snap(0f);
            DrawDashedVerticalLine(context, guidelinePen, snappedX, startY, endY, 6f / z, 4f / z);

            // Top Pill
            float cx = width / 2f;
            float cy = -top / 2f;
            DrawDistancePill(context, font, fontSize, $"Top: {Math.Round(top)}", cx, cy, z, dpiScale);
        }

        // 3. Right Guideline and Pill
        if (rightDistance > 0f)
        {
            float y = height / 2f;
            float snappedY = Snap(y);
            float startX = Snap(width);
            float endX = Snap(canvasWidth - left);
            DrawDashedHorizontalLine(context, guidelinePen, snappedY, startX, endX, 6f / z, 4f / z);

            // Right Pill
            float cx = width + rightDistance / 2f;
            float cy = height / 2f;
            DrawDistancePill(context, font, fontSize, $"Right: {Math.Round(rightDistance)}", cx, cy, z, dpiScale);
        }

        // 4. Bottom Guideline and Pill
        if (bottomDistance > 0f)
        {
            float x = width / 2f;
            float snappedX = Snap(x);
            float startY = Snap(height);
            float endY = Snap(canvasHeight - top);
            DrawDashedVerticalLine(context, guidelinePen, snappedX, startY, endY, 6f / z, 4f / z);

            // Bottom Pill
            float cx = width / 2f;
            float cy = height + bottomDistance / 2f;
            DrawDistancePill(context, font, fontSize, $"Bottom: {Math.Round(bottomDistance)}", cx, cy, z, dpiScale);
        }
    }

    private void DrawDistancePill(DrawingContext context, TtfFont font, float fontSize, string text, float cx, float cy, float z, float dpiScale)
    {
        float Snap(float coord) => MathF.Round(coord * dpiScale * 4f) / 4f / dpiScale;

        // Measure text
        var textLayout = new TextLayout(text, font, fontSize, float.PositiveInfinity, TextAlignment.Left, null);
        float textWidth = textLayout.MeasuredSize.X;
        float textHeight = textLayout.MeasuredSize.Y;

        // Pill dimensions with scaled padding
        float horizPadding = 8f / z;
        float vertPadding = 4f / z;
        float pillWidth = textWidth + horizPadding * 2f;
        float pillHeight = textHeight + vertPadding * 2f;

        // Snap positions
        float pillLeft = Snap(cx - pillWidth / 2f);
        float pillTop = Snap(cy - pillHeight / 2f);
        float pillRight = Snap(cx + pillWidth / 2f);
        float pillBottom = Snap(cy + pillHeight / 2f);
        
        Rect pillRect = new Rect(pillLeft, pillTop, pillRight - pillLeft, pillBottom - pillTop);
        float cornerRadius = pillRect.Height / 2f;

        // Brushes
        var bgBrush = new SolidColorBrush(new Vector4(0.08f, 0.08f, 0.08f, 0.65f)); // 65% opacity dark grey
        var textBrush = new SolidColorBrush(new Vector4(1f, 1f, 1f, 0.95f)); // 95% opacity white for high readability

        context.DrawRoundedRectangle(bgBrush, null, pillRect, cornerRadius);

        // Snap text position
        Vector2 textPos = new Vector2(
            Snap(cx - textWidth / 2f),
            Snap(cy - textHeight / 2f)
        );

        context.DrawText(text, font, fontSize, textBrush, textPos);
    }

    private void DrawDashedVerticalLine(DrawingContext context, Pen pen, float x, float y1, float y2, float dashLength, float gapLength)
    {
        if (dashLength + gapLength <= 0.001f) return;
        float y = y1;
        while (y < y2)
        {
            float nextY = Math.Min(y + dashLength, y2);
            context.DrawLine(pen, new Vector2(x, y), new Vector2(x, nextY));
            y += dashLength + gapLength;
        }
    }

    private void DrawDashedHorizontalLine(DrawingContext context, Pen pen, float y, float x1, float x2, float dashLength, float gapLength)
    {
        if (dashLength + gapLength <= 0.001f) return;
        float x = x1;
        while (x < x2)
        {
            float nextX = Math.Min(x + dashLength, x2);
            context.DrawLine(pen, new Vector2(x, y), new Vector2(nextX, y));
            x += dashLength + gapLength;
        }
    }
}
