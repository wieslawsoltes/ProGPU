using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Documents;
using System;
using System.Numerics;
using ProGPU.Layout;
using ProGPU.Scene;
using ProGPU.Vector;

namespace Microsoft.UI.Xaml.Controls;

public class CalendarView : Control
{
    private DateTime _displayDate = DateTime.Today;
    private DateTime? _selectedDate = DateTime.Today;
    private int _hoveredDayIndex = -1; // Index in 0..41 grid

    // Buttons for month navigation
    private Rect _prevBtnRect;
    private Rect _nextBtnRect;
    private bool _isPrevHovered;
    private bool _isNextHovered;

    public DateTime DisplayDate
    {
        get => _displayDate;
        set { _displayDate = value; Invalidate(); }
    }

    public DateTime? SelectedDate
    {
        get => _selectedDate;
        set
        {
            if (_selectedDate != value)
            {
                _selectedDate = value;
                if (value.HasValue)
                {
                    _displayDate = value.Value;
                }
                Invalidate();
                SelectedDatesChanged?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    public event EventHandler? SelectedDatesChanged;

    public CalendarView()
    {
        Width = 240f;
        Height = 270f;
        Background = new ThemeResourceBrush("CardBackground");
        BorderBrush = new ThemeResourceBrush("ControlBorder");
        BorderThickness = new Thickness(1f);
        CornerRadius = 6f;

        var defaultStyle = ThemeManager.GetDefaultStyle(GetType());
        if (defaultStyle != null)
        {
            Style = defaultStyle;
        }
    }

    protected override Vector2 MeasureOverride(Vector2 availableSize)
    {
        return new Vector2(Width, Height);
    }

    private Rect[] GetDayRects(out float cellW, out float cellH)
    {
        float gridY = 70f; // Start of month days grid
        float gridW = Size.X - 16f;
        float gridH = Size.Y - gridY - 8f;
        cellW = gridW / 7f;
        cellH = gridH / 6f;

        var rects = new Rect[42];
        for (int i = 0; i < 42; i++)
        {
            int row = i / 7;
            int col = i % 7;
            float x = 8f + col * cellW;
            float y = gridY + row * cellH;
            rects[i] = new Rect(x, y, cellW, cellH);
        }
        return rects;
    }

    public override void OnPointerMoved(PointerRoutedEventArgs e)
    {
        var localPos = e.Position;
        
        // 1. Check prev/next month button hovers
        bool wasPrevHovered = _isPrevHovered;
        bool wasNextHovered = _isNextHovered;
        _isPrevHovered = _prevBtnRect.Contains(localPos);
        _isNextHovered = _nextBtnRect.Contains(localPos);

        if (wasPrevHovered != _isPrevHovered || wasNextHovered != _isNextHovered)
        {
            Invalidate();
        }

        // 2. Check calendar day grid hovers
        float cellW, cellH;
        var dayRects = GetDayRects(out cellW, out cellH);
        int oldHoverIndex = _hoveredDayIndex;
        _hoveredDayIndex = -1;

        for (int i = 0; i < 42; i++)
        {
            if (dayRects[i].Contains(localPos))
            {
                _hoveredDayIndex = i;
                break;
            }
        }

        if (oldHoverIndex != _hoveredDayIndex)
        {
            Invalidate();
        }

        base.OnPointerMoved(e);
    }

    public override void OnPointerPressed(PointerRoutedEventArgs e)
    {
        var localPos = e.Position;

        // 1. Navigation Button Clicks
        if (_prevBtnRect.Contains(localPos))
        {
            DisplayDate = _displayDate.AddMonths(-1);
            e.Handled = true;
            return;
        }
        if (_nextBtnRect.Contains(localPos))
        {
            DisplayDate = _displayDate.AddMonths(1);
            e.Handled = true;
            return;
        }

        // 2. Day Cell Clicks
        float cellW, cellH;
        var dayRects = GetDayRects(out cellW, out cellH);
        for (int i = 0; i < 42; i++)
        {
            if (dayRects[i].Contains(localPos))
            {
                var targetDate = GetDateForIndex(i);
                SelectedDate = targetDate;
                e.Handled = true;
                break;
            }
        }

        base.OnPointerPressed(e);
    }

    public override void OnPointerReleased(PointerRoutedEventArgs e)
    {
        base.OnPointerReleased(e);
    }

    private DateTime GetDateForIndex(int index)
    {
        var firstOfMonth = new DateTime(_displayDate.Year, _displayDate.Month, 1);
        int dayOfWeekOffset = (int)firstOfMonth.DayOfWeek; // 0 for Sunday
        
        // Days backward to start grid from Sunday
        int daysBack = dayOfWeekOffset; 
        return firstOfMonth.AddDays(index - daysBack);
    }

    public override void OnRender(DrawingContext context)
    {
        var font = PopupService.DefaultFont;
        if (font == null)
        {
            base.OnRender(context);
            return;
        }

        // 1. Render main container card backdrop and border outline
        var rect = new Rect(Vector2.Zero, Size);
        context.DrawRoundedRectangle(
            Background ?? ThemeManager.GetBrush("CardBackground"), 
            new Pen(BorderBrush ?? ThemeManager.GetBrush("ControlBorder"), BorderThickness.Left), 
            rect, 
            CornerRadius
        );

        // 2. Render month navigation header bar
        string monthTitle = _displayDate.ToString("MMMM yyyy");
        context.DrawText(monthTitle, font, 14f, Foreground ?? ThemeManager.GetBrush("TextPrimary"), new Vector2(16f, 12f));

        // Arrow button rectangles
        float arrowY = 10f;
        _prevBtnRect = new Rect(Size.X - 68f, arrowY, 24f, 24f);
        _nextBtnRect = new Rect(Size.X - 36f, arrowY, 24f, 24f);

        // Prev month button (◀)
        var prevBrush = _isPrevHovered ? ThemeManager.GetBrush("ControlBackgroundHover") : ThemeManager.GetBrush("ControlBackground");
        context.DrawRoundedRectangle(prevBrush, null, _prevBtnRect, 4f);
        context.DrawText("◀", font, 10f, Foreground ?? ThemeManager.GetBrush("TextPrimary"), new Vector2(_prevBtnRect.X + 7f, _prevBtnRect.Y + 6f));

        // Next month button (▶)
        var nextBrush = _isNextHovered ? ThemeManager.GetBrush("ControlBackgroundHover") : ThemeManager.GetBrush("ControlBackground");
        context.DrawRoundedRectangle(nextBrush, null, _nextBtnRect, 4f);
        context.DrawText("▶", font, 10f, Foreground ?? ThemeManager.GetBrush("TextPrimary"), new Vector2(_nextBtnRect.X + 7f, _nextBtnRect.Y + 6f));

        // 3. Render day-of-week header column names
        string[] daysOfWeek = { "Su", "Mo", "Tu", "We", "Th", "Fr", "Sa" };
        float cellW, cellH;
        var dayRects = GetDayRects(out cellW, out cellH);
        
        for (int i = 0; i < 7; i++)
        {
            float headerX = 8f + i * cellW + (cellW - 14f) / 2f;
            context.DrawText(daysOfWeek[i], font, 11f, ThemeManager.GetBrush("TextSecondary"), new Vector2(headerX, 48f));
        }

        // Horizontal separator line under day names (1px thin rectangle)
        context.DrawRectangle(ThemeManager.GetBrush("ControlBorder"), null, new Rect(8f, 64f, Size.X - 16f, 1f));

        // 4. Render month days grid
        var firstOfMonth = new DateTime(_displayDate.Year, _displayDate.Month, 1);
        int currentMonth = _displayDate.Month;

        for (int i = 0; i < 42; i++)
        {
            var cellRect = dayRects[i];
            var date = GetDateForIndex(i);

            bool isSelected = SelectedDate.HasValue && SelectedDate.Value.Date == date.Date;
            bool isToday = DateTime.Today.Date == date.Date;
            bool isCurrentMonth = date.Month == currentMonth;
            bool isHovered = _hoveredDayIndex == i;

            // Highlight backgrounds
            var cellHighlightRect = new Rect(cellRect.X + 2f, cellRect.Y + 2f, cellRect.Width - 4f, cellRect.Height - 4f);
            if (isSelected)
            {
                // Active blue solid background
                var fill = ThemeManager.GetBrush("SystemAccentColor");
                context.DrawRoundedRectangle(fill, null, cellHighlightRect, 4f);
            }
            else if (isHovered)
            {
                // Subtle glowing glass card border on hover
                var fill = ThemeManager.GetBrush("ControlBackgroundHover");
                var pen = new Pen(ThemeManager.GetBrush("ControlBorderHover"), 1f);
                context.DrawRoundedRectangle(fill, pen, cellHighlightRect, 4f);
            }
            else if (isToday)
            {
                // Accent border for today
                var pen = new Pen(ThemeManager.GetBrush("SystemAccentColor"), 1f);
                context.DrawRoundedRectangle(null, pen, cellHighlightRect, 4f);
            }

            // Foreground text color
            Brush textBrush;
            if (isSelected)
            {
                textBrush = new SolidColorBrush(new Vector4(1f, 1f, 1f, 1f));
            }
            else if (isCurrentMonth)
            {
                textBrush = ThemeManager.GetBrush("TextPrimary");
            }
            else
            {
                // Muted/translucent grey for days outside the current month boundaries
                textBrush = ThemeManager.GetBrush("TextSecondary");
            }

            string dayText = date.Day.ToString();
            // Center number in cell
            float textOffsetW = dayText.Length == 1 ? 5f : 8f;
            float numX = cellRect.X + (cellRect.Width - textOffsetW - 6f) / 2f;
            float numY = cellRect.Y + (cellRect.Height - 12f) / 2f;

            context.DrawText(dayText, font, 11f, textBrush, new Vector2(numX, numY));
        }

        base.OnRender(context);
    }
}
