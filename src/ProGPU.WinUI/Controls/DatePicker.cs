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
        return Vector2.Transform(Vector2.Zero, GetGlobalTransformMatrix());
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
            _popupCalendar.FlowDirection = FlowDirection;

            var absPos = GetAbsolutePosition();
            // Force theme synchronization right before showing the popup
            _popupCalendar.NotifyThemeChanged();
            // Position exactly underneath the DatePicker input box
            float popupX = FlowDirection == FlowDirection.RightToLeft
                ? absPos.X + Size.X - _popupCalendar.Width
                : absPos.X;
            PopupService.ShowPopup(_popupCalendar, new Vector2(popupX, absPos.Y + Size.Y + 4f), this);
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
        Rect logicalTextBounds = new Rect(
            Padding.Left,
            textY,
            Math.Max(0f, Size.X - Padding.Left - 32f),
            14f);
        Rect textBounds = LogicalToPhysical(logicalTextBounds);
        context.DrawText(
            dateText,
            font,
            14f,
            textBrush,
            new Vector2(textBounds.X, textY),
            Matrix4x4.Identity,
            textBounds,
            textShapingOptions: GetTextShapingOptions(),
            textAlignment: FlowDirection == FlowDirection.RightToLeft
                ? ProGPU.Text.TextAlignment.Right
                : ProGPU.Text.TextAlignment.Left);

        // 3. Render a font-independent calendar icon on the logical trailing side.
        Rect iconRect = LogicalToPhysical(new Rect(Size.X - 25f, 0f, 13f, Size.Y));
        float iconX = iconRect.X;
        float iconY = (Size.Y - 14f) * 0.5f;
        var iconPen = new Pen(ThemeManager.GetBrush("TextSecondary"), 1.25f);
        context.DrawRoundedRectangle(null, iconPen, new Rect(iconX, iconY + 1.5f, 13f, 11f), 1.5f);
        context.DrawLine(iconPen, new Vector2(iconX, iconY + 5f), new Vector2(iconX + 13f, iconY + 5f));
        context.DrawLine(iconPen, new Vector2(iconX + 3.5f, iconY), new Vector2(iconX + 3.5f, iconY + 3.5f));
        context.DrawLine(iconPen, new Vector2(iconX + 9.5f, iconY), new Vector2(iconX + 9.5f, iconY + 3.5f));

        base.OnRender(context);
    }
}
