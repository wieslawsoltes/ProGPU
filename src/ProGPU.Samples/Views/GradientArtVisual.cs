using System;
using System.Numerics;
using ProGPU.Scene;
using ProGPU.Vector;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Documents;

namespace ProGPU.Samples;

public class GradientArtVisual : FrameworkElement, IAnimatedElement
{
    private readonly SolidColorBrush _backgroundBrush = new(0x0C0C12FF);
    private readonly Pen _cardBorderPen = new(new SolidColorBrush(0xFFFFFFFF), 2f);
    private readonly Pen _blobBorderPen = new(new SolidColorBrush(0xFFFFFF33), 1.5f);
    private readonly LinearGradientBrush _cardGradient = new(
        new Vector2(20f, 20f),
        new Vector2(240f, 200f),
        [
            new GradientStop(new Vector4(1f, 0f, 0.5f, 1f), 0.0f),
            new GradientStop(new Vector4(0f, 0.5f, 1f, 0.8f), 0.5f),
            new GradientStop(new Vector4(0f, 1f, 0.5f, 1f), 1.0f)
        ]);
    private readonly RadialGradientBrush _starGradient = new(
        new Vector2(100f, 100f),
        80f,
        [
            new GradientStop(new Vector4(1f, 0.9f, 0.1f, 1f), 0.0f),
            new GradientStop(new Vector4(1f, 0.4f, 0.0f, 0.9f), 0.5f),
            new GradientStop(new Vector4(0f, 0f, 0f, 0f), 1.0f)
        ]);
    private readonly LinearGradientBrush _blobGradient = new(
        new Vector2(150f, 50f),
        new Vector2(250f, 250f),
        [
            new GradientStop(new Vector4(0.2f, 1.0f, 1.0f, 0.8f), 0.0f),
            new GradientStop(new Vector4(0.8f, 0.2f, 1.0f, 0.7f), 1.0f)
        ]);
    private float _time = 0f;
    private readonly PathGeometry _starPath;
    private readonly PathGeometry _blobPath;

    public GradientArtVisual()
    {
        // Parse a beautiful star/gear path and a blob path
        _starPath = PathGeometry.Parse("M 100 10 L 125 70 L 190 75 L 140 120 L 155 185 L 100 150 L 45 185 L 60 120 L 10 75 L 75 70 Z");
        _blobPath = PathGeometry.Parse("M 150 150 C 250 50 350 250 250 250 C 150 250 50 350 150 150 Z");
        
        HorizontalAlignment = HorizontalAlignment.Stretch;
        VerticalAlignment = VerticalAlignment.Stretch;
        Height = 220f;
    }

    public void Update(float delta)
    {
        _time += delta;
        Invalidate();
    }

    public override void OnRender(DrawingContext context)
    {
        context.DrawRectangle(_backgroundBrush, null, new Rect(Vector2.Zero, Size));

        // 1. Overlapping Multi-stop Linear Gradient Card
        var startPt = new Vector2(20f + MathF.Sin(_time) * 10f, 20f);
        var endPt = new Vector2(240f + MathF.Cos(_time) * 10f, 200f);
        _cardGradient.StartPoint = startPt;
        _cardGradient.EndPoint = endPt;
        context.DrawRectangle(_cardGradient, _cardBorderPen, new Rect(20, 20, 220, 180));

        // 2. Overlapping Multi-stop Radial Gradient star path
        var centerPt = new Vector2(100f + MathF.Sin(_time * 2f) * 20f, 100f + MathF.Cos(_time * 2f) * 20f);
        _starGradient.Center = centerPt;
        _starGradient.GradientOrigin = centerPt;
        
        context.PushClip(new Rect(10, 10, Size.X - 20, Size.Y - 20));
        
        // Draw Radial Gradient star
        context.DrawPath(_starGradient, null, _starPath);
        
        // Draw overlapping linear gradient blob
        context.DrawPath(_blobGradient, _blobBorderPen, _blobPath);

        context.PopClip();
    }
}
