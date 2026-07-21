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
    private float _measuredItemWidth = 80f;
    private float _measuredItemHeight = 80f;
    private Orientation _orientation = Orientation.Horizontal;
    private int _maximumRowsOrColumns = -1;
    private float _cacheLength;

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
            if (!float.IsNaN(value) && (!float.IsFinite(value) || value <= 0f))
                throw new ArgumentOutOfRangeException(nameof(value));
            if (_itemWidth != value)
            {
                _itemWidth = value;
                _measuredItemWidth = float.IsNaN(value) ? 80f : value;
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
            if (!float.IsNaN(value) && (!float.IsFinite(value) || value <= 0f))
                throw new ArgumentOutOfRangeException(nameof(value));
            if (_itemHeight != value)
            {
                _itemHeight = value;
                _measuredItemHeight = float.IsNaN(value) ? 80f : value;
                UpdateViewport();
                Invalidate();
            }
        }
    }

    public Orientation Orientation
    {
        get => _orientation;
        set
        {
            if (_orientation == value) return;
            _orientation = value;
            UpdateViewport();
            InvalidateMeasure();
            Invalidate();
        }
    }

    public int MaximumRowsOrColumns
    {
        get => _maximumRowsOrColumns;
        set
        {
            if (value == 0 || value < -1) throw new ArgumentOutOfRangeException(nameof(value));
            if (_maximumRowsOrColumns == value) return;
            _maximumRowsOrColumns = value;
            UpdateViewport();
            InvalidateMeasure();
        }
    }

    public float CacheLength
    {
        get => _cacheLength;
        set
        {
            if (!float.IsFinite(value) || value < 0f) throw new ArgumentOutOfRangeException(nameof(value));
            if (_cacheLength == value) return;
            _cacheLength = value;
            UpdateViewport();
        }
    }

    private float EffectiveItemWidth => float.IsNaN(_itemWidth) ? _measuredItemWidth : _itemWidth;
    private float EffectiveItemHeight => float.IsNaN(_itemHeight) ? _measuredItemHeight : _itemHeight;

    public int ColumnsCount => Orientation == Orientation.Horizontal
        ? GetPrimaryCount(ViewportWidth, EffectiveItemWidth)
        : (int)Math.Ceiling((double)ItemsCount / GetPrimaryCount(ViewportHeight, EffectiveItemHeight));

    public int RowsCount => Orientation == Orientation.Vertical
        ? GetPrimaryCount(ViewportHeight, EffectiveItemHeight)
        : (int)Math.Ceiling((double)ItemsCount / GetPrimaryCount(ViewportWidth, EffectiveItemWidth));

    public override float TotalVirtualHeight => Orientation == Orientation.Horizontal
        ? RowsCount * EffectiveItemHeight
        : Math.Max(ViewportHeight, RowsCount * EffectiveItemHeight);

    public override float TotalVirtualWidth => Orientation == Orientation.Vertical
        ? ColumnsCount * EffectiveItemWidth
        : Math.Max(ViewportWidth, ColumnsCount * EffectiveItemWidth);

    public override bool IsHorizontal => Orientation == Orientation.Vertical;

    protected override void OnScrollOffsetChanged(float newOffset)
    {
        UpdateViewport();
    }

    protected override Vector2 MeasureOverride(Vector2 availableSize)
    {
        float width = float.IsInfinity(availableSize.X)
            ? Orientation == Orientation.Vertical ? TotalVirtualWidth : 400f
            : availableSize.X;
        float height = float.IsInfinity(availableSize.Y)
            ? Orientation == Orientation.Horizontal ? TotalVirtualHeight : 400f
            : availableSize.Y;

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

        if (float.IsNaN(_itemWidth) || float.IsNaN(_itemHeight))
        {
            Visual first = GetOrCreateVisual(0, createVisual, itemsControl, ownerBindVisual, directBindVisual);
            if (first is LayoutNode firstNode)
            {
                firstNode.Measure(new Vector2(
                    float.IsNaN(_itemWidth) ? float.PositiveInfinity : _itemWidth,
                    float.IsNaN(_itemHeight) ? float.PositiveInfinity : _itemHeight));
                if (float.IsNaN(_itemWidth)) _measuredItemWidth = Math.Max(1f, firstNode.DesiredSize.X);
                if (float.IsNaN(_itemHeight)) _measuredItemHeight = Math.Max(1f, firstNode.DesiredSize.Y);
            }
        }

        float itemWidth = EffectiveItemWidth;
        float itemHeight = EffectiveItemHeight;
        int primaryCount = Orientation == Orientation.Horizontal
            ? GetPrimaryCount(viewportWidth, itemWidth)
            : GetPrimaryCount(viewportHeight, itemHeight);
        int groupCount = (int)Math.Ceiling((double)itemsCount / primaryCount);
        float viewportLength = Orientation == Orientation.Horizontal ? viewportHeight : viewportWidth;
        float itemLength = Orientation == Orientation.Horizontal ? itemHeight : itemWidth;
        float realizationPadding = viewportLength * CacheLength;
        float realizationStart = Math.Max(0f, ScrollOffset - realizationPadding);
        float realizationEnd = ScrollOffset + viewportLength + realizationPadding;

        int startGroup = Math.Clamp((int)Math.Floor(realizationStart / itemLength), 0, groupCount - 1);
        int endGroup = Math.Clamp((int)Math.Ceiling(realizationEnd / itemLength), 0, groupCount - 1);

        int startIdx = startGroup * primaryCount;
        int endIdx = Math.Min(itemsCount - 1, (endGroup + 1) * primaryCount - 1);

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
            int row = Orientation == Orientation.Horizontal ? i / primaryCount : i % primaryCount;
            int col = Orientation == Orientation.Horizontal ? i % primaryCount : i / primaryCount;
            Visual visual = GetOrCreateVisual(i, createVisual, itemsControl, ownerBindVisual, directBindVisual);

            // Calculate screen position relative to viewport
            float posX = col * itemWidth;
            float posY = row * itemHeight;
            if (ScrollViewerOwner == null)
            {
                if (Orientation == Orientation.Horizontal) posY = MathF.Round(posY - ScrollOffset);
                else posX = MathF.Round(posX - ScrollOffset);
            }
            
            // Position child visual node
            visual.Offset = new Vector2(posX, posY);
            visual.Size = new Vector2(itemWidth, itemHeight);

            // If child is a LayoutNode, arrange it!
            if (visual is LayoutNode childNode)
            {
                childNode.Measure(new Vector2(itemWidth, itemHeight));
                childNode.Arrange(new Rect(posX, posY, itemWidth, itemHeight));
            }
        }
    }

    private int GetPrimaryCount(float viewportLength, float itemLength)
    {
        if (MaximumRowsOrColumns > 0) return MaximumRowsOrColumns;
        return Math.Max(1, (int)Math.Floor(Math.Max(1f, viewportLength) / itemLength));
    }

    private Visual GetOrCreateVisual(
        int index,
        Func<Visual> createVisual,
        ItemsControl? itemsControl,
        Action<Visual, object, int>? ownerBindVisual,
        Action<Visual, int>? directBindVisual)
    {
        if (_activeVisuals.TryGetValue(index, out Visual? visual)) return visual;

        visual = _recycledVisuals.Count > 0 ? _recycledVisuals.Pop() : createVisual();
        if (itemsControl is not null)
        {
            object? item = itemsControl.GetItemAt(index);
            if (item is not null) ownerBindVisual!(visual, item, index);
        }
        else
        {
            directBindVisual!(visual, index);
        }
        _activeVisuals[index] = visual;
        AddChild(visual);
        return visual;
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
        if (float.IsNaN(_itemWidth)) _measuredItemWidth = 80f;
        if (float.IsNaN(_itemHeight)) _measuredItemHeight = 80f;
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
