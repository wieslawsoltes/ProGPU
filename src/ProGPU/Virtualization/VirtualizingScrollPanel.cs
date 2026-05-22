using System;
using System.Collections.Generic;
using System.Numerics;
using ProGPU.Layout;
using ProGPU.Scene;

namespace ProGPU.Virtualization;

public class VirtualizingScrollPanel : LayoutNode
{
    private float _scrollOffset = 0f;
    private int _itemsCount = 0;
    private float _itemHeight = 40f;

    // Viewport binding properties
    public Func<Visual>? CreateVisualFactory { get; set; }
    public Action<Visual, int>? BindVisualCallback { get; set; }

    // Recycler pools (active and inactive)
    private readonly Stack<Visual> _recycledVisuals = new();
    private readonly Dictionary<int, Visual> _activeVisuals = new();

    public float ScrollOffset
    {
        get => _scrollOffset;
        set
        {
            float maxScroll = Math.Max(0f, TotalVirtualHeight - Size.Y);
            float clamped = Math.Clamp(value, 0f, maxScroll);
            if (_scrollOffset != clamped)
            {
                _scrollOffset = clamped;
                Invalidate();
            }
        }
    }

    public int ItemsCount
    {
        get => _itemsCount;
        set
        {
            if (_itemsCount != value)
            {
                _itemsCount = value;
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
                Invalidate();
            }
        }
    }

    public float TotalVirtualHeight => _itemsCount * _itemHeight;

    protected override Vector2 MeasureOverride(Vector2 availableSize)
    {
        // Viewport takes all available width, but height is constrained by parent or available space
        float height = float.IsInfinity(availableSize.Y) ? 500f : availableSize.Y;
        float width = float.IsInfinity(availableSize.X) ? 400f : availableSize.X;

        // Perform active viewport computation during layout pass
        UpdateViewport();

        return new Vector2(width, height);
    }

    protected override void ArrangeOverride(Rect arrangeRect)
    {
        UpdateViewport();
    }

    private void UpdateViewport()
    {
        if (ItemsCount == 0 || CreateVisualFactory == null || BindVisualCallback == null || Size.Y <= 0)
        {
            ClearActiveToRecycler();
            return;
        }

        // 1. Calculate visible item range
        int startIdx = (int)Math.Floor(ScrollOffset / ItemHeight);
        int endIdx = (int)Math.Ceiling((ScrollOffset + Size.Y) / ItemHeight);

        startIdx = Math.Clamp(startIdx, 0, ItemsCount - 1);
        endIdx = Math.Clamp(endIdx, 0, ItemsCount - 1);

        // 2. Recycle items that scrolled out of view
        var indicesToRecycle = new List<int>();
        foreach (var key in _activeVisuals.Keys)
        {
            if (key < startIdx || key > endIdx)
            {
                indicesToRecycle.Add(key);
            }
        }

        foreach (var idx in indicesToRecycle)
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
            if (!_activeVisuals.TryGetValue(i, out var visual))
            {
                // Grab from pool or allocate new
                visual = _recycledVisuals.Count > 0 ? _recycledVisuals.Pop() : CreateVisualFactory();
                
                // Bind dataset properties
                BindVisualCallback(visual, i);
                
                _activeVisuals[i] = visual;
                AddChild(visual);
            }

            // Calculate screen position relative to viewport
            float posY = i * ItemHeight - ScrollOffset;
            
            // Position child visual node
            visual.Offset = new Vector2(0f, posY);
            visual.Size = new Vector2(Size.X, ItemHeight);

            // If child is a LayoutNode, arrange it!
            if (visual is LayoutNode childNode)
            {
                childNode.Measure(new Vector2(Size.X, ItemHeight));
                childNode.Arrange(new Rect(0f, posY, Size.X, ItemHeight));
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
}
