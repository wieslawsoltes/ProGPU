using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Documents;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Numerics;
using ProGPU.Layout;
using ProGPU.Scene;
using ProGPU.Vector;
using ProGPU.Text;
using Windows.Devices.Input;
using static System.FormattableString;

namespace Microsoft.UI.Xaml.Controls;

public class TreeViewItem : Control
{
    private object? _header;
    private bool _isExpanded;
    private bool _isSelected;
    private int _level;
    private TreeView? _parentTreeView;
    private uint _pendingTouchPointerId;

    public object? Header
    {
        get => _header;
        set { if (_header != value) { _header = value; Invalidate(); } }
    }

    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (_isExpanded != value)
            {
                _isExpanded = value;
                Invalidate();
                if (_parentTreeView != null)
                {
                    _parentTreeView.OnItemExpandedChanged(this);
                }
            }
        }
    }

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected != value)
            {
                _isSelected = value;
                Invalidate();
                if (_isSelected && _parentTreeView != null)
                {
                    _parentTreeView.SelectedItem = this;
                }
            }
        }
    }

    public int Level
    {
        get => _level;
        internal set { if (_level != value) { _level = value; Invalidate(); } }
    }

    public object? TagValue { get; set; }

    public ObservableCollection<TreeViewItem> Items { get; }

    public TreeViewItem()
    {
        Items = new ObservableCollection<TreeViewItem>();
        Items.CollectionChanged += (s, e) => Invalidate();
        HeightConstraint = 24f; // More compact than NavigationViewItem
        HorizontalAlignment = HorizontalAlignment.Stretch;

        var defaultStyle = ThemeManager.GetDefaultStyle(GetType());
        if (defaultStyle != null)
        {
            Style = defaultStyle;
        }
    }

    public override Brush? GetCurrentBackground()
    {
        if (IsSelected) return ThemeManager.GetBrush("SelectionHighlight");
        if (IsPointerOver) return ThemeManager.GetBrush("ControlBackgroundHover");
        return null;
    }

    public TreeViewItem(object header) : this()
    {
        Header = header;
    }

    internal void SetParentTreeView(TreeView? tv, int level)
    {
        _parentTreeView = tv;
        Level = level;
        foreach (var child in Items)
        {
            child.SetParentTreeView(tv, level + 1);
        }
    }

    public override void OnPointerPressed(PointerRoutedEventArgs e)
    {
        if (!IsEnabled) return;
        if (e.Pointer.PointerDeviceType is PointerDeviceType.Touch or PointerDeviceType.Pen)
        {
            _pendingTouchPointerId = e.Pointer.PointerId;
            e.Handled = true;
            base.OnPointerPressed(e);
            return;
        }

        base.OnPointerPressed(e);
        Activate(e.GetCurrentPoint(this).Position);
        e.Handled = true;
    }

    public override void OnPointerReleased(PointerRoutedEventArgs e)
    {
        if (_pendingTouchPointerId == e.Pointer.PointerId)
        {
            if (IsEnabled && IsPointerPressed && IsPointerOver)
            {
                Activate(e.GetCurrentPoint(this).Position);
                e.Handled = true;
            }
            _pendingTouchPointerId = 0;
        }
        base.OnPointerReleased(e);
    }

    public override void OnPointerCanceled(PointerRoutedEventArgs e)
    {
        if (_pendingTouchPointerId == e.Pointer.PointerId) _pendingTouchPointerId = 0;
        base.OnPointerCanceled(e);
    }

    private void Activate(Vector2 position)
    {
        if (_parentTreeView == null) return;
        var indent = 12f + (Level * 16f);
        if (Items.Count > 0 && position.X >= indent - 14f && position.X <= indent + 6f)
        {
            IsExpanded = !IsExpanded;
        }
        else
        {
            _parentTreeView.SelectedItem = this;
        }
    }

    private float LogicalToPhysicalX(float x) =>
        FlowDirection == FlowDirection.RightToLeft ? Size.X - x : x;

    private Rect LogicalToPhysical(Rect rect) =>
        FlowDirection == FlowDirection.RightToLeft
            ? new Rect(Size.X - rect.Right, rect.Y, rect.Width, rect.Height)
            : rect;

    protected override Vector2 MeasureOverride(Vector2 availableSize)
    {
        float w = WidthConstraint ?? availableSize.X;
        float h = HeightConstraint ?? 24f;
        return new Vector2(w, h);
    }

    protected override void ArrangeOverride(Rect arrangeRect)
    {
        Size = new Vector2(arrangeRect.Width, arrangeRect.Height);
    }

    public override void OnRender(DrawingContext context)
    {
        Brush? bg = GetCurrentBackground();
        if (bg != null)
        {
            context.DrawRectangle(bg, null, new Rect(0f, 0f, Size.X, Size.Y));
        }

        if (IsSelected)
        {
            var accentBrush = ThemeManager.GetBrush("SystemAccentColor");
            context.DrawRectangle(
                accentBrush,
                null,
                LogicalToPhysical(new Rect(2f, 2f, 2f, Size.Y - 4f)));
        }

        var font = PopupService.DefaultFont;
        if (font != null)
        {
            float indentX = 12f + (Level * 16f);
            float centerY = (Size.Y - 12f) / 2f;

            // Draw expansion triangle arrow
            if (Items.Count > 0)
            {
                float arrowX = LogicalToPhysicalX(indentX - 8f);
                float arrowY = Size.Y * 0.5f;
                var arrowPen = new Pen(ThemeManager.GetBrush("TextSecondary"), 1.25f);
                if (IsExpanded)
                {
                    context.DrawLine(arrowPen, new Vector2(arrowX - 3f, arrowY - 1.5f), new Vector2(arrowX, arrowY + 1.5f));
                    context.DrawLine(arrowPen, new Vector2(arrowX, arrowY + 1.5f), new Vector2(arrowX + 3f, arrowY - 1.5f));
                }
                else
                {
                    float direction = FlowDirection == FlowDirection.RightToLeft ? -1f : 1f;
                    context.DrawLine(arrowPen, new Vector2(arrowX - direction * 1.5f, arrowY - 3f), new Vector2(arrowX + direction * 1.5f, arrowY));
                    context.DrawLine(arrowPen, new Vector2(arrowX + direction * 1.5f, arrowY), new Vector2(arrowX - direction * 1.5f, arrowY + 3f));
                }
            }

            // Draw Header Text
            float textY = (Size.Y - 12f) / 2f;
            string label = Header?.ToString() ?? string.Empty;
            Brush textBrush = IsSelected ? ThemeManager.GetBrush("TextPrimary") : ThemeManager.GetBrush("TextSecondary");
            Rect textBounds = LogicalToPhysical(new Rect(
                indentX + 6f,
                textY,
                Math.Max(0f, Size.X - indentX - 12f),
                12f));
            context.DrawText(
                label,
                font,
                11f,
                textBrush,
                new Vector2(textBounds.X, textY),
                Matrix4x4.Identity,
                textBounds,
                textShapingOptions: ProGPU.Text.TextShapingOptions.Default.WithDirection(
                    FlowDirection == FlowDirection.RightToLeft
                        ? ProGPU.Text.Shaping.ShapingDirection.RightToLeft
                        : ProGPU.Text.Shaping.ShapingDirection.LeftToRight),
                textAlignment: FlowDirection == FlowDirection.RightToLeft
                    ? ProGPU.Text.TextAlignment.Right
                    : ProGPU.Text.TextAlignment.Left);
        }

        base.OnRender(context);
    }
}

