using Thickness = Microsoft.UI.Xaml.Thickness;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Markup;
using System;
using System.Collections.Generic;
using System.Numerics;
using System.Reflection;
using ProGPU.Scene;
using ProGPU.Vector;
using ProGPU.Text;
using ProGPU.WinUI;

namespace ProGPU.Designer;

public class DragEventArgs : RoutedEventArgs
{
    public DataPackage Data { get; }
    public Vector2 Position { get; }

    public DragEventArgs(DataPackage data, Vector2 position)
    {
        Data = data;
        Position = position;
    }
}

public static class StandardDataFormats
{
    public const string Tool = "Tool";
}

public class DesignerCanvas : Panel
{
    public Brush? Background { get; set; }
    public Canvas DesignSurface { get; }
    public Canvas AdornerSurface { get; }

    private FrameworkElement? _selectedElement;
    public FrameworkElement? SelectedElement
    {
        get => _selectedElement;
        set => SelectElement(value);
    }
    private SelectionAdorner? _selectionAdorner;


    public event EventHandler<DragEventArgs>? DragOver;
    public new event EventHandler<DragEventArgs>? Drop;

    public float? ActiveVerticalSnapX { get; set; }
    public float? ActiveHorizontalSnapY { get; set; }

    // Zoom and Pan Properties
    public float ZoomScale { get; set; } = 1.0f;
    public Vector2 PanOffset { get; set; } = Vector2.Zero;

    public bool ShowGridLines { get; set; } = true;
    public bool GridSnappingEnabled { get; set; } = true;
    public float GridSize { get; set; } = 10f;
    public Func<float>? GetDpiScale { get; set; }

    public event Action? SelectionChanged;
    public event Action? CanvasModified;

    private readonly RTree<FrameworkElement> _spatialIndex = new RTree<FrameworkElement>();

    // Pointer movement state
    private bool _isDraggingElement;
    private Vector2 _dragStartOffset; // Logical offset
    private float _elementStartLeft;
    private float _elementStartTop;

    // Panning state
    private bool _isPanning;
    private Vector2 _panStartMouse;
    private Vector2 _panStartOffset;

    public new PanelChildrenCollection Children => DesignSurface.Children;

    public DesignerCanvas()
    {
        DesignSurface = new Canvas();
        AdornerSurface = new Canvas();

        base.AddChild(DesignSurface);
        base.AddChild(AdornerSurface);

        // Bind background dynamically using ThemeResourceBrush to comply with guidelines
        Background = new ThemeResourceBrush("PageBackground");

        AllowDrop = true;
        ApplyTransforms();
    }

    public new void AddChild(Visual child)
    {
        if (child is FrameworkElement fe)
        {
            fe.IsHitTestVisible = false;
        }
        DesignSurface.Children.Add(child);
        CanvasModified?.Invoke();
    }

    public void NotifyCanvasModified()
    {
        CanvasModified?.Invoke();
    }

    public void ApplyTransforms()
    {
        Matrix4x4 scale = Matrix4x4.CreateScale(ZoomScale, ZoomScale, 1.0f);
        Matrix4x4 translation = Matrix4x4.CreateTranslation(PanOffset.X, PanOffset.Y, 0.0f);
        Matrix4x4 transform = scale * translation;

        DesignSurface.Transform = transform;
        AdornerSurface.Transform = transform;
    }

    public void SelectElement(FrameworkElement? element)
    {
        if (_selectedElement == element) return;

        if (_selectionAdorner != null)
        {
            AdornerSurface.Children.Remove(_selectionAdorner);
            _selectionAdorner = null;
        }

        _selectedElement = element;

        if (_selectedElement != null)
        {
            _selectionAdorner = new SelectionAdorner(_selectedElement, this);
            AdornerSurface.Children.Add(_selectionAdorner);
            _selectionAdorner.UpdatePositionAndSize();
        }

        SelectionChanged?.Invoke();

        InvalidateArrange();
        Invalidate();
    }

    public void UpdateSelectionAdorner()
    {
        _selectionAdorner?.UpdatePositionAndSize();
    }

