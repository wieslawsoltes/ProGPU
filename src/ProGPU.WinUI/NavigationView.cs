using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Documents;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Numerics;
using ProGPU.Layout;
using ProGPU.Scene;
using ProGPU.Vector;
using ProGPU.Text;

namespace Microsoft.UI.Xaml.Controls;

public class NavigationView : FrameworkElement
{
    private class HamburgerButton : Button
    {
        public HamburgerButton()
        {
            CornerRadius = 4f;
            Width = 40f;
            Height = 40f;
        }

        public override void OnRender(DrawingContext context)
        {
            base.OnRender(context);
            
            // Draw custom three-bar Fluent hamburger lines in theme-aware TextPrimary
            var brush = ThemeManager.GetBrush("TextPrimary", this.ActualTheme);
            // Centered nicely inside 40x40 area
            context.DrawRectangle(brush, null, new Rect(11f, 14f, 18f, 2f));
            context.DrawRectangle(brush, null, new Rect(11f, 19f, 18f, 2f));
            context.DrawRectangle(brush, null, new Rect(11f, 24f, 18f, 2f));
        }
    }

    private class NavigationViewPane : Panel
    {
        private readonly NavigationView _navigationView;

        public NavigationViewPane(NavigationView navigationView)
        {
            _navigationView = navigationView;
        }

        protected override Vector2 MeasureOverride(Vector2 availableSize)
        {
            float w = availableSize.X;
            float h = 10f; // top margin
            
            foreach (var item in _navigationView._flatVisibleItems)
            {
                item.Measure(new Vector2(w, 40f));
                h += 40f;
            }

            if (_navigationView.SettingsItem != null)
            {
                _navigationView.SettingsItem.Measure(new Vector2(w, 40f));
                h += 60f; // spacing + height
            }

            return new Vector2(w, h);
        }

        protected override void ArrangeOverride(Rect arrangeRect)
        {
            float cursorY = arrangeRect.Y + 10f;
            foreach (var item in _navigationView._flatVisibleItems)
            {
                item.Arrange(new Rect(arrangeRect.X, cursorY, arrangeRect.Width, 40f));
                cursorY += 40f;
            }

            if (_navigationView.SettingsItem != null)
            {
                float settingsY = cursorY + 20f;
                _navigationView.SettingsItem.Arrange(new Rect(arrangeRect.X, settingsY, arrangeRect.Width, 40f));
            }
        }

        public override void OnRender(DrawingContext context)
        {
            var activeTheme = _navigationView.ActualTheme;
            var paneBg = ThemeManager.GetBrush("HeaderBackground", activeTheme);
            context.DrawRectangle(paneBg, null, new Rect(0f, 0f, Size.X, Size.Y));
            
            var sepBrush = ThemeManager.GetBrush("ControlBorder", activeTheme);
            context.DrawRectangle(sepBrush, null, new Rect(Size.X - 1f, 0f, 1f, Size.Y));

            base.OnRender(context);
        }
    }

    private readonly HamburgerButton _hamburgerButton;
    private readonly List<NavigationViewItem> _flatVisibleItems = new();
    internal List<NavigationViewItem> FlatVisibleItems => _flatVisibleItems;
    private bool _isPaneOpen;
    private NavigationViewItem? _selectedItem;
    private NavigationViewItem? _settingsItem;
    private FrameworkElement? _content;
    private readonly SplitView _splitView;
    private readonly NavigationViewPane _panePanel;

    public ObservableCollection<NavigationViewItem> MenuItems { get; }

    public bool IsPaneOpen
    {
        get => _isPaneOpen;
        set
        {
            if (_isPaneOpen != value)
            {
                _isPaneOpen = value;
                _splitView.IsPaneOpen = value;
                Invalidate();
            }
        }
    }

    public NavigationViewItem? SelectedItem
    {
        get => _selectedItem;
        set
        {
            if (_selectedItem != value)
            {
                if (_selectedItem != null)
                {
                    _selectedItem.IsSelected = false;
                }
                if (_settingsItem != null && _settingsItem != value)
                {
                    _settingsItem.IsSelected = false;
                }

                _selectedItem = value;
                if (_selectedItem != null)
                {
                    _selectedItem.IsSelected = true;
                    if (_selectedItem.Page != null)
                    {
                        Content = _selectedItem.Page;
                    }
                }
                
                SelectionChanged?.Invoke(this, EventArgs.Empty);
                UpdateTabStops();
                Invalidate();
            }
        }
    }

