using System.Numerics;
using ProGPU.Scene;
using ProGPU.Vector;

namespace Microsoft.UI.Xaml.Controls;

/// <summary>Retained, directly rendered container for one date in a CalendarView.</summary>
public class CalendarViewDayItem : Control
{
    private readonly List<Brush> _densityBrushes = new();

    public static readonly DependencyProperty DateProperty = DependencyProperty.Register(
        nameof(Date), typeof(DateTimeOffset), typeof(CalendarViewDayItem),
        new PropertyMetadata(default(DateTimeOffset)) { AffectsRender = true });

    public static readonly DependencyProperty IsBlackoutProperty = DependencyProperty.Register(
        nameof(IsBlackout), typeof(bool), typeof(CalendarViewDayItem),
        new PropertyMetadata(false) { AffectsRender = true });

    public DateTimeOffset Date => (DateTimeOffset)(GetValue(DateProperty) ?? default(DateTimeOffset));

    public bool IsBlackout
    {
        get => (bool)(GetValue(IsBlackoutProperty) ?? false);
        set => SetValue(IsBlackoutProperty, value);
    }

    internal void SetDate(DateTimeOffset date) => SetValue(DateProperty, date);

    public void SetDensityColors(IEnumerable<Windows.UI.Color> colors)
    {
        ArgumentNullException.ThrowIfNull(colors);
        _densityBrushes.Clear();
        foreach (var color in colors)
        {
            _densityBrushes.Add(new SolidColorBrush(new Vector4(
                color.R / 255f,
                color.G / 255f,
                color.B / 255f,
                color.A / 255f)));
        }
        Invalidate();
    }

    public override void OnRender(DrawingContext context)
    {
        var bounds = new Rect(Vector2.Zero, Size);
        if (Background != null || BorderBrush != null)
        {
            var pen = BorderBrush != null && BorderThickness.Left > 0
                ? new Pen(BorderBrush, BorderThickness.Left)
                : null;
            context.DrawRoundedRectangle(Background, pen, bounds, CornerRadius.RenderingRadius);
        }

        var font = Font ?? PopupService.DefaultFont;
        if (font != null && Date != default)
        {
            var foreground = IsBlackout
                ? ThemeManager.GetBrush("TextDisabled")
                : Foreground ?? ThemeManager.GetBrush("TextPrimary");
            context.DrawText(Date.Day.ToString(global::System.Globalization.CultureInfo.CurrentCulture), font,
                (float)FontSize, foreground, new Vector2(Padding.Left, Padding.Top));
        }

        if (_densityBrushes.Count > 0)
        {
            var width = Math.Max(1f, (Size.X - 4f) / _densityBrushes.Count);
            for (var index = 0; index < _densityBrushes.Count; index++)
                context.DrawRectangle(_densityBrushes[index], null,
                    new Rect(2f + index * width, Math.Max(0f, Size.Y - 3f), width, 2f));
        }

        base.OnRender(context);
    }
}