    protected override Vector2 MeasureOverride(Vector2 availableSize)
    {
        float w = !float.IsFinite(availableSize.X) ? 2000f : availableSize.X;
        float h = !float.IsFinite(availableSize.Y) ? 2000f : availableSize.Y;
        
        DesignSurface.Measure(new Vector2(w, h));
        AdornerSurface.Measure(new Vector2(w, h));
        
        return new Vector2(w, h);
    }

    protected override void ArrangeOverride(Rect arrangeRect)
    {
        DesignSurface.Arrange(arrangeRect);
        AdornerSurface.Arrange(arrangeRect);
        
        _selectionAdorner?.UpdatePositionAndSize();
    }

    public override void OnPointerPressed(PointerRoutedEventArgs e)
    {
        // 1. Middle-mouse panning check
        if (e.IsMiddleButtonPressed)
        {
            _isPanning = true;
            _panStartMouse = e.Position;
            _panStartOffset = PanOffset;
            InputSystem.CapturePointer(this);
            e.Handled = true;
            base.OnPointerPressed(e);
            return;
        }

        // 2. Check if a resize thumb in SelectionAdorner was hit
        var original = e.OriginalSource as FrameworkElement;
        bool hitThumb = false;
        var curr = original;
        while (curr != null)
        {
            if (curr is Thumb)
            {
                hitThumb = true;
                break;
            }
            curr = curr.Parent as FrameworkElement;
        }

        if (hitThumb)
        {
            base.OnPointerPressed(e);
            return;
        }

        // 3. Manual geometric hit test in logical coordinates using R-Tree
        Vector2 logicalPos = (e.Position - PanOffset) / ZoomScale;
        FrameworkElement? hitChild = null;

        RebuildSpatialIndex();
        var candidates = _spatialIndex.Query(logicalPos);
        
        // Sort candidates by their index in DesignSurface.Children descending to query topmost first
        candidates.Sort((a, b) => {
            int idxA = DesignSurface.Children.IndexOf(a);
            int idxB = DesignSurface.Children.IndexOf(b);
            return idxB.CompareTo(idxA);
        });

        foreach (var child in candidates)
        {
            var transformToLocal = DesignSurface.TransformToVisual(child);
            Vector2 localPoint = transformToLocal.TransformPoint(logicalPos);
            
            float w = float.IsNaN(child.Width) ? child.Size.X : child.Width;
            float h = float.IsNaN(child.Height) ? child.Size.Y : child.Height;
            if (w <= 0) w = 120f;
            if (h <= 0) h = 36f;
            
            Rect localBounds = new Rect(0, 0, w, h);
            if (localBounds.Contains(localPoint))
            {
                hitChild = child;
                break;
            }
        }

        if (hitChild != null)
        {
            SelectElement(hitChild);
            
            // Start dragging in logical space
            _isDraggingElement = true;
            _dragStartOffset = logicalPos;
            _elementStartLeft = Canvas.GetLeft(hitChild);
            _elementStartTop = Canvas.GetTop(hitChild);
            InputSystem.CapturePointer(this);
            e.Handled = true;
        }
        else
        {
            SelectElement(null);
        }
        
        base.OnPointerPressed(e);
    }

    public override void OnPointerMoved(PointerRoutedEventArgs e)
    {
        // 1. Panning update
        if (_isPanning)
        {
            Vector2 delta = e.Position - _panStartMouse;
            PanOffset = _panStartOffset + delta;
            ApplyTransforms();
            Invalidate();
            e.Handled = true;
            base.OnPointerMoved(e);
            return;
        }

        // 2. Dragging element update in logical space
        if (_isDraggingElement && SelectedElement != null)
        {
            Vector2 logicalPos = (e.Position - PanOffset) / ZoomScale;
            Vector2 delta = logicalPos - _dragStartOffset;
            float candidateLeft = _elementStartLeft + delta.X;
            float candidateTop = _elementStartTop + delta.Y;

            // Snaps coordinates if close
            Vector2 snapped = SnapPosition(SelectedElement, new Vector2(candidateLeft, candidateTop));

            Canvas.SetLeft(SelectedElement, snapped.X);
            Canvas.SetTop(SelectedElement, snapped.Y);

            _selectionAdorner?.UpdatePositionAndSize();
            
            SelectedElement.InvalidateMeasure();
            SelectedElement.InvalidateArrange();
            SelectedElement.Invalidate();

            CanvasModified?.Invoke();
            InvalidateArrange();
            Invalidate();
            e.Handled = true;
        }
        
        base.OnPointerMoved(e);
    }