    public NavigationViewItem? SettingsItem
    {
        get => _settingsItem;
        set
        {
            if (_settingsItem != value)
            {
                _settingsItem = value;
                RebuildPaneChildren();
            }
        }
    }

    public FrameworkElement? Content
    {
        get => _content;
        set
        {
            if (_content != value)
            {
                _content = value;
                _splitView.Content = value;
                RebuildPaneChildren();
            }
        }
    }

    protected override void OnPropertyChanged(Microsoft.UI.Xaml.DependencyProperty dp, object? oldValue, object? newValue)
    {
        base.OnPropertyChanged(dp, oldValue, newValue);
        if (dp == FontProperty)
        {
            Invalidate();
        }
    }

    public event EventHandler? SelectionChanged;

    public NavigationView()
    {
        MenuItems = new ObservableCollection<NavigationViewItem>();
        
        _hamburgerButton = new HamburgerButton();
        _hamburgerButton.Click += (s, e) => IsPaneOpen = !IsPaneOpen;
        
        MenuItems.CollectionChanged += OnMenuItemsChanged;
        
        _settingsItem = new NavigationViewItem("Settings", "⚙");

        _panePanel = new NavigationViewPane(this);
        _panePanel.CacheAsLayer = true;
        var paneScrollViewer = new ScrollViewer
        {
            Content = _panePanel,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch
        };
        _splitView = new SplitView
        {
            DisplayMode = SplitViewDisplayMode.CompactInline,
            PaneWidth = 240f,
            CompactPaneLength = 60f,
            Pane = paneScrollViewer,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch
        };
        AddChild(_splitView);

        RebuildPaneChildren();
    }

    private void OnMenuItemsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        RebuildPaneChildren();
    }

    public TtfFont? GetActiveFont()
    {
        return Font ?? PopupService.DefaultFont;
    }

    private void AddVisibleItems(NavigationViewItem item, int level, List<NavigationViewItem> list)
    {
        item.Level = level;
        list.Add(item);
        if (item.IsExpanded)
        {
            foreach (var sub in item.Items)
            {
                AddVisibleItems(sub, level + 1, list);
            }
        }
    }

    internal void OnItemExpandedChanged(NavigationViewItem item)
    {
        RebuildPaneChildren();
    }

    private void RebuildPaneChildren()
    {
        _panePanel.ClearChildren();

        // 2. Add currently expanded visual menu items stack
        _flatVisibleItems.Clear();
        foreach (var item in MenuItems)
        {
            AddVisibleItems(item, 0, _flatVisibleItems);
        }

        foreach (var item in _flatVisibleItems)
        {
            _panePanel.AddChild(item);
        }

        // 3. Add SettingsItem if available
        if (SettingsItem != null)
        {
            _panePanel.AddChild(SettingsItem);
        }

        UpdateTabStops();
        Invalidate();
    }

    internal void UpdateTabStops()
    {
        var items = FlatVisibleItems;
        var focusedItem = InputSystem.FocusedElement as NavigationViewItem;
        
        // Find which item should be the single tab stop
        NavigationViewItem? targetTab = null;
        
        // 1. If one of our items is currently focused, that is the tab stop
        if (focusedItem != null && (items.Contains(focusedItem) || focusedItem == SettingsItem))
        {
            targetTab = focusedItem;
        }
        // 2. Otherwise, the selected item is the tab stop
        else if (SelectedItem != null)
        {
            targetTab = SelectedItem;
        }
        // 3. Otherwise, the first visible item is the tab stop
        else if (items.Count > 0)
        {
            targetTab = items[0];
        }
        // 4. Otherwise, SettingsItem if available
        else if (SettingsItem != null)
        {
            targetTab = SettingsItem;
        }

        // Apply IsTabStop to all items
        foreach (var item in items)
        {
            item.IsTabStop = (item == targetTab);
        }
        if (SettingsItem != null)
        {
            SettingsItem.IsTabStop = (SettingsItem == targetTab);
        }
    }

    protected override Vector2 MeasureOverride(Vector2 availableSize)
    {
        _splitView.Measure(availableSize);
        return availableSize;
    }

    protected override void ArrangeOverride(Rect arrangeRect)
    {
        _splitView.Arrange(arrangeRect);
    }
}
