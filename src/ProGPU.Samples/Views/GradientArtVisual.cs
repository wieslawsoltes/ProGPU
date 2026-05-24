using System;
using System.Numerics;
using ProGPU.Scene;
using ProGPU.Vector;
using ProGPU.WinUI;

namespace ProGPU.Samples;

public class GradientArtVisual : FrameworkElement, IAnimatedElement
{
    private float _time = 0f;
    private readonly PathGeometry _starPath;
    private readonly PathGeometry _blobPath;

    public GradientArtVisual()
    {
        // Parse a beautiful star/gear path and a blob path
        _starPath = PathGeometry.Parse("M 100 10 L 125 70 L 190 75 L 140 120 L 155 185 L 100 150 L 45 185 L 60 120 L 10 75 L 75 70 Z");
        _blobPath = PathGeometry.Parse("M 150 150 C 250 50 350 250 250 250 C 150 250 50 350 150 150 Z");
        
        HorizontalAlignment = ProGPU.Layout.HorizontalAlignment.Stretch;
        VerticalAlignment = ProGPU.Layout.VerticalAlignment.Stretch;
        Height = 220f;
    }

    public void Update(float delta)
    {
        _time += delta;
        Invalidate();
    }

    public override void OnRender(DrawingContext context)
    {
        context.DrawRectangle(new SolidColorBrush(0x0C0C12FF), null, new Rect(Vector2.Zero, Size));

        // 1. Overlapping Multi-stop Linear Gradient Card
        var startPt = new Vector2(20f + MathF.Sin(_time) * 10f, 20f);
        var endPt = new Vector2(240f + MathF.Cos(_time) * 10f, 200f);
        var linGrad = new LinearGradientBrush(startPt, endPt, new GradientStop[]
        {
            new GradientStop(new Vector4(1f, 0f, 0.5f, 1f), 0.0f),  // Deep Magenta/Red
            new GradientStop(new Vector4(0f, 0.5f, 1f, 0.8f), 0.5f), // Translucent Bright Blue
            new GradientStop(new Vector4(0f, 1f, 0.5f, 1f), 1.0f)   // Vivid Emerald
        });
        context.DrawRectangle(linGrad, new Pen(new SolidColorBrush(0xFFFFFFFF), 2f), new Rect(20, 20, 220, 180));

        // 2. Overlapping Multi-stop Radial Gradient star path
        var centerPt = new Vector2(100f + MathF.Sin(_time * 2f) * 20f, 100f + MathF.Cos(_time * 2f) * 20f);
        var radGrad = new RadialGradientBrush(centerPt, 80f, new GradientStop[]
        {
            new GradientStop(new Vector4(1f, 0.9f, 0.1f, 1f), 0.0f), // Neon Yellow center
            new GradientStop(new Vector4(1f, 0.4f, 0.0f, 0.9f), 0.5f), // Electric Orange middle
            new GradientStop(new Vector4(0f, 0f, 0f, 0f), 1.0f)      // Transparent outer ring
        });
        
        context.PushClip(new Rect(10, 10, Size.X - 20, Size.Y - 20));
        
        // Draw Radial Gradient star
        context.DrawPath(radGrad, null, _starPath);
        
        // Draw overlapping linear gradient blob
        var blobLin = new LinearGradientBrush(new Vector2(150, 50), new Vector2(250, 250), new GradientStop[]
        {
            new GradientStop(new Vector4(0.2f, 1.0f, 1.0f, 0.8f), 0.0f), // Neon Cyan
            new GradientStop(new Vector4(0.8f, 0.2f, 1.0f, 0.7f), 1.0f)  // Neon Purple
        });
        context.DrawPath(blobLin, new Pen(new SolidColorBrush(0xFFFFFF33), 1.5f), _blobPath);

        context.PopClip();
    }
}