    public override void OnPointerReleased(PointerRoutedEventArgs e)
    {
        // 1. Release panning
        if (_isPanning)
        {
            _isPanning = false;
            InputSystem.ReleasePointerCapture();
            e.Handled = true;
            base.OnPointerReleased(e);
            return;
        }

        // 2. Release dragging element
        if (_isDraggingElement)
        {
            InputSystem.ReleasePointerCapture();
            _isDraggingElement = false;
            
            // Clear guidelines
            ActiveVerticalSnapX = null;
            ActiveHorizontalSnapY = null;
            
            CanvasModified?.Invoke();
            Invalidate();
            e.Handled = true;
        }
        
        base.OnPointerReleased(e);
    }

    public override void OnPointerWheelChanged(PointerRoutedEventArgs e)
    {
        if (InputSystem.Current.IsControlPressed)
        {
            float oldZoom = ZoomScale;
            float zoomFactor = e.WheelDelta > 0 ? 1.1f : 0.9f;
            float newZoom = Math.Clamp(oldZoom * zoomFactor, 0.15f, 4.0f);
            
            if (newZoom != oldZoom)
            {
                Vector2 mousePos = e.Position;
                Vector2 logicalPos = (mousePos - PanOffset) / oldZoom;
                ZoomScale = newZoom;
                PanOffset = mousePos - logicalPos * newZoom;
                
                ApplyTransforms();
                Invalidate();
            }
            e.Handled = true;
            return;
        }
        
        base.OnPointerWheelChanged(e);
    }

    public float SnapToGrid(float val, float gridSpacing = 10f)
    {
        return MathF.Round(val / gridSpacing) * gridSpacing;
    }

    public Vector2 SnapPositionToGrid(Vector2 pos, float gridSpacing = 10f)
    {
        return new Vector2(SnapToGrid(pos.X, gridSpacing), SnapToGrid(pos.Y, gridSpacing));
    }

    public Rect GetElementRect(FrameworkElement element)
    {
        float left = Canvas.GetLeft(element);
        float top = Canvas.GetTop(element);
        float width = float.IsNaN(element.Width) ? element.Size.X : element.Width;
        float height = float.IsNaN(element.Height) ? element.Size.Y : element.Height;
        
        if (width <= 0) width = 120f;
        if (height <= 0) height = 36f;
        
        return new Rect(left, top, width, height);
    }

    private void RebuildSpatialIndex()
    {
        var entries = new List<RTreeEntry<FrameworkElement>>();
        foreach (var child in DesignSurface.Children)
        {
            if (child is FrameworkElement fe)
            {
                float w = float.IsNaN(fe.Width) ? fe.Size.X : fe.Width;
                float h = float.IsNaN(fe.Height) ? fe.Size.Y : fe.Height;
                if (w <= 0) w = 120f;
                if (h <= 0) h = 36f;
                Rect localBounds = new Rect(0, 0, w, h);

                var transform = fe.TransformToVisual(DesignSurface);
                Rect transformedBounds = transform.TransformBounds(localBounds);

                entries.Add(new RTreeEntry<FrameworkElement>(transformedBounds, fe));
            }
        }
        _spatialIndex.Rebuild(entries);
    }

