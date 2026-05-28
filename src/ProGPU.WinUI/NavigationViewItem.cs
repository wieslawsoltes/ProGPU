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
using static System.FormattableString;

namespace Microsoft.UI.Xaml.Controls;

public class NavigationViewItem : Control
{
    private string _text = string.Empty;
    private string _icon = string.Empty;
    private bool _isSelected;
    private bool _isExpanded;
    private int _level;
    private FrameworkElement? _page;

    private float _cachedIconStartX = -9999f;
    private float _cachedIconStartY = -9999f;
    private PathGeometry? _iconGeo1;
    private PathGeometry? _iconGeo2;
    private PathGeometry? _iconGeo3;
    private PathGeometry? _iconGeo4;
    private PathGeometry? _iconGeo5;
    private PathGeometry? _iconGeo6;

    private SolidColorBrush? _translucentBrush;
    private SolidColorBrush? _translucentHeavyBrush;
    private Pen? _translucentPen;
    private SolidColorBrush? _orangeBrush;
    private Pen? _redPen;
    private SolidColorBrush? _greenBrush;
    private ElementTheme _cachedBrushesTheme = ElementTheme.Default;

    public override void OnVisualStateChanged()
    {
        _cachedBrushesTheme = ElementTheme.Default; // invalidate cached brushes/pens
        base.OnVisualStateChanged();
    }

    public string Text
    {
        get => _text;
        set { if (_text != value) { _text = value; Invalidate(); } }
    }

    public string Icon
    {
        get => _icon;
        set
        {
            if (_icon != value)
            {
                _icon = value;
                _cachedIconStartX = -9999f;
                Invalidate();
            }
        }
    }

    public bool IsSelected
    {
        get => _isSelected;
        set { if (_isSelected != value) { _isSelected = value; Invalidate(); } }
    }

    public bool IsExpanded
    {
        get => _isExpanded;
        set { if (_isExpanded != value) { _isExpanded = value; Invalidate(); } }
    }

    public int Level
    {
        get => _level;
        internal set { if (_level != value) { _level = value; Invalidate(); } }
    }

    public FrameworkElement? Page
    {
        get => _page;
        set { _page = value; }
    }

    public ObservableCollection<NavigationViewItem> Items { get; }

    public override Brush? GetCurrentBackground()
    {
        var theme = ActualTheme;
        if (IsSelected) return ThemeManager.GetBrush("NavigationViewItemBackgroundSelected", theme);
        if (IsPointerOver) return ThemeManager.GetBrush("NavigationViewItemBackgroundPointerOver", theme);
        return null;
    }

    public NavigationViewItem()
    {
        Items = new ObservableCollection<NavigationViewItem>();
        Items.CollectionChanged += (s, e) => Invalidate();
        HeightConstraint = 40f;

        var defaultStyle = ThemeManager.GetDefaultStyle(GetType());
        if (defaultStyle != null)
        {
            Style = defaultStyle;
        }
    }

    public NavigationViewItem(string text, string icon = "", FrameworkElement? page = null) : this()
    {
        Text = text;
        Icon = icon;
        Page = page;
    }

    private NavigationView? FindParentNavigationView()
    {
        var p = Parent;
        while (p != null)
        {
            if (p is NavigationView nav) return nav;
            p = p.Parent;
        }
        return null;
    }

    public override void OnPointerPressed(PointerRoutedEventArgs e)
    {
        if (IsEnabled)
        {
            base.OnPointerPressed(e);
            
            var nav = FindParentNavigationView();
            if (nav != null)
            {
                var activeFamily = nav.ActualThemeFamily;
                bool isChevronClick = false;
                if (Items.Count > 0 && nav.IsPaneOpen)
                {
                    if (activeFamily == VisualThemeFamily.macOS)
                    {
                        float baseIndent = 16f + (Level * 16f);
                        isChevronClick = e.Position.X >= baseIndent - 8f && e.Position.X <= baseIndent + 20f;
                    }
                    else
                    {
                        isChevronClick = e.Position.X >= Size.X - 40f;
                    }
                }

                if (isChevronClick)
                {
                    IsExpanded = !IsExpanded;
                    nav.OnItemExpandedChanged(this);
                }
                else
                {
                    nav.SelectedItem = this;
                }
                e.Handled = true;
            }
        }
    }

    private NavigationViewItem? FindParentNavigationViewItem()
    {
        var p = Parent;
        while (p != null)
        {
            if (p is NavigationViewItem parentItem) return parentItem;
            p = p.Parent;
        }
        return null;
    }

