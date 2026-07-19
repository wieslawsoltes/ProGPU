using System;
using System.Collections.Generic;
using System.Numerics;
using ProGPU.Layout;
using ProGPU.Scene;
using Microsoft.UI.Xaml;

namespace Microsoft.UI.Xaml.Controls;

public class UniformVirtualizingGridPanel : VirtualizingPanel
{
    private int _itemsCount = 0;
    private float _itemWidth = 80f;
    private float _itemHeight = 80f;

    public UniformVirtualizingGridPanel()
    {
        _ownerBindVisualCallback = BindOwnerVisual;
        ThemeManager.ThemeChanged += OnThemeManagerChanged;
    }

    private void OnThemeManagerChanged()
    {
        _recycledVisuals.Clear();
        Invalidate();
    }

    // Direct binding fallback backing fields
    private Func<Visual>? _createVisualFactory;
    private Action<Visual, int>? _bindVisualCallback;
    private readonly Action<Visual, int> _ownerBindVisualCallback;

    // Viewport binding properties (automatically hooks into ItemsControl if available)
    public Func<Visual>? CreateVisualFactory
    {
        get => ItemsControlOwner != null ? ItemsControlOwner.ItemTemplate : _createVisualFactory;
        set => _createVisualFactory = value;
    }

    public Action<Visual, int>? BindVisualCallback
    {
        get => ItemsControlOwner != null ? _ownerBindVisualCallback : _bindVisualCallback;
        set => _bindVisualCallback = value;
    }

    private void BindOwnerVisual(Visual visual, int index)
    {
        var itemsControl = ItemsControlOwner;
        var item = itemsControl?.GetItemAt(index);
        if (item != null)
        {
            itemsControl!.BindVisualCallback?.Invoke(visual, item, index);
        }
    }

    // Recycler pools (active and inactive)
    private readonly Stack<Visual> _recycledVisuals = new();
    private readonly Dictionary<int, Visual> _activeVisuals = new();
    private readonly List<int> _indicesToRecycle = new();

    public int ItemsCount
    {
        get => ItemsControlOwner != null ? GetItemsCount() : _itemsCount;
        set
        {
            if (ItemsControlOwner == null)
            {
                if (_itemsCount != value)
                {
                    _itemsCount = value;
                    UpdateViewport();
                    Invalidate();
                }
            }
        }
    }

    public float ItemWidth
    {
        get => _itemWidth;
        set
        {
            if (_itemWidth != value)
            {
                _itemWidth = value;
                UpdateViewport();
                Invalidate();
            }
        }
    }

    public float ItemHeight
    {
        get => _itemHeight;
        set
        {
            if (_itemHeight != value)
            {
                _itemHeight = value;
                UpdateViewport();
                Invalidate();
            }
        }
    }

    public int ColumnsCount => Math.Max(1, (int)Math.Floor(Math.Max(1f, ViewportWidth) / ItemWidth));
    
    public int RowsCount => (int)Math.Ceiling((double)ItemsCount / ColumnsCount);

    public override float TotalVirtualHeight => RowsCount * ItemHeight;

    protected override void OnScrollOffsetChanged(float newOffset)
    {
        UpdateViewport();
    }

    protected override Vector2 MeasureOverride(Vector2 availableSize)
    {
        float width = float.IsInfinity(availableSize.X) ? 400f : availableSize.X;
        float height = float.IsInfinity(availableSize.Y) ? TotalVirtualHeight : availableSize.Y;

        // Perform active viewport computation during layout pass
        UpdateViewport();

        return new Vector2(width, height);
    }

    protected override void ArrangeOverride(Rect arrangeRect)
    {
        base.ArrangeOverride(arrangeRect);
        UpdateViewport();
    }

