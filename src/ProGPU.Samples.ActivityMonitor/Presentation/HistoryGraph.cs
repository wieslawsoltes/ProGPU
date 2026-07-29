using System.Numerics;
using Microsoft.UI.Xaml;
using ProGPU.Scene;
using ProGPU.Vector;

namespace ProGPU.Samples.ActivityMonitor.Presentation;

internal sealed class HistoryGraph : FrameworkElement
{
    private const int Capacity = 120;
    private readonly float[] _primary = new float[Capacity];
    private readonly float[] _secondary = new float[Capacity];
    private readonly Vector2[] _primaryPoints = new Vector2[Capacity];
    private readonly Vector2[] _secondaryPoints = new Vector2[Capacity];
    private int _count;

    public HistoryGraph()
    {
        MinHeight = 96;
        HorizontalAlignment = HorizontalAlignment.Stretch;
        VerticalAlignment = VerticalAlignment.Stretch;
    }

    public void Append(double primary, double secondary = 0)
    {
        if (_count < Capacity)
        {
            _primary[_count] = SafeFloat(primary);
            _secondary[_count] = SafeFloat(secondary);
            _count++;
        }
        else
        {
            Array.Copy(_primary, 1, _primary, 0, Capacity - 1);
            Array.Copy(_secondary, 1, _secondary, 0, Capacity - 1);
            _primary[^1] = SafeFloat(primary);
            _secondary[^1] = SafeFloat(secondary);
        }
        Invalidate();
    }

    public override void OnRender(DrawingContext context)
    {
        var bounds = new Rect(Vector2.Zero, Size);
        context.DrawRectangle(
            ThemeManager.GetBrush("CardBackground", ActualTheme, ActualThemeFamily),
            ThemeManager.GetPen("ControlBorder", 1, ActualTheme, ActualThemeFamily),
            bounds);
        if (_count < 2 || Size.X <= 2 || Size.Y <= 2)
        {
            return;
        }

        context.DrawLine(
            ThemeManager.GetPen("ControlBorder", 1, ActualTheme, ActualThemeFamily),
            new Vector2(0, Size.Y * 0.5f),
            new Vector2(Size.X, Size.Y * 0.5f));
        float maximum = 1;
        for (int index = 0; index < _count; index++)
        {
            maximum = Math.Max(maximum, Math.Max(_primary[index], _secondary[index]));
        }

        float xStep = Size.X / Math.Max(1, Capacity - 1);
        float startX = Size.X - (_count - 1) * xStep;
        float height = Math.Max(1, Size.Y - 4);
        for (int index = 0; index < _count; index++)
        {
            float x = startX + index * xStep;
            _primaryPoints[index] = new Vector2(
                x,
                Size.Y - 2 - _primary[index] / maximum * height);
            _secondaryPoints[index] = new Vector2(
                x,
                Size.Y - 2 - _secondary[index] / maximum * height);
        }

        context.PushClip(bounds);
        context.DrawPolyline(
            ThemeManager.GetPen("SystemAccentColor", 1.5f, ActualTheme, ActualThemeFamily),
            _primaryPoints.AsSpan(0, _count));
        context.DrawPolyline(
            ThemeManager.GetPen("TabViewItemCloseHover", 1.5f, ActualTheme, ActualThemeFamily),
            _secondaryPoints.AsSpan(0, _count));
        context.PopClip();
    }

    private static float SafeFloat(double value) =>
        double.IsFinite(value) ? (float)Math.Clamp(value, 0, float.MaxValue) : 0;
}