public class TreeView : Control
{
    private TreeViewItem? _selectedItem;
    private readonly StackPanel _stackPanel;
    private readonly ScrollViewer _scrollViewer;

    public ObservableCollection<TreeViewItem> Items { get; }

    public TreeViewItem? SelectedItem
    {
        get => _selectedItem;
        set
        {
            if (_selectedItem != value)
            {
                if (_selectedItem != null) _selectedItem.IsSelected = false;
                _selectedItem = value;
                if (_selectedItem != null) _selectedItem.IsSelected = true;
                SelectionChanged?.Invoke(this, EventArgs.Empty);
                Invalidate();
            }
        }
    }

    public event EventHandler? SelectionChanged;

    public TreeView()
    {
        Items = new ObservableCollection<TreeViewItem>();
        Items.CollectionChanged += OnItemsChanged;

        _stackPanel = new StackPanel { Orientation = Orientation.Vertical, HorizontalAlignment = HorizontalAlignment.Stretch };
        _scrollViewer = new ScrollViewer { Content = _stackPanel, HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Stretch };
        
        AddChild(_scrollViewer);
        
        var defaultStyle = ThemeManager.GetDefaultStyle(GetType());
        if (defaultStyle != null)
        {
            Style = defaultStyle;
        }
    }

    private void OnItemsChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        RefreshTree();
    }

    internal void OnItemExpandedChanged(TreeViewItem item)
    {
        RefreshTree();
    }

    public void RefreshTree()
    {
        _stackPanel.ClearChildren();
        foreach (var rootItem in Items)
        {
            AddFlattenedItems(rootItem, 0);
        }
        Invalidate();
    }

    private void AddFlattenedItems(TreeViewItem item, int level)
    {
        item.SetParentTreeView(this, level);
        _stackPanel.AddChild(item);

        if (item.IsExpanded)
        {
            foreach (var child in item.Items)
            {
                AddFlattenedItems(child, level + 1);
            }
        }
    }

    protected override Vector2 MeasureOverride(Vector2 availableSize)
    {
        _scrollViewer.Measure(availableSize);
        return _scrollViewer.DesiredSize;
    }

    protected override void ArrangeOverride(Rect arrangeRect)
    {
        _scrollViewer.Arrange(arrangeRect);
    }
}
