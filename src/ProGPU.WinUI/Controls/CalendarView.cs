using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Controls.Primitives;
using System;
using System.Numerics;
using ProGPU.Layout;
using ProGPU.Scene;
using ProGPU.Vector;

namespace Microsoft.UI.Xaml.Controls;

public enum CalendarViewDisplayMode
{
    Month = 0,
    Year = 1,
    Decade = 2
}

public class CalendarView : Control
{
    public CalendarViewTemplateSettings TemplateSettings { get; } = new();

    public static readonly DependencyProperty CalendarItemForegroundProperty =
        DependencyProperty.Register(
            nameof(CalendarItemForeground),
            typeof(Brush),
            typeof(CalendarView),
            new PropertyMetadata(null) { AffectsRender = true });

    private static readonly DateTimeOffset DefaultMinDate = DateTimeOffset.Now.AddYears(-100);
    private static readonly DateTimeOffset DefaultMaxDate = DateTimeOffset.Now.AddYears(100);

    public static readonly DependencyProperty MinDateProperty =
        DependencyProperty.Register(
            nameof(MinDate), typeof(DateTimeOffset), typeof(CalendarView),
            new PropertyMetadata(DefaultMinDate, OnDateRangeChanged) { AffectsRender = true });

    public static readonly DependencyProperty MaxDateProperty =
        DependencyProperty.Register(
            nameof(MaxDate), typeof(DateTimeOffset), typeof(CalendarView),
            new PropertyMetadata(DefaultMaxDate, OnDateRangeChanged) { AffectsRender = true });

    public static readonly DependencyProperty CalendarIdentifierProperty =
        DependencyProperty.Register(
            nameof(CalendarIdentifier), typeof(string), typeof(CalendarView),
            new PropertyMetadata("GregorianCalendar") { AffectsMeasure = true, AffectsRender = true });

    public static readonly DependencyProperty DayOfWeekFormatProperty =
        DependencyProperty.Register(
            nameof(DayOfWeekFormat), typeof(string), typeof(CalendarView),
            new PropertyMetadata("{dayofweek.abbreviated(2)}") { AffectsMeasure = true, AffectsRender = true });

    public static readonly DependencyProperty FirstDayOfWeekProperty =
        DependencyProperty.Register(
            nameof(FirstDayOfWeek), typeof(DayOfWeek), typeof(CalendarView),
            new PropertyMetadata(DayOfWeek.Sunday) { AffectsMeasure = true, AffectsRender = true });

    public static readonly DependencyProperty DisplayModeProperty =
        DependencyProperty.Register(
            nameof(DisplayMode), typeof(CalendarViewDisplayMode), typeof(CalendarView),
            new PropertyMetadata(CalendarViewDisplayMode.Month) { AffectsMeasure = true, AffectsRender = true });

    public static readonly DependencyProperty IsTodayHighlightedProperty =
        DependencyProperty.Register(
            nameof(IsTodayHighlighted), typeof(bool), typeof(CalendarView),
            new PropertyMetadata(true) { AffectsRender = true });

    public static readonly DependencyProperty IsOutOfScopeEnabledProperty =
        DependencyProperty.Register(
            nameof(IsOutOfScopeEnabled), typeof(bool), typeof(CalendarView),
            new PropertyMetadata(true) { AffectsRender = true });

    public static readonly DependencyProperty IsGroupLabelVisibleProperty =
        DependencyProperty.Register(
            nameof(IsGroupLabelVisible), typeof(bool), typeof(CalendarView),
            new PropertyMetadata(false) { AffectsMeasure = true, AffectsRender = true });

    private DateTime _displayDate = DateTime.Today;
    private DateTime? _selectedDate = DateTime.Today;
    private int _hoveredDayIndex = -1; // Index in 0..41 grid

    // Buttons for month navigation
    private Rect _prevBtnRect;
    private Rect _nextBtnRect;
    private bool _isPrevHovered;
    private bool _isNextHovered;

    public DateTimeOffset MinDate
    {
        get => (DateTimeOffset)(GetValue(MinDateProperty) ?? DefaultMinDate);
        set
        {
            if (value > MaxDate)
                throw new ArgumentOutOfRangeException(nameof(value), value, "MinDate cannot be later than MaxDate.");
            SetValue(MinDateProperty, value);
        }
    }

