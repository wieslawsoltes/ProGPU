using System.Numerics;
using System.Globalization;
using ProGPU.Text;
using Microsoft.UI.Xaml;
using ProGPU.Hmi;
using ProGPU.Scene;
using ProGPU.Vector;
using ProGPU.WinUI.Designer;

namespace ProGPU.WinUI.Hmi.Designer;

internal sealed class HmiDesignerCanvas : DesignerCanvas
{
    internal HmiColorScheme ColorScheme { get; set; }
    internal HmiLinkLayer DiagramLayer { get; } = new();
    internal bool IsConnecting { get; set; }
    internal Action<HmiLinkEndpoint>? PortPicked { get; set; }
    internal Action<string?>? LinkPicked { get; set; }
    internal Action? GeometryPreviewChanged { get; set; }
    internal Func<PointerRoutedEventArgs, bool>? RoutePointerPressed { get; set; }
    internal Func<PointerRoutedEventArgs, bool>? RoutePointerMoved { get; set; }
    internal Func<PointerRoutedEventArgs, bool>? RoutePointerReleased { get; set; }
    internal Action<PointerRoutedEventArgs>? RoutePointerCanceled { get; set; }
    public HmiDesignerCanvas() => DesignSurface.Children.Add(DiagramLayer);

    public override void OnPointerPressed(PointerRoutedEventArgs e)
    {
        if (RoutePointerPressed?.Invoke(e) == true) { e.Handled = true; return; }
        if (!IsInteractionMode && e.IsLeftButtonPressed && !e.IsMiddleButtonPressed && !e.IsRightButtonPressed)
        {
            var logical = (e.Position - PanOffset) / ZoomScale;
            var point = new HmiPoint(logical.X, logical.Y);
            if (IsConnecting)
            {
                var endpoint = DiagramLayer.HitPort(point, 10 / ZoomScale);
                if (endpoint != null) PortPicked?.Invoke(endpoint);
                e.Handled = true;
                return;
            }
            string? link = DiagramLayer.HitLink(point, 6 / ZoomScale);
            LinkPicked?.Invoke(link);
            if (link != null) { e.Handled = true; return; }
        }
        base.OnPointerPressed(e);
    }
    internal TtfFont? RulerFont { get; set; }
    internal bool ShowRulers { get; set; } = true;
    private static readonly Pen LightBorder = new(HmiThemeResources.GetBrush(HmiColorScheme.Light, HmiBrushRole.Border), 1);
    private static readonly Pen DarkBorder = new(HmiThemeResources.GetBrush(HmiColorScheme.Dark, HmiBrushRole.Border), 1);
    private static readonly Pen ContrastBorder = new(HmiThemeResources.GetBrush(HmiColorScheme.HighContrast, HmiBrushRole.Text), 1);

    public override void OnPointerMoved(PointerRoutedEventArgs e)
    {
        if (RoutePointerMoved?.Invoke(e) == true) { e.Handled = true; return; }
        if (SelectedElement is HmiControl { IsDesignLocked: true } && e.IsLeftButtonPressed && !e.IsMiddleButtonPressed) return;
        base.OnPointerMoved(e);
        if (!IsInteractionMode && !IsConnecting && e.IsLeftButtonPressed && !e.IsMiddleButtonPressed)
            GeometryPreviewChanged?.Invoke();
    }

    public override void OnPointerReleased(PointerRoutedEventArgs e)
    {
        if (RoutePointerReleased?.Invoke(e) == true) { e.Handled = true; return; }
        base.OnPointerReleased(e);
    }
    public override void OnPointerCanceled(PointerRoutedEventArgs e)
    {
        RoutePointerCanceled?.Invoke(e);
        base.OnPointerCanceled(e);
    }
    public override void OnPointerCaptureLost(PointerRoutedEventArgs e)
    {
        RoutePointerCanceled?.Invoke(e);
        base.OnPointerCaptureLost(e);
    }

    public override void OnRender(DrawingContext context)
    {
        base.OnRender(context);
        // Draw the screen boundary in the same pan/zoom frame as the shared canvas.
        // It is not a fake child and never enters selection, ordering or saved project state.
        float width = DesignSurface.Width * ZoomScale;
        float height = DesignSurface.Height * ZoomScale;
        if (!float.IsFinite(width) || !float.IsFinite(height) || width <= 0 || height <= 0) return;
        var pen = ColorScheme switch { HmiColorScheme.Dark => DarkBorder, HmiColorScheme.HighContrast => ContrastBorder, _ => LightBorder };
        context.DrawRectangle(null, pen, new Rect(PanOffset.X - 1, PanOffset.Y - 1, width + 2, height + 2));
        if (ShowRulers && RulerFont != null && ZoomScale > 0 && float.IsFinite(ZoomScale) && float.IsFinite(PanOffset.X) && float.IsFinite(PanOffset.Y))
        {
            var background = HmiThemeResources.GetBrush(ColorScheme, HmiBrushRole.Workspace);
            var foreground = HmiThemeResources.GetBrush(ColorScheme, HmiBrushRole.Muted);
            context.DrawRectangle(background, null, new Rect(0, 0, Size.X, 18));
            context.DrawRectangle(background, null, new Rect(0, 0, 18, Size.Y));
            double target = 80 / ZoomScale;
            double order = Math.Pow(10, Math.Floor(Math.Log10(target)));
            double step = order * (target / order <= 2 ? 2 : target / order <= 5 ? 5 : 10);
            void Axis(bool horizontal)
            {
                double pan = horizontal ? PanOffset.X : PanOffset.Y;
                float length = horizontal ? Size.X : Size.Y;
                double first = Math.Ceiling((18 - pan) / (step * ZoomScale));
                for (int i = 0; i < 256; i++)
                {
                    double value = (first + i) * step;
                    float position = (float)(value * ZoomScale + pan);
                    if (position > length) break;
                    if (position < 18) continue;
                    if (horizontal)
                    {
                        context.DrawLine(pen, new(position, 13), new(position, 18));
                        context.DrawText(value.ToString("0.##", CultureInfo.InvariantCulture), RulerFont, 9, foreground, new(position + 3, 1));
                    }
                    else
                    {
                        context.DrawLine(pen, new(13, position), new(18, position));
                        context.DrawText(value.ToString("0.##", CultureInfo.InvariantCulture), RulerFont, 8, foreground, new(1, position + 3));
                    }
                }
            }
            Axis(true); Axis(false);
        }
    }
}
