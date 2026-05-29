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

namespace ProGPU.WinUI.Designer;

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
    
    public bool IsResizingElement { get; set; }
    public bool AlwaysShowPanelOutlines { get; set; } = false;

    public event Action? SelectionChanged;
    public event Action? CanvasModified;
    public event Action? CanvasModifying;

    public void NotifyCanvasModifying()
    {
        CanvasModifying?.Invoke();
    }

    private readonly RTree<FrameworkElement> _spatialIndex = new RTree<FrameworkElement>();
    private List<(FrameworkElement Element, Rect Rect)>? _cachedSnapElements;

    // Pointer movement state
    private bool _isDraggingElement;
    private Vector2 _dragStartOffset; // Logical offset
    private float _elementStartLeft;
    private float _elementStartTop;
    private Vector2 _dragStartElementPosInRoot;
    public FrameworkElement? HoveredElement { get; set; }
    
    private Vector2? _dragOverPosition;
    private bool _isExternalDragActive;

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

    public void PrepareSnapCache(FrameworkElement? excludeElement)
    {
        var allElements = new List<FrameworkElement>();
        GetAllElementsRecursive(DesignSurface, allElements);

        _cachedSnapElements = new List<(FrameworkElement Element, Rect Rect)>();
        foreach (var other in allElements)
        {
            if (other == excludeElement)
                continue;

            _cachedSnapElements.Add((other, GetElementRect(other)));
        }
    }

    public void ClearSnapCache()
    {
        _cachedSnapElements = null;
    }

    public List<(FrameworkElement Element, Rect Rect)>? CachedSnapElements => _cachedSnapElements;

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
            // Force a measure & arrange layout pass on DesignSurface so the element's actual position is fully computed
            float surfaceW = !float.IsFinite(Size.X) ? 2000f : Size.X;
            float surfaceH = !float.IsFinite(Size.Y) ? 2000f : Size.Y;
            DesignSurface.Measure(new Vector2(surfaceW, surfaceH));
            DesignSurface.Arrange(new Rect(Vector2.Zero, new Vector2(surfaceW, surfaceH)));

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
        _selectionAdorner?.UpdatePositionAndSize();
        AdornerSurface.Arrange(arrangeRect);
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
        
        List<FrameworkElement> hitElements = new List<FrameworkElement>();
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
                hitElements.Add(child);
            }
        }

        // Sort hitElements by visual depth descending (deepest/leaf-most first)
        hitElements.Sort((a, b) => GetVisualDepth(b).CompareTo(GetVisualDepth(a)));

        if (hitElements.Count > 0)
        {
            // To avoid deselecting children when trying to drag them,
            // prioritize keeping the currently selected element if it is under the pointer.
            if (SelectedElement != null && hitElements.Contains(SelectedElement))
            {
                hitChild = SelectedElement;
            }
            else
            {
                // Select the deepest leaf element under pointer (index 0 because we sorted descending)
                hitChild = hitElements[0];
            }
        }

        if (hitChild != null)
        {
            NotifyCanvasModifying();
            SelectElement(hitChild);
            InputSystem.SetFocus(hitChild);
            
            // Start dragging in logical space
            _isDraggingElement = true;
            _dragStartOffset = logicalPos;
            
            // Project initial absolute start position in DesignSurface coordinates
            var startTransform = hitChild.TransformToVisual(DesignSurface);
            _dragStartElementPosInRoot = startTransform.TransformPoint(Vector2.Zero);
            
            _elementStartLeft = Canvas.GetLeft(hitChild);
            _elementStartTop = Canvas.GetTop(hitChild);
            
            PrepareSnapCache(hitChild);
            
            InputSystem.CapturePointer(this);
            e.Handled = true;
        }
        else
        {
            SelectElement(null);
            InputSystem.SetFocus(this);
        }
        
        base.OnPointerPressed(e);
    }

    public override void OnPointerMoved(PointerRoutedEventArgs e)
    {
        // 1. Panning update
        if (_isPanning)
        {
            HoveredElement = null;
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
            HoveredElement = null;
            Vector2 logicalPos = (e.Position - PanOffset) / ZoomScale;
            _dragOverPosition = logicalPos;
            Vector2 delta = logicalPos - _dragStartOffset;
            
            var parentFe = SelectedElement.Parent as FrameworkElement;
            if (parentFe != null && parentFe != DesignSurface)
            {
                // Map logical mouse position to parent local coordinate space
                var toParent = DesignSurface.TransformToVisual(parentFe);
                Vector2 localMousePos = toParent.TransformPoint(logicalPos);
                
                // Get parent size bounds
                float pw = float.IsNaN(parentFe.Width) ? parentFe.Size.X : parentFe.Width;
                float ph = float.IsNaN(parentFe.Height) ? parentFe.Size.Y : parentFe.Height;
                if (pw <= 0) pw = 120f;
                if (ph <= 0) ph = 36f;
                
                Rect parentLocalBounds = new Rect(0, 0, pw, ph);
                
                if (!parentLocalBounds.Contains(localMousePos))
                {
                    // Dragged OUTSIDE bounds of panel! Stop panel in drag and drag out to canvas or other panel target
                    FrameworkElement? targetContainer = FindContainerAtPosition(DesignSurface, logicalPos, SelectedElement);
                    var newParent = targetContainer ?? DesignSurface;
                    
                    if (newParent != parentFe)
                    {
                        var currentTransform = SelectedElement.TransformToVisual(DesignSurface);
                        Vector2 currentRootPos = currentTransform.TransformPoint(Vector2.Zero);
                        
                        RemoveChildFromParent(SelectedElement);
                        AddChildToContainer(newParent, SelectedElement);
                        
                        if (newParent != DesignSurface)
                        {
                            var toNewParent = DesignSurface.TransformToVisual(newParent);
                            Vector2 parentLocalPos = toNewParent.TransformPoint(currentRootPos);
                            Canvas.SetLeft(SelectedElement, parentLocalPos.X);
                            Canvas.SetTop(SelectedElement, parentLocalPos.Y);
                        }
                        else
                        {
                            Canvas.SetLeft(SelectedElement, currentRootPos.X);
                            Canvas.SetTop(SelectedElement, currentRootPos.Y);
                        }
                        
                        // Atomically reset drag start markers to prevent any coordinate jumps
                        _dragStartOffset = logicalPos;
                        _dragStartElementPosInRoot = currentRootPos;
                        _elementStartLeft = Canvas.GetLeft(SelectedElement);
                        _elementStartTop = Canvas.GetTop(SelectedElement);
                        PrepareSnapCache(SelectedElement);
                    }
                }
                else
                {
                    // Dragged INSIDE bounds of panel! Invoke modular panel-specific drag editor
                    PanelDragEditorRegistry.HandleDrag(SelectedElement, parentFe, localMousePos, delta);
                }
            }
            else
            {
                // Standard canvas dragging (dragging in DesignSurface root)
                Vector2 candidatePosInRoot = _dragStartElementPosInRoot + delta;
                Vector2 snappedInRoot = SnapPosition(SelectedElement, candidatePosInRoot);
                
                Canvas.SetLeft(SelectedElement, snappedInRoot.X);
                Canvas.SetTop(SelectedElement, snappedInRoot.Y);
            }

            _selectionAdorner?.UpdatePositionAndSize();
            
            SelectedElement.InvalidateMeasure();
            SelectedElement.InvalidateArrange();
            SelectedElement.Invalidate();

            InvalidateArrange();
            Invalidate();
            e.Handled = true;
        }
        
        // 3. Hovered element tracking for spacing guide rendering
        if (!_isPanning && !_isDraggingElement)
        {
            Vector2 logicalPos = (e.Position - PanOffset) / ZoomScale;
            FrameworkElement? hoverChild = null;

            RebuildSpatialIndex();
            var hoverCandidates = _spatialIndex.Query(logicalPos);

            hoverCandidates.Sort((a, b) => {
                int depthA = GetVisualDepth(a);
                int depthB = GetVisualDepth(b);
                return depthB.CompareTo(depthA); // Query deepest leaf node first
            });

            foreach (var child in hoverCandidates)
            {
                if (child == SelectedElement) continue;

                var transformToLocal = DesignSurface.TransformToVisual(child);
                Vector2 localPoint = transformToLocal.TransformPoint(logicalPos);

                float w = float.IsNaN(child.Width) ? child.Size.X : child.Width;
                float h = float.IsNaN(child.Height) ? child.Size.Y : child.Height;
                if (w <= 0) w = 120f;
                if (h <= 0) h = 36f;

                Rect localBounds = new Rect(0, 0, w, h);
                if (localBounds.Contains(localPoint))
                {
                    hoverChild = child;
                    break;
                }
            }

            if (HoveredElement != hoverChild)
            {
                HoveredElement = hoverChild;
                Invalidate();
                _selectionAdorner?.Invalidate();
            }
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
            _dragOverPosition = null;
            
            ClearSnapCache();
            
            // Clear guidelines
            ActiveVerticalSnapX = null;
            ActiveHorizontalSnapY = null;
            
            // Figma-style element container reparenting
            if (SelectedElement != null)
            {
                Vector2 logicalPos = (e.Position - PanOffset) / ZoomScale;
                FrameworkElement? targetContainer = FindContainerAtPosition(DesignSurface, logicalPos, SelectedElement);
                
                if (targetContainer != null && targetContainer != SelectedElement.Parent)
                {
                    // Get current root-absolute coordinates of SelectedElement
                    var currentTransform = SelectedElement.TransformToVisual(DesignSurface);
                    Vector2 currentRootPos = currentTransform.TransformPoint(Vector2.Zero);
                    
                    // Remove from old parent container
                    RemoveChildFromParent(SelectedElement);
                    
                    // Add to new parent container
                    AddChildToContainer(targetContainer, SelectedElement);
                    
                    // Project root position to the new parent's coordinate space to preserve position
                    var toParent = DesignSurface.TransformToVisual(targetContainer);
                    Vector2 parentLocalPos = toParent.TransformPoint(currentRootPos);
                    
                    Canvas.SetLeft(SelectedElement, parentLocalPos.X);
                    Canvas.SetTop(SelectedElement, parentLocalPos.Y);
                    
                    _selectionAdorner?.UpdatePositionAndSize();
                }
            }
            
            CanvasModified?.Invoke();
            Invalidate();
            e.Handled = true;
        }
        
        base.OnPointerReleased(e);
    }

    public override void OnPointerExited(PointerRoutedEventArgs e)
    {
        if (HoveredElement != null)
        {
            HoveredElement = null;
            Invalidate();
            _selectionAdorner?.Invalidate();
        }
        base.OnPointerExited(e);
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
                CanvasModified?.Invoke();
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
        var transform = element.TransformToVisual(DesignSurface);
        float width = float.IsNaN(element.Width) ? element.Size.X : element.Width;
        float height = float.IsNaN(element.Height) ? element.Size.Y : element.Height;
        
        if (width <= 0) width = 120f;
        if (height <= 0) height = 36f;
        
        return transform.TransformBounds(new Rect(0, 0, width, height));
    }

    private void RebuildSpatialIndex()
    {
        var entries = new List<RTreeEntry<FrameworkElement>>();
        AddToSpatialIndexRecursive(DesignSurface, entries);
        _spatialIndex.Rebuild(entries);
    }

    private void AddToSpatialIndexRecursive(FrameworkElement parent, List<RTreeEntry<FrameworkElement>> entries)
    {
        if (parent is ContainerVisual container)
        {
            foreach (var child in container.Children)
            {
                if (child is FrameworkElement fe && fe.IsVisible && !fe.IsCollapsed)
                {
                    float w = float.IsNaN(fe.Width) ? fe.Size.X : fe.Width;
                    float h = float.IsNaN(fe.Height) ? fe.Size.Y : fe.Height;
                    if (w <= 0) w = 120f;
                    if (h <= 0) h = 36f;
                    Rect localBounds = new Rect(0, 0, w, h);

                    var transform = fe.TransformToVisual(DesignSurface);
                    Rect transformedBounds = transform.TransformBounds(localBounds);

                    entries.Add(new RTreeEntry<FrameworkElement>(transformedBounds, fe));

                    AddToSpatialIndexRecursive(fe, entries);
                }
            }
        }
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

        float snapThreshold = MathF.Min(5f, 5f / ZoomScale);
        float? snapX = null;
        float? snapY = null;
        
        float snappedLeft = gridSnappedX;
        float snappedTop = gridSnappedY;

        if (_cachedSnapElements != null)
        {
            foreach (var cache in _cachedSnapElements)
            {
                if (cache.Element == element)
                    continue;

                Rect otherRect = cache.Rect;
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
        }
        else
        {
            var allElements = new List<FrameworkElement>();
            GetAllElementsRecursive(DesignSurface, allElements);

            foreach (var other in allElements)
            {
                if (other == element)
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
        }

        ActiveVerticalSnapX = snapX;
        ActiveHorizontalSnapY = snapY;

        if (snapX != null || snapY != null)
        {
            Invalidate();
        }

        return new Vector2(snappedLeft, snappedTop);
    }

    private void GetAllElementsRecursive(FrameworkElement parent, List<FrameworkElement> results)
    {
        if (parent is ContainerVisual container)
        {
            foreach (var child in container.Children)
            {
                if (child is FrameworkElement fe)
                {
                    results.Add(fe);
                    GetAllElementsRecursive(fe, results);
                }
            }
        }
    }

    public float? GetSnapX(FrameworkElement element, float targetX)
    {
        float snapThreshold = MathF.Min(5f, 5f / ZoomScale);
        float? snapVal = null;

        if (_cachedSnapElements != null)
        {
            foreach (var cache in _cachedSnapElements)
            {
                if (cache.Element == element)
                    continue;

                Rect otherRect = cache.Rect;
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
        }
        else
        {
            var allElements = new List<FrameworkElement>();
            GetAllElementsRecursive(DesignSurface, allElements);

            foreach (var other in allElements)
            {
                if (other == element)
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
        float snapThreshold = MathF.Min(5f, 5f / ZoomScale);
        float? snapVal = null;

        if (_cachedSnapElements != null)
        {
            foreach (var cache in _cachedSnapElements)
            {
                if (cache.Element == element)
                    continue;

                Rect otherRect = cache.Rect;
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
        }
        else
        {
            var allElements = new List<FrameworkElement>();
            GetAllElementsRecursive(DesignSurface, allElements);

            foreach (var other in allElements)
            {
                if (other == element)
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
        }

        ActiveHorizontalSnapY = snapVal;
        if (snapVal != null)
        {
            Invalidate();
        }
        return snapVal;
    }

    public override void OnDragEnter(Microsoft.UI.Xaml.DragEventArgs e)
    {
        base.OnDragEnter(e);
        _isExternalDragActive = true;
        _dragOverPosition = (e.Position - PanOffset) / ZoomScale;
        Invalidate();
    }

    public override void OnDragOver(Microsoft.UI.Xaml.DragEventArgs e)
    {
        base.OnDragOver(e);
        _isExternalDragActive = true;
        _dragOverPosition = (e.Position - PanOffset) / ZoomScale;
        Invalidate();
    }

    public override void OnDragLeave(Microsoft.UI.Xaml.DragEventArgs e)
    {
        base.OnDragLeave(e);
        _isExternalDragActive = false;
        _dragOverPosition = null;
        Invalidate();
    }

    public override void OnDrop(Microsoft.UI.Xaml.DragEventArgs e)
    {
        _isExternalDragActive = false;
        _dragOverPosition = null;

        base.OnDrop(e);
        
        // Translate local pointer coordinates to logical design coordinate
        Vector2 logicalPos = (e.Position - PanOffset) / ZoomScale;
        
        var args = new ProGPU.WinUI.Designer.DragEventArgs(e.Data, logicalPos);
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
                "ProGPU.WinUI.Designer"
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
                        var hitContainer = FindContainerAtPosition(DesignSurface, args.Position, null);
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
            float physicalSpacing = gridSpacing * ZoomScale * dpiScale;
            if (physicalSpacing >= 8f)
            {
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

                if (float.IsFinite(minX) && float.IsFinite(maxX) &&
                    float.IsFinite(minY) && float.IsFinite(maxY))
                {
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

        // 3. Figma-Style Container Target Outlines (Drag, Resize, Hover, or Always-Show)
        bool showPanelOutlines = _isDraggingElement || _isExternalDragActive || IsResizingElement || AlwaysShowPanelOutlines;
        if (showPanelOutlines)
        {
            var containers = new List<FrameworkElement>();
            FindAllContainers(DesignSurface, containers, SelectedElement);

            FrameworkElement? activeContainer = null;
            if (_dragOverPosition != null)
            {
                activeContainer = FindContainerAtPosition(DesignSurface, _dragOverPosition.Value, SelectedElement);
            }
            else if (SelectedElement != null && IsResizingElement)
            {
                activeContainer = SelectedElement.Parent as FrameworkElement;
            }
            else if (HoveredElement != null)
            {
                var curr = HoveredElement;
                while (curr != null)
                {
                    if (IsValidDropContainer(curr) && curr != DesignSurface && curr != SelectedElement)
                    {
                        activeContainer = curr;
                        break;
                    }
                    curr = curr.Parent as FrameworkElement;
                }
            }

            var candidateColor = new SolidColorBrush(new Vector4(0.0f, 0.94f, 1.0f, 0.4f)); // Translucent Neon Blue (#00F0FF)
            var activeColor = new SolidColorBrush(new Vector4(0.0f, 0.94f, 1.0f, 1.0f));    // Solid Neon Blue
            
            var candidatePen = new Pen(candidateColor, 1.0f);
            var activePen = new Pen(activeColor, 2.0f);

            foreach (var panel in containers)
            {
                float w = float.IsNaN(panel.Width) ? panel.Size.X : panel.Width;
                float h = float.IsNaN(panel.Height) ? panel.Size.Y : panel.Height;
                if (w <= 0) w = 120f;
                if (h <= 0) h = 36f;

                var transform = panel.TransformToVisual(DesignSurface);
                Vector2 p00 = transform.TransformPoint(new Vector2(0, 0)) * ZoomScale + PanOffset;
                Vector2 p10 = transform.TransformPoint(new Vector2(w, 0)) * ZoomScale + PanOffset;
                Vector2 p11 = transform.TransformPoint(new Vector2(w, h)) * ZoomScale + PanOffset;
                Vector2 p01 = transform.TransformPoint(new Vector2(0, h)) * ZoomScale + PanOffset;

                // 4-Way Subpixel Snapping snapped to 1/4th of a physical pixel
                p00 = new Vector2(MathF.Round(p00.X * dpiScale * 4f) / 4f, MathF.Round(p00.Y * dpiScale * 4f) / 4f) / dpiScale;
                p10 = new Vector2(MathF.Round(p10.X * dpiScale * 4f) / 4f, MathF.Round(p10.Y * dpiScale * 4f) / 4f) / dpiScale;
                p11 = new Vector2(MathF.Round(p11.X * dpiScale * 4f) / 4f, MathF.Round(p11.Y * dpiScale * 4f) / 4f) / dpiScale;
                p01 = new Vector2(MathF.Round(p01.X * dpiScale * 4f) / 4f, MathF.Round(p01.Y * dpiScale * 4f) / 4f) / dpiScale;

                if (panel == activeContainer)
                {
                    context.DrawLine(activePen, p00, p10);
                    context.DrawLine(activePen, p10, p11);
                    context.DrawLine(activePen, p11, p01);
                    context.DrawLine(activePen, p01, p00);
                }
                else
                {
                    DrawDashedLine(context, candidatePen, p00, p10);
                    DrawDashedLine(context, candidatePen, p10, p11);
                    DrawDashedLine(context, candidatePen, p11, p01);
                    DrawDashedLine(context, candidatePen, p01, p00);
                }
            }
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

    private void DrawDashedLine(DrawingContext context, Pen pen, Vector2 pA, Vector2 pB, float dashLength = 6f, float gapLength = 4f)
    {
        float distance = Vector2.Distance(pA, pB);
        if (distance <= 0.001f || dashLength + gapLength <= 0.001f) return;
        
        Vector2 dir = Vector2.Normalize(pB - pA);
        float current = 0f;
        while (current < distance)
        {
            float next = Math.Min(current + dashLength, distance);
            context.DrawLine(pen, pA + dir * current, pA + dir * next);
            current += dashLength + gapLength;
        }
    }

    private FrameworkElement? FindContainerAtPosition(FrameworkElement parent, Vector2 logicalPos, FrameworkElement? excludeElement)
    {
        if (!parent.IsVisible || parent.IsCollapsed || parent == excludeElement || (excludeElement != null && IsAncestorOf(excludeElement, parent)))
        {
            return null;
        }

        FrameworkElement? hit = null;

        if (IsValidDropContainer(parent))
        {
            var transformToLocal = DesignSurface.TransformToVisual(parent);
            Vector2 localPoint = transformToLocal.TransformPoint(logicalPos);

            float w = float.IsNaN(parent.Width) ? parent.Size.X : parent.Width;
            float h = float.IsNaN(parent.Height) ? parent.Size.Y : parent.Height;
            if (w <= 0) w = 120f;
            if (h <= 0) h = 36f;

            Rect localBounds = new Rect(0, 0, w, h);
            if (localBounds.Contains(localPoint))
            {
                hit = parent;
            }
        }

        if (parent is ContainerVisual container)
        {
            for (int i = container.Children.Count - 1; i >= 0; i--)
            {
                var child = container.Children[i] as FrameworkElement;
                if (child != null)
                {
                    var childHit = FindContainerAtPosition(child, logicalPos, excludeElement);
                    if (childHit != null)
                    {
                        return childHit;
                    }
                }
            }
        }

        return hit;
    }

    private static PropertyInfo? GetPropertySafe(Type type, string name)
    {
        Type? currentType = type;
        while (currentType != null)
        {
            var prop = currentType.GetProperty(name, BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly);
            if (prop != null)
            {
                return prop;
            }
            currentType = currentType.BaseType;
        }
        return null;
    }

    private bool IsValidDropContainer(FrameworkElement fe)
    {
        if (fe is Panel) return true;
        if (fe is Border) return true;
        if (fe is ContentControl) return true;
        
        var type = fe.GetType();
        var contentPropertyAttr = type.GetCustomAttribute<ContentPropertyAttribute>(true);
        if (contentPropertyAttr != null && !string.IsNullOrEmpty(contentPropertyAttr.Name))
        {
            return true;
        }

        if (GetPropertySafe(type, "Child") != null || GetPropertySafe(type, "Content") != null)
        {
            return true;
        }

        return false;
    }

    private void FindAllContainers(FrameworkElement parent, List<FrameworkElement> results, FrameworkElement? excludeElement)
    {
        if (parent == null || parent == excludeElement || (excludeElement != null && IsAncestorOf(excludeElement, parent)))
            return;

        if (IsValidDropContainer(parent) && parent != DesignSurface)
        {
            results.Add(parent);
        }

        if (parent is ContainerVisual container)
        {
            for (int i = 0; i < container.Children.Count; i++)
            {
                if (container.Children[i] is FrameworkElement child)
                {
                    FindAllContainers(child, results, excludeElement);
                }
            }
        }
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
            var prop = GetPropertySafe(type, contentPropertyAttr.Name);
            if (prop != null && prop.CanWrite)
            {
                prop.SetValue(target, newChild);
                return;
            }
        }

        var childProp = GetPropertySafe(type, "Child");
        if (childProp != null && childProp.CanWrite && typeof(FrameworkElement).IsAssignableFrom(childProp.PropertyType))
        {
            childProp.SetValue(target, newChild);
            return;
        }

        var contentProp = GetPropertySafe(type, "Content");
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

    private void AddChildToContainer(FrameworkElement target, FrameworkElement newChild)
    {
        AddChildToTarget(target, newChild);
    }

    private void RemoveChildFromParent(FrameworkElement child)
    {
        if (child == null) return;
        var parent = child.Parent as FrameworkElement;
        if (parent == null)
        {
            var containerParent = child.Parent as ContainerVisual;
            containerParent?.RemoveChild(child);
            return;
        }

        var type = parent.GetType();
        var contentPropertyAttr = type.GetCustomAttribute<ContentPropertyAttribute>(true);
        if (contentPropertyAttr != null && !string.IsNullOrEmpty(contentPropertyAttr.Name))
        {
            var prop = GetPropertySafe(type, contentPropertyAttr.Name);
            if (prop != null)
            {
                if (prop.CanWrite && prop.GetValue(parent) == child)
                {
                    prop.SetValue(parent, null);
                    return;
                }
                else if (typeof(System.Collections.IList).IsAssignableFrom(prop.PropertyType))
                {
                    var list = prop.GetValue(parent) as System.Collections.IList;
                    if (list != null && list.Contains(child))
                    {
                        list.Remove(child);
                        return;
                    }
                }
            }
        }

        var childProp = GetPropertySafe(type, "Child");
        if (childProp != null && childProp.CanWrite && childProp.GetValue(parent) == child)
        {
            childProp.SetValue(parent, null);
            return;
        }

        var contentProp = GetPropertySafe(type, "Content");
        if (contentProp != null && contentProp.CanWrite && contentProp.GetValue(parent) == child)
        {
            contentProp.SetValue(parent, null);
            return;
        }

        if (parent is Panel panel)
        {
            panel.Children.Remove(child);
            return;
        }

        parent.RemoveChild(child);
    }

    private static bool IsAncestorOf(FrameworkElement possibleAncestor, FrameworkElement child)
    {
        var current = child.Parent as FrameworkElement;
        while (current != null)
        {
            if (current == possibleAncestor) return true;
            current = current.Parent as FrameworkElement;
        }
        return false;
    }

    private int GetVisualDepth(FrameworkElement fe)
    {
        int depth = 0;
        var current = fe.Parent;
        while (current != null)
        {
            depth++;
            current = current.Parent;
        }
        return depth;
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
