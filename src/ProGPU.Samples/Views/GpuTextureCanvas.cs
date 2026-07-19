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
    private readonly SolidColorBrush _backgroundBrush = new(0x0C0C12FF);
    private readonly SolidColorBrush _glassBackgroundBrush = new(0xFFFFFF15);
    private readonly Pen _glassBorderPen = new(new SolidColorBrush(0xFFFFFF35), 1.2f);
    private readonly SolidColorBrush _titleBrush = new(0x00E5FFFF);
    private readonly SolidColorBrush _primaryTextBrush = new(0xE0E0E0FF);
    private readonly SolidColorBrush _secondaryTextBrush = new(0x888899FF);
    private readonly SolidColorBrush _blurTextBrush = new(0x00FF88FF);
    private readonly SolidColorBrush _shadowTextBrush = new(0xFF5588FF);
    private readonly GpuTexture _source;
    private readonly GpuTexture _shadow;
    private readonly GpuTexture _blur;
    private float _displayedBlurRadius = float.NaN;
    private float _displayedShadowRadius = float.NaN;
    private string _blurRadiusText = string.Empty;
    private string _shadowRadiusText = string.Empty;

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
        context.DrawRectangle(_backgroundBrush, null, new Rect(Vector2.Zero, Size));

        Rect r = new Rect(Vector2.Zero, Size);
        float shadowRadius = AppState.GetShadowRadius();
        float blurRadius = AppState.GetBlurRadius();

        if (shadowRadius > 0)
        {
            context.DrawTexture(_shadow, r);
        }

        context.DrawTexture(_source, r);

        if (blurRadius > 0)
        {
            float cardW = Math.Min(310f, Size.X * 0.8f);
            float cardH = Math.Min(180f, Size.Y * 0.6f);
            float cardX = (Size.X - cardW) / 2f;
            float cardY = (Size.Y - cardH) / 2f;
            Rect cardRect = new Rect(cardX, cardY, cardW, cardH);

            context.PushClip(cardRect);
            context.DrawTexture(_blur, r);

            context.DrawRectangle(_glassBackgroundBrush, _glassBorderPen, cardRect);

            context.PopClip();

            var font = AppState.GetFont();
            if (font != null)
            {
                if (_displayedBlurRadius != blurRadius)
                {
                    _displayedBlurRadius = blurRadius;
                    _blurRadiusText = $"Blur Radius: {blurRadius:F1} px";
                }
                if (_displayedShadowRadius != shadowRadius)
                {
                    _displayedShadowRadius = shadowRadius;
                    _shadowRadiusText = $"Shadow Radius: {shadowRadius:F1} px";
                }

                context.DrawText("FROSTED ACROSS GLASS", font, 13f, _titleBrush, new Vector2(cardX + 20f, cardY + 30f));
                context.DrawText("Dual-pass horizontal + vertical", font, 11f, _primaryTextBrush, new Vector2(cardX + 20f, cardY + 60f));
                context.DrawText("Backdrop compute blur filter dispatches", font, 10f, _secondaryTextBrush, new Vector2(cardX + 20f, cardY + 85f));
                context.DrawText(_blurRadiusText, font, 10f, _blurTextBrush, new Vector2(cardX + 20f, cardY + 115f));
                context.DrawText(_shadowRadiusText, font, 10f, _shadowTextBrush, new Vector2(cardX + 160f, cardY + 115f));
            }
        }
    }
}
