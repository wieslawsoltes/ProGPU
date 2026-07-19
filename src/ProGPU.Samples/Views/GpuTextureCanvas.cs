using System;
using System.Numerics;
using ProGPU.Backend;
using ProGPU.Scene;
using ProGPU.Vector;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Documents;

namespace ProGPU.Samples;

public class GpuTextureCanvas : FrameworkElement
{
    private readonly GpuTexture _source;
    private readonly GpuTexture _shadow;
    private readonly GpuTexture _blur;

    public GpuTextureCanvas(GpuTexture source, GpuTexture shadow, GpuTexture blur)
    {
        _source = source;
        _shadow = shadow;
        _blur = blur;
        HorizontalAlignment = HorizontalAlignment.Stretch;
        VerticalAlignment = VerticalAlignment.Stretch;
    }

    public override void OnRender(DrawingContext context)
    {
        context.DrawRectangle(new SolidColorBrush(0x0C0C12FF), null, new Rect(Vector2.Zero, Size));

        Rect r = new Rect(Vector2.Zero, Size);

        if (AppState.GetShadowRadius() > 0)
        {
            context.DrawTexture(_shadow, r);
        }

        context.DrawTexture(_source, r);

        if (AppState.GetBlurRadius() > 0)
        {
            float cardW = Math.Min(310f, Size.X * 0.8f);
            float cardH = Math.Min(180f, Size.Y * 0.6f);
            float cardX = (Size.X - cardW) / 2f;
            float cardY = (Size.Y - cardH) / 2f;
            Rect cardRect = new Rect(cardX, cardY, cardW, cardH);

            context.PushClip(cardRect);
            context.DrawTexture(_blur, r);

            var glassBg = new SolidColorBrush(0xFFFFFF15);
            var glassBorder = new Pen(new SolidColorBrush(0xFFFFFF35), 1.2f);
            context.DrawRectangle(glassBg, glassBorder, cardRect);

            context.PopClip();

            var font = AppState.GetFont();
            if (font != null)
            {
                context.DrawText("FROSTED ACROSS GLASS", font, 13f, new SolidColorBrush(0x00E5FFFF), new Vector2(cardX + 20f, cardY + 30f));
                context.DrawText("Dual-pass horizontal + vertical", font, 11f, new SolidColorBrush(0xE0E0E0FF), new Vector2(cardX + 20f, cardY + 60f));
                context.DrawText("Backdrop compute blur filter dispatches", font, 10f, new SolidColorBrush(0x888899FF), new Vector2(cardX + 20f, cardY + 85f));
                
                context.DrawText($"Blur Radius: {AppState.GetBlurRadius():F1} px", font, 10f, new SolidColorBrush(0x00FF88FF), new Vector2(cardX + 20f, cardY + 115f));
                context.DrawText($"Shadow Radius: {AppState.GetShadowRadius():F1} px", font, 10f, new SolidColorBrush(0xFF5588FF), new Vector2(cardX + 160f, cardY + 115f));
            }
        }
    }
}
