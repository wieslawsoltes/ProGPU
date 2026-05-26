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

    public string Text
    {
        get => _text;
        set { if (_text != value) { _text = value; Invalidate(); } }
    }

    public string Icon
    {
        get => _icon;
        set { if (_icon != value) { _icon = value; Invalidate(); } }
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

    public NavigationViewItem()
    {
        Items = new ObservableCollection<NavigationViewItem>();
        Items.CollectionChanged += (s, e) => Invalidate();
        HeightConstraint = 40f;
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
                // In expanded view and clicking on the right expand/collapse indicator (arrow)
                if (Items.Count > 0 && nav.IsPaneOpen && e.Position.X >= Size.X - 40f)
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
        
        var activeTheme = this.ActualTheme;
        var textPrimary = ThemeManager.GetBrush("TextPrimary", activeTheme);
        var textSecondary = ThemeManager.GetBrush("TextSecondary", activeTheme);
        var accentBrush = ThemeManager.GetBrush("SystemAccentColor", activeTheme);

        var bgSelect = new SolidColorBrush(activeTheme == ElementTheme.Light ? new Vector4(0f, 0f, 0f, 0.08f) : new Vector4(1f, 1f, 1f, 0.07f));
        var bgHover = new SolidColorBrush(activeTheme == ElementTheme.Light ? new Vector4(0f, 0f, 0f, 0.05f) : new Vector4(1f, 1f, 1f, 0.05f));

        var primaryPen = new Pen(textPrimary, 1f);
        var secondaryPen = new Pen(textSecondary, 1f);
        var translucentPen = new Pen(new SolidColorBrush(activeTheme == ElementTheme.Light ? new Vector4(0f, 0f, 0f, 0.4f) : new Vector4(1f, 1f, 1f, 0.4f)), 1f);
        var translucentBrush = new SolidColorBrush(activeTheme == ElementTheme.Light ? new Vector4(0f, 0f, 0f, 0.15f) : new Vector4(1f, 1f, 1f, 0.15f));
        var translucentHeavyBrush = new SolidColorBrush(activeTheme == ElementTheme.Light ? new Vector4(0f, 0f, 0f, 0.5f) : new Vector4(1f, 1f, 1f, 0.5f));

        // 1. Draw modern backgrounds depending on active selection or hover
        if (IsSelected)
        {
            context.DrawRectangle(bgSelect, null, new Rect(0f, 0f, Size.X, Size.Y));
        }
        else if (IsPointerOver)
        {
            context.DrawRectangle(bgHover, null, new Rect(0f, 0f, Size.X, Size.Y));
        }

        // 2. Draw 3px left accent stripe indicator
        if (IsSelected)
        {
            context.DrawRectangle(accentBrush, null, new Rect(3f, 6f, 3f, Size.Y - 12f));
        }

        var font = nav?.GetActiveFont();
        if (font != null)
        {
            float startX = 16f + (Level * 16f); // nesting indentation
            float textY = (Size.Y - 14f) / 2f;

            // 3. Draw Icon
            if (!string.IsNullOrEmpty(Icon))
            {
                float startY = (Size.Y - 16f) / 2f;
                bool drewCustomIcon = false;

                if (Icon == "🖱" || Text == "Basic Input")
                {
                    // Clean computer mouse outline (rounded rect) with a scroll wheel line and active left click panel
                    var mouseOutline = PathGeometry.Parse(Invariant($"M {startX + 6} {startY + 1} H {startX + 10} Q {startX + 13} {startY + 1} {startX + 13} {startY + 4} V {startY + 12} Q {startX + 13} {startY + 15} {startX + 10} {startY + 15} H {startX + 6} Q {startX + 3} {startY + 15} {startX + 3} {startY + 12} V {startY + 4} Q {startX + 3} {startY + 1} {startX + 6} {startY + 1} Z"));
                    context.DrawPath(translucentBrush, primaryPen, mouseOutline);

                    // Active left click panel (semi-translucent fill)
                    var leftClick = PathGeometry.Parse(Invariant($"M {startX + 6} {startY + 1} H {startX + 8} V {startY + 8} H {startX + 3} V {startY + 4} Q {startX + 3} {startY + 1} {startX + 6} {startY + 1} Z"));
                    context.DrawPath(translucentHeavyBrush, null, leftClick);

                    // Horizontal and vertical split lines
                    var splitLines = PathGeometry.Parse(Invariant($"M {startX + 3} {startY + 8} H {startX + 13} M {startX + 8} {startY + 1} V {startY + 8}"));
                    context.DrawPath(null, primaryPen, splitLines);

                    // Scroll wheel
                    var wheel = PathGeometry.Parse(Invariant($"M {startX + 7.2f} {startY + 3} H {startX + 8.8f} V {startY + 6} H {startX + 7.2f} Z"));
                    context.DrawPath(textPrimary, null, wheel);

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
                    // Document sheet with folded corner and horizontal lines
                    var docOutline = PathGeometry.Parse(Invariant($"M {startX + 2} {startY + 1} H {startX + 10} L {startX + 14} {startY + 5} V {startY + 15} H {startX + 2} Z"));
                    var docFold = PathGeometry.Parse(Invariant($"M {startX + 10} {startY + 1} V {startY + 5} H {startX + 14} Z"));
                    var docLines = PathGeometry.Parse(Invariant($"M {startX + 4} {startY + 8} H {startX + 12} M {startX + 4} {startY + 10} H {startX + 12} M {startX + 4} {startY + 12} H {startX + 9}"));

                    context.DrawPath(translucentBrush, primaryPen, docOutline);
                    context.DrawPath(textPrimary, primaryPen, docFold);
                    context.DrawPath(null, translucentPen, docLines);

                    drewCustomIcon = true;
                }
                else if (Icon == "📊" || Text == "Data Virtualization")
                {
                    // Bar chart showing 3 ascending bars
                    var axis = PathGeometry.Parse(Invariant($"M {startX} {startY + 15} H {startX + 16}"));
                    context.DrawPath(null, secondaryPen, axis);

                    context.DrawRoundedRectangle(translucentBrush, primaryPen, new Rect(startX + 1f, startY + 10f, 3f, 5f), 1f);
                    context.DrawRoundedRectangle(translucentHeavyBrush, primaryPen, new Rect(startX + 6f, startY + 5f, 3f, 10f), 1f);
                    context.DrawRoundedRectangle(textPrimary, primaryPen, new Rect(startX + 11f, startY + 1f, 3f, 14f), 1f);

                    drewCustomIcon = true;
                }
                else if (Icon == "⚙" || Text == "Compute FX" || Text == "Settings")
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

                    context.DrawPath(translucentBrush, primaryPen, gearGeo);

                    // Draw inner hole circle of the gear:
                    var innerHole = PathGeometry.Parse(Invariant($"M {cx - 2f} {cy} Q {cx - 2f} {cy - 2f} {cx} {cy - 2f} Q {cx + 2f} {cy - 2f} {cx + 2f} {cy} Q {cx + 2f} {cy + 2f} {cx} {cy + 2f} Q {cx - 2f} {cy + 2f} {cx - 2f} {cy} Z"));
                    context.DrawPath(null, primaryPen, innerHole);

                    drewCustomIcon = true;
                }
                else if (Icon == "🎬" || Text == "Motion & Animations")
                {
                    // Slate (clapboard) base body
                    var baseOutline = PathGeometry.Parse(Invariant($"M {startX + 2} {startY + 6} H {startX + 14} V {startY + 15} H {startX + 2} Z"));
                    // Slate slanted cap
                    var capOutline = PathGeometry.Parse(Invariant($"M {startX + 2} {startY + 1} H {startX + 14} L {startX + 12} {startY + 5} H {startX + 4} Z"));
                    // Clapper stripes
                    var clapperStripes = PathGeometry.Parse(Invariant($"M {startX + 5} {startY + 1} L {startX + 3} {startY + 5} M {startX + 9} {startY + 1} L {startX + 7} {startY + 5} M {startX + 13} {startY + 1} L {startX + 11} {startY + 5}"));
                    // Play symbol inside body
                    var playTriangle = PathGeometry.Parse(Invariant($"M {startX + 6} {startY + 8} L {startX + 11} {startY + 10.5f} L {startX + 6} {startY + 13} Z"));

                    context.DrawPath(translucentBrush, primaryPen, baseOutline);
                    context.DrawPath(translucentBrush, primaryPen, capOutline);
                    context.DrawPath(null, primaryPen, clapperStripes);
                    context.DrawPath(textPrimary, null, playTriangle);

                    drewCustomIcon = true;
                }
                else if (Icon == "🛠" || Text == "Advanced Controls")
                {
                    // Crossed screwdriver and wrench
                    var sdHandle = PathGeometry.Parse(Invariant($"M {startX + 2} {startY + 14} L {startX + 6} {startY + 10}"));
                    var sdShaft = PathGeometry.Parse(Invariant($"M {startX + 6} {startY + 10} L {startX + 12} {startY + 4}"));
                    var sdTip = PathGeometry.Parse(Invariant($"M {startX + 12} {startY + 4} L {startX + 14} {startY + 2}"));

                    var wrHandle = PathGeometry.Parse(Invariant($"M {startX + 14} {startY + 14} L {startX + 8} {startY + 8}"));
                    var wrHead = PathGeometry.Parse(Invariant($"M {startX + 8} {startY + 8} L {startX + 6} {startY + 6} Q {startX + 2} {startY + 6} {startX + 2} {startY + 2} Q {startX + 6} {startY + 2} {startX + 6} {startY + 6} Z"));
                    
                    var penThickness2 = new Pen(textPrimary, 2f);

                    context.DrawPath(null, new Pen(accentBrush, 3f), sdHandle); // Accent handle
                    context.DrawPath(null, translucentPen, sdShaft); // Metal shaft
                    context.DrawPath(null, penThickness2, sdTip); // Tip

                    context.DrawPath(null, penThickness2, wrHandle); // Wrench handle
                    context.DrawPath(translucentBrush, primaryPen, wrHead); // Open jaw head

                    drewCustomIcon = true;
                }
                else if (Icon == "🎨" || Text == "Compositor API")
                {
                    // Clean artist palette outline with blending color blobs
                    var palette = PathGeometry.Parse(Invariant($"M {startX + 2} {startY + 8} Q {startX + 2} {startY + 2} {startX + 8} {startY + 2} Q {startX + 14} {startY + 2} {startX + 14} {startY + 8} Q {startX + 14} {startY + 14} {startX + 8} {startY + 14} Q {startX + 5} {startY + 14} {startX + 5} {startY + 11} Q {startX + 5} {startY + 8} {startX + 2} {startY + 8} Z"));
                    context.DrawPath(translucentBrush, primaryPen, palette);
                    context.DrawRoundedRectangle(accentBrush, null, new Rect(startX + 5f, startY + 4f, 2.5f, 2.5f), 1f);
                    context.DrawRoundedRectangle(new SolidColorBrush(new Vector4(0.2f, 0.7f, 0.3f, 1.0f)), null, new Rect(startX + 10f, startY + 5f, 2.5f, 2.5f), 1f);
                    context.DrawRoundedRectangle(textPrimary, null, new Rect(startX + 9f, startY + 10f, 2.5f, 2.5f), 1f);

                    drewCustomIcon = true;
                }
                else if (Icon == "🪟" || Text == "SplitView Layout")
                {
                    // Split panel layout: outer rect, vertical divider, right content lines
                    var outer = PathGeometry.Parse(Invariant($"M {startX + 2} {startY + 2} H {startX + 14} V {startY + 14} H {startX + 2} Z"));
                    var split = PathGeometry.Parse(Invariant($"M {startX + 6} {startY + 2} V {startY + 14}"));
                    var lines = PathGeometry.Parse(Invariant($"M {startX + 8} {startY + 5} H {startX + 12} M {startX + 8} {startY + 8} H {startX + 12} M {startX + 8} {startY + 11} H {startX + 11}"));

                    context.DrawPath(translucentBrush, primaryPen, outer);
                    context.DrawPath(null, primaryPen, split);
                    context.DrawPath(null, translucentPen, lines);

                    drewCustomIcon = true;
                }
                else if (Icon == "🖼" || Icon == "🖼️" || Text == "Image & Buttons")
                {
                    // Mountain range image frame with a solid sun
                    var outer = PathGeometry.Parse(Invariant($"M {startX + 1} {startY + 2} H {startX + 15} V {startY + 14} H {startX + 1} Z"));
                    var mountains = PathGeometry.Parse(Invariant($"M {startX + 1} {startY + 11} L {startX + 6} {startY + 6} L {startX + 10} {startY + 10} L {startX + 13} {startY + 7} L {startX + 15} {startY + 9}"));
                    var sun = PathGeometry.Parse(Invariant($"M {startX + 11} {startY + 5} Q {startX + 11} {startY + 3.5f} {startX + 12.5f} {startY + 3.5f} Q {startX + 14} {startY + 3.5f} {startX + 14} {startY + 5} Q {startX + 14} {startY + 6.5f} {startX + 12.5f} {startY + 6.5f} Q {startX + 11} {startY + 6.5f} {startX + 11} {startY + 5} Z"));

                    context.DrawPath(translucentBrush, primaryPen, outer);
                    context.DrawPath(null, primaryPen, mountains);
                    context.DrawPath(textSecondary, null, sun);

                    drewCustomIcon = true;
                }
                else if (Icon == "📐" || Text == "Drawing Context")
                {
                    // Ruler set-square triangle geometry
                    var outer = PathGeometry.Parse(Invariant($"M {startX + 1} {startY + 14} V {startY + 2} L {startX + 13} {startY + 14} Z"));
                    var inner = PathGeometry.Parse(Invariant($"M {startX + 4} {startY + 11} V {startY + 5} L {startX + 10} {startY + 11} Z"));

                    context.DrawPath(translucentBrush, primaryPen, outer);
                    context.DrawPath(null, primaryPen, inner);

                    drewCustomIcon = true;
                }
                else if (Icon == "📁" || Text == "File Storage")
                {
                    // standard folder vector path
                    var folder = PathGeometry.Parse(Invariant($"M {startX + 1} {startY + 3} H {startX + 6} L {startX + 8} {startY + 5} H {startX + 15} V {startY + 13} H {startX + 1} Z"));
                    var line = PathGeometry.Parse(Invariant($"M {startX + 1} {startY + 6} H {startX + 15}"));

                    context.DrawPath(translucentBrush, primaryPen, folder);
                    context.DrawPath(null, translucentPen, line);

                    drewCustomIcon = true;
                }
                else if (Icon == "💅" || Text == "Styles Showcase")
                {
                    // Paint brush shape
                    var handle = PathGeometry.Parse(Invariant($"M {startX + 2} {startY + 14} L {startX + 8} {startY + 8}"));
                    var head = PathGeometry.Parse(Invariant($"M {startX + 8} {startY + 8} L {startX + 12} {startY + 4} Q {startX + 15} {startY + 1} {startX + 15} {startY + 3} Q {startX + 13} {startY + 6} {startX + 10} {startY + 8} Z"));

                    context.DrawPath(null, primaryPen, handle);
                    context.DrawPath(accentBrush, primaryPen, head);

                    drewCustomIcon = true;
                }
                else if (Icon == "🏁" || Text == "MotionMark Showcase")
                {
                    // Premium custom checkered flag vector icon
                    
                    // 1. Draw flagpole
                    var flagpole = PathGeometry.Parse(Invariant($"M {startX + 2} {startY + 1} H {startX + 3.5f} V {startY + 15} H {startX + 2} Z"));
                    context.DrawPath(translucentHeavyBrush, null, flagpole);

                    // Flagpole knob
                    var knob = PathGeometry.Parse(Invariant($"M {startX + 1.5f} {startY + 1} Q {startX + 1.5f} {startY + 0} {startX + 2.75f} {startY + 0} Q {startX + 4} {startY + 0} {startX + 4} {startY + 1} Q {startX + 4} {startY + 2} {startX + 2.75f} {startY + 2} Q {startX + 1.5f} {startY + 2} {startX + 1.5f} {startY + 1} Z"));
                    context.DrawPath(textPrimary, null, knob);

                    // 2. Draw waving flag background (filled with translucentBrush for modern glass feel)
                    var flagBg = PathGeometry.Parse(Invariant($"M {startX + 4} {startY + 2} Q {startX + 7.6f} {startY + 3.8f} {startX + 11.3f} {startY + 1.8f} Q {startX + 13.1f} {startY + 1.0f} {startX + 15} {startY + 2} V {startY + 9} Q {startX + 13.1f} {startY + 8.0f} {startX + 11.3f} {startY + 8.8f} Q {startX + 7.6f} {startY + 10.8f} {startX + 4} {startY + 9} Z"));
                    context.DrawPath(translucentBrush, primaryPen, flagBg);

                    // 3. Draw checkered filled cells (filled with textPrimary)
                    // Row 1, Col 1
                    var cell1 = PathGeometry.Parse(Invariant($"M {startX + 4} {startY + 2} Q {startX + 5.8f} {startY + 2.9f} {startX + 7.6f} {startY + 2.8f} V {startY + 6.1f} Q {startX + 5.8f} {startY + 6.2f} {startX + 4} {startY + 5.5f} Z"));
                    context.DrawPath(textPrimary, null, cell1);

                    // Row 1, Col 3
                    var cell3 = PathGeometry.Parse(Invariant($"M {startX + 11.3f} {startY + 1.8f} Q {startX + 13.1f} {startY + 1.0f} {startX + 15} {startY + 2} V {startY + 5.5f} Q {startX + 13.1f} {startY + 4.5f} {startX + 11.3f} {startY + 5.0f} Z"));
                    context.DrawPath(textPrimary, null, cell3);

                    // Row 2, Col 2
                    var cell5 = PathGeometry.Parse(Invariant($"M {startX + 7.6f} {startY + 6.1f} Q {startX + 9.5f} {startY + 6.1f} {startX + 11.3f} {startY + 5.0f} V {startY + 8.4f} Q {startX + 9.5f} {startY + 9.5f} {startX + 7.6f} {startY + 9.3f} Z"));
                    context.DrawPath(textPrimary, null, cell5);

                    drewCustomIcon = true;
                }
                else if (Icon == "✨" || Text == "Framework Effects")
                {
                    var sparkle1 = PathGeometry.Parse(Invariant($"M {startX + 8} {startY + 1} Q {startX + 8} {startY + 5} {startX + 12} {startY + 5} Q {startX + 8} {startY + 5} {startX + 8} {startY + 9} Q {startX + 8} {startY + 5} {startX + 4} {startY + 5} Q {startX + 8} {startY + 5} {startX + 8} {startY + 1} Z"));
                    var sparkle2 = PathGeometry.Parse(Invariant($"M {startX + 12} {startY + 9} Q {startX + 12} {startY + 11} {startX + 14} {startY + 11} Q {startX + 12} {startY + 11} {startX + 12} {startY + 13} Q {startX + 12} {startY + 11} {startX + 10} {startY + 11} Q {startX + 12} {startY + 11} {startX + 12} {startY + 9} Z"));
                    
                    context.DrawPath(accentBrush, null, sparkle1);
                    context.DrawPath(textPrimary, null, sparkle2);
                    drewCustomIcon = true;
                }
                else if (Icon == "⌨️" || Icon == "⌨" || Text == "Keyboard & Focus" || Text == "Interactive Input")
                {
                    var keyboardFrame = PathGeometry.Parse(Invariant($"M {startX + 1} {startY + 4} H {startX + 15} V {startY + 12} H {startX + 1} Z"));
                    context.DrawPath(translucentBrush, primaryPen, keyboardFrame);
                    
                    var spacebar = PathGeometry.Parse(Invariant($"M {startX + 5} {startY + 10} H {startX + 11}"));
                    context.DrawPath(null, primaryPen, spacebar);
                    
                    var keys1 = PathGeometry.Parse(Invariant($"M {startX + 3} {startY + 6} H {startX + 4} M {startX + 6} {startY + 6} H {startX + 7} M {startX + 9} {startY + 6} H {startX + 10} M {startX + 12} {startY + 6} H {startX + 13}"));
                    var keys2 = PathGeometry.Parse(Invariant($"M {startX + 3} {startY + 8} H {startX + 4} M {startX + 6} {startY + 8} H {startX + 7} M {startX + 9} {startY + 8} H {startX + 10} M {startX + 12} {startY + 8} H {startX + 13}"));
                    context.DrawPath(null, translucentPen, keys1);
                    context.DrawPath(null, translucentPen, keys2);
                    
                    drewCustomIcon = true;
                }
                else if (Icon == "🔤" || Text == "Typography & Scripts")
                {
                    var charA = PathGeometry.Parse(Invariant($"M {startX + 3} {startY + 14} L {startX + 8} {startY + 2} L {startX + 13} {startY + 14} M {startX + 5} {startY + 10} H {startX + 11}"));
                    context.DrawPath(translucentBrush, primaryPen, charA);
                    
                    drewCustomIcon = true;
                }
                else if (Icon == "💥" || Text == "LOL/s Benchmark")
                {
                    var burst = PathGeometry.Parse(Invariant($"M {startX + 8} {startY + 1} L {startX + 10} {startY + 4} L {startX + 14} {startY + 3} L {startX + 12} {startY + 7} L {startX + 15} {startY + 9} L {startX + 11} {startY + 10} L {startX + 12} {startY + 14} L {startX + 8} {startY + 11} L {startX + 5} {startY + 14} L {startX + 6} {startY + 10} L {startX + 2} {startY + 9} L {startX + 5} {startY + 7} L {startX + 3} {startY + 3} L {startX + 7} {startY + 4} Z"));
                    var redPen = new Pen(new SolidColorBrush(0xFF5533FF), 1f);
                    var orangeBrush = new SolidColorBrush(0xFF9900FF);
                    context.DrawPath(orangeBrush, redPen, burst);
                    
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
                context.DrawText(arrow, font, 10f, translucentHeavyBrush, new Vector2(Size.X - 24f, (Size.Y - 10f) / 2f));
            }
        }

        base.OnRender(context);
    }
}
