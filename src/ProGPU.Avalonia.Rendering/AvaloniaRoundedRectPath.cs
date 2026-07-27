using System;
using System.Numerics;
using Avalonia.Media;
using ProGPU.Vector;
using VectorArcSegment = ProGPU.Vector.ArcSegment;
using VectorPathFigure = ProGPU.Vector.PathFigure;
using VectorLineSegment = ProGPU.Vector.LineSegment;
using VectorSweepDirection = ProGPU.Vector.SweepDirection;
using AColor = Avalonia.Media.Color;

namespace Avalonia.ProGpu;

partial class DrawingContextImpl
{
    private static ProGPU.Vector.PathGeometry CreateRoundedRectPath(
        RoundedRect rounded)
    {
        Rect rect = rounded.Rect;
        Vector topLeft = LimitRadius(
            rounded.RadiiTopLeft,
            rect);
        Vector topRight = LimitRadius(
            rounded.RadiiTopRight,
            rect);
        Vector bottomRight = LimitRadius(
            rounded.RadiiBottomRight,
            rect);
        Vector bottomLeft = LimitRadius(
            rounded.RadiiBottomLeft,
            rect);

        var path = new ProGPU.Vector.PathGeometry();
        var figure = new VectorPathFigure(
            new Vector2(
                (float)(rect.Left + topLeft.X),
                (float)rect.Top))
        {
            IsClosed = true
        };
        figure.Segments.Add(
            new VectorLineSegment(
                new Vector2(
                    (float)(rect.Right - topRight.X),
                    (float)rect.Top)));
        AddCorner(
            figure,
            topRight,
            rect.Right,
            rect.Top + topRight.Y);
        figure.Segments.Add(
            new VectorLineSegment(
                new Vector2(
                    (float)rect.Right,
                    (float)(rect.Bottom - bottomRight.Y))));
        AddCorner(
            figure,
            bottomRight,
            rect.Right - bottomRight.X,
            rect.Bottom);
        figure.Segments.Add(
            new VectorLineSegment(
                new Vector2(
                    (float)(rect.Left + bottomLeft.X),
                    (float)rect.Bottom)));
        AddCorner(
            figure,
            bottomLeft,
            rect.Left,
            rect.Bottom - bottomLeft.Y);
        figure.Segments.Add(
            new VectorLineSegment(
                new Vector2(
                    (float)rect.Left,
                    (float)(rect.Top + topLeft.Y))));
        AddCorner(
            figure,
            topLeft,
            rect.Left + topLeft.X,
            rect.Top);
        path.Figures.Add(figure);
        return path;
    }

    private static void AddCorner(
        VectorPathFigure figure,
        Vector radius,
        double endX,
        double endY)
    {
        if (radius.X <= 0d || radius.Y <= 0d)
        {
            return;
        }

        figure.Segments.Add(
            new VectorArcSegment(
                new Vector2(
                    (float)endX,
                    (float)endY),
                new Vector2(
                    (float)radius.X,
                    (float)radius.Y),
                rotationAngle: 0f,
                isLargeArc: false,
                VectorSweepDirection.Clockwise,
                isSmoothJoin: true));
    }

    private static Vector LimitRadius(
        Vector radius,
        Rect rect) =>
        new(
            Math.Clamp(
                double.IsFinite(radius.X) ? radius.X : 0d,
                0d,
                Math.Abs(rect.Width) * 0.5),
            Math.Clamp(
                double.IsFinite(radius.Y) ? radius.Y : 0d,
                0d,
                Math.Abs(rect.Height) * 0.5));

    private static Vector4 ToCornerVector(
        RoundedRect rect,
        bool horizontal) =>
        horizontal
            ? new Vector4(
                (float)rect.RadiiTopLeft.X,
                (float)rect.RadiiTopRight.X,
                (float)rect.RadiiBottomRight.X,
                (float)rect.RadiiBottomLeft.X)
            : new Vector4(
                (float)rect.RadiiTopLeft.Y,
                (float)rect.RadiiTopRight.Y,
                (float)rect.RadiiBottomRight.Y,
                (float)rect.RadiiBottomLeft.Y);

    private void DrawBoxShadows(
        RoundedRect rect,
        BoxShadows shadows)
    {
        for (int index = 0; index < shadows.Count; index++)
        {
            BoxShadow shadow = shadows[index];
            if (shadow.IsInset ||
                shadow.Color.A == 0)
            {
                continue;
            }

            Rect bounds = rect.Rect
                .Inflate(shadow.Spread)
                .Translate(
                    new Vector(
                        shadow.OffsetX,
                        shadow.OffsetY));
            AColor color = shadow.Color;
            var fill = _resources.GetSolidBrush(
                color.R,
                color.G,
                color.B,
                color.A,
                1f);
            if (shadow.Blur <= 0d)
            {
                DrawingContext.DrawRoundedRectangle(
                    fill,
                    null,
                    ToLocalRect(bounds),
                    (float)Math.Max(
                        0d,
                        rect.RadiiTopLeft.X +
                        shadow.Spread),
                    (float)Math.Max(
                        0d,
                        rect.RadiiTopLeft.Y +
                        shadow.Spread),
                    ToProGpuMatrix(CommandTransform));
                continue;
            }

            // A bounded translucent expansion is used for box-shadow
            // compatibility. Full blur effects use the retained effect path.
            int rings = Math.Clamp(
                (int)Math.Ceiling(shadow.Blur),
                1,
                8);
            for (int ring = rings; ring > 0; ring--)
            {
                float opacity =
                    (float)color.A / 255f /
                    (rings * 1.5f);
                var ringFill =
                    _resources.GetSolidBrush(
                        color.R,
                        color.G,
                        color.B,
                        255,
                        opacity);
                Rect ringBounds =
                    bounds.Inflate(
                        shadow.Blur *
                        ring /
                        rings);
                DrawingContext.DrawRoundedRectangle(
                    ringFill,
                    null,
                    ToLocalRect(ringBounds),
                    (float)Math.Max(
                        0d,
                        rect.RadiiTopLeft.X +
                        shadow.Spread),
                    (float)Math.Max(
                        0d,
                        rect.RadiiTopLeft.Y +
                        shadow.Spread),
                    ToProGpuMatrix(CommandTransform));
            }
        }
    }
}
