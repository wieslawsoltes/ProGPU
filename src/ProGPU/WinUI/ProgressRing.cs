using System;
using System.Numerics;
using ProGPU.Layout;
using ProGPU.Scene;
using ProGPU.Vector;

namespace ProGPU.WinUI;

public class ProgressRing : Control
{
    private bool _isActive = true;
    private float _rotationOffset;

    public bool IsActive
    {
        get => _isActive;
        set
        {
            if (_isActive != value)
            {
                _isActive = value;
                Invalidate();
            }
        }
    }

    public ProgressRing()
    {
        Width = 32f;
        Height = 32f;
        HorizontalAlignment = HorizontalAlignment.Center;
        VerticalAlignment = VerticalAlignment.Center;
        Background = new SolidColorBrush(0x00000000); // Fully transparent container
        BorderBrush = new SolidColorBrush(0x0078D4FF); // Segoe Accent Blue dots
    }

    protected override Vector2 MeasureOverride(Vector2 availableSize)
    {
        return new Vector2(Width, Height);
    }

    private static PathGeometry CreateCirclePath(float cx, float cy, float r)
    {
        var geo = new PathGeometry();
        var fig = new PathFigure(new Vector2(cx + r, cy), isClosed: true);
        
        float k = 0.552284749831f * r;
        fig.Segments.Add(new CubicBezierSegment(new Vector2(cx + r, cy + k), new Vector2(cx + k, cy + r), new Vector2(cx, cy + r)));
        fig.Segments.Add(new CubicBezierSegment(new Vector2(cx - k, cy + r), new Vector2(cx - r, cy + k), new Vector2(cx - r, cy)));
        fig.Segments.Add(new CubicBezierSegment(new Vector2(cx - r, cy - k), new Vector2(cx - k, cy - r), new Vector2(cx, cy - r)));
        fig.Segments.Add(new CubicBezierSegment(new Vector2(cx + k, cy - r), new Vector2(cx + r, cy - k), new Vector2(cx + r, cy)));
        
        geo.Figures.Add(fig);
        return geo;
    }

    public override void OnRender(DrawingContext context)
    {
        if (IsActive)
        {
            float cx = Size.X / 2f;
            float cy = Size.Y / 2f;
            float radius = (Size.X - 8f) / 2f; // Keep dot bounds inside control nicely
            float dotRadius = 3f;

            // Draw 8 circular dots in a loop with a tail fading opacity sweep
            for (int i = 0; i < 8; i++)
            {
                // Angle with dynamic rotation sweep offset
                float angle = (float)(i * (2.0 * Math.PI / 8.0) + _rotationOffset);
                float x = cx + radius * (float)Math.Cos(angle);
                float y = cy + radius * (float)Math.Sin(angle);

                // Sweep opacity: creates a fading trail of loaded dots
                float opacityFraction = (i / 8f);
                uint alpha = (uint)(255 * opacityFraction);
                
                // Segoe Accent Blue with dynamic alpha opacity
                var dotColor = new SolidColorBrush(0x0078D400 | alpha);

                var dotPath = CreateCirclePath(x, y, dotRadius);
                context.DrawPath(dotColor, null, dotPath);
            }

            // Animate spin speed smoothly at 60 FPS
            _rotationOffset = (float)((_rotationOffset + 0.08f) % (2.0 * Math.PI));

            // Self-invalidate to trigger continuous render presents on the WebGPU surface
            Invalidate();
        }

        base.OnRender(context);
    }
}