    public Vector2 SnapPosition(FrameworkElement element, Vector2 newPos)
    {
        Rect rect = GetElementRect(element);
        float w = rect.Width;
        float h = rect.Height;

        float x = newPos.X;
        float y = newPos.Y;

        // Grid snap default
        float spacing = GridSize;
        float gridSnappedX = GridSnappingEnabled ? SnapToGrid(x, spacing) : x;
        float gridSnappedY = GridSnappingEnabled ? SnapToGrid(y, spacing) : y;

        float snapThreshold = 8f / ZoomScale;
        float? snapX = null;
        float? snapY = null;
        
        float snappedLeft = gridSnappedX;
        float snappedTop = gridSnappedY;

        foreach (var child in DesignSurface.Children)
        {
            if (child == element || child is not FrameworkElement other)
                continue;

            Rect otherRect = GetElementRect(other);
            float otherLeft = otherRect.X;
            float otherTop = otherRect.Y;
            float otherRight = otherRect.X + otherRect.Width;
            float otherBottom = otherRect.Y + otherRect.Height;
            float otherCenterX = otherRect.X + otherRect.Width / 2f;
            float otherCenterY = otherRect.Y + otherRect.Height / 2f;

            // Check vertical alignments
            float[] myXs = { x, x + w, x + w / 2f };
            float[] otherXs = { otherLeft, otherRight, otherCenterX };

            for (int i = 0; i < myXs.Length; i++)
            {
                for (int j = 0; j < otherXs.Length; j++)
                {
                    if (Math.Abs(myXs[i] - otherXs[j]) <= snapThreshold)
                    {
                        snapX = otherXs[j];
                        if (i == 0) snappedLeft = otherXs[j];
                        else if (i == 1) snappedLeft = otherXs[j] - w;
                        else if (i == 2) snappedLeft = otherXs[j] - w / 2f;
                        break;
                    }
                }
                if (snapX != null) break;
            }

            // Check horizontal alignments
            float[] myYs = { y, y + h, y + h / 2f };
            float[] otherYs = { otherTop, otherBottom, otherCenterY };

            for (int i = 0; i < myYs.Length; i++)
            {
                for (int j = 0; j < otherYs.Length; j++)
                {
                    if (Math.Abs(myYs[i] - otherYs[j]) <= snapThreshold)
                    {
                        snapY = otherYs[j];
                        if (i == 0) snappedTop = otherYs[j];
                        else if (i == 1) snappedTop = otherYs[j] - h;
                        else if (i == 2) snappedTop = otherYs[j] - h / 2f;
                        break;
                    }
                }
                if (snapY != null) break;
            }
        }

        ActiveVerticalSnapX = snapX;
        ActiveHorizontalSnapY = snapY;

        if (snapX != null || snapY != null)
        {
            Invalidate();
        }

        return new Vector2(snappedLeft, snappedTop);
    }

    public float? GetSnapX(FrameworkElement element, float targetX)
    {
        float snapThreshold = 8f / ZoomScale;
        float? snapVal = null;
        foreach (var child in DesignSurface.Children)
        {
            if (child == element || child is not FrameworkElement other)
                continue;

            Rect otherRect = GetElementRect(other);
            float[] otherXs = { otherRect.X, otherRect.X + otherRect.Width, otherRect.X + otherRect.Width / 2f };

            foreach (var ox in otherXs)
            {
                if (Math.Abs(targetX - ox) <= snapThreshold)
                {
                    snapVal = ox;
                    break;
                }
            }
            if (snapVal != null) break;
        }

        ActiveVerticalSnapX = snapVal;
        if (snapVal != null)
        {
            Invalidate();
        }
        return snapVal;
    }

    public float? GetSnapY(FrameworkElement element, float targetY)
    {
        float snapThreshold = 8f / ZoomScale;
        float? snapVal = null;
        foreach (var child in DesignSurface.Children)
        {
            if (child == element || child is not FrameworkElement other)
                continue;

            Rect otherRect = GetElementRect(other);
            float[] otherYs = { otherRect.Y, otherRect.Y + otherRect.Height, otherRect.Y + otherRect.Height / 2f };

            foreach (var oy in otherYs)
            {
                if (Math.Abs(targetY - oy) <= snapThreshold)
                {
                    snapVal = oy;
                    break;
                }
            }
            if (snapVal != null) break;
        }

        ActiveHorizontalSnapY = snapVal;
        if (snapVal != null)
        {
            Invalidate();
        }
        return snapVal;
    }

