using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Numerics;
using ProGPU.Layout;
using ProGPU.Scene;
using ProGPU.Vector;
using ProGPU.Text;

namespace ProGPU.WinUI;

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
            
            // Draw custom three-bar Fluent hamburger lines in white
            var brush = new SolidColorBrush(0xFFFFFFFF);
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
            _navigationView._hamburgerButton.Measure(new Vector2(availableSize.X, 60f));

            foreach (var item in _navigationView._flatVisibleItems)
            {
                item.Measure(new Vector2(availableSize.X, 40f));
            }

            if (_navigationView.SettingsItem != null)
            {
                _navigationView.SettingsItem.Measure(new Vector2(availableSize.X, 40f));
            }

            return availableSize;
        }

        protected override void ArrangeOverride(Rect arrangeRect)
        {
            _navigationView._hamburgerButton.Arrange(new Rect(arrangeRect.X + (arrangeRect.Width - 40f) / 2f, arrangeRect.Y + 10f, 40f, 40f));

            float cursorY = arrangeRect.Y + 60f;
            foreach (var item in _navigationView._flatVisibleItems)
            {
                item.Arrange(new Rect(arrangeRect.X, cursorY, arrangeRect.Width, 40f));
                cursorY += 40f;
            }

            if (_navigationView.SettingsItem != null)
            {
                float settingsY = arrangeRect.Y + arrangeRect.Height - 50f;
                _navigationView.SettingsItem.Arrange(new Rect(arrangeRect.X, settingsY, arrangeRect.Width, 40f));
            }
        }

        public override void OnRender(DrawingContext context)
        {
            var paneBg = new SolidColorBrush(0x1F1F1FFF);
            context.DrawRectangle(paneBg, null, new Rect(0f, 0f, Size.X, Size.Y));
            
            var sepBrush = new SolidColorBrush(0xFFFFFF15);
            context.DrawRectangle(sepBrush, null, new Rect(Size.X - 1f, 0f, 1f, Size.Y));

            base.OnRender(context);
        }
    }

    private readonly HamburgerButton _hamburgerButton;
    private readonly List<NavigationViewItem> _flatVisibleItems = new();
    private bool _isPaneOpen;
    private NavigationViewItem? _selectedItem;
    private NavigationViewItem? _settingsItem;
    private FrameworkElement? _content;
    private TtfFont? _font;
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

    public TtfFont? Font
    {
        get => _font;
        set { if (_font != value) { _font = value; Invalidate(); } }
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
        _splitView = new SplitView
        {
            DisplayMode = SplitViewDisplayMode.CompactInline,
            PaneWidth = 240f,
            CompactPaneLength = 60f,
            Pane = _panePanel,
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
        if (Font != null) return Font;
        
        var p = Parent;
        while (p != null)
        {
            var prop = p.GetType().GetProperty("Font");
            if (prop != null && prop.GetValue(p) is TtfFont f) return f;
            p = p.Parent;
        }

        try
        {
            var asm = AppDomain.CurrentDomain.GetAssemblies();
            foreach (var assembly in asm)
            {
                var type = assembly.GetType("ProGPU.Samples.Program");
                if (type != null)
                {
                    var method = type.GetMethod("GetFont");
                    if (method != null && method.Invoke(null, null) is TtfFont staticFont)
                    {
                        return staticFont;
                    }
                }
            }
        }
        catch { }
        return null;
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

        // 1. Add Hamburger
        _panePanel.AddChild(_hamburgerButton);

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

        Invalidate();
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