    public override void OnKeyDown(KeyRoutedEventArgs e)
    {
        var nav = FindParentNavigationView();
        if (nav != null)
        {
            var items = nav.FlatVisibleItems;
            int index = items.IndexOf(this);

            if (this == nav.SettingsItem)
            {
                if (e.Key == Silk.NET.Input.Key.Up && items.Count > 0)
                {
                    InputSystem.SetFocus(items[^1]);
                    e.Handled = true;
                }
                else if (e.Key == Silk.NET.Input.Key.Down && items.Count > 0)
                {
                    InputSystem.SetFocus(items[0]);
                    e.Handled = true;
                }
                else if (e.Key == Silk.NET.Input.Key.Enter || e.Key == Silk.NET.Input.Key.Space)
                {
                    nav.SelectedItem = this;
                    e.Handled = true;
                }
                return;
            }

            if (e.Key == Silk.NET.Input.Key.Enter || e.Key == Silk.NET.Input.Key.Space)
            {
                nav.SelectedItem = this;
                e.Handled = true;
                return;
            }
            else if (e.Key == Silk.NET.Input.Key.Down)
            {
                if (index >= 0 && index < items.Count - 1)
                {
                    InputSystem.SetFocus(items[index + 1]);
                    e.Handled = true;
                }
                else if (index == items.Count - 1 && nav.SettingsItem != null)
                {
                    InputSystem.SetFocus(nav.SettingsItem);
                    e.Handled = true;
                }
                return;
            }
            else if (e.Key == Silk.NET.Input.Key.Up)
            {
                if (index > 0)
                {
                    InputSystem.SetFocus(items[index - 1]);
                    e.Handled = true;
                }
                else if (index == 0 && nav.SettingsItem != null)
                {
                    InputSystem.SetFocus(nav.SettingsItem);
                    e.Handled = true;
                }
                return;
            }
            else if (e.Key == Silk.NET.Input.Key.Right)
            {
                if (Items.Count > 0)
                {
                    if (!IsExpanded)
                    {
                        IsExpanded = true;
                        nav.OnItemExpandedChanged(this);
                        e.Handled = true;
                    }
                    else if (index >= 0 && index < items.Count - 1)
                    {
                        InputSystem.SetFocus(items[index + 1]);
                        e.Handled = true;
                    }
                }
                return;
            }
            else if (e.Key == Silk.NET.Input.Key.Left)
            {
                if (Items.Count > 0 && IsExpanded)
                {
                    IsExpanded = false;
                    nav.OnItemExpandedChanged(this);
                    e.Handled = true;
                }
                else if (Level > 0)
                {
                    var parentItem = FindParentNavigationViewItem();
                    if (parentItem != null)
                    {
                        InputSystem.SetFocus(parentItem);
                        e.Handled = true;
                    }
                }
                return;
            }
        }

        base.OnKeyDown(e);
    }

    protected override void RaisePropertyChanged(string propertyName)
    {
        base.RaisePropertyChanged(propertyName);
        if (propertyName == "IsFocused")
        {
            var nav = FindParentNavigationView();
            nav?.UpdateTabStops();
        }
    }

    protected override Vector2 MeasureOverride(Vector2 availableSize)
    {
        float w = WidthConstraint ?? availableSize.X;
        float h = HeightConstraint ?? 40f;
        return new Vector2(w, h);
    }

    protected override void ArrangeOverride(Rect arrangeRect)
    {
        Size = new Vector2(arrangeRect.Width, arrangeRect.Height);
    }