    public DateTimeOffset MaxDate
    {
        get => (DateTimeOffset)(GetValue(MaxDateProperty) ?? DefaultMaxDate);
        set
        {
            if (value < MinDate)
                throw new ArgumentOutOfRangeException(nameof(value), value, "MaxDate cannot be earlier than MinDate.");
            SetValue(MaxDateProperty, value);
        }
    }

    public string CalendarIdentifier
    {
        get => GetValue(CalendarIdentifierProperty) as string ?? "GregorianCalendar";
        set => SetValue(CalendarIdentifierProperty, value ?? "GregorianCalendar");
    }

    public string DayOfWeekFormat
    {
        get => GetValue(DayOfWeekFormatProperty) as string ?? "{dayofweek.abbreviated(2)}";
        set => SetValue(DayOfWeekFormatProperty, value ?? string.Empty);
    }

    public DayOfWeek FirstDayOfWeek
    {
        get => (DayOfWeek)(GetValue(FirstDayOfWeekProperty) ?? DayOfWeek.Sunday);
        set => SetValue(FirstDayOfWeekProperty, value);
    }

    public CalendarViewDisplayMode DisplayMode
    {
        get => (CalendarViewDisplayMode)(GetValue(DisplayModeProperty) ?? CalendarViewDisplayMode.Month);
        set => SetValue(DisplayModeProperty, value);
    }

    public bool IsTodayHighlighted
    {
        get => (bool)(GetValue(IsTodayHighlightedProperty) ?? true);
        set => SetValue(IsTodayHighlightedProperty, value);
    }

    public bool IsOutOfScopeEnabled
    {
        get => (bool)(GetValue(IsOutOfScopeEnabledProperty) ?? true);
        set => SetValue(IsOutOfScopeEnabledProperty, value);
    }

    public bool IsGroupLabelVisible
    {
        get => (bool)(GetValue(IsGroupLabelVisibleProperty) ?? false);
        set => SetValue(IsGroupLabelVisibleProperty, value);
    }

    public Brush? CalendarItemForeground
    {
        get => GetValue(CalendarItemForegroundProperty) as Brush;
        set => SetValue(CalendarItemForegroundProperty, value);
    }

    public DateTime DisplayDate
    {
        get => _displayDate;
        set
        {
            _displayDate = ClampToDateRange(value);
            Invalidate();
        }
    }