    private void UpdateViewport()
    {
        int itemsCount = ItemsCount;
        var createVisual = CreateVisualFactory;
        var itemsControl = ItemsControlOwner;
        var ownerBindVisual = itemsControl?.BindVisualCallback;
        var directBindVisual = _bindVisualCallback;

        float viewportWidth = ViewportWidth;
        float viewportHeight = ViewportHeight;

        if (itemsCount == 0 || createVisual == null ||
            (ownerBindVisual == null && directBindVisual == null) ||
            viewportHeight <= 0 || viewportWidth <= 0)
        {
            ClearActiveToRecycler();
            return;
        }

        int cols = ColumnsCount;
        int rows = RowsCount;

        // 1. Calculate visible item range
        int startRow = (int)Math.Floor(ScrollOffset / ItemHeight);
        int endRow = (int)Math.Ceiling((ScrollOffset + viewportHeight) / ItemHeight);

        startRow = Math.Clamp(startRow, 0, rows - 1);
        endRow = Math.Clamp(endRow, 0, rows - 1);

        int startIdx = startRow * cols;
        int endIdx = Math.Min(itemsCount - 1, (endRow + 1) * cols - 1);

        startIdx = Math.Clamp(startIdx, 0, itemsCount - 1);
        endIdx = Math.Clamp(endIdx, 0, itemsCount - 1);

        // 2. Recycle items that scrolled out of view
        _indicesToRecycle.Clear();
        foreach (var key in _activeVisuals.Keys)
        {
            if (key < startIdx || key > endIdx)
            {
                _indicesToRecycle.Add(key);
            }
        }

        foreach (var idx in _indicesToRecycle)
        {
            var vis = _activeVisuals[idx];
            _activeVisuals.Remove(idx);
            
            // Remove from rendering children and put in recycler pool
            RemoveChild(vis);
            _recycledVisuals.Push(vis);
        }

        // 3. Position and Bind newly visible items
        for (int i = startIdx; i <= endIdx; i++)
        {
            int row = i / cols;
            int col = i % cols;

            if (!_activeVisuals.TryGetValue(i, out var visual))
            {
                // Grab from pool or allocate new
                visual = _recycledVisuals.Count > 0 ? _recycledVisuals.Pop() : createVisual();
                
                // Bind dataset properties
                if (itemsControl != null)
                {
                    var item = itemsControl.GetItemAt(i);
                    if (item != null)
                    {
                        ownerBindVisual!(visual, item, i);
                    }
                }
                else
                {
                    directBindVisual!(visual, i);
                }
                
                _activeVisuals[i] = visual;
                AddChild(visual);
            }

            // Calculate screen position relative to viewport
            float posX = col * ItemWidth;
            float posY = row * ItemHeight;
            if (ScrollViewerOwner == null)
            {
                posY = MathF.Round(posY - ScrollOffset);
            }
            
            // Position child visual node
            visual.Offset = new Vector2(posX, posY);
            visual.Size = new Vector2(ItemWidth, ItemHeight);

            // If child is a LayoutNode, arrange it!
            if (visual is LayoutNode childNode)
            {
                childNode.Measure(new Vector2(ItemWidth, ItemHeight));
                childNode.Arrange(new Rect(posX, posY, ItemWidth, ItemHeight));
            }
        }
    }

    private void ClearActiveToRecycler()
    {
        foreach (var vis in _activeVisuals.Values)
        {
            RemoveChild(vis);
            _recycledVisuals.Push(vis);
        }
        _activeVisuals.Clear();
    }

    public override void ForceRebind()
    {
        ClearActiveToRecycler();
        base.ForceRebind();
    }

    public override void RebindVisibleItems()
    {
        var itemsControl = ItemsControlOwner;
        var ownerBindVisual = itemsControl?.BindVisualCallback;
        var directBindVisual = _bindVisualCallback;
        if (ownerBindVisual == null && directBindVisual == null)
        {
            return;
        }

        foreach (var pair in _activeVisuals)
        {
            if (itemsControl != null)
            {
                var item = itemsControl.GetItemAt(pair.Key);
                if (item != null)
                {
                    ownerBindVisual!(pair.Value, item, pair.Key);
                }
            }
            else
            {
                directBindVisual!(pair.Value, pair.Key);
            }
        }

        Invalidate();
    }
}