    public override void OnRender(DrawingContext context)
    {
        var nav = FindParentNavigationView();
        bool isPaneOpen = nav?.IsPaneOpen ?? false;
        var activeFamily = nav?.ActualThemeFamily ?? VisualThemeFamily.WinUI;
        
        var activeTheme = this.ActualTheme;
        var textPrimary = ThemeManager.GetBrush("TextPrimary", activeTheme);
        var textSecondary = ThemeManager.GetBrush("TextSecondary", activeTheme);
        var accentBrush = ThemeManager.GetBrush("SystemAccentColor", activeTheme);

        var primaryPen = ThemeManager.GetPen("TextPrimary", 1f, activeTheme);
        var secondaryPen = ThemeManager.GetPen("TextSecondary", 1f, activeTheme);

        if (_cachedBrushesTheme != activeTheme)
        {
            var textPrimaryColor = ThemeManager.GetColor("TextPrimary", activeTheme);
            _translucentBrush = new SolidColorBrush(new Vector4(textPrimaryColor.X, textPrimaryColor.Y, textPrimaryColor.Z, 0.15f));
            _translucentHeavyBrush = new SolidColorBrush(new Vector4(textPrimaryColor.X, textPrimaryColor.Y, textPrimaryColor.Z, 0.5f));
            _translucentPen = new Pen(new SolidColorBrush(new Vector4(textPrimaryColor.X, textPrimaryColor.Y, textPrimaryColor.Z, 0.4f)), 1f);
            _orangeBrush = new SolidColorBrush(new Vector4(1f, 0.6f, 0f, 1f));
            _redPen = new Pen(new SolidColorBrush(new Vector4(0.9f, 0.2f, 0.2f, 1f)), 1f);
            _greenBrush = new SolidColorBrush(new Vector4(0.2f, 0.7f, 0.3f, 1.0f));
            _cachedBrushesTheme = activeTheme;
        }

        var translucentBrush = _translucentBrush!;
        var translucentHeavyBrush = _translucentHeavyBrush!;
        var translucentPen = _translucentPen!;
        var OrangeBrush = _orangeBrush!;
        var RedPen = _redPen!;
        var greenBrush = _greenBrush!;

        if (activeFamily == VisualThemeFamily.macOS && IsSelected)
        {
            var whiteBrush = new SolidColorBrush(new Vector4(1f, 1f, 1f, 1f));
            var whitePen = new Pen(whiteBrush, 1f);

            textPrimary = whiteBrush;
            textSecondary = whiteBrush;
            accentBrush = whiteBrush;
            translucentBrush = new SolidColorBrush(new Vector4(1f, 1f, 1f, 0.25f));
            translucentHeavyBrush = new SolidColorBrush(new Vector4(1f, 1f, 1f, 0.6f));
            primaryPen = new Pen(new SolidColorBrush(new Vector4(1f, 1f, 1f, 0.8f)), 1f);
            secondaryPen = new Pen(new SolidColorBrush(new Vector4(1f, 1f, 1f, 0.8f)), 1f);
            translucentPen = new Pen(new SolidColorBrush(new Vector4(1f, 1f, 1f, 0.4f)), 1f);
            OrangeBrush = whiteBrush;
            RedPen = whitePen;
            greenBrush = whiteBrush;
        }

        // 1. Draw modern backgrounds depending on active selection or hover
        if (activeFamily == VisualThemeFamily.macOS)
        {
            if (IsSelected)
            {
                var macOSSelectedBg = ThemeManager.GetBrush("SystemAccentColor", activeTheme);
                context.DrawRoundedRectangle(macOSSelectedBg, null, new Rect(6f, 2f, Size.X - 12f, Size.Y - 4f), 5f);
            }
            else if (IsPointerOver)
            {
                var macOSHoverBg = activeTheme == ElementTheme.Light
                    ? new SolidColorBrush(new Vector4(0f, 0f, 0f, 0.05f))
                    : new SolidColorBrush(new Vector4(1f, 1f, 1f, 0.05f));
                context.DrawRoundedRectangle(macOSHoverBg, null, new Rect(6f, 2f, Size.X - 12f, Size.Y - 4f), 5f);
            }
        }
        else
        {
            var bg = GetCurrentBackground();
            if (bg != null)
            {
                context.DrawRectangle(bg, null, new Rect(0f, 0f, Size.X, Size.Y));
            }

            // 2. Draw 3px left accent stripe indicator
            if (IsSelected)
            {
                context.DrawRectangle(accentBrush, null, new Rect(3f, 6f, 3f, Size.Y - 12f));
            }
        }

        var font = nav?.GetActiveFont();
        if (font != null)
        {
            float baseIndent = 16f + (Level * 16f); // nesting indentation
            float startX = baseIndent;
            if (activeFamily == VisualThemeFamily.macOS)
            {
                startX += 14f;
            }
            float textY = (Size.Y - 14f) / 2f;

            // 3. Draw Icon
            if (!string.IsNullOrEmpty(Icon))
            {
                float startY = (Size.Y - 16f) / 2f;
                bool drewCustomIcon = false;

                if (_cachedIconStartX != startX || _cachedIconStartY != startY)
                {
                    _cachedIconStartX = startX;
                    _cachedIconStartY = startY;
                    _iconGeo1 = null;
                    _iconGeo2 = null;
                    _iconGeo3 = null;
                    _iconGeo4 = null;
                    _iconGeo5 = null;
                    _iconGeo6 = null;
                }

                if (Icon == "🖱" || Text == "Basic Input")
                {
                    if (_iconGeo1 == null)
                    {
                        _iconGeo1 = PathGeometry.Parse(Invariant($"M {startX + 6} {startY + 1} H {startX + 10} Q {startX + 13} {startY + 1} {startX + 13} {startY + 4} V {startY + 12} Q {startX + 13} {startY + 15} {startX + 10} {startY + 15} H {startX + 6} Q {startX + 3} {startY + 15} {startX + 3} {startY + 12} V {startY + 4} Q {startX + 3} {startY + 1} {startX + 6} {startY + 1} Z"));
                        _iconGeo2 = PathGeometry.Parse(Invariant($"M {startX + 6} {startY + 1} H {startX + 8} V {startY + 8} H {startX + 3} V {startY + 4} Q {startX + 3} {startY + 1} {startX + 6} {startY + 1} Z"));
                        _iconGeo3 = PathGeometry.Parse(Invariant($"M {startX + 3} {startY + 8} H {startX + 13} M {startX + 8} {startY + 1} V {startY + 8}"));
                        _iconGeo4 = PathGeometry.Parse(Invariant($"M {startX + 7.2f} {startY + 3} H {startX + 8.8f} V {startY + 6} H {startX + 7.2f} Z"));
                    }

                    // Clean computer mouse outline (rounded rect) with a scroll wheel line and active left click panel
                    context.DrawPath(translucentBrush, primaryPen, _iconGeo1!);

                    // Active left click panel (semi-translucent fill)
                    context.DrawPath(translucentHeavyBrush, null, _iconGeo2!);

                    // Horizontal and vertical split lines
                    context.DrawPath(null, primaryPen, _iconGeo3!);

                    // Scroll wheel
                    context.DrawPath(textPrimary, null, _iconGeo4!);

                    drewCustomIcon = true;
                }
                else if (Icon == "🔲" || Text == "Layout Panels")
                {
                    // 2x2 grid of small rounded rectangles
                    context.DrawRoundedRectangle(translucentBrush, primaryPen, new Rect(startX + 1f, startY + 1f, 6f, 6f), 1.5f);
                    context.DrawRoundedRectangle(translucentBrush, primaryPen, new Rect(startX + 9f, startY + 1f, 6f, 6f), 1.5f);
                    context.DrawRoundedRectangle(translucentBrush, primaryPen, new Rect(startX + 1f, startY + 9f, 6f, 6f), 1.5f);
                    context.DrawRoundedRectangle(translucentBrush, primaryPen, new Rect(startX + 9f, startY + 9f, 6f, 6f), 1.5f);

                    drewCustomIcon = true;
                }
                else if (Icon == "📄" || Text == "Text & Documents")
                {
                    if (_iconGeo1 == null)
                    {
                        _iconGeo1 = PathGeometry.Parse(Invariant($"M {startX + 2} {startY + 1} H {startX + 10} L {startX + 14} {startY + 5} V {startY + 15} H {startX + 2} Z"));
                        _iconGeo2 = PathGeometry.Parse(Invariant($"M {startX + 10} {startY + 1} V {startY + 5} H {startX + 14} Z"));
                        _iconGeo3 = PathGeometry.Parse(Invariant($"M {startX + 4} {startY + 8} H {startX + 12} M {startX + 4} {startY + 10} H {startX + 12} M {startX + 4} {startY + 12} H {startX + 9}"));
                    }

                    // Document sheet with folded corner and horizontal lines
                    context.DrawPath(translucentBrush, primaryPen, _iconGeo1!);
                    context.DrawPath(textPrimary, primaryPen, _iconGeo2!);
                    context.DrawPath(null, translucentPen, _iconGeo3!);

                    drewCustomIcon = true;
                }
                else if (Icon == "📊" || Text == "Data Virtualization")
                {
                    if (_iconGeo1 == null)
                    {
                        _iconGeo1 = PathGeometry.Parse(Invariant($"M {startX} {startY + 15} H {startX + 16}"));
                    }

                    // Bar chart showing 3 ascending bars
                    context.DrawPath(null, secondaryPen, _iconGeo1!);

                    context.DrawRoundedRectangle(translucentBrush, primaryPen, new Rect(startX + 1f, startY + 10f, 3f, 5f), 1f);
                    context.DrawRoundedRectangle(translucentHeavyBrush, primaryPen, new Rect(startX + 6f, startY + 5f, 3f, 10f), 1f);
                    context.DrawRoundedRectangle(textPrimary, primaryPen, new Rect(startX + 11f, startY + 1f, 3f, 14f), 1f);

                    drewCustomIcon = true;
                }
                else if (Icon == "⚙" || Text == "Compute FX" || Text == "Settings")
                {
                    if (_iconGeo1 == null)
                    {
                        // Gear cogwheel path using vector geometry
                        var gearGeo = new PathGeometry();
                        var gearFig = new PathFigure(Vector2.Zero) { IsClosed = true };
                        int numTeeth = 8;
                        float cx = startX + 8f;
                        float cy = startY + 8f;
                        for (int i = 0; i < numTeeth; i++)
                        {
                            float angleStart = (float)(i * 2 * Math.PI / numTeeth);
                            float angleMid1 = angleStart + (float)(0.25 * 2 * Math.PI / numTeeth);
                            float angleMid2 = angleStart + (float)(0.55 * 2 * Math.PI / numTeeth);
                            float angleEnd = angleStart + (float)(0.8 * 2 * Math.PI / numTeeth);

                            // Inner base start
                            float x1 = cx + 5f * (float)Math.Cos(angleStart);
                            float y1 = cy + 5f * (float)Math.Sin(angleStart);
                            
                            // Outer tooth start
                            float x2 = cx + 7.5f * (float)Math.Cos(angleMid1);
                            float y2 = cy + 7.5f * (float)Math.Sin(angleMid1);
                            
                            // Outer tooth end
                            float x3 = cx + 7.5f * (float)Math.Cos(angleMid2);
                            float y3 = cy + 7.5f * (float)Math.Sin(angleMid2);
                            
                            // Inner base end
                            float x4 = cx + 5f * (float)Math.Cos(angleEnd);
                            float y4 = cy + 5f * (float)Math.Sin(angleEnd);

                            if (i == 0)
                            {
                                gearFig.StartPoint = new Vector2(x1, y1);
                            }
                            else
                            {
                                gearFig.Segments.Add(new LineSegment(new Vector2(x1, y1)));
                            }
                            gearFig.Segments.Add(new LineSegment(new Vector2(x2, y2)));
                            gearFig.Segments.Add(new LineSegment(new Vector2(x3, y3)));
                            gearFig.Segments.Add(new LineSegment(new Vector2(x4, y4)));
                        }
                        gearGeo.Figures.Add(gearFig);
                        _iconGeo1 = gearGeo;

                        // Draw inner hole circle of the gear:
                        _iconGeo2 = PathGeometry.Parse(Invariant($"M {cx - 2f} {cy} Q {cx - 2f} {cy - 2f} {cx} {cy - 2f} Q {cx + 2f} {cy - 2f} {cx + 2f} {cy} Q {cx + 2f} {cy + 2f} {cx} {cy + 2f} Q {cx - 2f} {cy + 2f} {cx - 2f} {cy} Z"));
                    }

                    context.DrawPath(translucentBrush, primaryPen, _iconGeo1!);
                    context.DrawPath(null, primaryPen, _iconGeo2!);

                    drewCustomIcon = true;
                }
                else if (Icon == "🎬" || Text == "Motion & Animations")
                {
                    if (_iconGeo1 == null)
                    {
                        // Slate (clapboard) base body
                        _iconGeo1 = PathGeometry.Parse(Invariant($"M {startX + 2} {startY + 6} H {startX + 14} V {startY + 15} H {startX + 2} Z"));
                        // Slate slanted cap
                        _iconGeo2 = PathGeometry.Parse(Invariant($"M {startX + 2} {startY + 1} H {startX + 14} L {startX + 12} {startY + 5} H {startX + 4} Z"));
                        // Clapper stripes
                        _iconGeo3 = PathGeometry.Parse(Invariant($"M {startX + 5} {startY + 1} L {startX + 3} {startY + 5} M {startX + 9} {startY + 1} L {startX + 7} {startY + 5} M {startX + 13} {startY + 1} L {startX + 11} {startY + 5}"));
                        // Play symbol inside body
                        _iconGeo4 = PathGeometry.Parse(Invariant($"M {startX + 6} {startY + 8} L {startX + 11} {startY + 10.5f} L {startX + 6} {startY + 13} Z"));
                    }

                    context.DrawPath(translucentBrush, primaryPen, _iconGeo1!);
                    context.DrawPath(translucentBrush, primaryPen, _iconGeo2!);
                    context.DrawPath(null, primaryPen, _iconGeo3!);
                    context.DrawPath(textPrimary, null, _iconGeo4!);

                    drewCustomIcon = true;
                }
                else if (Icon == "🛠" || Text == "Advanced Controls")
                {
                    if (_iconGeo1 == null)
                    {
                        // Crossed screwdriver and wrench
                        _iconGeo1 = PathGeometry.Parse(Invariant($"M {startX + 2} {startY + 14} L {startX + 6} {startY + 10}"));
                        _iconGeo2 = PathGeometry.Parse(Invariant($"M {startX + 6} {startY + 10} L {startX + 12} {startY + 4}"));
                        _iconGeo3 = PathGeometry.Parse(Invariant($"M {startX + 12} {startY + 4} L {startX + 14} {startY + 2}"));

                        _iconGeo4 = PathGeometry.Parse(Invariant($"M {startX + 14} {startY + 14} L {startX + 8} {startY + 8}"));
                        _iconGeo5 = PathGeometry.Parse(Invariant($"M {startX + 8} {startY + 8} L {startX + 6} {startY + 6} Q {startX + 2} {startY + 6} {startX + 2} {startY + 2} Q {startX + 6} {startY + 2} {startX + 6} {startY + 6} Z"));
                    }

                    var penThickness2 = ThemeManager.GetPen("TextPrimary", 2f, activeTheme);

                    context.DrawPath(null, ThemeManager.GetPen("SystemAccentColor", 3f, activeTheme), _iconGeo1!); // Accent handle
                    context.DrawPath(null, translucentPen, _iconGeo2!); // Metal shaft
                    context.DrawPath(null, penThickness2, _iconGeo3!); // Tip

                    context.DrawPath(null, penThickness2, _iconGeo4!); // Wrench handle
                    context.DrawPath(translucentBrush, primaryPen, _iconGeo5!); // Open jaw head

                    drewCustomIcon = true;
                }
                else if (Icon == "🎨" || Text == "Compositor API")
                {
                    if (_iconGeo1 == null)
                    {
                        // Clean artist palette outline with blending color blobs
                        _iconGeo1 = PathGeometry.Parse(Invariant($"M {startX + 2} {startY + 8} Q {startX + 2} {startY + 2} {startX + 8} {startY + 2} Q {startX + 14} {startY + 2} {startX + 14} {startY + 8} Q {startX + 14} {startY + 14} {startX + 8} {startY + 14} Q {startX + 5} {startY + 14} {startX + 5} {startY + 11} Q {startX + 5} {startY + 8} {startX + 2} {startY + 8} Z"));
                    }

                    context.DrawPath(translucentBrush, primaryPen, _iconGeo1!);
                    context.DrawRoundedRectangle(accentBrush, null, new Rect(startX + 5f, startY + 4f, 2.5f, 2.5f), 1f);
                    context.DrawRoundedRectangle(greenBrush, null, new Rect(startX + 10f, startY + 5f, 2.5f, 2.5f), 1f);
                    context.DrawRoundedRectangle(textPrimary, null, new Rect(startX + 9f, startY + 10f, 2.5f, 2.5f), 1f);

                    drewCustomIcon = true;
                }
                else if (Icon == "🪟" || Text == "SplitView Layout")
                {
                    if (_iconGeo1 == null)
                    {
                        // Split panel layout: outer rect, vertical divider, right content lines
                        _iconGeo1 = PathGeometry.Parse(Invariant($"M {startX + 2} {startY + 2} H {startX + 14} V {startY + 14} H {startX + 2} Z"));
                        _iconGeo2 = PathGeometry.Parse(Invariant($"M {startX + 6} {startY + 2} V {startY + 14}"));
                        _iconGeo3 = PathGeometry.Parse(Invariant($"M {startX + 8} {startY + 5} H {startX + 12} M {startX + 8} {startY + 8} H {startX + 12} M {startX + 8} {startY + 11} H {startX + 11}"));
                    }

                    context.DrawPath(translucentBrush, primaryPen, _iconGeo1!);
                    context.DrawPath(null, primaryPen, _iconGeo2!);
                    context.DrawPath(null, translucentPen, _iconGeo3!);

                    drewCustomIcon = true;
                }
                else if (Icon == "🖼" || Icon == "🖼️" || Text == "Image & Buttons")
                {
                    if (_iconGeo1 == null)
                    {
                        // Mountain range image frame with a solid sun
                        _iconGeo1 = PathGeometry.Parse(Invariant($"M {startX + 1} {startY + 2} H {startX + 15} V {startY + 14} H {startX + 1} Z"));
                        _iconGeo2 = PathGeometry.Parse(Invariant($"M {startX + 1} {startY + 11} L {startX + 6} {startY + 6} L {startX + 10} {startY + 10} L {startX + 13} {startY + 7} L {startX + 15} {startY + 9}"));
                        _iconGeo3 = PathGeometry.Parse(Invariant($"M {startX + 11} {startY + 5} Q {startX + 11} {startY + 3.5f} {startX + 12.5f} {startY + 3.5f} Q {startX + 14} {startY + 3.5f} {startX + 14} {startY + 5} Q {startX + 14} {startY + 6.5f} {startX + 12.5f} {startY + 6.5f} Q {startX + 11} {startY + 6.5f} {startX + 11} {startY + 5} Z"));
                    }

                    context.DrawPath(translucentBrush, primaryPen, _iconGeo1!);
                    context.DrawPath(null, primaryPen, _iconGeo2!);
                    context.DrawPath(textSecondary, null, _iconGeo3!);

                    drewCustomIcon = true;
                }
                else if (Icon == "📐" || Text == "Drawing Context")
                {
                    if (_iconGeo1 == null)
                    {
                        // Ruler set-square triangle geometry
                        _iconGeo1 = PathGeometry.Parse(Invariant($"M {startX + 1} {startY + 14} V {startY + 2} L {startX + 13} {startY + 14} Z"));
                        _iconGeo2 = PathGeometry.Parse(Invariant($"M {startX + 4} {startY + 11} V {startY + 5} L {startX + 10} {startY + 11} Z"));
                    }

                    context.DrawPath(translucentBrush, primaryPen, _iconGeo1!);
                    context.DrawPath(null, primaryPen, _iconGeo2!);

                    drewCustomIcon = true;
                }
                else if (Icon == "📁" || Text == "File Storage")
                {
                    if (_iconGeo1 == null)
                    {
                        // standard folder vector path
                        _iconGeo1 = PathGeometry.Parse(Invariant($"M {startX + 1} {startY + 3} H {startX + 6} L {startX + 8} {startY + 5} H {startX + 15} V {startY + 13} H {startX + 1} Z"));
                        _iconGeo2 = PathGeometry.Parse(Invariant($"M {startX + 1} {startY + 6} H {startX + 15}"));
                    }

                    context.DrawPath(translucentBrush, primaryPen, _iconGeo1!);
                    context.DrawPath(null, translucentPen, _iconGeo2!);

                    drewCustomIcon = true;
                }
                else if (Icon == "💅" || Text == "Styles Showcase")
                {
                    if (_iconGeo1 == null)
                    {
                        // Paint brush shape
                        _iconGeo1 = PathGeometry.Parse(Invariant($"M {startX + 2} {startY + 14} L {startX + 8} {startY + 8}"));
                        _iconGeo2 = PathGeometry.Parse(Invariant($"M {startX + 8} {startY + 8} L {startX + 12} {startY + 4} Q {startX + 15} {startY + 1} {startX + 15} {startY + 3} Q {startX + 13} {startY + 6} {startX + 10} {startY + 8} Z"));
                    }

                    context.DrawPath(null, primaryPen, _iconGeo1!);
                    context.DrawPath(accentBrush, primaryPen, _iconGeo2!);

                    drewCustomIcon = true;
                }
                else if (Icon == "🏁" || Text == "MotionMark Showcase")
                {
                    if (_iconGeo1 == null)
                    {
                        // Premium custom checkered flag vector icon
                        
                        // 1. Draw flagpole
                        _iconGeo1 = PathGeometry.Parse(Invariant($"M {startX + 2} {startY + 1} H {startX + 3.5f} V {startY + 15} H {startX + 2} Z"));

                        // Flagpole knob
                        _iconGeo2 = PathGeometry.Parse(Invariant($"M {startX + 1.5f} {startY + 1} Q {startX + 1.5f} {startY + 0} {startX + 2.75f} {startY + 0} Q {startX + 4} {startY + 0} {startX + 4} {startY + 1} Q {startX + 4} {startY + 2} {startX + 2.75f} {startY + 2} Q {startX + 1.5f} {startY + 2} {startX + 1.5f} {startY + 1} Z"));

                        // 2. Draw waving flag background (filled with translucentBrush for modern glass feel)
                        _iconGeo3 = PathGeometry.Parse(Invariant($"M {startX + 4} {startY + 2} Q {startX + 7.6f} {startY + 3.8f} {startX + 11.3f} {startY + 1.8f} Q {startX + 13.1f} {startY + 1.0f} {startX + 15} {startY + 2} V {startY + 9} Q {startX + 13.1f} {startY + 8.0f} {startX + 11.3f} {startY + 8.8f} Q {startX + 7.6f} {startY + 10.8f} {startX + 4} {startY + 9} Z"));

                        // 3. Draw checkered filled cells (filled with textPrimary)
                        // Row 1, Col 1
                        _iconGeo4 = PathGeometry.Parse(Invariant($"M {startX + 4} {startY + 2} Q {startX + 5.8f} {startY + 2.9f} {startX + 7.6f} {startY + 2.8f} V {startY + 6.1f} Q {startX + 5.8f} {startY + 6.2f} {startX + 4} {startY + 5.5f} Z"));

                        // Row 1, Col 3
                        _iconGeo5 = PathGeometry.Parse(Invariant($"M {startX + 11.3f} {startY + 1.8f} Q {startX + 13.1f} {startY + 1.0f} {startX + 15} {startY + 2} V {startY + 5.5f} Q {startX + 13.1f} {startY + 4.5f} {startX + 11.3f} {startY + 5.0f} Z"));

                        // Row 2, Col 2
                        _iconGeo6 = PathGeometry.Parse(Invariant($"M {startX + 7.6f} {startY + 6.1f} Q {startX + 9.5f} {startY + 6.1f} {startX + 11.3f} {startY + 5.0f} V {startY + 8.4f} Q {startX + 9.5f} {startY + 9.5f} {startX + 7.6f} {startY + 9.3f} Z"));
                    }

                    context.DrawPath(translucentHeavyBrush, null, _iconGeo1!);
                    context.DrawPath(textPrimary, null, _iconGeo2!);
                    context.DrawPath(translucentBrush, primaryPen, _iconGeo3!);
                    context.DrawPath(textPrimary, null, _iconGeo4!);
                    context.DrawPath(textPrimary, null, _iconGeo5!);
                    context.DrawPath(textPrimary, null, _iconGeo6!);

                    drewCustomIcon = true;
                }
                else if (Icon == "✨" || Text == "Framework Effects")
                {
                    if (_iconGeo1 == null)
                    {
                        _iconGeo1 = PathGeometry.Parse(Invariant($"M {startX + 8} {startY + 1} Q {startX + 8} {startY + 5} {startX + 12} {startY + 5} Q {startX + 8} {startY + 5} {startX + 8} {startY + 9} Q {startX + 8} {startY + 5} {startX + 4} {startY + 5} Q {startX + 8} {startY + 5} {startX + 8} {startY + 1} Z"));
                        _iconGeo2 = PathGeometry.Parse(Invariant($"M {startX + 12} {startY + 9} Q {startX + 12} {startY + 11} {startX + 14} {startY + 11} Q {startX + 12} {startY + 11} {startX + 12} {startY + 13} Q {startX + 12} {startY + 11} {startX + 10} {startY + 11} Q {startX + 12} {startY + 11} {startX + 12} {startY + 9} Z"));
                    }

                    context.DrawPath(accentBrush, null, _iconGeo1!);
                    context.DrawPath(textPrimary, null, _iconGeo2!);
                    drewCustomIcon = true;
                }
                else if (Icon == "⌨️" || Icon == "⌨" || Text == "Keyboard & Focus" || Text == "Interactive Input")
                {
                    if (_iconGeo1 == null)
                    {
                        _iconGeo1 = PathGeometry.Parse(Invariant($"M {startX + 1} {startY + 4} H {startX + 15} V {startY + 12} H {startX + 1} Z"));
                        _iconGeo2 = PathGeometry.Parse(Invariant($"M {startX + 5} {startY + 10} H {startX + 11}"));
                        _iconGeo3 = PathGeometry.Parse(Invariant($"M {startX + 3} {startY + 6} H {startX + 4} M {startX + 6} {startY + 6} H {startX + 7} M {startX + 9} {startY + 6} H {startX + 10} M {startX + 12} {startY + 6} H {startX + 13}"));
                        _iconGeo4 = PathGeometry.Parse(Invariant($"M {startX + 3} {startY + 8} H {startX + 4} M {startX + 6} {startY + 8} H {startX + 7} M {startX + 9} {startY + 8} H {startX + 10} M {startX + 12} {startY + 8} H {startX + 13}"));
                    }

                    context.DrawPath(translucentBrush, primaryPen, _iconGeo1!);
                    context.DrawPath(null, primaryPen, _iconGeo2!);
                    context.DrawPath(null, translucentPen, _iconGeo3!);
                    context.DrawPath(null, translucentPen, _iconGeo4!);

                    drewCustomIcon = true;
                }
                else if (Icon == "🔤" || Text == "Typography & Scripts")
                {
                    if (_iconGeo1 == null)
                    {
                        _iconGeo1 = PathGeometry.Parse(Invariant($"M {startX + 3} {startY + 14} L {startX + 8} {startY + 2} L {startX + 13} {startY + 14} M {startX + 5} {startY + 10} H {startX + 11}"));
                    }

                    context.DrawPath(translucentBrush, primaryPen, _iconGeo1!);

                    drewCustomIcon = true;
                }
                else if (Icon == "💥" || Text == "LOL/s Benchmark")
                {
                    if (_iconGeo1 == null)
                    {
                        _iconGeo1 = PathGeometry.Parse(Invariant($"M {startX + 8} {startY + 1} L {startX + 10} {startY + 4} L {startX + 14} {startY + 3} L {startX + 12} {startY + 7} L {startX + 15} {startY + 9} L {startX + 11} {startY + 10} L {startX + 12} {startY + 14} L {startX + 8} {startY + 11} L {startX + 5} {startY + 14} L {startX + 6} {startY + 10} L {startX + 2} {startY + 9} L {startX + 5} {startY + 7} L {startX + 3} {startY + 3} L {startX + 7} {startY + 4} Z"));
                    }

                    context.DrawPath(OrangeBrush, RedPen, _iconGeo1!);

                    drewCustomIcon = true;
                }
                else if (Icon == "🔘" || Text == "Radio Button")
                {
                    // concentric/nested double circle vector: outer circle outline, inner active dot
                    float cx = startX + 8f;
                    float cy = startY + 8f;

                    context.DrawCircle(translucentBrush, primaryPen, new Vector2(cx, cy), 7f);
                    context.FillCircle(accentBrush, new Vector2(cx, cy), 3f);

                    drewCustomIcon = true;
                }
                else if (Icon == "⭐" || Text == "Rating Control")
                {
                    if (_iconGeo1 == null)
                    {
                        // 5-point star vector icon matching the rating system
                        float cx = startX + 8f;
                        float cy = startY + 8f;
                        float r = 7.5f;

                        var starGeo = new PathGeometry();
                        var fig = new PathFigure(Vector2.Zero) { IsClosed = true };
                        int points = 5;
                        double innerRadius = r * 0.4;

                        for (int i = 0; i < 2 * points; i++)
                        {
                            double angle = i * Math.PI / points - Math.PI / 2;
                            double radius = (i % 2 == 0) ? r : innerRadius;
                            float x = (float)(cx + radius * Math.Cos(angle));
                            float y = (float)(cy + radius * Math.Sin(angle));

                            if (i == 0)
                            {
                                fig.StartPoint = new Vector2(x, y);
                            }
                            else
                            {
                                fig.Segments.Add(new LineSegment(new Vector2(x, y)));
                            }
                        }
                        starGeo.Figures.Add(fig);
                        _iconGeo1 = starGeo;
                    }

                    context.DrawPath(translucentBrush, primaryPen, _iconGeo1!);
                    drewCustomIcon = true;
                }
                else if (Icon == "🔒" || Text == "Password Box")
                {
                    if (_iconGeo1 == null)
                    {
                        // Padlock vector icon with lock body, curved shackle, and keyhole
                        _iconGeo1 = PathGeometry.Parse(Invariant($"M {startX + 3} {startY + 7} H {startX + 13} Q {startX + 14} {startY + 7} {startX + 14} {startY + 8} V {startY + 13} Q {startX + 14} {startY + 14} {startX + 13} {startY + 14} H {startX + 3} Q {startX + 2} {startY + 14} {startX + 2} {startY + 13} V {startY + 8} Q {startX + 2} {startY + 7} {startX + 3} {startY + 7} Z"));
                        _iconGeo2 = PathGeometry.Parse(Invariant($"M {startX + 5} {startY + 7} V {startY + 4} Q {startX + 5} {startY + 1} {startX + 8} {startY + 1} Q {startX + 11} {startY + 1} {startX + 11} {startY + 4} V {startY + 7}"));
                        _iconGeo3 = PathGeometry.Parse(Invariant($"M {startX + 7.2f} {startY + 9.5f} Q {startX + 7.2f} {startY + 8.7f} {startX + 8} {startY + 8.7f} Q {startX + 8.8f} {startY + 8.7f} {startX + 8.8f} {startY + 9.5f} Q {startX + 8.8f} {startY + 10.3f} {startX + 8} {startY + 10.3f} Q {startX + 7.2f} {startY + 10.3f} {startX + 7.2f} {startY + 9.5f} Z M {startX + 8} {startY + 10.3f} V {startY + 12f}"));
                    }

                    context.DrawPath(translucentBrush, primaryPen, _iconGeo1!);
                    context.DrawPath(null, primaryPen, _iconGeo2!);
                    context.DrawPath(textPrimary, primaryPen, _iconGeo3!);
                    drewCustomIcon = true;
                }



                if (!drewCustomIcon)
                {
                    // Fallback to text icon if not matched
                    context.DrawText(Icon, font, 16f, textPrimary, new Vector2(startX, startY));
                }

                startX += 28f;
            }

            // 4. Draw label text in theme colors
            if (isPaneOpen && !string.IsNullOrEmpty(Text))
            {
                var textBrush = IsSelected ? textPrimary : textSecondary;
                context.DrawText(Text, font, 14f, textBrush, new Vector2(startX, textY));
            }

            // 5. Draw nested expandable arrow indicator
            if (isPaneOpen && Items.Count > 0)
            {
                string arrow = IsExpanded ? "▼" : "▶";
                if (activeFamily == VisualThemeFamily.macOS)
                {
                    context.DrawText(arrow, font, 10f, translucentHeavyBrush, new Vector2(baseIndent - 2f, (Size.Y - 10f) / 2f));
                }
                else
                {
                    context.DrawText(arrow, font, 10f, translucentHeavyBrush, new Vector2(Size.X - 24f, (Size.Y - 10f) / 2f));
                }
            }
        }

        // 6. Draw modern internal focus outline snugly inside the navigation item
        if (IsFocused && InputSystem.IsKeyboardFocusActive)
        {
            var focusPen = ThemeManager.GetPen("SystemAccentColor", 1.5f, activeTheme);
            context.DrawRoundedRectangle(null, focusPen, new Rect(2f, 2f, Size.X - 4f, Size.Y - 4f), 4f);
        }
    }
}