    public DateTime? SelectedDate
    {
        get => _selectedDate;
        set
        {
            if (_selectedDate != value)
            {
                var clampedValue = value.HasValue ? ClampToDateRange(value.Value) : (DateTime?)null;
                _selectedDate = clampedValue;
                if (clampedValue.HasValue)
                    _displayDate = clampedValue.Value;
                Invalidate();
                SelectedDatesChanged?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    public event EventHandler? SelectedDatesChanged;

    private static void OnDateRangeChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs args)
    {
        _ = args;
        var calendar = (CalendarView)dependencyObject;
        calendar._displayDate = calendar.ClampToDateRange(calendar._displayDate);
        if (calendar._selectedDate.HasValue)
            calendar._selectedDate = calendar.ClampToDateRange(calendar._selectedDate.Value);
        calendar.Invalidate();
    }

    private DateTime ClampToDateRange(DateTime value)
    {
        var minimum = MinDate.LocalDateTime;
        var maximum = MaxDate.LocalDateTime;
        return value < minimum ? minimum : value > maximum ? maximum : value;
    }

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

    private Rect LogicalToPhysical(Rect rect) =>
        FlowDirection == FlowDirection.RightToLeft
            ? new Rect(Size.X - rect.Right, rect.Y, rect.Width, rect.Height)
            : rect;

    private ProGPU.Text.TextShapingOptions GetTextShapingOptions() =>
        ProGPU.Text.TextShapingOptions.Default.WithDirection(
            FlowDirection == FlowDirection.RightToLeft
                ? ProGPU.Text.Shaping.ShapingDirection.RightToLeft
                : ProGPU.Text.Shaping.ShapingDirection.LeftToRight);

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
                if (targetDate < MinDate.LocalDateTime || targetDate > MaxDate.LocalDateTime)
                    break;
                if (!IsOutOfScopeEnabled && targetDate.Month != _displayDate.Month)
                    break;
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
        int daysBack = ((int)firstOfMonth.DayOfWeek - (int)FirstDayOfWeek + 7) % 7;
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
            CornerRadius.RenderingRadius
        );

        // 2. Render month navigation header bar
        // Arrow button rectangles
        float arrowY = 10f;
        _prevBtnRect = new Rect(Size.X - 68f, arrowY, 24f, 24f);
        _nextBtnRect = new Rect(Size.X - 36f, arrowY, 24f, 24f);
        Rect physicalPrevRect = LogicalToPhysical(_prevBtnRect);
        Rect physicalNextRect = LogicalToPhysical(_nextBtnRect);

        string monthTitle = _displayDate.ToString("MMMM yyyy");
        Rect logicalTitleBounds = new Rect(16f, 12f, Math.Max(0f, _prevBtnRect.X - 24f), 14f);
        Rect titleBounds = LogicalToPhysical(logicalTitleBounds);
        context.DrawText(
            monthTitle,
            font,
            14f,
            Foreground ?? ThemeManager.GetBrush("TextPrimary"),
            new Vector2(titleBounds.X, titleBounds.Y),
            Matrix4x4.Identity,
            titleBounds,
            textShapingOptions: GetTextShapingOptions(),
            textAlignment: FlowDirection == FlowDirection.RightToLeft
                ? ProGPU.Text.TextAlignment.Right
                : ProGPU.Text.TextAlignment.Left);

        // Prev month button. Use retained vector strokes rather than a font-dependent glyph.
        var prevBrush = _isPrevHovered ? ThemeManager.GetBrush("ControlBackgroundHover") : ThemeManager.GetBrush("ControlBackground");
        context.DrawRoundedRectangle(prevBrush, null, physicalPrevRect, 4f);

        // Next month button.
        var nextBrush = _isNextHovered ? ThemeManager.GetBrush("ControlBackgroundHover") : ThemeManager.GetBrush("ControlBackground");
        context.DrawRoundedRectangle(nextBrush, null, physicalNextRect, 4f);
        var arrowPen = new Pen(Foreground ?? ThemeManager.GetBrush("TextPrimary"), 1.5f);
        bool isRtl = FlowDirection == FlowDirection.RightToLeft;
        DrawNavigationChevron(context, arrowPen, physicalPrevRect, pointsRight: isRtl);
        DrawNavigationChevron(context, arrowPen, physicalNextRect, pointsRight: !isRtl);

        // 3. Render day-of-week header column names
        float cellW, cellH;
        var dayRects = GetDayRects(out cellW, out cellH);
        
        for (int i = 0; i < 7; i++)
        {
            var day = (DayOfWeek)(((int)FirstDayOfWeek + i) % 7);
            var dayName = System.Globalization.CultureInfo.CurrentCulture.DateTimeFormat.GetAbbreviatedDayName(day);
            if (dayName.Length > 2 && DayOfWeekFormat.Contains("abbreviated(2)", StringComparison.Ordinal))
                dayName = dayName[..2];
            Rect physicalCell = LogicalToPhysical(dayRects[i]);
            float headerX = physicalCell.X + (physicalCell.Width - 14f) / 2f;
            context.DrawText(dayName, font, 11f, ThemeManager.GetBrush("TextSecondary"), new Vector2(headerX, 48f));
        }

        // Horizontal separator line under day names (1px thin rectangle)
        context.DrawRectangle(ThemeManager.GetBrush("ControlBorder"), null, new Rect(8f, 64f, Size.X - 16f, 1f));

        // 4. Render month days grid
        var firstOfMonth = new DateTime(_displayDate.Year, _displayDate.Month, 1);
        int currentMonth = _displayDate.Month;

        for (int i = 0; i < 42; i++)
        {
            var cellRect = LogicalToPhysical(dayRects[i]);
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
            else if (isToday && IsTodayHighlighted)
            {
                // Accent border for today
                var pen = new Pen(ThemeManager.GetBrush("SystemAccentColor"), 1f);
                context.DrawRoundedRectangle(null, pen, cellHighlightRect, 4f);
            }

            // Foreground text color
            Brush textBrush;
            if (isSelected)
            {
                textBrush = ThemeManager.GetBrush("CardBackground", ElementTheme.Light);
            }
            else if (isCurrentMonth || !IsOutOfScopeEnabled)
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

    private static void DrawNavigationChevron(DrawingContext context, Pen pen, Rect rect, bool pointsRight)
    {
        float direction = pointsRight ? 1f : -1f;
        var center = new Vector2(rect.X + rect.Width * 0.5f, rect.Y + rect.Height * 0.5f);
        context.DrawLine(pen, new Vector2(center.X - direction * 2.5f, center.Y - 4f), center);
        context.DrawLine(pen, center, new Vector2(center.X - direction * 2.5f, center.Y + 4f));
    }
}
