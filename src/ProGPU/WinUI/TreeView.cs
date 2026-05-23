using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Numerics;
using ProGPU.Layout;
using ProGPU.Scene;
using ProGPU.Vector;
using ProGPU.Text;
using static System.FormattableString;

namespace ProGPU.WinUI;

public class TreeViewItem : Control
{
    private object? _header;
    private bool _isExpanded;
    private bool _isSelected;
    private int _level;
    private TreeView? _parentTreeView;

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
        Background = new SolidColorBrush(0x00000000);
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
        if (IsEnabled)
        {
            base.OnPointerPressed(e);
            if (_parentTreeView != null)
            {
                float indent = 12f + (Level * 16f);
                // Toggle expand/collapse arrow click if inside arrow bounds
                if (Items.Count > 0 && e.Position.X >= indent - 14f && e.Position.X <= indent + 6f)
                {
                    IsExpanded = !IsExpanded;
                }
                else
                {
                    _parentTreeView.SelectedItem = this;
                }
                e.Handled = true;
            }
        }
    }

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
        if (IsSelected)
        {
            context.DrawRectangle(new SolidColorBrush(0x0078D425), null, new Rect(0f, 0f, Size.X, Size.Y)); // Translucent Segoe active row
            context.DrawRectangle(new SolidColorBrush(0x0078D4FF), null, new Rect(2f, 2f, 2f, Size.Y - 4f)); // Left active accent indicator stripe
        }
        else if (IsPointerOver)
        {
            context.DrawRectangle(new SolidColorBrush(0xFFFFFF0D), null, new Rect(0f, 0f, Size.X, Size.Y)); // Hover feedback
        }

        var font = PopupService.DefaultFont;
        if (font != null)
        {
            float indentX = 12f + (Level * 16f);
            float centerY = (Size.Y - 12f) / 2f;

            // Draw expansion triangle arrow
            if (Items.Count > 0)
            {
                string arrow = IsExpanded ? "▼" : "▶";
                context.DrawText(arrow, font, 8f, new SolidColorBrush(0xFFFFFF80), new Vector2(indentX - 12f, centerY + 2f));
            }

            // Draw Header Text
            float textX = indentX + 6f;
            float textY = (Size.Y - 12f) / 2f;
            string label = Header?.ToString() ?? string.Empty;
            Brush textBrush = IsSelected ? new SolidColorBrush(0xFFFFFFFF) : new SolidColorBrush(0xCCCCCCFF);
            context.DrawText(label, font, 11f, textBrush, new Vector2(textX, textY));
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
        
        Background = new SolidColorBrush(0x0C0C12FA); // Dark translucent plate
        BorderBrush = new SolidColorBrush(0xFFFFFF15);
        BorderThickness = new Thickness(1f);
        CornerRadius = 4f;
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
