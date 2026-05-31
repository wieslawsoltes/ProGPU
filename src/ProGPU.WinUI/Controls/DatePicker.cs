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

public class DatePicker : Control
{
    private DateTime? _selectedDate;
    private string _header = "Select Date";
    private CalendarView? _popupCalendar;
    private bool _isHovered;

    public DateTime? SelectedDate
    {
        get => _selectedDate;
        set
        {
            if (_selectedDate != value)
            {
                _selectedDate = value;
                Invalidate();
                SelectedDateChanged?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    public string Header
    {
        get => _header;
        set { _header = value; Invalidate(); }
    }

    public event EventHandler? SelectedDateChanged;

    public DatePicker()
    {
        WidthConstraint = 180f;
        HeightConstraint = 32f;
        CornerRadius = 4f;
        Padding = new Thickness(12f, 0f, 12f, 0f);

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

    private Vector2 GetAbsolutePosition()
    {
        Vector2 pos = Offset;
        Visual? current = Parent;
        while (current != null)
        {
            pos += current.Offset;
            current = current.Parent;
        }
        return pos;
    }

    public override void OnPointerMoved(PointerRoutedEventArgs e)
    {
        _isHovered = new Rect(Vector2.Zero, Size).Contains(e.Position);
        base.OnPointerMoved(e);
    }

    public override void OnPointerPressed(PointerRoutedEventArgs e)
    {
        if (e.Position.X >= 0 && e.Position.X <= Size.X && e.Position.Y >= 0 && e.Position.Y <= Size.Y)
        {
            // Spawn calendar dropdown
            if (_popupCalendar == null)
            {
                _popupCalendar = new CalendarView();
                _popupCalendar.SelectedDatesChanged += (s, ev) =>
                {
                    SelectedDate = _popupCalendar.SelectedDate;
                    PopupService.HidePopup(_popupCalendar);
                };
            }

            _popupCalendar.SelectedDate = SelectedDate ?? DateTime.Today;

            var absPos = GetAbsolutePosition();
            // Force theme synchronization right before showing the popup
            _popupCalendar.NotifyThemeChanged();
            // Position exactly underneath the DatePicker input box
            PopupService.ShowPopup(_popupCalendar, new Vector2(absPos.X, absPos.Y + Size.Y + 4f), this);
            e.Handled = true;
        }
        base.OnPointerPressed(e);
    }

    public override void OnRender(DrawingContext context)
    {
        var font = PopupService.DefaultFont;
        if (font == null)
        {
            base.OnRender(context);
            return;
        }

        // 1. Draw input box background and border outline
        var rect = new Rect(Vector2.Zero, Size);
        
        var bg = GetCurrentBackground() ?? ThemeManager.GetBrush("ControlBackground");
        var borderBrush = GetCurrentBorderBrush() ?? ThemeManager.GetBrush("ControlBorder");
        var borderPen = new Pen(borderBrush, BorderThickness.Left > 0 ? BorderThickness.Left : 1f);
            
        context.DrawRoundedRectangle(bg, borderPen, rect, CornerRadius);

        // 2. Render selected date label or placeholder text
        string dateText = SelectedDate.HasValue 
            ? SelectedDate.Value.ToString("yyyy-MM-dd") 
            : "Select a date...";
            
        var textBrush = SelectedDate.HasValue 
            ? (Foreground ?? ThemeManager.GetBrush("TextPrimary")) 
            : ThemeManager.GetBrush("TextSecondary");

        float textY = (Size.Y - 14f) / 2f;
        context.DrawText(dateText, font, 14f, textBrush, new Vector2(Padding.Left, textY));

        // 3. Render modern vector calendar icon "📅" on the right side
        float iconX = Size.X - 26f;
        float iconY = (Size.Y - 14f) / 2f;
        context.DrawText("📅", font, 11f, ThemeManager.GetBrush("TextSecondary"), new Vector2(iconX, iconY));

        base.OnRender(context);
    }
}