    public override void OnDrop(Microsoft.UI.Xaml.DragEventArgs e)
    {
        base.OnDrop(e);
        
        // Translate local pointer coordinates to logical design coordinate
        Vector2 logicalPos = (e.Position - PanOffset) / ZoomScale;
        
        var args = new ProGPU.Designer.DragEventArgs(e.Data, logicalPos);
        OnDrop(args);
        
        if (args.Handled)
        {
            e.Handled = true;
        }
    }

    public void OnDrop(DragEventArgs args)
    {
        if (!AllowDrop) return;
        
        Drop?.Invoke(this, args);

        if (args.Handled) return;

        if (args.Data.Contains(StandardDataFormats.Tool))
        {
            var toolData = args.Data.GetData(StandardDataFormats.Tool);
            string? toolName = toolData as string;
            if (string.IsNullOrEmpty(toolName)) return;

            Type? controlType = null;

            string[] searchNamespaces = {
                "Microsoft.UI.Xaml.Controls",
                "Microsoft.UI.Xaml",
                "ProGPU.Designer"
            };

            foreach (var ns in searchNamespaces)
            {
                var typeName = $"{ns}.{toolName}";
                foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
                {
                    controlType = assembly.GetType(typeName);
                    if (controlType != null) break;
                }
                if (controlType != null) break;
            }

            if (controlType == null)
            {
                foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
                {
                    foreach (var type in assembly.GetTypes())
                    {
                        if (type.Name.Equals(toolName, StringComparison.OrdinalIgnoreCase))
                        {
                            controlType = type;
                            break;
                        }
                    }
                    if (controlType != null) break;
                }
            }

            if (controlType != null && typeof(FrameworkElement).IsAssignableFrom(controlType))
            {
                try
                {
                    var newInstance = Activator.CreateInstance(controlType) as FrameworkElement;
                    if (newInstance != null)
                    {
                        newInstance.IsHitTestVisible = false;

                        if (float.IsNaN(newInstance.Width) || newInstance.Width <= 0) newInstance.Width = 120f;
                        if (float.IsNaN(newInstance.Height) || newInstance.Height <= 0) newInstance.Height = 36f;

                        if (newInstance is Button button)
                        {
                            var richText = new RichTextBlock { Font = ThemeResourceFont() };
                            richText.Inlines.Add(new Run(toolName));
                            button.Content = richText;
                        }
                        else if (newInstance is TextBlock textBlock)
                        {
                            textBlock.Text = toolName;
                        }
                        else if (newInstance is CheckBox checkBox)
                        {
                            var richText = new RichTextBlock { Font = ThemeResourceFont() };
                            richText.Inlines.Add(new Run(toolName));
                            checkBox.Content = richText;
                        }
                        else if (newInstance is RadioButton radioButton)
                        {
                            var richText = new RichTextBlock { Font = ThemeResourceFont() };
                            richText.Inlines.Add(new Run(toolName));
                            radioButton.Content = richText;
                        }
                        else if (newInstance is ToggleSwitch toggleSwitch)
                        {
                            var richText = new RichTextBlock { Font = ThemeResourceFont() };
                            richText.Inlines.Add(new Run(toolName));
                            toggleSwitch.Content = richText;
                        }
                        else if (newInstance is ComboBox comboBox)
                        {
                            comboBox.PlaceholderText = toolName;
                        }

                        // Determine unique name
                        int suffix = 1;
                        string baseName = $"{toolName}";
                        string candidateName = $"{baseName}_{suffix}";
                        var existingNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        FindNamesInVisualTree(DesignSurface, existingNames);
                        while (existingNames.Contains(candidateName))
                        {
                            candidateName = $"{baseName}_{++suffix}";
                        }
                        newInstance.Name = candidateName;

                        // Find drop container under logicalPos
                        FrameworkElement dropTarget = DesignSurface;
                        var hitContainer = FindContainerAtPosition(DesignSurface, args.Position);
                        if (hitContainer != null)
                        {
                            dropTarget = hitContainer;
                        }

                        if (dropTarget is Canvas canvasTarget)
                        {
                            // Snap to grid relative to the canvas target
                            Vector2 snappedPos = SnapPositionToGrid(args.Position - canvasTarget.Offset, GridSize);
                            Canvas.SetLeft(newInstance, snappedPos.X);
                            Canvas.SetTop(newInstance, snappedPos.Y);
                            canvasTarget.Children.Add(newInstance);
                        }
                        else
                        {
                            // Add child to the non-canvas container (Panel, Border, ContentControl)
                            AddChildToTarget(dropTarget, newInstance);
                        }

                        SelectElement(newInstance);

                        CanvasModified?.Invoke();

                        InvalidateMeasure();
                        InvalidateArrange();
                        Invalidate();
                        DesignSurface.InvalidateArrange();
                        DesignSurface.Invalidate();
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[DesignerCanvas] Error instantiating {toolName}: {ex.Message}");
                }
            }
        }
    }

    private TtfFont? ThemeResourceFont()
    {
        return PopupService.DefaultFont;
    }

    public override void OnRender(DrawingContext context)
    {
        if (Background is SolidColorBrush solidBg)
        {
            context.DrawRectangle(solidBg, null, new Rect(0, 0, Size.X, Size.Y));
        }
        else if (Background is ThemeResourceBrush themeBg)
        {
            var brush = ThemeManager.GetBrush(themeBg.ResourceKey, ActualTheme);
            context.DrawRectangle(brush, null, new Rect(0, 0, Size.X, Size.Y));
        }

        base.OnRender(context);

        float dpiScale = GetDpiScale?.Invoke() ?? 1.0f;
        if (dpiScale == 1.0f)
        {
            var activeWindow = WindowManager.ActiveWindows.Count > 0 ? WindowManager.ActiveWindows[0] : null;
            if (activeWindow != null && activeWindow.SilkWindow != null)
            {
                dpiScale = (float)activeWindow.SilkWindow.FramebufferSize.X / activeWindow.SilkWindow.Size.X;
            }
        }

        // 1. Grid Background (Scaled & Panned)
        if (ShowGridLines && GridSize > 1f)
        {
            float gridSpacing = GridSize;
            var gridBrush = ActualTheme == ElementTheme.Dark
                ? new SolidColorBrush(new Vector4(1f, 1f, 1f, 0.08f))
                : new SolidColorBrush(new Vector4(0f, 0f, 0f, 0.06f));

            // Calculate visible logical bounds
            Vector2 minLogical = (Vector2.Zero - PanOffset) / ZoomScale;
            Vector2 maxLogical = (Size - PanOffset) / ZoomScale;

            float minX = MathF.Floor(minLogical.X / gridSpacing) * gridSpacing;
            float maxX = MathF.Ceiling(maxLogical.X / gridSpacing) * gridSpacing;
            float minY = MathF.Floor(minLogical.Y / gridSpacing) * gridSpacing;
            float maxY = MathF.Ceiling(maxLogical.Y / gridSpacing) * gridSpacing;

            if (!float.IsFinite(minX) || !float.IsFinite(maxX) ||
                !float.IsFinite(minY) || !float.IsFinite(maxY))
            {
                return;
            }

            for (float x = minX; x <= maxX; x += gridSpacing)
            {
                for (float y = minY; y <= maxY; y += gridSpacing)
                {
                    // Calculate screen position
                    Vector2 screenPos = new Vector2(x, y) * ZoomScale + PanOffset;

                    // Skip if outside screen bounds
                    if (screenPos.X < 0 || screenPos.X > Size.X || screenPos.Y < 0 || screenPos.Y > Size.Y)
                        continue;

                    // DPI-Aware Snapping: snaps in physical coordinates snapped to 1/4th of a physical pixel, then snap-backed
                    float physX = MathF.Round(screenPos.X * dpiScale * 4f) / 4f;
                    float physY = MathF.Round(screenPos.Y * dpiScale * 4f) / 4f;

                    Vector2 snapBackPos = new Vector2(physX, physY) / dpiScale;
                    
                    context.FillCircle(gridBrush, snapBackPos, 0.75f);
                }
            }
        }

        // 2. High-Contrast Dashed Neon Guidelines (Scaled & Snapped)
        var neonBrush = new SolidColorBrush(new Vector4(1f, 0.078f, 0.576f, 1f)); // Neon Pink!
        var neonPen = new Pen(neonBrush, 1.5f);

        if (ActiveVerticalSnapX != null)
        {
            float snapX = ActiveVerticalSnapX.Value;
            float screenSnapX = snapX * ZoomScale + PanOffset.X;
            screenSnapX = MathF.Round(screenSnapX * dpiScale * 4f) / 4f / dpiScale;
            DrawDashedVerticalLine(context, neonPen, screenSnapX, 0f, Size.Y);
        }

        if (ActiveHorizontalSnapY != null)
        {
            float snapY = ActiveHorizontalSnapY.Value;
            float screenSnapY = snapY * ZoomScale + PanOffset.Y;
            screenSnapY = MathF.Round(screenSnapY * dpiScale * 4f) / 4f / dpiScale;
            DrawDashedHorizontalLine(context, neonPen, screenSnapY, 0f, Size.X);
        }
    }

    private void DrawDashedVerticalLine(DrawingContext context, Pen pen, float x, float y1, float y2, float dashLength = 6f, float gapLength = 4f)
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

    private void DrawDashedHorizontalLine(DrawingContext context, Pen pen, float y, float x1, float x2, float dashLength = 6f, float gapLength = 4f)
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

    private FrameworkElement? FindContainerAtPosition(Visual parent, Vector2 parentPos)
    {
        if (parent is not FrameworkElement fe) return null;

        Vector2 localPos = parentPos - fe.Offset;

        Rect localBounds = new Rect(Vector2.Zero, fe.Size);
        if (!localBounds.Contains(localPos))
        {
            return null;
        }

        if (fe is ContainerVisual container)
        {
            for (int i = container.Children.Count - 1; i >= 0; i--)
            {
                var child = container.Children[i];
                if (child is FrameworkElement childFe)
                {
                    var hit = FindContainerAtPosition(childFe, localPos);
                    if (hit != null)
                    {
                        return hit;
                    }
                }
            }
        }

        if (IsValidDropContainer(fe))
        {
            return fe;
        }

        return null;
    }

    private bool IsValidDropContainer(FrameworkElement fe)
    {
        if (fe is Panel) return true;
        
        var type = fe.GetType();
        var contentPropertyAttr = type.GetCustomAttribute<ContentPropertyAttribute>(true);
        if (contentPropertyAttr != null && !string.IsNullOrEmpty(contentPropertyAttr.Name))
        {
            return true;
        }

        if (type.GetProperty("Child") != null || type.GetProperty("Content") != null)
        {
            return true;
        }

        return false;
    }

    private void AddChildToTarget(FrameworkElement target, FrameworkElement newChild)
    {
        if (target == null || newChild == null) return;

        if (target is Panel panel)
        {
            panel.Children.Add(newChild);
            return;
        }

        var type = target.GetType();
        var contentPropertyAttr = type.GetCustomAttribute<ContentPropertyAttribute>(true);
        if (contentPropertyAttr != null && !string.IsNullOrEmpty(contentPropertyAttr.Name))
        {
            var prop = type.GetProperty(contentPropertyAttr.Name);
            if (prop != null && prop.CanWrite)
            {
                prop.SetValue(target, newChild);
                return;
            }
        }

        var childProp = type.GetProperty("Child");
        if (childProp != null && childProp.CanWrite && typeof(FrameworkElement).IsAssignableFrom(childProp.PropertyType))
        {
            childProp.SetValue(target, newChild);
            return;
        }

        var contentProp = type.GetProperty("Content");
        if (contentProp != null && contentProp.CanWrite)
        {
            contentProp.SetValue(target, newChild);
            return;
        }

        if (target is ContainerVisual container)
        {
            container.AddChild(newChild);
        }
    }

    private void FindNamesInVisualTree(Visual? root, HashSet<string> names)
    {
        if (root is FrameworkElement fe)
        {
            if (!string.IsNullOrEmpty(fe.Name)) names.Add(fe.Name);
            if (fe is ContainerVisual container)
            {
                foreach (var child in container.Children)
                {
                    FindNamesInVisualTree(child, names);
                }
            }
        }
    }
}
